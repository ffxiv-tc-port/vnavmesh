using DotRecast.Detour;
using Navmesh.NavVolume;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Numerics;

namespace Navmesh;

// full set of data needed for navigation in the zone
public record class Navmesh(int CustomizationVersion, DtNavMesh Mesh, VoxelMap? Volume)
{
    public static readonly uint Magic = 0x444D564E; // 'NVMD'
    // 24: SceneExtractor 新增「材質位 0x2000000 ⇒ ForceUnwalkable」(競技場破洞)。
    // 25: 自訂連結的 area id 從單一常數 OffMeshEndpoint(5) 換成 AreaId 位元旗標(見下)。
    //     這改變了序列化網格裡多邊形的 area 值,舊快取的 5 在新語意下會被讀成
    //     Warp|Shortcut(1|4),尋路成本與 FollowPath 的條件判斷都會錯 ⇒ **必須 bump**。
    //     這兩版都是網格內容的變更,不 bump 的話既有使用者一直吃舊快取、修正等於沒發生。
    //     📌 編號與上游的 25 對齊是巧合:上游的 24 對應 `594ef7b stop filtering bgparts`
    //     (丟掉 matMask 那一路的 forceClear),我方刻意不取那顆。
    // 26: SceneExtractor.CalculateSphereBounds 改算精確的橢球包圍盒。原本非等比縮放的球體會被低估,
    //     低估的包圍盒會讓 NavmeshRasterizer 誤剔除整個實例、以及少填一段內部實體 => 網格內容會變,
    //     不 bump 的話既有使用者一直吃舊快取、修正等於沒發生。等比縮放的球體結果不變。
    public static readonly uint Version = 26;

    // 自訂連結建出來的多邊形兩端座標。**不序列化** —— 真正的連結是直接加進 DtNavMesh 的,
    // 這個欄位只給偵錯視覺化用(見 Debug/DebugLinks.cs)。
    public readonly List<(Vector3 Start, Vector3 End)> Links = [];

    // 自訂連結多邊形的 area id。用位元旗標而不是單一數值,是為了讓「這條連結是什麼種類」與
    // 「這是不是終點」可以同時表達 —— FollowPath 要靠 Endpoint 位判斷該不該停下來等纜車。
    // ⚠️ 有 None = 0,所以 default(AreaId) 落在合法值上(不是「沒有零值的列舉」那個坑)。
    [Flags]
    public enum AreaId
    {
        None = 0,
        Warp = 0x01,        // 直接傳送(以太之光之類),目前沒有實作
        ClientPath = 0x02,  // 走進觸發區之後由客戶端播的固定路徑(宇宙快線、部分副本轉場)
        Shortcut = 0x04,    // 一般走路速度的捷徑,只是整條路比較短(跳下平台、穿過 recast 認為太窄的縫)

        Endpoint = 0x10,    // 終點側要標起來,供 FollowPath 的邏輯與尋路啟發式使用
        ClientPathEnd = ClientPath | Endpoint,

        Default = 0x3F
    }

    // throws an exception on failure
    public static Navmesh Deserialize(BinaryReader reader, int expectedCustomizationVersion)
    {
        var magic = reader.ReadUInt32();
        var version = reader.ReadUInt32();
        if (magic != Magic || version != Version)
            throw new Exception("Incorrect header");
        var customizationVersion = reader.ReadInt32();
        if (customizationVersion != expectedCustomizationVersion)
            throw new Exception("Outdated customization version");

        using var compressedReader = new BinaryReader(new BrotliStream(reader.BaseStream, CompressionMode.Decompress, true));
        var mesh = DeserializeMesh(compressedReader);
        var volume = DeserializeVolume(compressedReader);
        return new(customizationVersion, mesh, volume);
    }

    public void Serialize(BinaryWriter writer)
    {
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(CustomizationVersion);

        using var compressedWriter = new BinaryWriter(new BrotliStream(writer.BaseStream, CompressionLevel.Optimal, true));
        SerializeMesh(compressedWriter, Mesh);
        SerializeVolume(compressedWriter, Volume);
    }

    private static DtNavMesh DeserializeMesh(BinaryReader reader)
    {
        var numTiles = reader.ReadInt32();
        var opts = DeserializeMeshParams(reader);
        var result = new DtNavMesh(opts, reader.ReadInt32());
        for (int i = 0; i < numTiles; ++i)
        {
            var tileRef = reader.ReadInt64();
            var tile = DeserializeMeshTile(reader);
            result.AddTile(tile, i, tileRef);
        }
        return result;
    }

    private static void SerializeMesh(BinaryWriter writer, DtNavMesh mesh)
    {
        writer.Write(mesh.GetTileCount());
        SerializeMeshParams(writer, mesh.GetParams());
        writer.Write(mesh.GetMaxVertsPerPoly());

        for (int i = 0; i < mesh.GetMaxTiles(); ++i)
        {
            DtMeshTile tile = mesh.GetTile(i);
            if (tile?.data?.header == null)
                continue;
            writer.Write(mesh.GetTileRef(tile));
            SerializeMeshTile(writer, tile.data);
        }
    }

    private static DtNavMeshParams DeserializeMeshParams(BinaryReader reader) => new()
    {
        orig = DeserializeVector3(reader).SystemToRecast(),
        tileWidth = reader.ReadSingle(),
        tileHeight = reader.ReadSingle(),
        maxTiles = reader.ReadInt32(),
        maxPolys = reader.ReadInt32()
    };

    private static void SerializeMeshParams(BinaryWriter writer, DtNavMeshParams opt)
    {
        SerializeVector3(writer, opt.orig.RecastToSystem());
        writer.Write(opt.tileWidth);
        writer.Write(opt.tileHeight);
        writer.Write(opt.maxTiles);
        writer.Write(opt.maxPolys);
    }

    private static DtMeshData DeserializeMeshTile(BinaryReader reader)
    {
        var tile = new DtMeshData();
        tile.header = new();
        tile.header.magic = DtNavMesh.DT_NAVMESH_MAGIC;
        tile.header.version = DtNavMesh.DT_NAVMESH_VERSION;
        tile.header.x = reader.ReadInt32();
        tile.header.y = reader.ReadInt32();
        tile.header.layer = reader.ReadInt32();
        tile.header.userId = reader.ReadInt32();
        tile.header.walkableHeight = reader.ReadSingle();
        tile.header.walkableRadius = reader.ReadSingle();
        tile.header.walkableClimb = reader.ReadSingle();
        var bounds = DeserializeBounds(reader);
        tile.header.bmin = bounds.min.SystemToRecast();
        tile.header.bmax = bounds.max.SystemToRecast();

        tile.header.vertCount = reader.ReadInt32();
        tile.verts = new float[tile.header.vertCount * 3];
        for (int i = 0; i < tile.verts.Length; ++i)
            tile.verts[i] = reader.ReadSingle();

        tile.header.polyCount = reader.ReadInt32();
        tile.polys = new DtPoly[tile.header.polyCount];
        for (int i = 0; i < tile.header.polyCount; ++i)
        {
            var nv = reader.ReadByte();
            var poly = tile.polys[i] = new DtPoly(i, nv);
            poly.vertCount = nv;
            poly.areaAndtype = reader.ReadByte();
            poly.flags = reader.ReadUInt16();
            for (int j = 0; j < nv; ++j)
                poly.verts[j] = reader.ReadUInt16();
            for (int j = 0; j < nv; ++j)
                poly.neis[j] = reader.ReadUInt16();
        }
        //tile.header.maxLinkCount = reader.ReadInt32(); - some legacy thing, always 0

        tile.header.detailMeshCount = reader.ReadInt32();
        tile.detailMeshes = new DtPolyDetail[tile.header.detailMeshCount];
        for (int i = 0; i < tile.header.detailMeshCount; ++i)
            tile.detailMeshes[i] = new(reader.ReadInt32(), reader.ReadInt32(), reader.ReadByte(), reader.ReadByte());

        tile.header.detailVertCount = reader.ReadInt32();
        tile.detailVerts = new float[tile.header.detailVertCount * 3];
        for (int i = 0; i < tile.detailVerts.Length; ++i)
            tile.detailVerts[i] = reader.ReadSingle();

        tile.header.detailTriCount = reader.ReadInt32();
        tile.detailTris = new int[tile.header.detailTriCount * 4];
        for (int i = 0; i < tile.detailTris.Length; ++i)
            tile.detailTris[i] = reader.ReadByte();

        tile.header.bvQuantFactor = reader.ReadSingle();
        tile.header.bvNodeCount = reader.ReadInt32();
        tile.bvTree = new DtBVNode[tile.header.bvNodeCount];
        for (int i = 0; i < tile.header.bvNodeCount; ++i)
        {
            var node = tile.bvTree[i] = new();
            node.bmin[0] = reader.ReadInt32();
            node.bmin[1] = reader.ReadInt32();
            node.bmin[2] = reader.ReadInt32();
            node.bmax[0] = reader.ReadInt32();
            node.bmax[1] = reader.ReadInt32();
            node.bmax[2] = reader.ReadInt32();
            node.i = reader.ReadInt32();
        }

        tile.header.offMeshBase = reader.ReadInt32();
        tile.header.offMeshConCount = reader.ReadInt32();
        tile.offMeshCons = new DtOffMeshConnection[tile.header.offMeshConCount];
        for (int i = 0; i < tile.header.offMeshConCount; i++)
        {
            var conn = tile.offMeshCons[i] = new();
            conn.pos[0] = DeserializeVector3(reader).SystemToRecast();
            conn.pos[1] = DeserializeVector3(reader).SystemToRecast();
            conn.rad = reader.ReadSingle();
            conn.poly = reader.ReadUInt16();
            conn.flags = reader.ReadByte();
            conn.side = reader.ReadByte();
            conn.userId = reader.ReadInt32();
        }

        return tile;
    }

    private static void SerializeMeshTile(BinaryWriter writer, DtMeshData tile)
    {
        writer.Write(tile.header.x);
        writer.Write(tile.header.y);
        writer.Write(tile.header.layer);
        writer.Write(tile.header.userId);
        writer.Write(tile.header.walkableHeight);
        writer.Write(tile.header.walkableRadius);
        writer.Write(tile.header.walkableClimb);
        SerializeBounds(writer, tile.header.bmin.RecastToSystem(), tile.header.bmax.RecastToSystem());

        writer.Write(tile.header.vertCount);
        for (int i = 0; i < tile.header.vertCount * 3; ++i)
            writer.Write(tile.verts[i]);

        writer.Write(tile.header.polyCount);
        for (int i = 0; i < tile.header.polyCount; ++i)
        {
            var poly = tile.polys[i];
            writer.Write((byte)poly.vertCount);
            writer.Write((byte)poly.areaAndtype);
            writer.Write((ushort)poly.flags);
            for (int j = 0; j < poly.vertCount; ++j)
                writer.Write((ushort)poly.verts[j]);
            for (int j = 0; j < poly.vertCount; ++j)
                writer.Write((ushort)poly.neis[j]);
        }
        //writer.Write(tile.header.maxLinkCount); - some legacy thing, always 0

        writer.Write(tile.header.detailMeshCount);
        for (int i = 0; i < tile.header.detailMeshCount; ++i)
        {
            ref var mesh = ref tile.detailMeshes[i];
            writer.Write(mesh.vertBase);
            writer.Write(mesh.triBase);
            writer.Write((byte)mesh.vertCount);
            writer.Write((byte)mesh.triCount);
        }

        writer.Write(tile.header.detailVertCount);
        for (int i = 0; i < tile.header.detailVertCount * 3; ++i)
            writer.Write(tile.detailVerts[i]);

        writer.Write(tile.header.detailTriCount);
        for (int i = 0; i < tile.header.detailTriCount * 4; ++i)
            writer.Write((byte)tile.detailTris[i]);

        writer.Write(tile.header.bvQuantFactor);
        writer.Write(tile.header.bvNodeCount);
        for (int i = 0; i < tile.header.bvNodeCount; ++i)
        {
            var node = tile.bvTree[i];
            writer.Write(node.bmin[0]);
            writer.Write(node.bmin[1]);
            writer.Write(node.bmin[2]);
            writer.Write(node.bmax[0]);
            writer.Write(node.bmax[1]);
            writer.Write(node.bmax[2]);
            writer.Write(node.i);
        }

        writer.Write(tile.header.offMeshBase);
        writer.Write(tile.header.offMeshConCount);
        for (int i = 0; i < tile.header.offMeshConCount; i++)
        {
            var conn = tile.offMeshCons[i];
            SerializeVector3(writer, conn.pos[0].RecastToSystem());
            SerializeVector3(writer, conn.pos[1].RecastToSystem());
            writer.Write(conn.rad);
            writer.Write((ushort)conn.poly);
            writer.Write((byte)conn.flags);
            writer.Write((byte)conn.side);
            writer.Write(conn.userId);
        }
    }

    private static VoxelMap? DeserializeVolume(BinaryReader reader)
    {
        var numLevels = reader.ReadInt32();
        if (numLevels == 0)
            return null;

        var tilesPerLevel = new int[numLevels];
        foreach (ref var l in tilesPerLevel.AsSpan())
            l = reader.ReadInt32();
        var (min, max) = DeserializeBounds(reader);
        var volume = new VoxelMap(min, max, tilesPerLevel);
        DeserializeVolumeTile(reader, volume.RootTile);
        return volume;
    }

    private static void SerializeVolume(BinaryWriter writer, VoxelMap? volume)
    {
        if (volume == null)
        {
            writer.Write(0); // 0 levels;
            return;
        }

        writer.Write(volume.Levels.Length);
        foreach (ref var l in volume.Levels.AsSpan())
            writer.Write(l.NumCellsX); // note: current assumption is that all dimensions are identical

        SerializeBounds(writer, volume.RootTile.BoundsMin, volume.RootTile.BoundsMax);
        SerializeVolumeTile(writer, volume.RootTile);
    }

    private static unsafe void DeserializeVolumeTile(BinaryReader reader, VoxelMap.Tile tile)
    {
        for (int i = 0; i < tile.Contents.Length; ++i)
        {
            var v = tile.Contents[i] = reader.ReadUInt16();
            if (v == 0 || v == ushort.MaxValue)
            {
                var run = reader.ReadUInt16();
                while (run-- != 0)
                    tile.Contents[++i] = v;
            }
        }

        var numSubtiles = reader.ReadInt32();
        for (int i = 0; i < numSubtiles; ++i)
        {
            var subBounds = DeserializeBounds(reader);
            var subTile = new VoxelMap.Tile(tile.Owner, subBounds.min, subBounds.max, tile.Level + 1);
            DeserializeVolumeTile(reader, subTile);
            tile.Subdivision.Add(subTile);
        }
    }

    private static unsafe void SerializeVolumeTile(BinaryWriter writer, VoxelMap.Tile tile)
    {
        // use simple run-length encoding for fully empty / fully solid tiles
        for (int i = 0; i < tile.Contents.Length; ++i)
        {
            var v = tile.Contents[i];
            writer.Write(v);
            if (v == 0 || v == ushort.MaxValue)
            {
                ushort run = 0;
                while (i + 1 < tile.Contents.Length && tile.Contents[i + 1] == v)
                {
                    ++run;
                    ++i;
                }
                writer.Write(run);
            }
        }

        writer.Write(tile.Subdivision.Count);
        foreach (var sub in tile.Subdivision)
        {
            SerializeBounds(writer, sub.BoundsMin, sub.BoundsMax);
            SerializeVolumeTile(writer, sub);
        }
    }

    private static (Vector3 min, Vector3 max) DeserializeBounds(BinaryReader reader) => (DeserializeVector3(reader), DeserializeVector3(reader));
    private static void SerializeBounds(BinaryWriter writer, Vector3 min, Vector3 max)
    {
        SerializeVector3(writer, min);
        SerializeVector3(writer, max);
    }

    private static Vector3 DeserializeVector3(BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    private static void SerializeVector3(BinaryWriter writer, Vector3 v)
    {
        writer.Write(v.X);
        writer.Write(v.Y);
        writer.Write(v.Z);
    }
}
