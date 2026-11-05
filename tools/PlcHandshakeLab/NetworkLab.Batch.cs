using System.Diagnostics;

namespace PlcHandshakeLab;

public sealed partial class NetworkLab
{
    private string? _batchRun;
    private string[] _batchStations = [];
    private int _batchRounds;
    private int _batchRound;
    private bool _batchActive;
    private bool _batchStopped;
    private bool _batchAwaitingCallFeedback;
    private bool _batchAwaitingClosure;
    private long _batchSubmittedAt;
    private string _batchStatus = "同批呼叫未启动";

    public bool BatchCallActive { get { lock (_gate) return _batchActive; } }
    public string BatchCallStatus { get { lock (_gate) return _batchStatus; } }

    /// <summary>
    /// Starts a batch whose selected stations are raised in one TickAutomatic
    /// pass. A later round is raised only after every station has returned to
    /// the complete idle/new-call baseline.
    /// </summary>
    public void StartBatchCalls(IReadOnlyCollection<string> stations, int rounds)
    {
        if (stations is null) throw new ArgumentNullException(nameof(stations));
        var selected = stations.Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim()).ToArray();
        if (selected.Length < 2) throw new ArgumentException("同批呼叫至少选择两个工位。");
        if (selected.Distinct(StringComparer.OrdinalIgnoreCase).Count() != selected.Length)
            throw new ArgumentException("同批呼叫工位不能重复。");
        if (rounds is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(rounds), "轮次应为1至100。");

        lock (_gate)
        {
            if (_batchActive) throw new InvalidOperationException("已有同批呼叫正在运行。");
            if (_callRun is not null && !_stopCalls && _callCompleted < _callLimit)
                throw new InvalidOperationException("顺序循环尚未结束，不能启动同批呼叫。");
            if (_fleetRun is not null && !_fleetStopped)
                throw new InvalidOperationException("双车专用用例尚未结束，不能启动同批呼叫。");
            if (!_communicationHealthy || _recoveryReason is not null)
                throw new InvalidOperationException("S7通信未恢复或存在待核对周期，不能启动同批呼叫。");
            if (selected.Any(station => !_states.ContainsKey((station, 0))))
                throw new ArgumentException("同批呼叫包含当前点表中不存在的工位。");
            if (BatchBlocker(selected) is { } blocker)
                throw new InvalidOperationException("同批呼叫未启动，因" + blocker + "；未写入任何工位。");

            _batchRun = Guid.NewGuid().ToString("N");
            _batchStations = selected;
            _batchRounds = rounds;
            _batchRound = 1;
            _batchActive = true;
            _batchStopped = false;
            _batchAwaitingCallFeedback = false;
            _batchAwaitingClosure = false;
            _batchSubmittedAt = 0;
            _batchStatus = $"第1/{_batchRounds}轮准备同批呼叫";
            SetAutomatic(true);
            RaiseBatchCalls();
            Record("启动同批呼叫", string.Join(",", selected), "", "", "",
                $"{_batchRun}；{selected.Length}个工位同一次扫描升为1；共{rounds}轮；每轮全部闭环后才进入下一轮。");
        }
    }

    public void StopBatchCalls()
    {
        lock (_gate)
        {
            if (!_batchActive) return;
            _batchActive = false;
            _batchStopped = true;
            _batchAwaitingCallFeedback = false;
            _batchAwaitingClosure = false;
            _batchStatus = $"已停止第{_batchRound}/{_batchRounds}轮之后的自动呼叫；当前P/H电平保留，等待正常闭合。";
            Record("停止同批后续轮次", string.Join(",", _batchStations), "", "", "", _batchStatus);
        }
    }

    private void RaiseBatchCalls()
    {
        if (!_batchActive || _batchStopped) return;
        if (BatchBlocker(_batchStations) is { } blocker)
        {
            _batchStatus = $"第{_batchRound}/{_batchRounds}轮等待呼叫条件：{blocker}";
            return;
        }

        foreach (var station in _batchStations)
            Set(Find(station, 0), true, "同批呼叫：同一扫描周期产生满箱呼叫");
        _batchAwaitingCallFeedback = true;
        _batchAwaitingClosure = false;
        _batchSubmittedAt = Stopwatch.GetTimestamp();
        _batchStatus = $"第{_batchRound}/{_batchRounds}轮已同批置1：{string.Join('、', _batchStations)}；等待设备反馈";
        Record("同批满箱呼叫", string.Join(",", _batchStations), "", "", "1",
            $"第{_batchRound}/{_batchRounds}轮；{_batchStations.Length}个工位在同一个自动扫描周期触发；任务由正式调度创建和派车。");
    }

    private void TickBatchCalls()
    {
        if (!_batchActive || _batchStopped || _batchRun is null) return;
        if (_recoveryReason is not null || !_communicationHealthy)
        {
            if (_batchActive)
            {
                _batchActive = false;
                _batchStopped = true;
                _batchStatus = "同批呼叫因通信中断或恢复门禁停止新增；当前电平保留，请先核对闭合。";
            }
            return;
        }

        if (_batchAwaitingCallFeedback)
        {
            var pending = _batchStations.Where(station => !BatchCallAccepted(station)).ToArray();
            if (pending.Length == 0)
            {
                _batchAwaitingCallFeedback = false;
                _batchAwaitingClosure = true;
                _batchStatus = $"第{_batchRound}/{_batchRounds}轮已收到全部工位反馈，等待所有工位完整闭环";
                Record("同批呼叫已反馈", string.Join(",", _batchStations), "", "", "",
                    $"第{_batchRound}/{_batchRounds}轮；进入全员闭环等待，不提前发下一轮。");
                return;
            }
            if (Stopwatch.GetElapsedTime(_batchSubmittedAt) > TimeSpan.FromSeconds(8))
            {
                _batchActive = false;
                _batchStopped = true;
                _batchAwaitingCallFeedback = false;
                _batchStatus = $"第{_batchRound}/{_batchRounds}轮8秒内未观察到全部呼叫反馈（{string.Join('、', pending)}）；已停止新增且不自动重发。";
                Record("同批呼叫反馈超时", string.Join(",", pending), "", "", "", _batchStatus);
                return;
            }
            _batchStatus = $"第{_batchRound}/{_batchRounds}轮等待呼叫反馈：{string.Join('、', pending)}";
            return;
        }

        if (_batchAwaitingClosure)
        {
            if (BatchBlocker(_batchStations) is { } blocker)
            {
                _batchStatus = $"第{_batchRound}/{_batchRounds}轮等待全员闭环：{blocker}";
                return;
            }

            Record("同批轮次完整闭环", string.Join(",", _batchStations), "", "", "",
                $"第{_batchRound}/{_batchRounds}轮；所有所选工位呼叫、应答、取箱通知、返箱责任、放箱通知及共享倒料位均已闭合。");
            _batchAwaitingClosure = false;
            if (_batchRound >= _batchRounds)
            {
                _batchActive = false;
                _batchStatus = $"已完成{_batchRounds}轮同批呼叫；全部工位均已按闭环条件完成。";
                Record("同批呼叫完成", string.Join(",", _batchStations), "", "", "", _batchStatus);
                return;
            }
            _batchRound++;
            _batchStatus = $"第{_batchRound}/{_batchRounds}轮已获准，准备同批呼叫";
            RaiseBatchCalls();
            return;
        }

        RaiseBatchCalls();
    }

    private bool BatchCallAccepted(string station) =>
        Get(station, 0) || Get(station, 2) || Get(station, 3) || Get(station, 4) || Get(station, 5);

    private string? BatchBlocker(IEnumerable<string> stations)
    {
        foreach (var station in stations)
        {
            if (!_states.ContainsKey((station, 0))) return station + "不在当前点表中";
            if (Get(station, 10)) return station + "工位故障为1";
            if (!Get(station, 11)) return station + "下游在线为0";
            if (!Get(station, 1)) return station + "箱体在位为0";
            if (Get(station, 0)) return station + "满箱呼叫仍为1";
            if (Get(station, 2)) return station + "满箱呼叫应答未清0";
            if (Get(station, 3)) return station + "满箱已取通知未清0";
            if (Get(station, 4)) return station + "空箱返箱责任未清0";
            if (Get(station, 5)) return station + "空箱已放通知未清0";
        }
        if (_permit) return "倒料许可责任尚未闭合";
        if (Get(NetworkLabConfig.Shared, 12)) return "倒料设备故障为1";
        foreach (var signal in new[] { 6, 7, 8, 9 })
            if (Get(NetworkLabConfig.Shared, signal))
                return $"倒料共享{Sequence.Signals[signal].Name}未清0";
        return null;
    }

    private void PauseBatchForCommunicationLoss()
    {
        if (!_batchActive) return;
        _batchActive = false;
        _batchStopped = true;
        _batchAwaitingCallFeedback = false;
        _batchAwaitingClosure = false;
        _batchStatus = "通信中断，已停止后续同批呼叫；全部现有P/H电平保留并等待现场核对。";
        Record("同批呼叫通信中断", string.Join(",", _batchStations), "", "", "", _batchStatus);
    }
}
