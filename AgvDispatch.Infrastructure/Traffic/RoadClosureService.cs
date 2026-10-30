using System.Security.Cryptography;
using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence;

/// <summary>Durable, explicit closures of the site pairs addressable by Robokit 3066.</summary>
public sealed class RoadClosureService(ISqlSugarClient database, IActiveMapRuntime runtime,
    IMapExecutionGate executionGate, RoadClosureRoutingOptions? options = null, TimeProvider? clock = null) : IRoadClosureService
{
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private State _state = new(false, 0, []);
    private sealed record State(bool Ready, long Revision, IReadOnlyList<RoadClosureRecord> Closures);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private DateTime UtcNow => (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try { await ReloadAsync(cancellationToken); }
        finally { _mutex.Release(); }
    }

    public RoadClosureSnapshot GetSnapshot()
    {
        var state = Volatile.Read(ref _state);
        var map = runtime.Current;
        return Snapshot(state, map);
    }

    public async Task<RoadClosureSnapshot> ListAsync(CancellationToken cancellationToken)
    {
        if (!Volatile.Read(ref _state).Ready) await InitializeAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return GetSnapshot();
    }

    public async Task<RoadClosureResult> CreateAsync(TrafficUserContext user, RoadClosureCreateRequest request, CancellationToken cancellationToken)
    {
        if (!user.Has(TrafficPermission.Administer)) return Result(false, "道路封锁需要管理员权限。");
        using var mapLease = await executionGate.EnterControlAsync(cancellationToken);
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            await EnsureReadyAsync(cancellationToken);
            if (!Text(request.RequestId, 128) || !Text(request.Reason, 2000) || !Text(request.Source, 128))
                return Result(false, "请求号、来源和封锁原因不能为空或过长。");
            var intent = Hash(new { request, Operator = user.WindowsIdentity });
            var existing = await database.Queryable<RoadClosureEntity>().FirstAsync(x => x.RequestId == request.RequestId, cancellationToken);
            if (existing is not null) return existing.IntentHash == intent
                ? new(true, "该封锁请求已处理。", _state.Revision, Convert(existing)) : Result(false, "请求号已用于不同封锁内容。");
            if (await database.Queryable<RoadClosureEventEntity>().AnyAsync(x => x.RequestId == request.RequestId, cancellationToken))
                return Result(false, "请求号已用于其他道路操作。");
            var map = runtime.Current;
            if (!Matches(map, request.ExpectedMapId, request.ExpectedVersionId, request.ExpectedDefinitionHash))
                return Result(false, "运行地图、版本或定义指纹已变化，请重新选择道路。");
            var edge = map!.Definition.Edges.FirstOrDefault(e => Equal(e.Id, request.EdgeId));
            if (edge is null || !edge.Enabled || !HasDirection(edge, request.FromNodeId, request.ToNodeId) ||
                !map.Definition.Nodes.Any(n => n.Enabled && Equal(n.Id, request.FromNodeId)) ||
                !map.Definition.Nodes.Any(n => n.Enabled && Equal(n.Id, request.ToNodeId)))
                return Result(false, "所选边和封锁方向不属于当前启用道路，单向道路不能选择逆向。");
            var traversals = ExpandSitePair(map.Definition, request.FromNodeId, request.ToNodeId, request.Bidirectional);
            if (traversals.Count == 0) return Result(false, "当前地图没有可封锁的行驶方向。");
            var revision = _state.Revision + 1;
            var row = new RoadClosureEntity
            {
                ClosureId = Guid.NewGuid().ToString("N"), RequestId = request.RequestId, IntentHash = intent,
                MapId = map.MapId, VersionId = map.VersionId, DefinitionHash = EngineeringRuntimeIdentity.MapHash(map.Definition),
                EdgeId = edge.Id, FromNodeId = request.FromNodeId, ToNodeId = request.ToNodeId,
                Bidirectional = request.Bidirectional, TraversalsJson = JsonSerializer.Serialize(traversals, Json),
                Source = request.Source.Trim(), Reason = request.Reason.Trim(), CreatedBy = user.WindowsIdentity,
                CreatedAtUtc = UtcNow, CreatedRevision = revision, Active = true
            };
            await database.Ado.BeginTranAsync();
            try
            {
                await database.Insertable(row).ExecuteCommandAsync(cancellationToken);
                await database.Insertable(new RoadClosureEventEntity { EventId = Guid.NewGuid().ToString("N"),
                    ClosureId = row.ClosureId, RequestId = request.RequestId, IntentHash = intent, Kind = "Created",
                    Operator = user.WindowsIdentity, Reason = row.Reason, OccurredAtUtc = row.CreatedAtUtc, Revision = revision })
                    .ExecuteCommandAsync(cancellationToken);
                await database.Ado.CommitTranAsync();
            }
            catch { await database.Ado.RollbackTranAsync(); throw; }
            Publish(revision, _state.Closures.Append(Convert(row)).ToArray());
            return new(true, "已封锁所选站点对的全部平行路径" + (request.Bidirectional ? "及反向通道。" : "（仅所选行驶方向）。"), revision, Convert(row));
        }
        finally { _mutex.Release(); }
    }

    public async Task<RoadClosureResult> ReleaseAsync(TrafficUserContext user, RoadClosureReleaseRequest request, CancellationToken cancellationToken)
    {
        if (!user.Has(TrafficPermission.Administer)) return Result(false, "解除道路封锁需要管理员权限。");
        using var mapLease = await executionGate.EnterControlAsync(cancellationToken);
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            await EnsureReadyAsync(cancellationToken);
            if (!Text(request.RequestId, 128) || !Text(request.Reason, 2000) || !request.SiteClearedConfirmed)
                return Result(false, "必须确认现场障碍已清理，并填写请求号和解除原因。");
            var intent = Hash(new { request, Operator = user.WindowsIdentity });
            var existing = await database.Queryable<RoadClosureEventEntity>().FirstAsync(x => x.RequestId == request.RequestId, cancellationToken);
            if (existing is not null) return existing.Kind == "Released" && existing.IntentHash == intent
                ? new(true, "该解除请求已处理。", _state.Revision, _state.Closures.FirstOrDefault(c => c.ClosureId == existing.ClosureId))
                : Result(false, "请求号已用于不同道路操作。");
            if (request.ExpectedRevision != _state.Revision) return Result(false, "道路封锁版本已变化，请刷新后重新确认。");
            var row = await database.Queryable<RoadClosureEntity>().FirstAsync(x => x.ClosureId == request.ClosureId, cancellationToken);
            if (row is null || !row.Active) return Result(false, "封锁不存在或已经解除。");
            // Historical map closures can be explicitly released, but never silently expire
            // on map switch, restart or an elapsed waiting timeout.
            var revision = _state.Revision + 1;
            row.Active = false; row.ReleasedBy = user.WindowsIdentity; row.ReleasedAtUtc = UtcNow;
            row.ReleaseReason = request.Reason.Trim(); row.ReleasedRevision = revision;
            await database.Ado.BeginTranAsync();
            try
            {
                await database.Updateable(row).ExecuteCommandAsync(cancellationToken);
                await database.Insertable(new RoadClosureEventEntity { EventId = Guid.NewGuid().ToString("N"),
                    ClosureId = row.ClosureId, RequestId = request.RequestId, IntentHash = intent, Kind = "Released",
                    Operator = user.WindowsIdentity, Reason = row.ReleaseReason, OccurredAtUtc = row.ReleasedAtUtc.Value, Revision = revision })
                    .ExecuteCommandAsync(cancellationToken);
                await database.Ado.CommitTranAsync();
            }
            catch { await database.Ado.RollbackTranAsync(); throw; }
            Publish(revision, _state.Closures.Select(c => c.ClosureId == row.ClosureId ? Convert(row) : c).ToArray());
            return new(true, "道路封锁已解除；已有其他封锁仍保持有效。", revision, Convert(row));
        }
        finally { _mutex.Release(); }
    }

    public async Task<RoadClosurePreview> PreviewAsync(RoadClosurePreviewRequest request, CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            await EnsureReadyAsync(cancellationToken);
            var map = runtime.Current; var state = Snapshot(_state, map);
            RoadClosurePreview Rejected(string message) => new(false, message, state.Revision, state.MapId, state.VersionId,
                state.DefinitionHash, request.SourceSite, request.TargetSite, null, state.BlockedTraversals);
            if (request.ExpectedRevision != state.Revision || !Matches(map, request.ExpectedMapId, request.ExpectedVersionId, request.ExpectedDefinitionHash))
                return Rejected("地图或道路封锁版本已变化，请刷新后重新预览。");
            if (state.IdentityConflict) return Rejected("同一地图版本的定义与持久封锁不一致，必须核对后处理。");
            if (string.IsNullOrWhiteSpace(request.SourceSite) || string.IsNullOrWhiteSpace(request.TargetSite)) return Rejected("请选择实际起终站点。");
            var definition = map!.Definition;
            var source = MapRouteSiteMapper.ResolveLogicalNodeId(definition, request.SourceSite);
            var target = MapRouteSiteMapper.ResolveLogicalNodeId(definition, request.TargetSite);
            var protectedNodes = (options?.ProtectedTransitSites ?? []).Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => MapRouteSiteMapper.ResolveLogicalNodeId(definition, s)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var route = map.Graph.FindShortestTransportRoute(source, target, forbiddenIntermediateNodes: protectedNodes,
                unavailableTraversals: state.BlockedTraversals);
            return route is null ? Rejected("当前封锁及单向道路约束下没有可用路线，请等待清障或人工处理。") :
                new(true, "候选路线仅供预览；实际发送仍需复核车辆、任务、路权和道路封锁版本。", state.Revision,
                    state.MapId, state.VersionId, state.DefinitionHash, request.SourceSite, request.TargetSite,
                    MapRouteSiteMapper.ToAgvSitePlan(definition, route), state.BlockedTraversals);
        }
        finally { _mutex.Release(); }
    }

    public async Task<IDisposable> EnterRouteSendAsync(IReadOnlyList<string> agvSitePath, CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            if (!_state.Ready) throw new InvalidOperationException("道路封锁尚未恢复，禁止发送导航。");
            var map = runtime.Current; var snapshot = Snapshot(_state, map);
            if (snapshot.IdentityConflict) throw new InvalidOperationException("地图定义与持久封锁不一致，禁止发送导航。");
            if (map is null)
            {
                if (_state.Closures.Any(c => c.Active)) throw new InvalidOperationException("当前无运行地图，不能绕过持久道路封锁发送旧路径。");
                return new Lease(_mutex);
            }
            var blocked = snapshot.BlockedTraversals;
            if (blocked.Count > 0)
            {
                if (!Matches(map, snapshot.MapId!, snapshot.VersionId!, snapshot.DefinitionHash!))
                    throw new InvalidOperationException("运行地图在道路复核期间变化，禁止发送导航。");
                if (agvSitePath.Count < 2) throw new InvalidOperationException("存在道路封锁，必须提供含实际起点的完整逐段站点链。");
                var definition = map.Definition;
                var nodes = agvSitePath.Select(site =>
                {
                    var matches = definition.Nodes.Where(n => n.Enabled && (Equal(n.Id, site) || Equal(n.AgvSiteId, site))).ToArray();
                    if (matches.Length != 1) throw new InvalidOperationException($"站点{site}未唯一匹配当前启用节点，禁止绕过道路封锁。");
                    return matches[0].Id;
                }).ToArray();
                for (var i = 0; i + 1 < nodes.Length; i++)
                {
                    if (!definition.Edges.Any(e => e.Enabled && HasDirection(e, nodes[i], nodes[i + 1])))
                        throw new InvalidOperationException($"站点链{agvSitePath[i]} → {agvSitePath[i + 1]}不属于当前启用道路。");
                    // 3066 carries station pairs, not a selectable edge/path ID. A
                    // parallel edge must not make a sealed station pair sendable.
                    var fromSite = Site(definition, nodes[i]); var toSite = Site(definition, nodes[i + 1]);
                    if (blocked.Any(t => Equal(Site(definition, t.FromNodeId), fromSite) && Equal(Site(definition, t.ToNodeId), toSite)))
                        throw new InvalidOperationException($"道路已封锁：{agvSitePath[i]} → {agvSitePath[i + 1]}，禁止发送旧路径。");
                }
            }
            return new Lease(_mutex);
        }
        catch { _mutex.Release(); throw; }
    }

    private async Task EnsureReadyAsync(CancellationToken ct) { if (!_state.Ready) await ReloadAsync(ct); }
    private async Task ReloadAsync(CancellationToken ct)
    {
        Volatile.Write(ref _state, _state with { Ready = false });
        var rows = await database.Queryable<RoadClosureEntity>().OrderBy(c => c.CreatedRevision).ToListAsync(ct);
        var events = await database.Queryable<RoadClosureEventEntity>().OrderBy(e => e.Revision).ToListAsync(ct);
        var records = rows.Select(Convert).ToArray();
        foreach (var row in rows)
        {
            var created = events.Where(e => e.ClosureId == row.ClosureId && e.Kind == "Created").ToArray();
            var released = events.Where(e => e.ClosureId == row.ClosureId && e.Kind == "Released").ToArray();
            var record = records.Single(c => c.ClosureId == row.ClosureId);
            if (created.Length != 1 || created[0].Revision != row.CreatedRevision ||
                created[0].RequestId != row.RequestId || created[0].IntentHash != row.IntentHash ||
                created[0].Operator != row.CreatedBy || created[0].Reason != row.Reason ||
                record.Traversals.Count == 0 || record.Traversals.Any(t => !Text(t.EdgeId, 512) || !Text(t.FromNodeId, 512) || !Text(t.ToNodeId, 512)) ||
                row.Active && (released.Length != 0 || row.ReleasedRevision is not null || row.ReleasedAtUtc is not null || row.ReleasedBy is not null || row.ReleaseReason is not null) ||
                !row.Active && (released.Length != 1 || released[0].Revision != row.ReleasedRevision ||
                    released[0].Operator != row.ReleasedBy || released[0].Reason != row.ReleaseReason || row.ReleasedAtUtc is null || row.ReleasedRevision <= row.CreatedRevision))
                throw new InvalidDataException("持久道路封锁与创建/解除审计不一致，禁止恢复导航。");
        }
        if (events.Any(e => !rows.Any(r => r.ClosureId == e.ClosureId) || e.Kind is not ("Created" or "Released")) ||
            events.Select((e, index) => e.Revision == index + 1L).Any(valid => !valid))
            throw new InvalidDataException("道路封锁审计存在缺失或未知操作，禁止恢复导航。");
        Publish(events.LastOrDefault()?.Revision ?? 0, records);
    }
    private void Publish(long revision, IReadOnlyList<RoadClosureRecord> rows) =>
        Volatile.Write(ref _state, new(true, revision, Array.AsReadOnly(rows.ToArray())));
    private static RoadClosureSnapshot Snapshot(State state, ActiveMapRuntimeSnapshot? map) =>
        new(state.Ready, state.Revision, map?.MapId, map?.VersionId,
            map is null ? null : EngineeringRuntimeIdentity.MapHash(map.Definition), state.Closures);
    private RoadClosureResult Result(bool accepted, string message) => new(accepted, message, _state.Revision, null);
    private static bool Matches(ActiveMapRuntimeSnapshot? map, string mapId, string version, string hash) => map is not null &&
        map.MapId == mapId && map.VersionId == version && Equal(EngineeringRuntimeIdentity.MapHash(map.Definition), hash);
    private static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;
    private static bool Equal(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static bool HasDirection(MapEdge edge, string from, string to) => Equal(edge.From, from) && Equal(edge.To, to) ||
        edge.Bidirectional && Equal(edge.To, from) && Equal(edge.From, to);
    private static string Site(MapDefinition map, string id) => map.Nodes.FirstOrDefault(n => Equal(n.Id, id)) is { } node ? node.AgvSiteId ?? node.Id : id;
    private static IReadOnlyList<MapDirectedTraversal> ExpandSitePair(MapDefinition map, string from, string to, bool both)
    {
        var fromSite = Site(map, from); var toSite = Site(map, to);
        bool MatchesPair(string a, string b) => Equal(Site(map, a), fromSite) && Equal(Site(map, b), toSite) ||
            both && Equal(Site(map, a), toSite) && Equal(Site(map, b), fromSite);
        var traversals = new List<MapDirectedTraversal>();
        foreach (var edge in map.Edges)
        {
            if (MatchesPair(edge.From, edge.To)) traversals.Add(new(edge.Id, edge.From, edge.To));
            if (edge.Bidirectional && MatchesPair(edge.To, edge.From)) traversals.Add(new(edge.Id, edge.To, edge.From));
        }
        return Array.AsReadOnly(traversals.ToArray());
    }
    private static string Hash(object value) => System.Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Json)));
    private static DateTimeOffset Utc(DateTime time) => new(DateTime.SpecifyKind(time, DateTimeKind.Utc));
    private static RoadClosureRecord Convert(RoadClosureEntity row) => new(row.ClosureId, row.RequestId, row.MapId,
        row.VersionId, row.DefinitionHash, row.EdgeId, row.FromNodeId, row.ToNodeId, row.Bidirectional,
        Array.AsReadOnly((JsonSerializer.Deserialize<MapDirectedTraversal[]>(row.TraversalsJson, Json) ?? []).ToArray()),
        row.Source, row.Reason, row.CreatedBy, Utc(row.CreatedAtUtc), row.CreatedRevision, row.Active, row.ReleasedBy,
        row.ReleasedAtUtc.HasValue ? Utc(row.ReleasedAtUtc.Value) : null, row.ReleaseReason, row.ReleasedRevision);
    private sealed class Lease(SemaphoreSlim mutex) : IDisposable
    {
        private SemaphoreSlim? _mutex = mutex;
        public void Dispose() => Interlocked.Exchange(ref _mutex, null)?.Release();
    }
}
