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
        using(var capabilityRequest=new HttpRequestMessage(HttpMethod.Get,"capabilities"))
        {
            capabilityRequest.Headers.Add("X-Agv-Lab-Control","business-run-v1");
            using var response=await client.SendAsync(capabilityRequest);
            if(response.StatusCode!=HttpStatusCode.OK||!(await response.Content.ReadAsStringAsync()).Contains("business-box-action-v1"))
                throw new Exception("box feedback capability unavailable");
        }
        if(await Send(true,false)!=HttpStatusCode.OK)throw new Exception("loopback call plan rejected");
        lab.TickAutomatic();
        if(!lab.Snapshot("CNC-001").Single(p=>p.Mapping.Station=="CNC-001"&&p.Mapping.Signal==0).Value)throw new Exception("HTTP call plan not applied");
        var feedback=new CycleTransportFeedback(id,0,"CNC-001","task","AGV-01","pickup-step","original-vendor","Pickup",DateTimeOffset.UtcNow);
        async Task<HttpStatusCode> Proof(bool header, CycleTransportFeedback value)
        {
            using var request=new HttpRequestMessage(HttpMethod.Post,"cycle-feedback"){Content=JsonContent.Create(value)};
            if(header)request.Headers.Add("X-Agv-Lab-Control","business-run-v1");
            using var response=await client.SendAsync(request);return response.StatusCode;
        }
        if(await Proof(false,feedback)!=HttpStatusCode.Forbidden||await Proof(true,feedback)!=HttpStatusCode.Conflict)
            throw new Exception("feedback accepted before call acknowledgement or without header");
        var ack=lab.Config.Points.Single(p=>p.Station=="CNC-001"&&p.Signal==2);
        lab.WriteHost(ack.Db,ack.Byte,ack.Bit,[1],true,"test");lab.TickAutomatic();
        if(await Proof(true,feedback with {ObservedAt=DateTimeOffset.UtcNow.AddSeconds(-10)})!=HttpStatusCode.Conflict||
            await Proof(true,feedback with {ObservedAt=DateTimeOffset.UtcNow})!=HttpStatusCode.OK||
            lab.Snapshot("CNC-001").Single(p=>p.Mapping.Station=="CNC-001"&&p.Mapping.Signal==1).Value)
            throw new Exception("HTTP original-action box feedback failed");
        if(await Proof(true,feedback with {VendorTaskId="another-original",ObservedAt=DateTimeOffset.UtcNow})!=HttpStatusCode.Conflict)
            throw new Exception("retry changed original action identity");
        if(await Send(true,false,true)!=HttpStatusCode.OK||!lab.CallPlanStatus.Contains("停止新增"))throw new Exception("HTTP stop failed");
        Console.WriteLine("ControlTests passed: isolated HTTP start/stop, missing-header and browser-origin rejection; no site devices.");
    }
}
