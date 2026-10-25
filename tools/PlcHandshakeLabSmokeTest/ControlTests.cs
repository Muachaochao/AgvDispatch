using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using PlcHandshakeLab;

internal static class ControlTests
{
    public static async Task RunAsync()
    {
        var probe=new TcpListener(IPAddress.Loopback,0);probe.Start();var port=((IPEndPoint)probe.LocalEndpoint).Port;probe.Stop();
        var path=Path.Combine(Path.GetTempPath(),"lab-http-"+Guid.NewGuid().ToString("N"),"session.jsonl");
        using var lab=new NetworkLab(NetworkLabConfig.CreateDefault(),path);
        await using var server=new LabControlServer(lab,port);server.Start();
        using var client=new HttpClient(new SocketsHttpHandler{UseProxy=false}){BaseAddress=new Uri($"http://127.0.0.1:{port}/"),Timeout=TimeSpan.FromSeconds(5)};
        var id=Guid.NewGuid().ToString("N");
        async Task<HttpStatusCode> Send(bool header,bool origin,bool stop=false)
        {
            using var request=new HttpRequestMessage(HttpMethod.Post,"cycle"){Content=JsonContent.Create(new{runId=id,stations=new[]{"CNC-001","CNC-002"},rounds=2,stop})};
            if(header)request.Headers.Add("X-Agv-Lab-Control","business-run-v1");
            if(origin)request.Headers.Add("Origin","http://example.invalid");
            using var response=await client.SendAsync(request);return response.StatusCode;
        }
        if(await Send(false,false)!=HttpStatusCode.Forbidden||await Send(true,true)!=HttpStatusCode.Forbidden)
            throw new Exception("browser or missing-header control not rejected");
        if(await Send(true,false)!=HttpStatusCode.OK)throw new Exception("loopback call plan rejected");
        lab.TickAutomatic();
        if(!lab.Snapshot("CNC-001").Single(p=>p.Mapping.Station=="CNC-001"&&p.Mapping.Signal==0).Value)throw new Exception("HTTP call plan not applied");
        if(await Send(true,false,true)!=HttpStatusCode.OK||!lab.CallPlanStatus.Contains("停止新增"))throw new Exception("HTTP stop failed");
        Console.WriteLine("ControlTests passed: isolated HTTP start/stop, missing-header and browser-origin rejection; no site devices.");
    }
}
