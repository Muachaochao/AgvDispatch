namespace AgvDispatch.Connectors.Plc;

public sealed class PlcOptions
{
    private static readonly HashSet<string> SupportedSeries = new(
        ["S1200", "S1500", "S300", "S400", "S200Smart", "S200"],
        StringComparer.OrdinalIgnoreCase);

    public const string SectionName = "Plc";

    public bool Enabled { get; set; }
    public bool AllowWrites { get; set; }
    public bool VerifyWrites { get; set; } = true;
    public string DeviceId { get; set; } = "PLC-MAIN";
    public string DisplayName { get; set; } = "总控PLC";
    public string Driver { get; set; } = "SiemensS7";
    public string SiemensSeries { get; set; } = "S1200";
    public string IpAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 102;
    public byte? Rack { get; set; }
    public byte? Slot { get; set; }
    public byte? ConnectionType { get; set; }
    public int? LocalTsap { get; set; }
    public int? DestTsap { get; set; }
    public int ConnectTimeoutMs { get; set; } = 3000;
    public int ReceiveTimeoutMs { get; set; } = 3000;
    public int PollIntervalMs { get; set; } = 500;
    public int ReconnectDelayMs { get; set; } = 3000;
    public int StaleAfterMs { get; set; } = 5000;
    public int InterruptedFailureThreshold { get; set; } = 3;
    public int ReadBlockSizeBytes { get; set; } = 200;
    public string SignalCsvPath { get; set; } = "config/plc-signals.csv";
    public string AuthorizationCodeEnvironmentVariable { get; set; } = "HSL_AUTHORIZATION_CODE";

    internal string? GetConfigurationError()
    {
        if (!Enabled)
        {
            return null;
        }

        if (!string.Equals(Driver, "SiemensS7", StringComparison.OrdinalIgnoreCase))
        {
            return $"暂不支持PLC驱动“{Driver}”，当前项目使用SiemensS7。";
        }

        if (!SupportedSeries.Contains(SiemensSeries))
        {
            return $"不支持西门子系列“{SiemensSeries}”。可选：{string.Join(", ", SupportedSeries)}。";
        }

        if (string.IsNullOrWhiteSpace(IpAddress))
        {
            return "PLC IP地址未配置。";
        }

        if (Port is < 1 or > 65535)
        {
            return "PLC端口必须在1到65535之间。";
        }

        if (ConnectTimeoutMs <= 0 || ReceiveTimeoutMs <= 0 || PollIntervalMs <= 0 ||
            ReconnectDelayMs <= 0 || StaleAfterMs <= 0)
        {
            return "PLC超时、采集周期、重连周期和信号过期时间必须大于0毫秒。";
        }

        if (InterruptedFailureThreshold < 2)
        {
            return "PLC中断判定阈值至少为2次连续失败。";
        }

        if (ReadBlockSizeBytes is < 1 or > 65535)
        {
            return "PLC批量读取块大小必须在1到65535字节之间。";
        }

        return null;
    }
}
