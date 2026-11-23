using System.Text.Json;
using System.Text.Json.Serialization;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;

namespace AgvDispatch.Service;

public sealed class ActiveMapLoader(
    ILogger<ActiveMapLoader> logger,
    IDatabaseInitializer databaseInitializer,
    ISqlSugarClient database,
    IActiveMapRuntime runtime,
    TrafficResourceCatalog resources,
    IMapSwitchService mapSwitches) : IHostedService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await databaseInitializer.InitializeAsync(cancellationToken);
        var project = await database.Queryable<MapProjectEntity>()
            .Where(item => item.IsActive)
            .FirstAsync(cancellationToken);
        if (project is null || string.IsNullOrWhiteSpace(project.ActiveVersionId))
        {
            logger.LogInformation("当前没有激活的数据库地图，路径规划继续使用安全配置或保持禁用。");
            await mapSwitches.InitializeAsync(cancellationToken);
            return;
        }
        var version = await database.Queryable<MapVersionEntity>()
            .Where(item => item.VersionId == project.ActiveVersionId)
            .FirstAsync(cancellationToken);
        if (version is null)
        {
            logger.LogError("活动地图{MapId}的运行版本不存在，未加载运行地图。", project.MapId);
            return;
        }
        var definition = JsonSerializer.Deserialize<MapDefinition>(version.DefinitionJson, JsonOptions) ??
                         throw new InvalidDataException("活动地图定义无法反序列化。");
        runtime.Replace(new ActiveMapRuntimeSnapshot(
            project.MapId,
            version.VersionId,
            definition,
            AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow()));
        var runtimeResources = MapCorridorResources.AllResources(definition);
        var automaticGuards = runtimeResources.Count(resource => MapCorridorResources.IsAutomatic(resource.Id));
        logger.LogInformation(
            "地图路权守卫审计。MapId={MapId}, AutomaticCorridorAndJunctionGuards={Guards}, ExplicitResources={Explicit}",
            project.MapId, automaticGuards, runtimeResources.Count - automaticGuards);
        // R18: a CAD-sized map can silently add over two thousand implicit
        // capacity-one guards, serialising every main road. Keep compatibility,
        // but make the scale visible and require the operator to review explicit
        // DUAL-LANE/LANE resources before treating throughput as commissioned.
        if (automaticGuards > 2000)
            logger.LogWarning(
                "地图自动路权守卫达到{Guards}个，超过2000个审计阈值。主干道可能被隐式串行化；请显式声明双车道/单行车道资源并复测吞吐。",
                automaticGuards);
        resources.Replace(runtimeResources);
        await database.Deleteable<TrafficResourceDefinitionEntity>().ExecuteCommandAsync(cancellationToken);
        if (runtimeResources.Count > 0)
        {
            await database.Insertable(runtimeResources.Select(resource =>
                new TrafficResourceDefinitionEntity
                {
                    ResourceId = resource.Id,
                    ResourceType = (int)resource.Type,
                    Capacity = resource.Capacity,
                    UpdatedAt = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().LocalDateTime
                }).ToArray()).ExecuteCommandAsync(cancellationToken);
        }
        await mapSwitches.InitializeAsync(cancellationToken);
        logger.LogInformation(
            "已加载活动地图。MapId={MapId}, Version={Version}, Nodes={Nodes}, Edges={Edges}",
            project.MapId, version.VersionNumber, definition.Nodes.Count, definition.Edges.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
