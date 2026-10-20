using System.Text;

namespace PlcHandshakeLab;

public sealed record LabEvent(DateTimeOffset Time, int Stage, string Kind, string Signal, string Before, string After, string Detail)
{
    public override string ToString() => $"{Time:HH:mm:ss.fff}  [{Stage:00}] {Detail}";
}

/// <summary>只管理离线值。未包含任何通信驱动、自动动作或现场状态推断。</summary>
public sealed class LabSession
{
    public int Current { get; private set; }
    public bool[] Values { get; private set; } = new bool[10];
    public List<LabEvent> Events { get; } = [];
    public bool[] Changed { get; private set; } = new bool[10];
    public bool IsAligned => Enumerable.Range(0, 10).All(i => Sequence.Matches(Current, i, Values[i]));

    public LabSession() => GoTo(0);

    public void GoTo(int index)
    {
        if (index < 0 || index >= Sequence.Stages.Length) throw new ArgumentOutOfRangeException(nameof(index));
        Current = index;
        var next = Sequence.Apply(index, Values);
        Changed = next.Select((v, i) => v != Values[i]).ToArray();
        for (var i = 0; i < next.Length; i++)
            if (Changed[i]) Record("标准电平", i, Values[i], next[i]);
        Values = next;
        Note("阶段", $"进入 {index + 1:00} / {Sequence.Stages[index].Name}（条件假定成立）");
    }

    public void Toggle(int index)
    {
        if (index < 0 || index >= Values.Length) throw new ArgumentOutOfRangeException(nameof(index));
        Changed = new bool[10];
        Changed[index] = true;
        var before = Values[index];
        Values[index] = !before;
        Record("手动模拟", index, before, Values[index]);
    }

    public void Reset()
    {
        // 第22步的可选值采用此前模拟值；新周期必须从明确初值重新开始。
        Values = new bool[10];
        GoTo(0);
    }

    public bool Advance(bool loop)
    {
        if (!IsAligned) return false;
        if (Current == Sequence.Stages.Length - 1)
        {
            if (!loop) return false;
            Reset();
        }
        else GoTo(Current + 1);
        return true;
    }

    public void Note(string kind, string detail)
    {
        Events.Add(new(DateTimeOffset.Now, Current + 1, kind, "", "", "", detail));
        Trim();
    }

    private void Record(string kind, int index, bool before, bool after)
    {
        var signal = Sequence.Signals[index];
        var name = $"{signal.Owner}:{signal.Address} {signal.Name}";
        Events.Add(new(DateTimeOffset.Now, Current + 1, kind, name, before ? "1" : "0", after ? "1" : "0",
            $"{kind}  {name}  {(before ? 1 : 0)} → {(after ? 1 : 0)}"));
        Trim();
    }

    private void Trim()
    {
        if (Events.Count > 500) Events.RemoveRange(0, Events.Count - 500);
    }

    public string ExportCsv()
    {
        static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        var output = new StringBuilder("时间,阶段,类型,点位,原值,新值,说明\r\n");
        foreach (var e in Events)
            output.AppendLine(string.Join(",", new[] { e.Time.ToString("O"), e.Stage.ToString(), e.Kind, e.Signal, e.Before, e.After, e.Detail }.Select(Quote)));
        return output.ToString();
    }
}
