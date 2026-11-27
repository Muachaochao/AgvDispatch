using AgvDispatch.Application.Abstractions;
using AgvDispatch.Service;

// E1: pipe callers receive the role configured for their account. Accounts without a configured role see the
// snapshot only, and every mutating permission is denied. No named pipe is opened here.
var checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }

TrafficSecurityOptions Options(params TrafficSecurityUserOptions[] users) => new() { Users = users.ToList() };
var service = new ConfiguredTrafficAuthorizationService(Options(
    new TrafficSecurityUserOptions { WindowsIdentity = @"PLANT\op1", DisplayName = "operator", Role = "Operator" },
    new TrafficSecurityUserOptions { WindowsIdentity = @"PLANT\mon", DisplayName = "monitor", Role = "Monitor" },
    new TrafficSecurityUserOptions { WindowsIdentity = @"PLANT\adm", DisplayName = "administrator", Role = "Administrator" },
    new TrafficSecurityUserOptions { WindowsIdentity = @"PLANT\disabled", Role = "Administrator", Enabled = false }));

var operatorUser = service.Resolve(@"PLANT\op1");
Check(operatorUser.Role == TrafficRole.Operator && operatorUser.Has(TrafficPermission.View) &&
      operatorUser.Has(TrafficPermission.AcknowledgeAlarm) && operatorUser.Has(TrafficPermission.SubmitManualRelease),
    "a configured operator receives the operator permissions");
Check(!operatorUser.Has(TrafficPermission.RecoverExecution) && !operatorUser.Has(TrafficPermission.ManageMap) &&
      !operatorUser.Has(TrafficPermission.ManageConnections) && !operatorUser.Has(TrafficPermission.Administer),
    "a configured operator cannot recover execution, manage maps or connections, or administer");
Check(service.Resolve("op1").Role == TrafficRole.Operator, "a short name resolves only when it identifies exactly one configured account");
Check(service.Resolve(@"PLANT\mon").Has(TrafficPermission.View) && !service.Resolve(@"PLANT\mon").Has(TrafficPermission.AcknowledgeAlarm),
    "a monitor reads the snapshot but cannot acknowledge alarms");
Check(service.Resolve(@"PLANT\adm").Has(TrafficPermission.ManageMap) && service.Resolve(@"PLANT\adm").Has(TrafficPermission.RecoverExecution),
    "an administrator keeps every permission through Administer");

var unlisted = service.Resolve(@"PLANT\stranger");
Check(unlisted.Role == TrafficRole.Unassigned && unlisted.Permissions == TrafficPermission.View,
    "an unlisted account is view-only, never administrator");
Check(service.Resolve(@"OTHER\op1").Permissions == TrafficPermission.View,
    "a different domain with the same short name is not matched to the configured operator");
Check(service.Resolve(@"PLANT\disabled").Permissions == TrafficPermission.View, "a disabled account gets no configured role");
Check(service.Resolve("").Permissions == TrafficPermission.View, "an empty identity is view-only");

var unconfigured = new ConfiguredTrafficAuthorizationService(Options());
var anyone = unconfigured.Resolve("SOMEBODY");
Check(anyone.Permissions == TrafficPermission.View && !anyone.Has(TrafficPermission.ManageConnections) &&
      !anyone.Has(TrafficPermission.RecoverExecution), "with no Security:Users, every caller is view-only");

var custom = new ConfiguredTrafficAuthorizationService(Options(
    new TrafficSecurityUserOptions { WindowsIdentity = @"PLANT\ack", Role = "", Permissions = ["AcknowledgeAlarm"] }));
Check(custom.Resolve(@"PLANT\ack").Permissions == (TrafficPermission.None | TrafficPermission.AcknowledgeAlarm),
    "an explicit permission list without a role grants only the listed permission");

void Rejects(TrafficSecurityOptions options, string label)
{
    try { _ = new ConfiguredTrafficAuthorizationService(options); }
    catch (InvalidOperationException) { Check(true, label); return; }
    throw new Exception(label + " was accepted");
}
Rejects(Options(new TrafficSecurityUserOptions { WindowsIdentity = @"PLANT\x", Role = "Root" }), "an unknown role is rejected at startup");
Rejects(Options(new TrafficSecurityUserOptions { WindowsIdentity = @"PLANT\x", Role = "Monitor", Permissions = ["ManageMap"] }),
    "an extra permission beyond a fixed role is rejected at startup");
Rejects(Options(new TrafficSecurityUserOptions { WindowsIdentity = @"PLANT\x", Role = "Operator" }, new TrafficSecurityUserOptions { WindowsIdentity = @"plant\X", Role = "Monitor" }),
    "a duplicate account is rejected at startup");

TrafficSecurityStartupValidator.Validate(Options(), false, @"PLANT\svc");
Check(true, "read-only deployment may start without operator accounts");

void RejectsStartup(TrafficSecurityOptions options, bool mutating, string processIdentity, string label)
{
    try { TrafficSecurityStartupValidator.Validate(options, mutating, processIdentity); }
    catch (InvalidOperationException) { Check(true, label); return; }
    throw new Exception(label + " was accepted");
}

RejectsStartup(Options(), true, @"PLANT\svc",
    "a write-capable deployment with no configured users is rejected");
RejectsStartup(Options(new TrafficSecurityUserOptions { WindowsIdentity = @"PLANT\mon", Role = "Monitor" }),
    true, @"PLANT\svc", "a monitor-only user cannot unlock write-capable deployment");
RejectsStartup(Options(new TrafficSecurityUserOptions { WindowsIdentity = @"PLANT\op", Role = "Operator" }),
    true, @"PLANT\svc", "a write user outside the pipe owner and without OperatorGroup is rejected");
Check(IsStartupAccepted(new TrafficSecurityOptions
{
    Users = [new TrafficSecurityUserOptions { WindowsIdentity = @"PLANT\svc", Role = "Administrator" }]
}, true, @"PLANT\svc"), "the local service account can be the sole write operator");
Check(IsStartupAccepted(new TrafficSecurityOptions
{
    OperatorGroup = "AGV Operators",
    Users = [new TrafficSecurityUserOptions { WindowsIdentity = @"PLANT\op", Role = "Operator" }]
}, true, @"PLANT\svc"), "a configured operator group admits a remote write operator");

bool IsStartupAccepted(TrafficSecurityOptions options, bool mutating, string processIdentity)
{
    try { TrafficSecurityStartupValidator.Validate(options, mutating, processIdentity); return true; }
    catch (InvalidOperationException) { return false; }
}

Console.WriteLine($"PASS: {checks} role resolution checks; no pipe or Windows identity required.");
