using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AgvDispatch.Connectors.Agv;
using AgvDispatch.Domain.Agvs;

internal static class StandbyChainChecks
{
    public static async Task RunAsync(Action<bool,string> check)
    {
        using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        var robot=new AgvRobotOptions{VehicleId="ORIGINAL",IpAddress="127.0.0.1",Port=((IPEndPoint)listener.LocalEndpoint).Port};
        var options=new AgvOptions{Enabled=true,AllowCommands=false,NavigationCommandMode="SpecifiedPath",Robots=[robot]};
        await using var manager=new AgvConnectionManager(options);
        var gateway=new SeerAgvGateway(new AgvStateCache(options),manager,options);
        var cases=new (int[] Statuses,AgvTransportTaskState Expected,string Message)[]
        {
            ([2,4],AgvTransportTaskState.GoingToDestination,"尾段完成而前段仍运行不能确认全链完成"),
            ([404,4],AgvTransportTaskState.Unknown,"前段404即使尾段完成仍需核对"),
            ([4],AgvTransportTaskState.Unknown,"缺少前段响应不能确认完成"),
            ([5,4],AgvTransportTaskState.Failed,"前段失败不能被尾段完成覆盖"),
            ([6,4],AgvTransportTaskState.Cancelled,"前段取消不能被尾段完成覆盖"),
            ([3,4],AgvTransportTaskState.Suspended,"前段暂停不能被尾段完成覆盖"),
            ([4,4],AgvTransportTaskState.Completed,"全部原路段均成功才能确认全链完成")
        };
        var server=ServeAsync();
        foreach(var scenario in cases)
            check((await gateway.GetTransportTaskStatusAsync("FLEET-STANDBY-run-ORIGINAL","ORIGINAL",["START","MID","HOME"],default))?.State==scenario.Expected,
                scenario.Message);
        await server;
        try{await gateway.GetTransportTaskStatusAsync("FLEET-STANDBY-run-ORIGINAL","OTHER",["START","HOME"],default);throw new Exception("不能换车查询");}
        catch(InvalidOperationException){check(true,"原车缺失时不扫描其它车辆替代");}
        async Task ServeAsync()
        {
            using var client=await listener.AcceptTcpClientAsync();using var stream=client.GetStream();
            foreach(var scenario in cases)
            {
                var header=new byte[16];await stream.ReadExactlyAsync(header);
                var type=BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(8,2));
                if(type!=1110)throw new Exception("只读核对不得发送导航或动作");
                var payload=new byte[BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4,4))];await stream.ReadExactlyAsync(payload);
                using var json=JsonDocument.Parse(payload);var ids=json.RootElement.GetProperty("task_ids").EnumerateArray().Select(e=>e.GetString()).ToArray();
                check(ids.SequenceEqual(["FLEET-STANDBY-run-ORIGINAL-01","FLEET-STANDBY-run-ORIGINAL"]),"原厂家编号与原路线重建所有逐段查询身份");
                var body=JsonSerializer.SerializeToUtf8Bytes(new{task_status_list=scenario.Statuses.Select((status,i)=>new{task_id=ids[scenario.Statuses.Length==1?1:i],status}).ToArray()});
                BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4,4),body.Length);
                BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8,2),(ushort)(type+10000));
                await stream.WriteAsync(header);await stream.WriteAsync(body);
            }
        }
    }
}
