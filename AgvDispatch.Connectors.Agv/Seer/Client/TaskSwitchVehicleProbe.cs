using System.Text.Json;
using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.Connectors.Agv;

// Uses only the configured vehicle's status channel. No navigation, cancellation,
// lowering or lifting command is issued, even when the requested state is absent.
public sealed class TaskSwitchVehicleProbe(AgvOptions options, AgvConnectionManager connections)
    : ITaskSwitchVehicleProbe
{
    public async Task<TaskSwitchVehicleEvidence> ReadAsync(string vehicleId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(vehicleId)) return Rejected(vehicleId ?? "", "未指定待核对车辆。");
        if (!options.Enabled) return Rejected(vehicleId, "AGV 通信未启用，无法核对空载及顶升下降。");
        if (!connections.TryGet(vehicleId, out var client) || !client.Robot.Enabled)
            return Rejected(vehicleId, "指定车辆未配置或通信已禁用，无法核对空载及顶升下降。");

        try
        {
            using var response = await client.RequestAsync(1027, null, ct);
            if (!options.Enabled || !client.CommunicationEnabled || !client.Robot.Enabled)
                return Rejected(client.Robot.VehicleId, "查询期间车辆通信已禁用，本次反馈不用于换单。");
            var observedAt = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow();
            var fields = ReadFields(response.RootElement, "1027");
            if (fields.TryGetValue("ret_code", out var returnCode) && RequiredInt(returnCode, "ret_code") != 0)
                throw new InvalidDataException("1027 返回非零错误码，无法核对顶升状态。");

            var enabled = RequiredBool(fields, "jack_enable");
            var emergency = RequiredBool(fields, "jack_emc");
            var errorCode = RequiredInt(RequiredField(fields, "jack_error_code"), "jack_error_code");
            var state = RequiredInt(RequiredField(fields, "jack_state"), "jack_state");
            var loaded = RequiredBool(fields, "jack_isFull");
            var blockers = new List<string>();
            var cleanupBlockers = new List<string>();
            void Shared(string message) { blockers.Add(message); cleanupBlockers.Add(message); }
            if (!enabled) Shared("车端顶升未启用，不能确认换单所需机械状态。");
            if (emergency) Shared("顶升急停尚未解除。");
            if (errorCode != 0) Shared($"顶升错误码为 {errorCode}，不能换单。");
            if (loaded) blockers.Add("车端报告顶升上有载荷，不能按空车换单。");
            else cleanupBlockers.Add("车端未报告载箱，不能按“完成原业务返箱”换单。");
            if (state != 3)
                blockers.Add(state switch
                {
                    0 => "顶升正在上升，尚未下降到位。",
                    1 => "顶升处于上升到位状态，尚未下降到位。",
                    2 => "顶升正在下降，尚未下降到位。",
                    4 => "顶升已停止，但未确认下降到位。",
                    255 => "顶升执行失败，尚未确认下降到位。",
                    _ => $"未知顶升状态码 {state}，不能推定下降到位。"
                });
            if (state != 1)
                cleanupBlockers.Add(state switch
                {
                    0 => "顶升正在上升，尚未确认载箱到位。",
                    2 => "顶升正在下降，不能开始返箱收尾换单。",
                    3 => "顶升处于下降状态，载箱任务尚未确认可安全运行。",
                    4 => "顶升已停止，但未确认载箱到位。",
                    255 => "顶升执行失败，不能开始返箱收尾换单。",
                    _ => $"未知顶升状态码 {state}，不能核定载箱收尾。"
                });
            var emptyAndLowered = blockers.Count == 0;
            var tasksKnown = false;
            var noActiveTasks = false;
            try
            {
                using var taskResponse = await client.RequestAsync(1110, new { }, ct);
                noActiveTasks = ReadTaskList(taskResponse.RootElement);
                tasksKnown = true;
            }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                blockers.Add("车端全任务列表未核定：" + Limit(error.Message));
                cleanupBlockers.Add("车端全任务列表未核定：" + Limit(error.Message));
            }
            if (!options.Enabled || !client.CommunicationEnabled || !client.Robot.Enabled)
                return Rejected(client.Robot.VehicleId, "查询期间车辆通信已禁用，本次反馈不用于换单。");
            return new(client.Robot.VehicleId, emptyAndLowered, observedAt, blockers.ToArray())
            {
                TasksKnown = tasksKnown,
                NoActiveTasks = tasksKnown && noActiveTasks,
                BusinessCleanupReady = cleanupBlockers.Count == 0,
                BusinessCleanupBlockers = cleanupBlockers.ToArray(),
                Loaded = loaded,
                JackState = state,
            };
        }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            return Rejected(client.Robot.VehicleId, "空载及顶升反馈不可用：" + Limit(error.Message));
        }
    }

    private static Dictionary<string, JsonElement> ReadFields(JsonElement root, string context)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{context} 响应不是状态对象。");
        var fields = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in root.EnumerateObject())
            if (!fields.TryAdd(field.Name, field.Value))
                throw new InvalidDataException($"{context} 响应含重复字段 {field.Name}，无法确定反馈。");
        return fields;
    }

    private static bool ReadTaskList(JsonElement root)
    {
        var fields = ReadFields(root, "1110");
        ValidateTaskReturnCode(fields);
        if (fields.TryGetValue("task_status_package", out var wrapped))
        {
            if (fields.ContainsKey("task_status_list"))
                throw new InvalidDataException("1110 同时存在顶层列表及包装列表，归属有歧义。");
            fields = ReadFields(wrapped, "1110 task_status_package");
            ValidateTaskReturnCode(fields);
        }
        if (!fields.TryGetValue("task_status_list", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("1110 缺少有效 task_status_list；缺失列表不等于没有任务。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var noActive = true;
        foreach (var row in rows.EnumerateArray())
        {
            var task = ReadFields(row, "1110 任务记录");
            if (!task.TryGetValue("task_id", out var rawId) || rawId.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(rawId.GetString()))
                throw new InvalidDataException("1110 任务缺少明确 task_id，不能核定任务归属。");
            if (!ids.Add(rawId.GetString()!))
                throw new InvalidDataException("1110 含重复 task_id，不能核定任务归属。");
            if (!task.TryGetValue("status", out var rawStatus) || rawStatus.ValueKind != JsonValueKind.Number ||
                !rawStatus.TryGetInt32(out var status) || status is < 1 or > 7)
                throw new InvalidDataException("1110 含缺失或未知状态（包括 404），不能核定原任务是否结束。");
            if (status is 1 or 2 or 3) noActive = false;
        }
        return noActive;
    }

    private static void ValidateTaskReturnCode(IReadOnlyDictionary<string, JsonElement> fields)
    {
        if (!fields.TryGetValue("ret_code", out var value)) return;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var code) || code != 0)
            throw new InvalidDataException("1110 返回错误码无效或非零，不能核定全任务列表。");
    }

    private static JsonElement RequiredField(IReadOnlyDictionary<string, JsonElement> fields, string name) =>
        fields.TryGetValue(name, out var value) ? value : throw new InvalidDataException($"1027 缺少明确字段 {name}。");

    private static bool RequiredBool(IReadOnlyDictionary<string, JsonElement> fields, string name)
    {
        var value = RequiredField(fields, name);
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException($"1027 字段 {name} 必须为布尔值。");
        return value.GetBoolean();
    }

    private static int RequiredInt(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result : throw new InvalidDataException($"1027 字段 {name} 必须为整数。");

    private static TaskSwitchVehicleEvidence Rejected(string vehicleId, string reason) =>
        new(vehicleId, false, AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow(), [reason]);

    private static string Limit(string message) => message.Length > 300 ? message[..300] : message;
}
