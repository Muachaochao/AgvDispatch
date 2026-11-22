using System.Diagnostics;
using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Infrastructure.Traffic;
using SqlSugar;

namespace AgvDispatch.Service;

/// <summary>
/// Builds segment-progress evidence from the commissioned Robokit task status
/// and the exact sent route snapshot. The current running move task identifies
/// the current edge; all earlier edges are the only edges considered proven as
/// fully traversed. The vehicle pose and body envelope are still required by the
/// evaluator before a resource can be released.
/// </summary>
public sealed class AgvTrafficProgressReader(
    ISqlSugarClient database,
    IAgvStateCache vehicles,
    HandshakeExecutionOptions handshake,
    TrafficSegmentReleaseWorkerOptions options,
    TimeProvider? clock = null) : IAuthoritativeTrafficProgressReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<DispatchTaskStepType> TransportSteps =
    [
        DispatchTaskStepType.GoToPickup,
        DispatchTaskStepType.DeliverFullBoxToDumper,
        DispatchTaskStepType.EnterDumpingPosition,
        DispatchTaskStepType.ReturnEmptyBox
    ];

    public Task<TrafficProgressPair?> ReadAsync(string vehicleId, CancellationToken ct) =>
        ReadPairAsync(vehicleId, null, ct);

    public Task<TrafficProgressPair?> ReadAfterAsync(string vehicleId, TrafficProgressFrame anchor, CancellationToken ct) =>
        ReadPairAsync(vehicleId, anchor, ct);

    private async Task<TrafficProgressPair?> ReadPairAsync(string vehicleId, TrafficProgressFrame? anchor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(vehicleId)) return null;
        var context = await ReadContextAsync(vehicleId, ct);
        if (context is null) return null;

        // Timeout and polling cadence describe real observation effort. The
        // frame timestamps and freshness checks still use the business clock.
        // Scaling the interval at 8x floods the single-process simulator without
        // producing more intermediate positions.
        var budget = TimeSpan.FromSeconds(options.FrameWaitSeconds);
        var interval = TimeSpan.FromMilliseconds(50);
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < budget)
        {
            ct.ThrowIfCancellationRequested();
            if (vehicles.TryGetConsecutiveTelemetry(vehicleId, out var previous, out var latest) &&
                CreateFrame(previous, context) is { } first && FollowsAnchor(first, anchor) &&
                CreateFrame(latest, context) is { } second && IsLaterFrame(first, second))
                return new(first, second);
            await Task.Delay(interval, ct);
        }
        return null;
    }

    private static bool IsLaterFrame(TrafficProgressFrame first, TrafficProgressFrame second) =>
        second.Sequence > first.Sequence && (second.ObservedAt - first.ObservedAt).TotalSeconds >= 0.15;

    private static bool FollowsAnchor(TrafficProgressFrame frame, TrafficProgressFrame? anchor) =>
        anchor is null || frame.ControllerEpoch == anchor.ControllerEpoch &&
        frame.Sequence >= anchor.Sequence && frame.ObservedAt >= anchor.ObservedAt &&
        frame.LastFullyTraversedEdgeIndex >= anchor.LastFullyTraversedEdgeIndex;

    private async Task<ProgressContext?> ReadContextAsync(string vehicleId, CancellationToken ct)
    {
        var steps = await database.Queryable<DispatchTaskStepEntity>()
            .Where(step => step.VehicleId == vehicleId &&
                step.Status == (int)DispatchTaskStepStatus.Executing)
            .ToListAsync(ct);
        steps = steps.Where(step => TransportSteps.Contains((DispatchTaskStepType)step.StepType)).ToList();
        if (steps.Count != 1) return null;
        var step = steps[0];
        if (string.IsNullOrWhiteSpace(step.VendorTaskId)) return null;
        var commands = await database.Queryable<AgvCommandOutboxEntity>()
            .Where(command => command.StepId == step.StepId && command.VendorCommandId == step.VendorTaskId &&
                command.CommandType == (int)AgvCommandType.Transport &&
                command.Status == (int)AgvCommandStatus.Sent)
            .ToListAsync(ct);
        var matchingCommands = commands.Where(item => item.VendorCommandId == step.VendorTaskId).ToArray();
        if (matchingCommands.Length != 1) return null;
        var command = matchingCommands[0];
        if (command.SentAt is not { } sentAt) return null;
        var route = TransportRouteSnapshots.Read(command);
        var payload = JsonSerializer.Deserialize<AgvTransportCommandPayload>(command.PayloadJson, JsonOptions);
        if (payload?.Path is not { Count: >= 2 } path || payload.Windows is null ||
            route.AgvSites.Length != route.EdgeIds.Length + 1) return null;

        var windowStart = 0;
        if (payload.Windows.Count == 0)
        {
            if (path.Count != route.AgvSites.Length) return null;
        }
        else
        {
            var windows = payload.Windows.OrderBy(item => item.Index).ToArray();
            if (payload.WindowIndex < 0 || payload.WindowIndex >= windows.Length ||
                windows.Where((item, index) => item.Index != index || item.EdgeIds is null).Any())
                return null;
            var current = windows[payload.WindowIndex];
            windowStart = windows.Take(payload.WindowIndex).Sum(item => item.EdgeIds.Count);
            if (current.EdgeIds.Count != path.Count - 1 ||
                !route.EdgeIds.Skip(windowStart).Take(current.EdgeIds.Count)
                    .SequenceEqual(current.EdgeIds, StringComparer.OrdinalIgnoreCase))
                return null;
        }
        if (!route.AgvSites.Skip(windowStart).Take(path.Count)
                .SequenceEqual(path, StringComparer.OrdinalIgnoreCase))
            return null;
        var profiles = handshake.DumperExit.BodyProfiles
            .Where(profile => profile.VehicleId.Equals(vehicleId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (profiles.Length != 1 || !profiles[0].CalibrationVerified ||
            !profiles[0].IncludesMaximumPermittedLoad ||
            !double.IsFinite(profiles[0].MaximumPositionUncertaintyMeters))
            return null;

        var segmentIds = AgvTransportSegmentIds.For(step.VendorTaskId, path.Count);
        return new(step.TaskId, step.StepId, step.VendorTaskId, command.CommandId,
            command.ExecutionRouteSnapshotHash!, route.MapId, route.MapVersionId,
            route.CoordinateFrameId, path.ToArray(), segmentIds.ToArray(), windowStart,
            profiles[0].MaximumPositionUncertaintyMeters, new DateTimeOffset(sentAt));
    }

    private static TrafficProgressFrame? CreateFrame(AgvRobotSnapshot snapshot, ProgressContext context)
    {
        if (snapshot.Connection != ConnectionState.Online || snapshot.Quality != SignalQuality.Good ||
            snapshot.LastSuccessfulPollAt is not { } observedAt || observedAt < context.SentAt || snapshot.X is not { } x ||
            snapshot.Y is not { } y || snapshot.LocalizationConfidence is not { } confidence ||
            snapshot.PathPercentage is not { } percentage || !double.IsFinite(x) ||
            !double.IsFinite(y) || !double.IsFinite(confidence) || !double.IsFinite(percentage) ||
            percentage is < 0 or > 1)
            return null;

        var indexed = snapshot.Tasks
            .Select(task => (Task: task, Index: Array.FindIndex(context.SegmentIds,
                id => id.Equals(task.TaskId, StringComparison.Ordinal))))
            .Where(row => row.Index >= 0)
            .ToArray();
        var running = indexed.Where(row => row.Task.Status == 2).ToArray();
        var candidates = running.Length == 1
            ? running
            : indexed.Where(row => row.Task.Status == 1).ToArray();
        if (candidates.Length != 1) return null;
        var currentIndex = candidates[0].Index;
        if (!string.Equals(snapshot.SourceStation, context.WindowPath[currentIndex], StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(snapshot.TargetStation, context.WindowPath[currentIndex + 1], StringComparison.OrdinalIgnoreCase))
            return null;

        // A running move task proves only that its preceding edges are complete.
        // The current edge is not declared complete from percentage alone.
        var lastFullyTraversed = context.WindowStartEdgeIndex + currentIndex - 1;
        var epoch = "ROBOKIT-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(
                $"{snapshot.VehicleId}|{context.VendorTaskId}|{context.CommandId}|{context.RouteRevision}")))[..16];
        return new(context.TaskId, context.StepId, snapshot.VehicleId, context.VendorTaskId,
            context.CommandId, context.RouteRevision, context.MapId, context.MapVersionId,
            context.CoordinateFrameId, epoch, snapshot.Version, lastFullyTraversed, x, y,
            confidence, context.PositionUncertaintyMeters, snapshot.Quality, true, true, observedAt);
    }

    private sealed record ProgressContext(string TaskId, string StepId, string VendorTaskId,
        string CommandId, string RouteRevision, string MapId, string MapVersionId,
        string CoordinateFrameId,
        string[] WindowPath, string[] SegmentIds, int WindowStartEdgeIndex,
        double PositionUncertaintyMeters, DateTimeOffset SentAt);
}
