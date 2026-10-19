namespace Navmesh.NavVolume;

/// <summary>
/// 飛行搜尋的三個旋鈕；<see cref="Default"/>＝與沒有這個結構之前逐點相同。
/// <see cref="VoxelPathfind.Start"/> 讀一次，搜尋途中改它不影響這一次。
/// </summary>
public readonly record struct VoxelPathfindTuning(float HeuristicWeight, float RaycastRange, int MaxSteps)
{
    /// <summary>HeuristicWeight 0＝各模式原本的值（舊搜尋 1、快速搜尋 <see cref="VoxelPathfind.FastHeuristicWeight"/>）；
    /// RaycastRange 0＝搜尋中的視線檢查不限距離；MaxSteps＝<see cref="VoxelPathfind.MaxSteps"/>。</summary>
    public static VoxelPathfindTuning Default => new(0, 0, VoxelPathfind.MaxSteps);
}
