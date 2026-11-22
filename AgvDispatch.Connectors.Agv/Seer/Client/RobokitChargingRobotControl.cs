using System.Text.Json;
using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.Connectors.Agv;

public sealed class RobokitChargingRobotControlOptions
{
    public const string SectionName = "RobokitCharging";
    public int OtherApiPort { get; set; } = 19210;
    // API 6005 only switches the robot relay. This requires confirmation for the
    // actual robot/charger installation; it does not implement a charger protocol.
    public bool RelayControlConfirmed { get; set; }
}

/// <summary>Implements the locally archived 1007 battery and 6005 relay APIs.
/// Neither a successful relay response nor auto_charge proves that charging has begun.</summary>
public sealed class RobokitChargingRobotControl(
    AgvOptions options,
    AgvConnectionManager connections,
    RobokitChargingRobotControlOptions settings) : IChargingRobotControl
{
    public async Task SetChargingRelayAsync(string vehicleId, bool enabled, CancellationToken cancellationToken)
    {
        if (!options.AllowCommands || !settings.RelayControlConfirmed)
            throw new InvalidOperationException("AGV动作或现场核定的6005充电继电器控制未启用。");
        var client = RequireClient(vehicleId);
        var port = client.Robot.OtherApiPort ?? settings.OtherApiPort;
        if (port is < 1 or > 65535)
            throw new InvalidOperationException("Robokit其他API端口无效。");
        // There is intentionally no retry: a lost reply may follow a successful
        // relay switch. The durable charging state machine owns reconciliation.
        await using var other = new SeerTcpChannel(client.Robot.IpAddress, port, options);
        using var response = await other.RequestAsync(6005, new { status = enabled }, cancellationToken);
        RequireResponse(response.RootElement, 6005, requireReturnCode: true);
    }

    public async Task<ChargingBatteryReading> ReadBatteryAsync(string vehicleId, CancellationToken cancellationToken)
    {
        var client = RequireClient(vehicleId);
        using var response = await client.RequestAsync(1007, new { simple = false }, cancellationToken);
        var root = response.RootElement;
        RequireResponse(root, 1007, requireReturnCode: false);
        var level = OptionalDecimal(root, "battery_level");
        if (level is < 0 or > 1)
            throw new InvalidDataException("1007电量超出文档规定的[0,1]范围。");
        return new(vehicleId, level * 100m, OptionalBool(root, "charging"),
            OptionalDouble(root, "current"), OptionalDouble(root, "battery_temp"),
            OptionalBool(root, "manual_charge"), OptionalBool(root, "auto_charge"), AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow());
    }

    private SeerRobotClient RequireClient(string vehicleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleId);
        if (!options.Enabled) throw new InvalidOperationException("AGV采集未启用。");
        if (options.GetConfigurationError() is { } error) throw new InvalidOperationException(error);
        if (!connections.TryGet(vehicleId, out var client))
            throw new InvalidOperationException($"车辆{vehicleId}未配置或通信未启用。");
        return client;
    }

    private static void RequireResponse(JsonElement root, ushort request, bool requireReturnCode)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{request}响应不是JSON对象，结果不能核定。");
        if (!root.TryGetProperty("ret_code", out var code))
        {
            if (requireReturnCode) throw new InvalidDataException($"{request}缺少明确ret_code，发送结果不确定，禁止自动重发。");
            return;
        }
        if (code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out var value))
            throw new InvalidDataException($"{request}返回码类型无效，结果不能核定。");
        if (value != 0) throw new InvalidOperationException($"Robokit {request}拒绝请求，ret_code={value}。");
    }

    private static bool? OptionalBool(JsonElement root, string field)
    {
        if (!root.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException($"1007字段{field}必须是boolean。")
        };
    }

    private static decimal? OptionalDecimal(JsonElement root, string field)
    {
        if (!root.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        throw new InvalidDataException($"1007字段{field}不是有效数字。");
    }

    private static double? OptionalDouble(JsonElement root, string field)
    {
        if (!root.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)) return number;
        throw new InvalidDataException($"1007字段{field}不是有限数字。");
    }
}
