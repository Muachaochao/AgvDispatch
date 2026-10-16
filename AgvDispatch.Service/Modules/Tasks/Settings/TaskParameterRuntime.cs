using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Service;

public sealed record TaskParameterDocument(int Version, string Revision, string RequestId, string MapId, string MapVersionId,
    TaskParameterConfiguration Configuration, IReadOnlyList<TaskParameterAudit> Audits);
public sealed record TaskParameterStorageOptions(string Path);

// All runtime reads and updates use the shared execution gate; workers keep their existing option references.
public sealed class TaskParameterRuntime(DispatchOrchestratorOptions dispatch, DispatchTaskCreationOptions creation)
{
    private IReadOnlyList<TaskStepTimeoutParameter> _stepTimeouts = Enum.GetValues<DispatchTaskStepType>()
        .Select(type => new TaskStepTimeoutParameter(type, 0)).ToArray();
    public IReadOnlyList<TaskStepTimeoutParameter> StepTimeouts => _stepTimeouts;
    public string Revision { get; private set; } = "initial";
    public TaskParameterConfiguration Capture() => new(creation.DumperStationId, dispatch.GetDumperWaitingSite() ?? "", dispatch.DumperDumpingSite,
        dispatch.StationPickupSites.OrderBy(pair => pair.Key).Select(pair => new TaskPickupParameter(pair.Key, pair.Value)).ToArray(),
        TransportSteps.Select(type => dispatch.GetAction(type) is { } action
            ? new TaskActionParameter(type, action.Operation, action.JackHeight, action.Recognize, action.UsePgv, action.UseDownPgv)
            : new TaskActionParameter(type, "", null, null, null, null)).ToArray()) { StepTimeouts = _stepTimeouts.ToArray() };
    public static readonly DispatchTaskStepType[] TransportSteps = [DispatchTaskStepType.GoToPickup, DispatchTaskStepType.DeliverFullBoxToDumper,
        DispatchTaskStepType.EnterDumpingPosition, DispatchTaskStepType.ReturnEmptyBox];

    public static void CheckStructure(TaskParameterConfiguration configuration)
    {
        if (configuration is null || configuration.Pickups is null || configuration.Actions is null ||
            configuration.Pickups.Count is < 1 or > 100 || configuration.Actions.Count != 4)
            throw new ArgumentException("需配置 1–100 个取货工位和四个运输动作。");
        if (configuration.StepTimeouts is null || configuration.StepTimeouts.Count != 9 ||
            configuration.StepTimeouts.Any(row => row is null || row.Seconds is < 0 or > 604800) ||
            !configuration.StepTimeouts.Select(row => row.StepType).Order().SequenceEqual(Enum.GetValues<DispatchTaskStepType>().Order()))
            throw new ArgumentException("九个步骤需各配置一次超时阈值：0–604800 整数秒，0 表示关闭。");
        void Id(string? id, int length, string name)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > length || id != id.Trim() || id.Any(char.IsControl))
                throw new ArgumentException($"{name}不能为空、带前后空格或控制字符，最长 {length} 字符。");
        }
        Id(configuration.DumperStationId, 32, "倒料 PLC 工位"); Id(configuration.WaitingSite, 128, "倒料等待站点"); Id(configuration.DumpingSite, 128, "倒料站点");
        foreach (var pickup in configuration.Pickups)
        {
            if (pickup is null) throw new ArgumentException("取货工位项不能为空。");
            Id(pickup.StationId, 32, "取货 PLC 工位"); Id(pickup.PickupSite, 128, "取货 AGV 站点");
        }
        if (configuration.Pickups.Select(pickup => pickup.StationId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != configuration.Pickups.Count)
            throw new ArgumentException("取货 PLC 工位编号不能重复。");
        if (configuration.Actions.Any(action => action is null) ||
            !configuration.Actions.Select(action => action.StepType).Order().SequenceEqual(TransportSteps.Order()))
            throw new ArgumentException("四个运输步骤必须各配置一次。");
        foreach (var action in configuration.Actions)
        {
            if (!new[] { "Wait", "JackLoad", "JackUnload", "JackHeight" }.Contains(action.Operation, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException($"步骤 {action.StepType} 的动作无效。");
            if (action.JackHeight is { } height && (!double.IsFinite(height) || height < 0) ||
                action.Operation.Equals("JackHeight", StringComparison.OrdinalIgnoreCase) && action.JackHeight is null)
                throw new ArgumentException($"步骤 {action.StepType} 的 JackHeight 必须为有限、非负数值。");
        }
    }
    public static void ApplyConfiguration(TaskParameterConfiguration configuration, DispatchOrchestratorOptions target, DispatchTaskCreationOptions creationTarget)
    {
        // Construct both dictionaries before assigning any runtime fields.
        var pickups = configuration.Pickups.ToDictionary(row => row.StationId, row => row.PickupSite, StringComparer.OrdinalIgnoreCase);
        var actions = configuration.Actions.ToDictionary(row => row.StepType.ToString(), row => new AgvStepActionOptions
        { Operation = row.Operation, JackHeight = row.JackHeight, Recognize = row.Recognize, UsePgv = row.UsePgv, UseDownPgv = row.UseDownPgv }, StringComparer.OrdinalIgnoreCase);
        target.StationPickupSites = pickups; target.StepActions = actions;
        target.DumperWaitingSite = configuration.WaitingSite; target.DumperDumpingSite = configuration.DumpingSite;
        creationTarget.DumperStationId = configuration.DumperStationId;
    }
    public void Apply(TaskParameterDocument document)
    {
        CheckStructure(document.Configuration);
        ApplyConfiguration(document.Configuration, dispatch, creation);
        _stepTimeouts = document.Configuration.StepTimeouts.ToArray();
        Revision = document.Revision;
    }
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
}
