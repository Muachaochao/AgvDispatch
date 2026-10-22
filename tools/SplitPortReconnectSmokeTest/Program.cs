using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AgvDispatch.Connectors.Agv;

await using var robot = new SplitRobot();
var options = new AgvOptions { Enabled=true, AllowCommands=true, RequestIntervalMs=5, RequestTimeoutMs=1000, ConnectTimeoutMs=1000 };
await using var client = new SeerRobotClient(new() { VehicleId="SPLIT-SIM",IpAddress="127.0.0.1",Port=robot.StatusPort,NavigationPort=robot.NavigationPort }, options);
await client.GoToTargetAsync("FIRST", ["A","B"], null, true, default);
await robot.FirstNavigationClosed.Task.WaitAsync(TimeSpan.FromSeconds(3));
await Task.Delay(100); // Wait for the deliberately sent FIN; no control request is retried.
if ((await client.QueryTaskAsync("SECOND",default))?.Status!=404) throw new Exception("Status port must remain usable");
await client.GoToTargetAsync("SECOND", ["B","C"], null, true, default);
if (robot.NavigationConnections!=2 || robot.Accepted.Count!=2 || !robot.Accepted.SequenceEqual(["FIRST","SECOND"]) || robot.StatusWrites!=0)
    throw new Exception("Wrong port, stale socket reused, or duplicated control request");
Console.WriteLine("PASS 状态端口仍在线时识别导航端口已到达的FIN，发车前建立新连接");
Console.WriteLine("PASS 两条原3066各接收一次，状态端口没有控制写入");

sealed class SplitRobot : IAsyncDisposable
{
    readonly TcpListener status=new(IPAddress.Loopback,0),nav=new(IPAddress.Loopback,0);
    readonly CancellationTokenSource stop=new();
    readonly List<Task> workers=[];
    readonly List<TcpClient> clients=[];
    public TaskCompletionSource FirstNavigationClosed {get;} = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<string> Accepted {get;}=[];
    public int NavigationConnections,StatusWrites;
    public int StatusPort => ((IPEndPoint)status.LocalEndpoint).Port;
    public int NavigationPort => ((IPEndPoint)nav.LocalEndpoint).Port;
    public SplitRobot() { status.Start();nav.Start();workers.Add(Accept(status,false));workers.Add(Accept(nav,true)); }
    async Task Accept(TcpListener listener,bool navigation)
    {
        try { while(!stop.IsCancellationRequested) {
            var client=await listener.AcceptTcpClientAsync(stop.Token);
            lock(clients)clients.Add(client);
            if(navigation)Interlocked.Increment(ref NavigationConnections);
            lock(workers)workers.Add(Serve(client,navigation));
        }} catch(OperationCanceledException) {} catch(SocketException)when(stop.IsCancellationRequested){}
    }
    async Task Serve(TcpClient client,bool navigation)
    {
        using(client)try {
            var stream=client.GetStream();
            while(!stop.IsCancellationRequested) {
                var head=new byte[16];await stream.ReadExactlyAsync(head,stop.Token);
                var size=BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(4,4));
                if(size<0||size>1048576)throw new InvalidDataException();
                var body=new byte[size];await stream.ReadExactlyAsync(body,stop.Token);
                var api=BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(8,2));
                using var request=JsonDocument.Parse(body);
                object result;
                if(api==3066) {
                    if(!navigation)StatusWrites++;
                    var id=request.RootElement.GetProperty("move_task_list")[0].GetProperty("task_id").GetString()!;
                    lock(Accepted)Accepted.Add(id);
                    result=new{ret_code=0};
                } else if(api==1110) {
                    var id=request.RootElement.GetProperty("task_ids")[0].GetString();
                    result=new{task_status_package=new{task_status_list=new[]{new{task_id=id,status=404,type=1}}}};
                } else throw new InvalidDataException("Unexpected API");
                var bytes=JsonSerializer.SerializeToUtf8Bytes(result);
                BinaryPrimitives.WriteInt32BigEndian(head.AsSpan(4,4),bytes.Length);
                BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(8,2),(ushort)(api+10000));
                await stream.WriteAsync(head,stop.Token);await stream.WriteAsync(bytes,stop.Token);
                if(navigation && Accepted.Count==1) { client.Client.Shutdown(SocketShutdown.Both); client.Close();FirstNavigationClosed.TrySetResult();return; }
            }
        }catch(Exception e)when(e is IOException or SocketException or OperationCanceledException or ObjectDisposedException){}
    }
    public async ValueTask DisposeAsync() {
        stop.Cancel();status.Stop();nav.Stop();lock(clients)foreach(var client in clients)client.Dispose();
        Task[] pending;lock(workers)pending=workers.ToArray();await Task.WhenAll(pending);stop.Dispose();
    }
}
