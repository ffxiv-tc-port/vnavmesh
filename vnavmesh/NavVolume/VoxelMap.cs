using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Navmesh.NavVolume;

public class VoxelMap
{
    public class Level
    {
        public Vector3 CellSize { get; init; }
        public Vector3 InvCellSize { get; init; }
        public int NumCellsX { get; init; }
        public int NumCellsY { get; init; }
        public int NumCellsZ { get; init; }
        public int ShiftYX { get; init; }
        public int ShiftXZ { get; init; }

        public int NumCellsTotal => NumCellsX * NumCellsY * NumCellsZ;

        public Level(Vector3 extent, int nx, int ny, int nz)
        {
            if ((nx & (nx - 1)) != 0 || (ny & (ny - 1)) != 0 || (nz & (nz - 1)) != 0)
                throw new ArgumentException($"Non-power-of-2 cell size not supported: got {nx}*{ny}*{nz}");

            CellSize = extent / new Vector3(nx, ny, nz);
            InvCellSize = new Vector3(1) / CellSize;
            NumCellsX = nx;
            NumCellsY = ny;
            NumCellsZ = nz;
            ShiftYX = BitOperations.Log2((uint)ny);
            ShiftXZ = BitOperations.Log2((uint)nx);
        }

        public Level(Vector3 extent, int ncells) : this(extent, ncells, ncells, ncells) { }

        public bool InBounds(int x, int y, int z) => x >= 0 && x < NumCellsX && y >= 0 && y < NumCellsY && z >= 0 && z < NumCellsZ;
        public bool InBounds((int x, int y, int z) v) => InBounds(v.x, v.y, v.z);

        public (int x, int y, int z) ClampMin(int x, int y, int z) => (Math.Max(x, 0), Math.Max(y, 0), Math.Max(z, 0));
        public (int x, int y, int z) ClampMin((int x, int y, int z) v) => ClampMin(v.x, v.y, v.z);

        public (int x, int y, int z) ClampMax(int x, int y, int z) => (Math.Min(x, NumCellsX - 1), Math.Min(y, NumCellsY - 1), Math.Min(z, NumCellsZ - 1));
        public (int x, int y, int z) ClampMax((int x, int y, int z) v) => ClampMax(v.x, v.y, v.z);

        public (int x, int y, int z) Clamp(int x, int y, int z) => ClampMax(ClampMin(x, y, z));
        public (int x, int y, int z) Clamp((int x, int y, int z) v) => ClampMax(ClampMin(v.x, v.y, v.z));

        public (int x, int y, int z) IndexToVoxel(ushort index)
        {
            var y = index & (NumCellsY - 1);
            var xz = index >> ShiftYX;
            var x = xz & (NumCellsX - 1);
            var z = xz >> ShiftXZ;
            return (x, y, z);
        }

        public ushort VoxelToIndex(int x, int y, int z) => (ushort)((((z << ShiftXZ) + x) << ShiftYX) + y);
        public ushort VoxelToIndex((int x, int y, int z) v) => VoxelToIndex(v.x, v.y, v.z);
    }

    public class Tile
    {
        public VoxelMap Owner { get; init; }
        public Vector3 BoundsMin { get; init; }
        public Vector3 BoundsMax { get; init; }
        public int Level { get; init; }
        // cell value: high bit unset: empty voxel (TODO: region id in low bits?); high bit set: voxel with solid geometry (VoxelIdMask if leaf, subvoxel index otherwise); order is (y,x,z)
        // 兩種儲存恰有一個非 null：_cells 每格一個值；_solidBits 只在每格都是 0 或 0xFFFF 時使用（1 位元＝0xFFFF）。
        // 最底層 tile 依建置器的構造只會是這兩種值，一格從 2 位元組降到 1 位元。
        private ushort[]? _cells;
        private ulong[]? _solidBits;
        public int NumCells { get; }
        public List<Tile> Subdivision { get; init; } = new();

        public Level LevelDesc => Owner.Levels[Level];
        public bool IsCompact => _solidBits != null;

        public Tile(VoxelMap owner, Vector3 boundsMin, Vector3 boundsMax, int level)
        {
            Owner = owner;
            BoundsMin = boundsMin;
            BoundsMax = boundsMax;
            Level = level;
            NumCells = owner.Levels[level].NumCellsTotal;
            _cells = new ushort[NumCells];
        }

        // cells 只被讀取、不被保留；能用位元集就用位元集，否則複製一份。
        public Tile(VoxelMap owner, Vector3 boundsMin, Vector3 boundsMax, int level, ReadOnlySpan<ushort> cells)
        {
            Owner = owner;
            BoundsMin = boundsMin;
            BoundsMax = boundsMax;
            Level = level;
            NumCells = owner.Levels[level].NumCellsTotal;
            if (cells.Length != NumCells)
                throw new ArgumentException($"Cell count mismatch: got {cells.Length}, expected {NumCells}");
            _solidBits = TryPackSolidBits(cells);
            if (_solidBits == null)
                _cells = cells.ToArray();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ushort GetCell(int index)
        {
            var bits = _solidBits;
            if (bits != null)
            {
                if ((uint)index >= (uint)NumCells)
                    ThrowIndexOutOfRange();
                return (bits[index >> 6] & (1ul << index)) != 0 ? ushort.MaxValue : (ushort)0;
            }
            return _cells![index];
        }

        private static void ThrowIndexOutOfRange() => throw new IndexOutOfRangeException();

        // 位元集放不下的值會先把整個 tile 展開回 ushort[]，之後讀到的值與一直用 ushort[] 相同。
        public void SetCell(int index, ushort value)
        {
            var bits = _solidBits;
            if (bits != null)
            {
                if ((uint)index >= (uint)NumCells)
                    ThrowIndexOutOfRange();
                if (value == 0)
                {
                    bits[index >> 6] &= ~(1ul << index);
                    return;
                }
                if (value == ushort.MaxValue)
                {
                    bits[index >> 6] |= 1ul << index;
                    return;
                }
                var cells = new ushort[NumCells];
                for (int i = 0; i < cells.Length; ++i)
                    cells[i] = GetCell(i);
                _cells = cells;
                _solidBits = null;
            }
            _cells![index] = value;
        }

        // 只在還沒對外公開（建置中、反序列化中）時呼叫；與讀取並行不安全。
        public bool TryCompact()
        {
            if (_cells == null)
                return true;
            var bits = TryPackSolidBits(_cells);
            if (bits == null)
                return false;
            _solidBits = bits;
            _cells = null;
            return true;
        }

        private static ulong[]? TryPackSolidBits(ReadOnlySpan<ushort> cells)
        {
            foreach (var v in cells)
                if (v != 0 && v != ushort.MaxValue)
                    return null;
            var bits = new ulong[(cells.Length + 63) >> 6];
            for (int i = 0; i < cells.Length; ++i)
                if (cells[i] != 0)
                    bits[i >> 6] |= 1ul << i;
            return bits;
        }

        public (int x, int y, int z) WorldToVoxel(Vector3 v)
        {
            var frac = (v - BoundsMin) * LevelDesc.InvCellSize;
            return ((int)frac.X, (int)frac.Y, (int)frac.Z);
        }
        public Vector3 VoxelToWorld(int x, int y, int z) => BoundsMin + new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) * LevelDesc.CellSize;
        public Vector3 VoxelToWorld((int x, int y, int z) v) => VoxelToWorld(v.x, v.y, v.z);

        public (Vector3 min, Vector3 max) CalculateSubdivisionBounds(int x, int y, int z)
        {
            var ld = LevelDesc;
            var min = BoundsMin + new Vector3(x, y, z) * ld.CellSize;
            return (min, min + ld.CellSize);
        }
        public (Vector3 min, Vector3 max) CalculateSubdivisionBounds((int x, int y, int z) v) => CalculateSubdivisionBounds(v.x, v.y, v.z);

        // 不變式：每一層都要自己驗邊界。子 tile 的邊界是各自用浮點重算的，
        // 父層選中這個 tile 不保證子層算出的座標仍落在 [0, NumCells) 內。
        public (ulong index, bool empty) FindLeafVoxel(Vector3 p)
        {
            var v = WorldToVoxel(p);
            if (!LevelDesc.InBounds(v))
                return (InvalidVoxel, false); // out of bounds; consider everything outside to be occupied

            var idx = LevelDesc.VoxelToIndex(v);
            var data = GetCell(idx);
            if ((data & VoxelOccupiedBit) == 0)
            {
                return (EncodeIndex(idx), true); // empty at this level
            }
            data &= VoxelIdMask;
            if (data == VoxelIdMask)
            {
                return (EncodeIndex(idx), false); // occupied leaf
            }
            else
            {
                var sub = Subdivision[data].FindLeafVoxel(p);
                return (EncodeIndex(idx, sub.index), sub.empty);
            }
        }

        public IEnumerable<(ulong index, bool empty)> EnumerateLeafVoxels(Vector3 bmin, Vector3 bmax)
        {
            var ld = LevelDesc;
            var vmin = ld.ClampMin(WorldToVoxel(bmin));
            var vmax = ld.ClampMax(WorldToVoxel(bmax));
            for (int z = vmin.z; z <= vmax.z; z++)
            {
                for (int x = vmin.x; x <= vmax.x; ++x)
                {
                    for (int y = vmin.y; y <= vmax.y; ++y)
                    {
                        var idx = ld.VoxelToIndex(x, y, z);
                        var data = GetCell(idx);
                        if ((data & VoxelOccupiedBit) == 0)
                        {
                            yield return (EncodeIndex(idx), true); // empty at this level
                            continue;
                        }
                        data &= VoxelIdMask;
                        if (data == VoxelIdMask)
                        {
                            yield return(EncodeIndex(idx), false); // occupied leaf
                        }
                        else
                        {
                            foreach (var sub in Subdivision[data].EnumerateLeafVoxels(bmin, bmax))
                                yield return (EncodeIndex(idx, sub.index), sub.empty);
                        }
                    }
                }
            }
        }
    }

    public Level[] Levels { get; init; }
    public Tile RootTile { get; init; }

    public const ushort VoxelOccupiedBit = 0x8000;
    public const ushort VoxelIdMask = 0x7fff;

    // voxel index is stored as (... L2 L1 L0)
    public const ushort IndexLevelMask = 0xffff;
    public const int IndexLevelShift = 16;

    public const ulong InvalidVoxel = ulong.MaxValue;

    public static ulong EncodeIndex(ushort tileIndex, ulong subIndex = InvalidVoxel) => (subIndex << IndexLevelShift) + tileIndex;
    public static ulong EncodeSubIndex(ulong voxel, ushort tileIndex, int level)
    {
        var shift = IndexLevelShift * level;
        voxel &= ~((ulong)IndexLevelMask << shift);
        voxel |= (ulong)tileIndex << shift;
        return voxel;
    }
    public static ushort DecodeIndex(ref ulong index)
    {
        var tileIndex = (ushort)(index & IndexLevelMask);
        index >>= IndexLevelShift;
        return tileIndex;
    }

    public VoxelMap(Vector3 boundsMin, Vector3 boundsMax, int[] tilesPerLevel)
    {
        Levels = new Level[tilesPerLevel.Length];
        var levelExtent = boundsMax - boundsMin;
        for (int i = 0; i < Levels.Length; ++i)
        {
            Levels[i] = new(levelExtent, tilesPerLevel[i]);
            levelExtent = Levels[i].CellSize;
        }

        RootTile = new(this, boundsMin, boundsMax, 0);
    }

    // 建置完、對外公開前呼叫；見 Tile.TryCompact。
    public void CompactTiles() => CompactTiles(RootTile);

    private static void CompactTiles(Tile tile)
    {
        tile.TryCompact();
        foreach (var sub in tile.Subdivision)
            CompactTiles(sub);
    }

    public bool IsEmpty(ulong voxel)
    {
        var tile = RootTile;
        while (true)
        {
            var tileIndex = DecodeIndex(ref voxel);
            if (tileIndex == IndexLevelMask)
                return false; // asking for non-leaf => consider non-empty

            var data = tile.GetCell(tileIndex);
            if ((data & VoxelOccupiedBit) == 0)
                return true; // found empty voxel
            data &= VoxelIdMask;
            if (data == VoxelIdMask)
                return false; // found non-empty leaf
            tile = tile.Subdivision[data];
        }
    }

    public (ulong voxel, bool empty) FindLeafVoxel(Vector3 p) => RootTile.FindLeafVoxel(p);

    public (Vector3 min, Vector3 max) VoxelBounds(ulong voxel, float eps)
    {
        var tile = RootTile;
        var eps3 = new Vector3(eps);
        while (true)
        {
            var tileIndex = DecodeIndex(ref voxel);
            if (tileIndex == IndexLevelMask)
            {
                return (tile.BoundsMin + eps3, tile.BoundsMax - eps3);
            }

            var data = tile.GetCell(tileIndex);
            var id = data & VoxelIdMask;
            if ((data & VoxelOccupiedBit) == 0 || id == VoxelIdMask)
            {
                var bb = tile.CalculateSubdivisionBounds(Levels[tile.Level].IndexToVoxel(tileIndex));
                return (bb.min + eps3, bb.max - eps3);
            }

            tile = tile.Subdivision[id];
        }
    }

    public void Build(Voxelizer vox, int tx, int tz)
    {
        // downsample
        var voxelizers = new Voxelizer[Levels.Length];
        voxelizers[Levels.Length - 1] = vox;
        for (int i = Levels.Length - 1; i > 0; --i)
        {
            ref var l = ref Levels[i];
            voxelizers[i - 1] = voxelizers[i].Downsample(l.NumCellsX, l.NumCellsY, l.NumCellsZ);
        }

        // subdivide L0
        var ny = Levels[0].NumCellsY;
        var idx = Levels[0].VoxelToIndex(tx, 0, tz);
        for (int ty = 0; ty < ny; ++ty, ++idx)
        {
            BuildTile(RootTile, idx, 0, ty, 0, voxelizers);
        }
    }

    private void BuildTile(Tile parent, ushort index, int x0, int y0, int z0, Span<Voxelizer> source)
    {
        var (solid, empty) = source[0].Get(x0, y0, z0);
        if (!solid)
        {
            // fully empty => nothing to do
        }
        else if (!empty)
        {
            // fully solid
            parent.SetCell(index, VoxelOccupiedBit | VoxelIdMask);
        }
        else
        {
            parent.SetCell(index, (ushort)(VoxelOccupiedBit | parent.Subdivision.Count));
            var (min, max) = parent.CalculateSubdivisionBounds(parent.LevelDesc.IndexToVoxel(index));
            var tile = new Tile(this, min, max, parent.Level + 1);
            parent.Subdivision.Add(tile);

            // subdivide
            ref var l = ref Levels[tile.Level];
            int xOff = x0 * l.NumCellsX;
            int yOff = y0 * l.NumCellsY;
            int zOff = z0 * l.NumCellsZ;
            ushort i = 0;
            for (int z = 0; z < l.NumCellsZ; ++z)
            {
                for (int x = 0; x < l.NumCellsX; ++x)
                {
                    for (int y = 0; y < l.NumCellsY; ++y, ++i)
                    {
                        BuildTile(tile, i, xOff + x, yOff + y, zOff + z, source.Slice(1));
                    }
                }
            }
        }
    }
}
