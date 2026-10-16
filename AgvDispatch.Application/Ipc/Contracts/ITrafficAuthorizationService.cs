namespace AgvDispatch.Application.Abstractions;

[Flags]
public enum TrafficPermission
{
    None = 0,
    View = 1,
    SubmitManualRelease = 2,
    ApproveManualRelease = 4,
    Administer = 8,
    AcknowledgeAlarm = 16,
    ManageMap = 32,
    ManageConnections = 64,
    RecoverExecution = 128
}

public sealed record TrafficUserContext(
    string WindowsIdentity,
    string DisplayName,
    TrafficPermission Permissions)
{
    // Technical compatibility context for local operation mode, not a user role or login session.
    public static TrafficUserContext LocalOperation(string windowsIdentity) =>
        new(windowsIdentity, windowsIdentity, TrafficPermission.View | TrafficPermission.Administer);
    public TrafficRole Role { get; init; } = TrafficRole.Custom;
    public bool Has(TrafficPermission permission) =>
        (Permissions & TrafficPermission.Administer) != 0 ||
        (Permissions & permission) == permission;
}

public enum TrafficRole { Unassigned = 0, Monitor = 10, Operator = 20, Administrator = 30, Custom = 40 }

public static class TrafficRoles
{
    public static TrafficPermission PermissionsFor(TrafficRole role) => role switch
    {
        TrafficRole.Administrator => TrafficPermission.View | TrafficPermission.Administer,
        TrafficRole.Operator => TrafficPermission.View | TrafficPermission.SubmitManualRelease | TrafficPermission.AcknowledgeAlarm,
        TrafficRole.Monitor => TrafficPermission.View,
        _ => TrafficPermission.None
    };
    public static string DisplayName(TrafficRole role) => role switch
    { TrafficRole.Administrator => "管理员", TrafficRole.Operator => "操作员", TrafficRole.Monitor => "监控员", TrafficRole.Custom => "自定义授权", _ => "未授权" };
}

public interface ITrafficAuthorizationService
{
    TrafficUserContext Resolve(string windowsIdentity);
}
