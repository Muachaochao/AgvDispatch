using System.Security.Principal;
using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.Service;

/// <summary>
/// A read-only deployment may run without accounts, but a deployment that can
/// write PLC signals, send vehicle commands or change durable execution state
/// must have an explicit operator identity. The named-pipe ACL is the second
/// half of this gate: operators must be reachable through OperatorGroup, or be
/// the same local account that owns the service process.
/// </summary>
public static class TrafficSecurityStartupValidator
{
    public static void Validate(TrafficSecurityOptions options, bool mutatingCapabilityEnabled,
        string? processIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var authorization = new ConfiguredTrafficAuthorizationService(options);
        if (!mutatingCapabilityEnabled) return;

        var enabled = options.Users.Where(user => user.Enabled).ToArray();
        if (enabled.Length == 0)
            throw new InvalidOperationException(
                "当前配置允许PLC写入、车辆命令或执行状态变更，但Security:Users为空；" +
                "请配置至少一个可写操作员，或将全部写操作关闭后再启动。");

        var mutatingUsers = enabled
            .Select(user => authorization.Resolve(user.WindowsIdentity))
            .Where(user => (user.Permissions & ~TrafficPermission.View) != 0)
            .ToArray();
        if (mutatingUsers.Length == 0)
            throw new InvalidOperationException(
                "当前配置允许写操作，但Security:Users中没有Operator、Administrator或显式写权限账号；拒绝启动。");

        if (!string.IsNullOrWhiteSpace(options.OperatorGroup)) return;
        var owner = processIdentity ?? WindowsIdentity.GetCurrent().Name;
        if (mutatingUsers.Length == 1 &&
            SameIdentity(mutatingUsers[0].WindowsIdentity, owner))
            return;
        throw new InvalidOperationException(
            "当前配置允许写操作，但Security:OperatorGroup为空，且可写账号不是服务进程账号；" +
            "命名管道不会允许其他账号连接。请配置OperatorGroup，或仅配置本机服务账号。");
    }

    private static bool SameIdentity(string left, string right)
    {
        static string Normalize(string value) => value.Trim().TrimEnd('$');
        var a = Normalize(left);
        var b = Normalize(right);
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        var aShort = a[(a.LastIndexOf('\\') + 1)..];
        var bShort = b[(b.LastIndexOf('\\') + 1)..];
        return string.Equals(aShort, bShort, StringComparison.OrdinalIgnoreCase);
    }
}
