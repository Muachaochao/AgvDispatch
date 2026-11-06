using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence;

public sealed partial class TaskCenterReadRepository
{
    private async Task<(IReadOnlyList<TaskExecutionEvent> Items, int Total)> ReadTimelineAsync(string taskId, CancellationToken ct)
    {
        var query = database.Queryable<ExecutionEvidenceEntity>().Where(e => e.TaskId == taskId);
        var count = await query.Clone().CountAsync(ct);
        var rows = await query.OrderBy(e => e.OccurredAt, OrderByType.Desc).OrderBy(e => e.Id, OrderByType.Desc)
            .Take(100).ToListAsync(ct);
        return (rows.OrderBy(e => e.OccurredAt).ThenBy(e => e.Id, StringComparer.Ordinal).Select(e =>
            new TaskExecutionEvent(e.Id, Time(e.OccurredAt), e.StepId, e.Kind, Title(e.Kind), Summary(e), "持久化执行证据/后台记录时间")).ToArray(), count);
    }

    private static string Title(string kind) => kind switch
    {
        "StepObservation" => "执行器首次观察步骤",
        "StepConditionsObserved" => "步骤条件发生变化",
        "PlcWriteIntent" => "PLC写入意图已记录",
        "PlcWriteAcknowledged" => "PLC写调用已返回成功",
        "AgvSendIntent" => "车辆命令发送意图已记录",
        "AgvSendAcknowledged" => "车辆适配器返回任务身份",
        "DumperExitBlocked" => "倒料区交接核验未通过",
        "ObstacleRecoveryDetected" => "车端报告前方障碍",
        "ObstacleDetourPlanned" => "已生成安全替代路线",
        "ObstacleOriginalCancelRequested" => "已请求取消原车端导航",
        "ObstacleAutomaticDetourSent" => "已发送自动改道任务",
        "ObstacleDetourSendUncertain" => "改道发送结果待核对",
        "ObstacleRecoveryFrozen" => "障碍恢复任务已冻结",
        "ObstacleRecoveryObservationCleared" => "车端障碍信号已解除",
        _ => kind
    };

    private static string Summary(ExecutionEvidenceEntity row)
    {
        // Do not forward arbitrary device payloads, host paths or identity baselines to the browser.
        if(row.Kind=="DumperExitBlocked")
        {
            try
            {
                using var doc=JsonDocument.Parse(row.Evidence);
                if(doc.RootElement.ValueKind==JsonValueKind.Object&&doc.RootElement.TryGetProperty("Reason",out var reason)&&reason.ValueKind==JsonValueKind.String)
                {
                    var text=reason.GetString()??"";
                    text=new string(text.Take(512).Select(c=>char.IsControl(c)?' ':c).ToArray());
                    return $"当次交接核验未通过：{text}；这是记录时的阻塞原因，当前阶段与新鲜证据需另行核对。";
                }
            }
            catch(JsonException) { }
            return "当次交接核验未通过，原因格式不可解析；原记录保留在后台，不据此释放占用。";
        }
        if (row.Kind is "PlcWriteIntent" or "PlcWriteAcknowledged")
        {
            try
            {
                using var doc = JsonDocument.Parse(row.Evidence);
                return $"{Text(doc.RootElement, "stationId")}/{Text(doc.RootElement, "signalName")} 写入{Text(doc.RootElement, "value")}；"
                    + (row.Kind == "PlcWriteIntent" ? "这是写入前意图，不代表成功。" : "写调用成功不等于PLC业务接收或机械动作完成。");
            }
            catch (JsonException) { return "写入证据格式不可解析；原文保留在后台。"; }
        }
        if (row.Kind == "StepConditionsObserved")
        {
            try
            {
                using var doc = JsonDocument.Parse(row.Evidence);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("Conditions", out var conditions) && conditions.ValueKind == JsonValueKind.Array)
                    return string.Join("；", conditions.EnumerateArray().Take(8).Select(c =>
                        $"{Text(c, "StationId")}/{Text(c, "VariableName")}：{Text(c, "CurrentValue")}，{Text(c, "Result")}"));
            }
            catch (JsonException) { return "证据格式不可解析；原文保留在后台，不能据此推断通过。"; }
        }
        return row.Kind switch
        {
            "AgvSendAcknowledged" => "仅证明适配器已返回身份；不等同于车辆运动或动作完成。",
            "PlcWriteIntent" => "写入前记录；不代表写入成功、PLC业务接收或机械动作完成。",
            "StepObservation" => "本次执行器观察，不代表步骤开始时间或物理到位。",
            "ObstacleRecoveryDetected" => "这是车端先给出的Blocked/52200证据；后台尚未取消原任务或释放责任。",
            "ObstacleDetourPlanned" => "候选路线已按当前地图、道路封锁和交通路权生成；发送前仍须复核停稳、载荷和PLC。",
            "ObstacleOriginalCancelRequested" => "取消应答不代表原导航已经终止；必须等待全部3066分段终态。",
            "ObstacleAutomaticDetourSent" => "替代导航已发送并绑定原业务任务；到点、取放箱和PLC完成仍需后续证据。",
            "ObstacleDetourSendUncertain" => "只查询已持久化的新厂家编号，禁止重发；无法证明时冻结原任务。",
            "ObstacleRecoveryFrozen" => "原业务任务、载荷、PLC责任和路权保持；按下一步指引人工处置。",
            "ObstacleRecoveryObservationCleared" => "只证明车端当前不再报告受阻，不证明原任务继续、完成或可释放路权。",
            _ => "原始证据保存在后台；本条记录不单独证明机械动作完成。"
        };
    }

    private static string Text(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return "未知";
        var text = value.ToString();
        return text.Length > 96 ? text[..96] : text;
    }
}
