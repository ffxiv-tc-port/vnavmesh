using Navmesh.Movement;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace Navmesh;

public class AsyncMoveRequest : IDisposable
{
    private NavmeshManager _manager;
    private FollowPath _follow;
    private Task<List<Vector3>>? _pendingTask;
    private CancellationTokenSource? _pendingCts;
    private bool _pendingFly;
    private float _pendingDestRange;

    public bool TaskInProgress => _pendingTask != null;

    public AsyncMoveRequest(NavmeshManager manager, FollowPath follow)
    {
        _manager = manager;
        _follow = follow;

        _follow.OnStuck += (dest, fly, range) =>
        {
            if (!Service.Config.RetryOnStuck)
                return;

            MoveTo(dest, fly, range);
        };
    }

    public void Dispose()
    {
        if (_pendingTask != null)
        {
            // Request cancellation first so a still-running pathfind (especially flying/volume
            // queries, which poll the token from inside their search loop) has a chance to stop
            // quickly on its own. Even so, mesh pathfinds don't check the token mid-search, so
            // still bound the wait defensively rather than blocking the game indefinitely - on
            // timeout we drop the task without observing its result rather than race it.
            _pendingCts?.Cancel();
            if (!_pendingTask.IsCompleted && !_pendingTask.Wait(TimeSpan.FromSeconds(5)))
            {
                Service.Log.Warning("[navmesh] Timed out waiting for in-progress pathfind to finish; abandoning it");
                _pendingCts?.Dispose();
                _pendingCts = null;
                _pendingTask = null;
                return;
            }
            _pendingTask.Dispose();
            _pendingTask = null;
            _pendingCts?.Dispose();
            _pendingCts = null;
        }
    }

    public void Update()
    {
        if (_pendingTask != null && _pendingTask.IsCompleted)
        {
            Service.Log.Information($"Pathfinding complete");
            try
            {
                _follow.Move(_pendingTask.Result, !_pendingFly, _pendingDestRange);
            }
            catch (Exception ex)
            {
                Plugin.DuoLog(ex, "Failed to find path");
            }
            _pendingTask.Dispose();
            _pendingTask = null;
            _pendingCts?.Dispose();
            _pendingCts = null;
        }
    }

    public bool MoveTo(Vector3 dest, bool fly, float range = 0)
    {
        if (_pendingTask != null)
        {
            Service.Log.Error($"Pathfinding task is in progress...");
            return false;
        }

        var toleranceStr = range > 0 ? $" within {range}y" : "";

        Service.Log.Info($"Queueing {(fly ? "fly" : "move")}-to {dest:f3}{toleranceStr}");
        _pendingCts = new CancellationTokenSource();
        _pendingTask = _manager.QueryPath(Service.ClientState.LocalPlayer?.Position ?? default, dest, fly, _pendingCts.Token, range);
        _pendingFly = fly;
        _pendingDestRange = range;
        return true;
    }
}
