using PlcHandshakeLab;
using System.Net;
using System.Net.Sockets;

internal static class RecoveryAndStageTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "plc-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        void Reject(Action action, string message) { try { action(); } catch (InvalidOperationException) { checks++; return; } throw new Exception(message); }
        NetworkLab NewLab()
        {
            var config = NetworkLabConfig.CreateDefault(); config.AutoDoneSeconds = .1; config.Port = NetworkTests.FreePort();
            return new(config, Path.Combine(root, Guid.NewGuid() + ".jsonl"));
        }
        static bool Value(NetworkLab lab, int signal) => lab.Snapshot("CNC-001").Single(p => p.Mapping.Signal == signal).Value;
        static void Host(NetworkLab lab, int signal, bool value) => lab.WriteHost(1, 610, signal - 6, [(byte)(value ? 1 : 0)], true, "test");
        static void Permit(NetworkLab lab)
        {
            lab.SetAutomatic(true); Host(lab, 6, true); lab.TickAutomatic(); Host(lab, 6, false); lab.TickAutomatic();
        }
        try
        {
            using (var lab = NewLab())
            {
                Permit(lab); lab.SetAutomatic(false); lab.SetAutomatic(true);
                Check(lab.AutomaticStatus.Contains("内部设备保留"), "Ready消费后切模式仍保留许可");
                Host(lab, 7, true); lab.TickAutomatic(); await Task.Delay(130); lab.TickAutomatic();
                Check(Value(lab, 9), "切模式后本轮可产生Done");
                Host(lab, 7, false); lab.TickAutomatic(); Check(!Value(lab, 9), "第一轮闭合");
                Host(lab, 6, true); lab.TickAutomatic(); Check(Value(lab, 8), "连续第二轮重新授予许可");
                Host(lab, 6, false); lab.TickAutomatic(); Host(lab, 7, true); lab.TickAutomatic(); await Task.Delay(130); lab.TickAutomatic();
                Check(Value(lab, 9), "连续第二轮Done"); Host(lab, 7, false); lab.TickAutomatic(); Check(!Value(lab, 9), "第二轮闭合");
            }
            using (var lab = NewLab())
            {
                Permit(lab); Host(lab, 7, true); lab.TickAutomatic();
                lab.ManualSet(NetworkLabConfig.Shared, 12, true); lab.TickAutomatic();
                Host(lab, 7, false); lab.ManualSet(NetworkLabConfig.Shared, 12, false); lab.TickAutomatic();
                Check(lab.AutomaticStatus.Contains("暂停待核对"), "故障中取消周期不能静默恢复");
                Host(lab, 6, true); lab.TickAutomatic(); Check(!Value(lab, 8), "恢复门禁不发新Ready");
                Reject(() => lab.ConfirmCycleClosed("test"), "自动模式拒绝人工解除保留");
                lab.SetAutomatic(false);
                Reject(() => lab.ConfirmCycleClosed("test"), "共享申请未低不能解除保留");
                Host(lab, 6, false);
                Reject(() => lab.ConfirmCycleClosed(""), "必须提供恢复依据");
                lab.ConfirmCycleClosed("已核对调度取消及共享全部低基线"); Permit(lab);
                Host(lab, 7, true); lab.TickAutomatic(); await Task.Delay(130); lab.TickAutomatic();
                Check(Value(lab, 9), "明确闭合后下一轮可完成");
                Check(lab.ExportCsv().Contains("确认模拟周期闭合"), "恢复确认持久化");
            }
            using (var lab = NewLab())
            {
                Permit(lab); lab.ManualSet("CNC-002", 11, false);
                Host(lab, 7, true); lab.TickAutomatic(); await Task.Delay(130); lab.TickAutomatic();
                Check(Value(lab, 9), "无关工位离线不封锁共享模拟倒料");
            }
            using (var lab = NewLab())
            {
                Permit(lab); lab.UpdateCommunication(false); Host(lab, 7, true); await Task.Delay(130); lab.TickAutomatic();
                Check(!Value(lab, 9), "断连不自动产生Done"); lab.UpdateCommunication(true); lab.TickAutomatic();
                await Task.Delay(130); lab.TickAutomatic();
                Check(Value(lab, 9) && !lab.AutomaticStatus.Contains("暂停待核对"),
                    "后台重连后自动续接同一内存周期，不重复触发且重新计时");
                Host(lab, 7, false); lab.TickAutomatic();
                Check(!Value(lab, 9), "自动续接周期仍由原设备周期闭合撤Done");
            }
            using (var lab = NewLab())
            {
                lab.SelectStage("CNC-001", 0);
                Reject(() => lab.RecordStageTest("CNC-001", "通过", "V", "T", "观察", true), "未交换的初值不能通过");
                lab.Read(1, 0, 3, "test"); lab.Read(1, 610, 1, "test");
                var point = lab.Snapshot("CNC-001").Single(p => p.Mapping.Signal == 2);
                Check(point.Source == "初值" && point.HostAccessedAt is not null, "读取新鲜度与最后变化分离");
                Check(!CommunicationStatus.PointFresh(point, DateTimeOffset.Now.AddSeconds(4)), "点位交换过期");
                Reject(() => lab.RecordStageTest("CNC-001", "通过", "V", "T", "观察", false), "实际条件未确认不能通过");
                lab.ManualSet("CNC-001", 0, true);
                Reject(() => lab.RecordStageTest("CNC-001", "通过", "V", "T", "观察", true), "电平偏差不能通过");
                lab.RecordStageTest("CNC-001", "失败", "V", "T", "呼叫异常|需核对", false);
                lab.ManualSet("CNC-001", 0, false);
                lab.UpdateCommunication(false);
                Reject(() => lab.RecordStageTest("CNC-001", "通过", "V", "T", "观察", true), "断连不能通过");
                lab.UpdateCommunication(true);
                Reject(() => lab.RecordStageTest("CNC-001", "通过", "V", "T", "观察", true), "P改变后旧读取不能作为本次交换");
                lab.Read(1, 0, 3, "test"); lab.Read(1, 610, 1, "test");
                lab.ManualSet("CNC-001", 10, true);
                Reject(() => lab.RecordStageTest("CNC-001", "通过", "V", "T", "观察", true), "工位故障不能记录通过");
                lab.ManualSet("CNC-001", 10, false);
                lab.RecordStageTest("CNC-001", "通过", "V", "T", "实际有箱、无旧任务", true);
                lab.BeginTestRound("CNC-001"); lab.RecordStageTest("CNC-001", "跳过", "V", "T2", "仅测试记录分组", false);
                var report = lab.ExportStageReport();
                Check(report.Contains("第2轮") && report.Contains("未测") && report.Contains("呼叫异常\\|需核对"), "报告保留轮次、未测与失败历史");
                Check(lab.TestResults().Length == 3 && lab.TestResults()[1].Outcome == "通过", "复测不覆盖原失败记录");
                var now = DateTimeOffset.Now;
                Check(!CommunicationStatus.Evaluate(true, 0, now, now).Fresh, "断开状态");
                Check(!CommunicationStatus.Evaluate(true, 1, now.AddSeconds(-4), now).Fresh, "连接未断但通信停滞");
                Check(CommunicationStatus.Evaluate(true, 1, now, now).Fresh, "有效通信状态");
                Check(!CommunicationStatus.Evaluate(true, 1, null, now).Fresh, "仅连接未交换不算正常");
            }
            using (var lab = NewLab())
            {
                lab.Read(1, 0, 1, "single-bool", 4);
                Check(lab.Snapshot("CNC-001").Count(p => p.HostAccessedAt is not null) == 1, "单BOOL读取不刷新其他同字节位");
                var original = lab.Snapshot("CNC-001").Single(p => p.Mapping.Signal == 2);
                await Task.Delay(10); lab.WriteHost(1, 0, 4, [0], true, "unchanged");
                var repeated = lab.Snapshot("CNC-001").Single(p => p.Mapping.Signal == 2);
                Check(repeated.UpdatedAt == original.UpdatedAt && repeated.HostAccessedAt > original.HostAccessedAt, "同值写入刷新交换时间且不伪造电平变化");
            }
            using (var lab = NewLab())
            {
                await using var server = new S7LabServer(lab); server.Start();
                var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, server.Port);
                Check(server.LastDataRequest is null, "仅建TCP连接不算数据交换");
                await NetworkTests.Exchange(client.GetStream(), NetworkTests.Variable(4, 1, 0, 611, false));
                for (var i = 0; i < 100 && server.LastDataRequest is null; i++) await Task.Delay(10);
                Check(lab.Snapshot("CNC-001").All(p => p.HostAccessedAt is not null), "真实BYTE读取更新覆盖点位的新鲜度");
                lab.SelectStage("CNC-001", 0); lab.RecordStageTest("CNC-001", "通过", "TCP-V", "TCP-T", "真实S7交换及合成初态已核实", true);
                lab.SetAutomatic(true);
                await NetworkTests.Exchange(client.GetStream(), NetworkTests.Variable(5, 1, 610, 0, true, [1])); lab.TickAutomatic();
                await NetworkTests.Exchange(client.GetStream(), NetworkTests.Variable(5, 1, 610, 0, true, [0])); lab.TickAutomatic();
                Check(lab.AutomaticStatus.Contains("内部设备保留"), "TCP路径消费Ready后保留有效");
                client.Dispose();
                for (var i = 0; i < 100 && !lab.AutomaticStatus.Contains("暂停待核对"); i++) await Task.Delay(10);
                Check(server.Clients == 0 && lab.AutomaticStatus.Contains("暂停待核对"), "真实TCP断连建立活动周期恢复门禁");
                using var reconnect = new TcpClient(); await reconnect.ConnectAsync(IPAddress.Loopback, server.Port);
                await NetworkTests.Exchange(reconnect.GetStream(), NetworkTests.Variable(4, 1, 0, 611, false));
                for (var i = 0; i < 20; i++) { lab.TickAutomatic(); await Task.Delay(10); }
                Check(!lab.AutomaticStatus.Contains("暂停待核对") && lab.AutomaticStatus.Contains("内部设备保留"),
                    "真实重连保留原许可并自动续接，不产生新信号");
                await NetworkTests.Exchange(reconnect.GetStream(), NetworkTests.Variable(5, 1, 610, 1, true, [1])); lab.TickAutomatic();
                await Task.Delay(130); lab.TickAutomatic();
                Check(Value(lab, 9), "断连重连后的原许可仍由后台周期正常闭合");
                await NetworkTests.Exchange(reconnect.GetStream(), NetworkTests.Variable(5, 1, 610, 1, true, [0])); lab.TickAutomatic();
                Check(!Value(lab, 9), "后台退出周期后自动撤Done");
                Check(lab.ExportCsv().Contains("断开连接") && lab.ExportCsv().Contains("通信恢复"), "断连与恢复日志持久化");
            }
            Console.WriteLine($"PASS: {checks} recovery/stage checks. 模式许可保留、两轮、故障取消、真实TCP断连重连、逐点新鲜度与阶段报告。");
        }
        finally { Directory.Delete(root, true); }
    }
}
