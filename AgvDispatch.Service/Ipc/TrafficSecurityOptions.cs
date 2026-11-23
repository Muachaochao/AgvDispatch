using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.Service;

public sealed class TrafficSecurityOptions
{
    public const string SectionName = "Security";
    public string PipeName { get; set; } = "AgvDispatch.Service";
    public int ApprovalTimeoutMinutes { get; set; } = 15;
    public List<TrafficSecurityUserOptions> Users { get; set; } = [];
    // Windows group name or SID whose members may connect to the pipe as operators. Their
    // roles come from Users; an account not listed there gets view-only access.
    public string OperatorGroup { get; set; } = string.Empty;
}

public sealed class TrafficSecurityUserOptions
{
    public bool Enabled { get; set; } = true;
    public string WindowsIdentity { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public List<string> Permissions { get; set; } = [];
    public string Role { get; set; } = string.Empty;
}

public sealed class ConfiguredTrafficAuthorizationService : ITrafficAuthorizationService
{
    private readonly IReadOnlyDictionary<string, TrafficUserContext> _users;

    public ConfiguredTrafficAuthorizationService(TrafficSecurityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var users = new Dictionary<string, TrafficUserContext>(StringComparer.OrdinalIgnoreCase);
        foreach (var configured in options.Users.Where(item => item.Enabled))
        {
            var identity = configured.WindowsIdentity.Trim();
            if (identity.Length == 0)
            {
                throw new InvalidOperationException("Security:Users中的WindowsIdentity不能为空。");
            }
            var permissions = TrafficPermission.None;
            var role = TrafficRole.Custom;
            if (!string.IsNullOrWhiteSpace(configured.Role))
            {
                if (!Enum.TryParse(configured.Role, true, out role) ||
                    role is not (TrafficRole.Administrator or TrafficRole.Operator or TrafficRole.Monitor))
                    throw new InvalidOperationException($"账号{identity}角色无效，必须为Administrator、Operator或Monitor。");
                permissions = TrafficRoles.PermissionsFor(role);
            }
            foreach (var value in configured.Permissions)
            {
                if (!Enum.TryParse<TrafficPermission>(value, true, out var permission) ||
                    permission == TrafficPermission.None)
                {
                    throw new InvalidOperationException(
                        $"账号{identity}配置了未知权限：{value}。");
                }
                if (role != TrafficRole.Custom && (permissions & permission) != permission)
                    throw new InvalidOperationException($"账号{identity}的额外权限{permission}超出{role}角色，拒绝启动。");
                permissions |= permission;
            }
            if (!users.TryAdd(identity, new TrafficUserContext(
                    identity,
                    string.IsNullOrWhiteSpace(configured.DisplayName)
                        ? identity
                        : configured.DisplayName.Trim(),
                    permissions) { Role = role }))
            {
                throw new InvalidOperationException($"Windows账号{identity}重复配置。");
            }
        }
        _users = users;
    }

    public TrafficUserContext Resolve(string windowsIdentity)
    {
        var identity = windowsIdentity?.Trim() ?? string.Empty;
        if (_users.TryGetValue(identity, out var user))
        {
            return user;
        }

        // Never match a different domain's qualified identity by a shared short name.
        if (identity.Contains('\\') || identity.Contains('@'))
            return Unconfigured(identity);
        // GetImpersonationUserName may return either DOMAIN\user or only user,
        // depending on whether the account is local or domain-backed. A short-name
        // fallback is accepted only when it identifies exactly one configured user.
        var shortName = GetShortName(identity);
        var matches = _users.Values
            .Where(candidate => string.Equals(
                GetShortName(candidate.WindowsIdentity), shortName,
                StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : Unconfigured(identity);
    }

    // An operator without a configured role may read the snapshot; every mutating operation is denied.
    private static TrafficUserContext Unconfigured(string identity) =>
        new(identity, identity, TrafficPermission.View) { Role = TrafficRole.Unassigned };

    private static string GetShortName(string identity)
    {
        var separator = identity.LastIndexOf('\\');
        return separator >= 0 ? identity[(separator + 1)..] : identity;
    }
}
