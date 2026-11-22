using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Devices;

namespace AgvDispatch.Service;

// Read-only aggregation. Does not probe devices, submit tasks, release gates or change configuration.
public sealed class CommissioningQueryService(ISystemStatusSnapshotService system, IManualTaskCreationService manual,
    ICommunicationSettingsService communication, IExecutionRecoveryService recovery, IActiveMapRuntime map,
    IPlcSignalCache signals, IPlcConfigurationInspector inspector, HandshakeExecutionOptions handshake,
    DispatchTaskCreationOptions creation, DispatchTaskStepOptions steps, AgvCommandOutboxOptions outbox,
    IMapExecutionGate gate) : ICommissioningQueryService
{
    public async Task<CommissioningReport> ReadAsync(TrafficUserContext user, CommissioningQuery query, CancellationToken ct)
    {
        if (!user.Has(TrafficPermission.View)) throw new UnauthorizedAccessException("没有联调检查查看权限。");
        if (query.StationId==null || query.VehicleId==null || query.StationId.Length > 32 || query.VehicleId.Length > 32) throw new ArgumentException("工位或车辆编号无效。");
        var originalMap = map.Current;
        var status = await system.GetAsync(user, ct);
        var options = await manual.GetOptionsAsync(ct);
        var settings = await communication.GetAsync(user, ct);
        var restore = await recovery.GetAsync(ct);
        var now = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetUtcNow();
        var runtime = BackendBuildIdentity.Read(handshake, creation, steps, outbox);
        var checks = new List<CommissioningCheck>();
        void Add(string id,string group,bool valid,string title,string reason,string target,string action) =>
            checks.Add(new(id,group,valid?"Pass":"Blocked",title,reason,target,action,now));
        Add("database","后台",status.Database.Online,"数据库连接",status.Database.Message,"service","查看后台部署说明");
        Add("restart","后台",!settings.RestartRequired,"保存配置与活动配置",settings.RestartRequired?"保存配置尚未加载，需要受控重启后台后重新检查。":"后台报告无需重启。","service","查看重启流程");
        Add("map","地图",originalMap!=null,"正式运行地图",originalMap==null?"尚未激活运行地图；浏览器编辑稿不算运行地图。":$"{originalMap.MapId} / {originalMap.VersionId}","map","打开地图工作区");
        Add("gate","执行门禁",gate.CanStartNewWork,"地图/恢复执行门禁",gate.CanStartNewWork?"当前允许申请新任务，提交仍须重新核验。":"地图切换或恢复屏障尚未放行。","recovery","查看恢复处理入口");
        Add("recovery","执行门禁",restore.Released,"启动恢复批次",restore.Message,"recovery","查看恢复处理入口");
        foreach(var item in new[]{("creation",runtime.TaskCreationEnabled,"任务创建"),("dispatch",status.Safety.DispatchingEnabled,"任务调度"),("steps",runtime.StepExecutionEnabled,"步骤执行"),("outbox",runtime.OutboxEnabled,"车辆命令发送"),
            ("plc-communication",status.Safety.PlcCommunicationEnabled,"PLC通信"),("plc-write",status.Safety.PlcWritesAllowed,"PLC写入"),("agv-communication",status.Safety.AgvCommunicationEnabled,"车辆通信"),("agv-command",status.Safety.AgvCommandsAllowed,"车辆指令")})
            Add(item.Item1,"运行开关",item.Item2,item.Item3,item.Item2?"当前活动配置已启用；不代表设备业务条件通过。":"当前活动配置关闭；检查页不会自动开启。","service","查看后台运行配置说明");
        checks.Add(new("contract","握手","Notice","当前执行契约",handshake.Enabled?$"新契约启用：{handshake.PlcContractVersion}。配置声明不证明PLC程序已匹配。":"原执行器运行；持久化新契约未启用。","contract","查看握手时序与启用边界",now));
        if(handshake.Enabled)Add("contract-version","握手",handshake.PlcContractVersion==HandshakeExecutionOptions.Version,"契约版本匹配",$"配置 {handshake.PlcContractVersion}；软件支持 {HandshakeExecutionOptions.Version}","contract","查看握手契约");
        var station=options.Stations.FirstOrDefault(s=>s.StationId==query.StationId);
        var vehicle=options.Vehicles.FirstOrDefault(v=>v.VehicleId==query.VehicleId);
        if(options.Stations.Count==0)Add("station-configuration","工位",false,"工位映射配置","后台没有工位到取货站的映射，需先配置后重新检查。","task-settings","查看工位映射配置入口");
        if(options.Vehicles.Count==0)checks.Add(new("vehicle-observation","车辆","Unknown","车辆观察列表为空","没有后台车辆观察；先检查车辆配置及活动通信开关，再查看实时状态。","vehicles","打开车辆配置",now));
        Add("station-selection","工位",station!=null,"选择检查工位",station==null?"请选择当前后台配置中的工位。":$"{station.StationId} → {station.PickupSite}","selection","选择工位和车辆");
        if(station!=null)
        {
            Add("nine-step-config","工位",station.ConfigurationValid,"九步配置与PLC点位",station.ConfigurationValid?"九步静态配置校验通过，实际电平另见下表。":string.Join("；",station.PreviewSteps.SelectMany(s=>s.ValidationErrors.Select(e=>$"第{s.Sequence}步：{e}"))),"task-settings","查看任务参数配置入口");
            var stationReason=station.ConfigurationValid?station.Explanation:"九步前置配置未通过，详见上方缺项；本项不作为工位电平通过证明。";
            Add("station-available","工位",station.Available,"工位当前业务条件",stationReason+(!options.Available?" 全局创建条件："+options.Explanation:""),"signals","查看本工位信号");
        }
        Add("vehicle-selection","车辆",vehicle!=null,"选择检查车辆",vehicle==null?"请选择后台已观察到的车辆；未观察到时先检查通信配置。":vehicle.Label,"vehicles","打开车辆配置");
        if(vehicle!=null)
        {
            Add("vehicle-available","车辆",vehicle.Available,"车辆接单状态",vehicle.Explanation+(!options.Available?" 全局创建条件："+options.Explanation:""),"vehicle-status","查看车辆实时状态");
            var bindings=settings.Active.MapBindings?.Where(b=>b.VehicleId==query.VehicleId && b.LocalMapId==originalMap?.MapId && b.LocalMapVersion==originalMap?.VersionId).ToArray()??[];
            Add("vehicle-binding","车辆",bindings.Length==1&&!string.IsNullOrWhiteSpace(bindings[0].RobotMap)&&!string.IsNullOrWhiteSpace(bindings[0].RobotMapMd5),"当前版本车端地图绑定",bindings.Length==1?"核对当前版本绑定的地图/MD5；实际车端一致性须点击四段路线核验。":"当前运行版本缺少唯一车辆地图绑定。","map-binding","查看WPF地图绑定入口");
            Add("coordinates","车辆",bindings.Length==1&&bindings[0].CoordinatesVerified&&!string.IsNullOrWhiteSpace(bindings[0].ConfirmationNote),"坐标核定记录",bindings.Length==1&&bindings[0].CoordinatesVerified?"已保存核定记录；不是本次自动测量证明。":"缺少本版本坐标核定记录，不能据地图动画认定真实车位。","map-binding","查看坐标核定入口");
            if(station!=null)Add("pickup-route","路线",station.PickupReachableVehicleIds.Contains(query.VehicleId),"当前站点到取货点",station.PickupReachableVehicleIds.Contains(query.VehicleId)?"本地有向图存在到取货点路线；车端路径仍需显式核验。":"当前站点到取货点无有效本地路线。","map","检查航点及道路方向");
        }
        checks.Add(new("route-verification","路线","Unknown","车端四段路线与地图当前核验","检查页不自动访问车辆查询接口。选定工位和车辆后，点击四段核验；结果仅代表核验时的观察，提交会再次检查。","route","核对四段路线",now));
        var observed = new List<CommissioningSignal>();
        if(station!=null)
        {
            var names=new Dictionary<string,string>{["FullBoxRequested"]="满箱呼叫",["BoxPresent"]="箱体检测",["Faulted"]="工位异常",["CommunicationOnline"]="通讯状态",["FullBoxRequestAcknowledged"]="满箱呼叫响应",["FullBoxRemoved"]="满箱拉走",["EmptyBoxTaskAcknowledged"]="空箱任务响应",["EmptyBoxPlaced"]="空箱放到位",["AgvAtWaitingPosition"]="AGV到等待位",["AgvAtDumpingPosition"]="AGV到倒料位",["DumperReady"]="倒料机准备OK",["DumpingCompletedMayLeave"]="倒料完成允许离开"};
            foreach(var pair in TaskHandshakeSignals.Pickup.Select(v=>(Station:station.StationId,Variable:v,Write:Array.IndexOf(TaskHandshakeSignals.Pickup,v)>=4))
                .Concat(TaskHandshakeSignals.Dumper.Select(v=>(Station:options.DumperStationId,Variable:v,Write:v is "AgvAtWaitingPosition" or "AgvAtDumpingPosition"))))
            {
                signals.TryGet(pair.Station,pair.Variable,out var signal);
                var configurationError=inspector.GetSignalConfigurationError(pair.Station,pair.Variable,pair.Write);
                var fresh = signal?.Value is bool value ? TaskSignalPredicate.EvaluateFresh(pair.Station,pair.Variable,value,signal,now,handshake.StateMaxAgeSeconds) : null;
                var name=signal?.ChineseName??(pair.Variable=="Faulted"&&pair.Station==options.DumperStationId?"倒料机发生故障":names.GetValueOrDefault(pair.Variable,pair.Variable));
                observed.Add(new(pair.Station,pair.Variable,name,signal?.Address??"未配置",pair.Write?"调度":"PLC",signal?.Value,signal?.Quality.ToString()??"Unknown",signal?.UpdatedAt,
                    configurationError!=null?"Blocked":fresh?.Satisfied==true?"Pass":"Unknown",configurationError??fresh?.Reason??"没有有效采样，不能将未知按0处理。"));
            }
            Add("signals-config","信号",observed.All(s=>s.Status!="Blocked"),"13位点位及访问方向",observed.Any(s=>s.Status=="Blocked")?"存在点位配置或访问方向问题，详见信号表。":"配置检查通过；输出回读不代表PLC业务接收。","signals","查看13位信号");
            checks.Add(new("signals-fresh","信号",observed.All(s=>s.Status=="Pass")?"Pass":"Unknown","13位采样有效性",observed.All(s=>s.Status=="Pass")?"当前13位启用、Good且新鲜；本行不判断业务期望电平。":"存在缺失、禁用、质量无效或过期采样，详见下表。","signals","查看13位信号",now));
            foreach(var condition in new (string Id,string Station,string Variable,bool Expected,string Title)[]{("source-online",station.StationId,"CommunicationOnline",true,"来源工位通讯正常"),
                ("source-fault",station.StationId,"Faulted",false,"来源工位无故障"),("source-box",station.StationId,"BoxPresent",true,"来源工位箱体在位"),
                ("dumper-fault",options.DumperStationId,"Faulted",false,"倒料机无故障")})
            {
                signals.TryGet(condition.Station,condition.Variable,out var signal);
                var result=TaskSignalPredicate.EvaluateFresh(condition.Station,condition.Variable,condition.Expected,signal,now,handshake.StateMaxAgeSeconds);
                var dataValid=signal?.Value is bool value&&TaskSignalPredicate.EvaluateFresh(condition.Station,condition.Variable,value,signal,now,handshake.StateMaxAgeSeconds).Satisfied;
                checks.Add(new(condition.Id,"业务前提",result.Satisfied?"Pass":dataValid?"Blocked":"Unknown",condition.Title,
                    $"{condition.Station}/{condition.Variable} 期望{(condition.Expected?1:0)}；{result.Reason}","signals","查看13位信号",signal?.UpdatedAt));
            }
        }
        if(map.Current?.RuntimeId!=originalMap?.RuntimeId || options.MapId!=originalMap?.MapId || options.MapVersionId!=originalMap?.VersionId || settings.RuntimeMapId!=originalMap?.MapId || settings.RuntimeMapVersion!=originalMap?.VersionId)
            checks.Add(new("map-changed","地图","Blocked","采集窗口地图一致性","采集期间地图发生变化或接口版本不一致，请重新检查；不得沿用本次结果。","map","重新核对运行地图",now));
        return new(now,runtime,status.Service.Version,status.Service.StartedAt,originalMap?.MapId,originalMap?.VersionId,options.ParameterRevision,query.StationId,query.VehicleId,options,checks,observed);
    }
}
