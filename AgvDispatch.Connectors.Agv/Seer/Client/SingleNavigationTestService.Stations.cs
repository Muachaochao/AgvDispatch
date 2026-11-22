using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Agvs;

namespace AgvDispatch.Connectors.Agv;

public sealed partial class SingleNavigationTestService
{
    private static bool ValidSite(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 128 && !id.Any(char.IsControl);

    private static NavigationTestStation[] ParseStations(JsonElement response)
    {
        if(!response.TryGetProperty("stations",out var list)||list.ValueKind!=JsonValueKind.Array||list.GetArrayLength() is <1 or >10000)
            throw new InvalidDataException("车端未返回有效站点列表。");
        var rows=new List<NavigationTestStation>();
        foreach(var row in list.EnumerateArray())
        {
            if(row.ValueKind!=JsonValueKind.Object||!ValidSite(Text(row,"id"))||Number(row,"x") is not {} x||Number(row,"y") is not {} y)
                throw new InvalidDataException("车端站点编号或坐标缺失、无效，请核对1301响应。");
            rows.Add(new(Text(row,"id")!,x,y));
        }
        if(rows.Select(r=>r.Id).Distinct(StringComparer.Ordinal).Count()!=rows.Count)
            throw new InvalidDataException("车端站点编号重复。");
        return rows.ToArray();
    }

    private static string[] ParseRoute(JsonElement response,NavigationTestQuery query,IReadOnlyList<NavigationTestStation> stations)
    {
        if(!response.TryGetProperty("list",out var list)||list.ValueKind!=JsonValueKind.Array||list.GetArrayLength() is <2 or >10000)
            throw new InvalidOperationException("车端未返回可达路线，请检查单向路径及站点连通性。");
        var known=stations.Select(s=>s.Id).ToHashSet(StringComparer.Ordinal);
        var route=new List<string>();
        foreach(var entry in list.EnumerateArray())
        {
            if(entry.ValueKind!=JsonValueKind.String||!ValidSite(entry.GetString())||!known.Contains(entry.GetString()!))
                throw new InvalidDataException("车端路线包含无效或不在当前地图中的站点。");
            route.Add(entry.GetString()!);
        }
        if(route[0]!=query.SourceSite||route[^1]!=query.TargetSite||route.Distinct(StringComparer.Ordinal).Count()!=route.Count)
            throw new InvalidDataException("车端路线起终点不匹配或存在重复站点，请重新核验。");
        return route.ToArray();
    }

    private static bool AtStation(AgvRobotTelemetry telemetry,double x,double y) =>
        telemetry.X is {} tx&&telemetry.Y is {} ty&&double.IsFinite(tx)&&double.IsFinite(ty)&&
        Math.Sqrt(Math.Pow(x-tx,2)+Math.Pow(y-ty,2))<=0.25;

    private static (string Map,string Md5) MapIdentity(JsonElement response)
    {
        var map=Text(response,"current_map");var md5=Text(response,"current_map_md5");
        if(string.IsNullOrWhiteSpace(map)||md5?.Length!=32||md5.Any(c=>!Uri.IsHexDigit(c)))
            throw new InvalidDataException("车端地图身份缺失或无效。");
        return (map,md5);
    }

    public async Task<NavigationTestStations> ReadStationsAsync(CancellationToken ct)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            _preview=null;
            // This is read-only and remains available while the new-work recovery gate is held.
            if(!_settings.Enabled||string.IsNullOrWhiteSpace(_settings.VehicleId)||!_options.Enabled||
                _options.GetConfigurationError() is not null||!_connections.TryGet(_settings.VehicleId,out var client))
                throw new InvalidOperationException("测试入口或指定测试车通信未启用。");
            using var lease=await _execution.EnterControlAsync(ct);
            using var before=await client.RequestAsync(1300,null,ct);
            var identity=MapIdentity(before.RootElement);
            using var stations=await client.RequestAsync(1301,null,ct);
            var rows=ParseStations(stations.RootElement);
            var (telemetry,_)=await client.PollAsync(ct);
            using var after=await client.RequestAsync(1300,null,ct);
            using var load=await client.RequestAsync(1022,null,ct);
            if(MapIdentity(after.RootElement)!=identity||Int(load.RootElement,"loadmap_status")!=1)
                throw new InvalidOperationException("读取期间车端地图变化或尚未加载成功，请重新读取站点。");
            var near=rows.Where(s=>AtStation(telemetry,s.X,s.Y)).ToArray();
            var suggested=near.SingleOrDefault(s=>s.Id==telemetry.CurrentStation)?.Id??(near.Length==1?near[0].Id:null);
            return new(_settings.VehicleId,identity.Map,identity.Md5,rows,suggested,AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow());
        }
        finally{_mutex.Release();}
    }

    private static async Task<string?> CheckArrivalAsync(SeerRobotClient client,AgvRobotTelemetry telemetry,NavigationTestRecord record,CancellationToken ct)
    {
        using var before=await client.RequestAsync(1300,null,ct);
        var identity=MapIdentity(before.RootElement);
        if(identity!=(record.RobotMap,record.RobotMapMd5))return "原任务报告完成，但车端地图身份已变化，不能确认本次到点。";
        var x=record.TargetX;var y=record.TargetY;
        if(x is null||y is null)
        {
            // Historical 3066 records contain no target coordinates. Reconcile them without resending.
            using var stations=await client.RequestAsync(1301,null,ct);
            var target=ParseStations(stations.RootElement).SingleOrDefault(s=>s.Id==record.TargetSite);
            x=target?.X;y=target?.Y;
        }
        using var after=await client.RequestAsync(1300,null,ct);
        using var load=await client.RequestAsync(1022,null,ct);
        if(MapIdentity(after.RootElement)!=identity||Int(load.RootElement,"loadmap_status")!=1)
            return "到点核对期间地图变化或尚未加载成功，请继续查询。";
        if(telemetry.LocalizationConfidence is not >=0.5||x is null||y is null||!AtStation(telemetry,x.Value,y.Value))
            return "原任务报告完成，但车辆位置或定位置信度尚未确认到达所选终点，请现场核对并继续查询。";
        return null;
    }
}
