# 铝屑回收调度系统

工业AGV回收调度系统，当前以Web操作界面接入C#调度后台，保留WPF工程与运维工具。仓库保存开发工程，不包含现场资料、生产数据或授权信息。

## 从 GitHub 获取工程

需要 Windows、.NET 10 SDK。首次克隆后执行：

```powershell
powershell -NoProfile -File tools/Initialize-LocalConfiguration.ps1
dotnet restore AgvDispatch.sln
dotnet build AgvDispatch.sln -c Debug --no-restore
```

初始化脚本只在缺少配置时创建安全示例，不覆盖已有配置。示例不配置现场点表，PLC/AGV通信、写命令及自动调度均关闭。现场点表、CAD、地图、协议档案、运行数据与真实appsettings由本地单独提供；缺少厂家档案的文档核对工具不能当作已通过。默认本地地图编辑不需要调度服务。

提交前执行 `powershell -NoProfile -File tools/Test-SourceUpload.ps1`。规则扫描只是辅助，仍需人工复核。依赖仍有已记录的ImageSharp安全告警，不能将成功构建视作无漏洞。

地图编辑入口、模式和保存方式见 [地图离线编辑与在线发布](docs/architecture/地图离线编辑与在线发布.md)。现场软件使用指南、联调记录及发布证据仅在本地保留，不随仓库上传。

当前项目依据（2026-10-13 用户确认）：对接快捷 AGV 厂家，使用 Robokit TCP。2026-09-20 之前的旧架构和协议资料不再作为需求或验收依据。旧 Okapi、MES HTTP 及多厂商方案不能用于推导当前必需功能。现场协议档案及其来源清单本地保留，不在本仓库分发。

## 最新版的构建与启动

完整构建使用 `tools/Build-CommissioningBundle.ps1 -WebPort 5188`，验证使用 `tools/Verify-CommissioningBundle.ps1`。每次输出独立目录，避免覆盖旧配置与数据。完整包内双击 `Start-Latest-Web.cmd`，等待后台和Web接入就绪后自动打开浏览器；重复点击复用本版进程。停止使用 `Stop-Latest-Web.cmd`，只核对并停止本入口记录的进程。入口不依赖系统安装.NET或PowerShell脚本执行策略。

一键入口强制关闭设备通信及执行，用于安全开发联调；不会导入旧数据库或现场配置。后台缺配置时如实显示缺项，可显式切换合成演示。日常入口与阶段边界见 [Web统一发布与固定入口](docs/architecture/Web统一发布与固定入口-20261018.md)。

## 技术栈

- C# / .NET 10 LTS
- Vue 3 / TypeScript / Vite / Three.js（Web操作界面）
- ASP.NET Core WebHost（本机HTTP/WebSocket到Named Pipe适配）
- WPF / MVVM / Prism（保留的工程与运维客户端）
- HandyControl
- SqlSugarCore / SQLite
- HslCommunication
- ACadSharp.Image（DWG/DXF转PNG/SVG）
- NLog
- LiveCharts2
- Windows Service

## 项目结构

详细目录、职责及故障定位请看 [项目结构说明](docs/architecture/项目结构说明.md)。源码已按“架构层 → 业务模块 → 功能职责”分类，现有命名空间和运行配置路径保持不变。

- `AgvDispatch.Web`：Web大屏、工程编辑、任务与联调检查
- `AgvDispatch.WebHost`：本机Web接入及完整版本一键启动
- `AgvDispatch.Wpf`：保留的工程配置与运维工具
- `AgvDispatch.Service`：后台调度Windows服务
- `AgvDispatch.Domain`：任务、工位、AGV等领域模型
- `AgvDispatch.Application`：PLC、AGV及数据库应用接口
- `AgvDispatch.Infrastructure`：SqlSugar与SQLite基础设施
- `AgvDispatch.Connectors.Plc`：HSL PLC适配层
- `AgvDispatch.Connectors.Agv`：AGV厂家协议适配层

AGV适配器已经接入Robokit TCP状态、导航和取消接口，但所有写命令默认关闭。PLC适配器已经接入HslCommunication 12.2.0，
使用西门子S7协议，支持BOOL点位读写、断线重连、写入回读校验和写入安全开关。

## PLC通信配置

PLC连接参数位于`AgvDispatch.Service/appsettings.json`的`Plc`节点，点位位于
`AgvDispatch.Service/config/plc-signals.csv`。CSV共包含305个CNC实例的2440个BOOL点位和
倒料机5个BOOL点位，字段包括S7地址、英文变量名、中文名称、工位编号、数据类型、方向、
读写权限、地址可信状态、启用状态、置位/复位条件和来源说明。

连接层支持HSL提供的以下西门子PLC系列：

- `S1200`
- `S1500`
- `S300`
- `S400`
- `S200Smart`
- `S200`

默认保持：

- `Enabled=false`：不连接现场PLC；
- `AllowWrites=false`：即使启用通信，也禁止写现场点位；
- CSV中CNC 1、CNC 2及倒料机地址标记为`Confirmed`并启用；
- CNC 3~305由数组步长推理，标记为`Inferred`且默认禁用，必须和TIA Portal导出表核对后再启用。

HSL授权码不写入源码或配置文件。运行服务的Windows账户需设置用户级环境变量：

```powershell
[Environment]::SetEnvironmentVariable('HSL_AUTHORIZATION_CODE', '<授权码>', 'User')
```

开发程序会先读取进程环境变量，再读取当前Windows用户环境变量。若以后将服务改为
`LocalSystem`等独立服务账户，需要给该服务账户或机器级环境变量配置同名授权码。

当前CSV使用西门子绝对地址，例如`DB1.DBX0.0`。S7-1200/S7-1500需要确认DB1关闭
`Optimized block access`或以其他方式允许绝对地址访问。S300/S400需核对`Rack`和`Slot`，
S200系列需要按现场参数配置`LocalTsap`和`DestTsap`。

联调建议按以下顺序逐级放开：先填写PLC系列和IP并设置`Enabled=true`，只验证读取；
输入稳定后核对全部CSV地址并逐批启用；最后在现场具备安全条件时将`AllowWrites`改为`true`。

如果电气工程师更新Excel点表，可重新生成CSV：

```powershell
python tools/generate_plc_signal_csv.py <点表.xlsx> AgvDispatch.Service/config/plc-signals.csv
```

### PLC后台采集

`PlcPollingService`在Windows服务中持续运行，不依赖任何WPF页面。它通过
`HslPlcGateway`按DB块批量读取已启用点位，将结果写入线程安全的`PlcSignalCache`，
并由`PlcConnectionSupervisor`统一判断连接状态。

关键配置：

- `PollIntervalMs`：正常采集周期，默认500ms；
- `ReadBlockSizeBytes`：S7批量读取分块大小，默认200字节；
- `InterruptedFailureThreshold`：连续失败达到该次数后判定中断，默认3次；
- `ReconnectDelayMs`：中断后的重连间隔，默认3000ms；
- `StaleAfterMs`：最后成功采集超过该时间后，缓存信号标记为`Stale`。

连接状态统一为`Disconnected`、`Connecting`、`Online`、`Degraded`、
`Interrupted`和`Faulted`。信号质量统一为`Unknown`、`Good`、`Stale`、
`Bad`和`Disabled`。通信失败后保留最后一个有效值，但质量会转为`Stale`或`Bad`，
业务层不能只读取BOOL值而忽略质量。

### 工位状态层

`StationStateService`把CNC工位的`FullBoxRequested`、`BoxPresent`、`Faulted`和
`CommunicationOnline`组合成统一业务状态。PLC信号变化时立即重算，对数据库中的未完成
任务则按`StationState:RefreshIntervalMs`（默认2000ms）批量刷新。状态快照可供调度服务、
报警服务和前端共同使用，页面不再自行拼接PLC条件。

只有在通信与四个必要信号质量均为`Good`、工位无故障、检测到箱体、收到满箱呼叫且没有
未完成任务时，`CanCreateTask`才为`true`。满箱但无箱体会判为信号冲突；同一工位存在两条
及以上未完成任务会判为重复任务冲突并禁止继续自动调度。倒料机握手暂不纳入CNC工位状态，
后续应使用独立的倒料机状态服务。

### 任务自动创建与PLC应答

`TaskCreationCoordinator`监听工位`CanCreateTask`从`false`变为`true`的上升沿，并在
SQLite事务内创建任务。数据库使用局部唯一索引保证每个工位最多只有一条未完成任务，
因此重复事件、并发请求和服务重启都不会产生重复任务。

新任务先以`Created`状态持久化，然后向PLC写入`FullBoxRequestAcknowledged=1`。写入及
回读校验成功后，任务才进入`WaitingForVehicle`；写入失败时保留`Created`任务并记录错误，
后台按照`TaskCreation:AcknowledgementRetryIntervalMs`自动重试。`TaskCreation:Enabled`
可以单独关闭自动建单；现场写入仍同时受`Plc:AllowWrites`安全开关保护。

### AGV任务编排

`DispatchTaskOrchestrator`按优先级和创建时间选择`WaitingForVehicle`任务，从在线空闲车辆中
优先选择电量较高的车辆。一个满箱业务任务在创建事务内拆成9个持久化步骤：前往CNC、
取满箱、送往倒料等待位、等待倒料机Ready、进入倒料位、倒料、送空箱返回、放置空箱和PLC收尾。PLC满箱应答成功时只把
第一步激活为`Ready`，不会提前跳到后续流程。

车辆分配不会直接调用AGV网络接口，而是在同一SQLite事务中占用任务、绑定车辆、把首步骤
转为`CommandPending`并插入`agv_command_outbox`。`AgvCommandOutboxDispatcher`再独立领取
命令并调用AGV适配器；失败采用指数退避，达到最大次数后任务转`Faulted`。每条命令使用稳定
的`StepId`作为跨重启幂等键。若服务在“AGV已接收、数据库尚未确认”之间断电，重启后会回收
超时的`Sending`命令并以相同幂等键重试，因此厂家适配器必须先查询再创建，不能盲目重复下发。

`DispatchTaskStepCoordinator`持续读取数据库中的唯一`Ready`步骤，并根据PLC回读推进闭环：
箱体检测从1变0后确认满箱取走；到达倒料等待位后置位`AgvAtWaitingPosition`并等待
`DumperReady`；进入倒料位后置位`AgvAtDumpingPosition`并等待
`DumpingCompletedMayLeave`；空箱返回后等待`BoxPresent=1`，最后复位CNC握手信号并完成任务。
所有输入必须为`Good`质量才允许推进，所有置位信号必须经过下一轮PLC回读确认后才会复位，
避免在一个PLC扫描周期内产生看不见的短脉冲。步骤处理异常会同时写入任务和步骤错误字段。

后续三段运输——送往倒料等待位、从等待位进入倒料位、空箱返回CNC——均沿用原任务车辆，
并通过同一Outbox发送。任务在等待PLC握手期间仍占用车辆和并发槽位，不会把该车错误分配给
第二个任务。`DumperWaitingSite`和`DumperDumpingSite`必须分别映射到AGV地图中的两个站点。

真实地图站点尚未配置，因此`Dispatching:Enabled`默认是`false`。启用前必须填写
`DumperWaitingSite`、`DumperDumpingSite`，并在`StationPickupSites`中建立CNC工位编号到AGV
地图站点编号的明确映射。每一段实际可行路径都必须写入`Dispatching:Routes`，键格式为
`起点->终点`，值为包含起点、途经点和终点的连续站点数组；默认不允许自动退化成直达路径。
`StepActions`把运输步骤映射为`Wait`、`JackLoad`、`JackUnload`或`JackHeight`动作。

厂家创建采用Robokit `3066 robot_task_gotargetlist_req`。路径中的中间子任务号由`StepId`
稳定派生，最后一个子任务号就是`StepId`。每次发送前先用`1110`按该编号查询；发送响应超时后
也会进行多次确认查询，解决“机器人已接收但服务未收到响应”的重复派单风险。任务取消采用
`3003`并明确携带`taskId`，不会误取消车辆上的全部任务。`3051`只保留为单车现场调试模式；
厂家文档明确提示它不适合多车调度场景，配置为`GotargetCommissioning`时系统强制只允许一台车。

现场下发受两级独立开关保护：`Dispatching:Enabled=false`时不产生新调度命令，
`Agv:AllowCommands=false`时厂家适配器拒绝导航和取消命令。地图和动作参数验证完成、单车
空载联调通过前，两者都应保持`false`。`AgvCommandOutbox`可配置轮询周期、发送租约、最大
尝试次数和重试延迟。

### Robokit多车状态采集

`SeerRobotClient`按照Robokit TCP/IP协议直接连接单台机器人，`AgvConnectionManager`管理
多台机器人客户端，`AgvPollingService`为每台车运行独立采集循环，结果进入线程安全的
`AgvStateCache`。命令接口已经实现，但默认由`Agv:AllowCommands=false`硬性禁止发送。

协议报文头固定16字节并使用网络字节序：同步字节`0x5A`、协议版本、请求序号、数据长度、
报文编号和6字节内部区域。采集使用批量状态接口`1100`并显式关闭激光点云，另用任务状态
接口`1110`获取最近完成及全部未完成任务。位置`1004`、电量`1007`和报警`1050`均包含在
批量状态中。请求和响应按序号关联，响应编号必须等于请求编号加10000。

`Agv:Enabled`默认关闭。联调前填写5台车的实际IP，确认Robokit TCP端口和版本：RBK 3.4
使用协议版本1，RBK 3.5使用版本2。采集包含位置、定位置信度、当前站点、电量、阻挡、
急停、导航信息、任务列表、报警、响应时间、连续失败次数和数据质量。

启用命令前还需选择`NavigationCommandMode`。生产多车模式使用`SpecifiedPath`（3066），且
只有车辆在线、状态质量为`Good`、无急停/错误报警、缓存当前站点等于路径起点时才允许创建。
`CommandConfirmationAttempts`和`CommandConfirmationDelayMs`控制发送结果不确定时的确认查询。

### 地图、路径规划与交通资源

`MapGraph`将AGV地图拆为节点、带方向路段和交通资源。节点类型包括普通路径点、工位、路口、
等待位和充电位；路段包含距离、限速、启用状态和是否双向。单行道使用`Bidirectional=false`，
不要同时创建反向边。窄道、路口、单线区、工位占用区和充电位通过`ResourceIds`挂到节点或
路段上，同一个互斥区域的所有入口和双向路段必须引用同一个资源编号。

`ConfiguredRoutePlanner`使用Dijkstra按`距离/限速`计算当前最短可行路径。已被其他业务任务
占满的资源会从本轮图搜索中排除，因此有备用通道时会自动绕行，没有通路时任务保持等待，
不会生成不完整的3066路径。相邻节点必须与Robokit地图中真实直接相连。

`TrafficResourceManager`把路权写入SQLite的`traffic_resource_lease`表，并在事务内按资源容量
互斥申请。路权以业务任务和运输步骤标识，可跨服务重启恢复。同一业务任务进入下一运输段时，
先取得新路径资源，再释放上一段资源，避免交接瞬间出现空锁。最终PLC收尾成功后释放该任务
全部路权；故障、取消或仅仅超过租约时间都不会自动解锁，必须在确认车辆实际位置安全后由后续
人工处置功能释放。`ExpiresAt`当前仅用于显示心跳是否过期，不是自动解锁依据。

地图配置位于`appsettings.json`的`Map`节点，默认`Enabled=false`。基本结构示例：

```json
{
  "Map": {
    "Enabled": true,
    "LeaseDurationSeconds": 120,
    "Resources": [
      { "Id": "NARROW-01", "Type": "NarrowAisle", "Capacity": 1 }
    ],
    "Nodes": [
      { "Id": "PARK-01", "X": 0, "Y": 0, "Type": "Station", "Enabled": true, "ResourceIds": [], "AgvSiteId": "LM-PARK-001" },
      { "Id": "CROSS-01", "X": 8.5, "Y": 0, "Type": "Intersection", "Enabled": true, "ResourceIds": ["NARROW-01"], "AgvSiteId": "LM-CROSS-001" }
    ],
    "Edges": [
      { "Id": "E-001", "From": "PARK-01", "To": "CROSS-01", "Distance": 8.5, "SpeedLimit": 1.0, "Bidirectional": true, "Enabled": true, "ResourceIds": ["NARROW-01"] }
    ]
  }
}
```

CAD只作为底图和坐标来源，不直接充当可调度地图。应统一坐标原点与单位后，从CAD/Robokit地图
提取路径中心线、站点和关键路口，生成上述拓扑；界面可继续叠加CAD的2D或2.5D视觉层，但实际
调度始终使用节点、路段与资源图。

### 可编辑多地图工作区

WPF左侧导航已增加“地图管理”。地图不是直接修改运行中的全局配置，而是按地图项目独立保存，
每张地图包含底图来源、坐标系、单位、坐标换算比例以及站点、路段、交通资源三类可编辑对象。
界面可创建和切换多张地图，实时预览拓扑，并分别维护单行/双向路段、限速、路段启用状态、
路口、窄道、单线区、工位区和充电区等互斥资源。

地图采用`Draft -> Published -> Archived`版本模型：编辑只影响草稿；保存时运行完整地图校验；
存在阻断错误时可以继续保存草稿，但不能发布。发布成功后，旧发布版转归档，当前草稿成为候选
基线，同时自动复制出下一版草稿；只有再次明确执行“激活运行地图”才会切换调度运行版本。

项目资料中的`B07-1F布局图0425.dwg`已可登记为底图源，文件头识别为`DWG-AC1032`
（AutoCAD 2018格式）。后台使用`ACadSharp.Image`直接读取DWG/DXF并生成PNG预览和SVG图层文件，
不依赖本机AutoCAD。复杂CAD转换通过独立后台队列串行执行，Named Pipe提交后立即返回，服务重启
会恢复Pending/Processing任务；转换失败只把该地图标记为Failed，不影响PLC、AGV和诊断服务。
首次转换提取全部CAD图层，后续可在页面勾选墙体、设备轮廓、道路中心线等白名单图层重新生成。

地图页面支持2点或更多控制点标定，使用二维相似变换统一计算比例、旋转和平移，并显示RMS误差。
建议至少使用3个分布较远的现场基准点验证误差。标定完成后开启“画布标点”，可直接在CAD预览上
创建调度站点；界面会把点击位置从CAD坐标换算为地图米制坐标。CAD坐标若为毫米，初始元数据使用
`Unit=mm`、`CoordinateScale=0.001`。当前阶段仍以2D为准，不先做3D，因为3D不会提高路径规划与
互斥控制的可靠性。

画布编辑支持网格吸附、已有站点吸附、连续画路、结构级撤销/重做（含`Ctrl+Z`/`Ctrl+Y`）。
常用快捷键为`Ctrl+S`保存草稿、`Ctrl+D`复制所选节点及内部拓扑、`Ctrl+M`合并两个节点、
`Ctrl+A`全选、`Ctrl+L`最近邻连线、`Delete`删除选择；这些操作与工具栏按钮走同一套命令和撤销栈。
没有CAD底图时也能从零建图：空白画布使用独立且可配置的米制宽高，首个节点不依赖已有坐标范围，
因此连续标点、拖动和缩放不会因节点范围变化而跳动。节点画笔可直接选择Waypoint、Station、
Intersection、WaitingPoint或ChargingPoint；新路段使用工具栏当前的单/双向与限速参数。

批量几何工具包括左/右/顶/底对齐、水平/垂直中线对齐、水平/垂直等距分布、左右90度旋转、
水平/垂直镜像、节点类型批量应用、最近邻自动连线、两点直角连线和按中心角闭合环线。拓扑清理只
删除自环与重复路段并合并资源绑定，不会擅自删除孤立工位。几何变换会重算相邻路段长度；全部操作
进入撤销栈。画布按节点类型着色，路段直接显示`▶`或`↔`方向标记，选中路段使用绿色高亮。
先在“交通资源”页选择路口或窄道资源，再开启“资源涂刷”，点击节点或路段即可绑定/取消绑定；
当前资源会以橙色高亮。站点同时维护业务节点`Id`和机器人地图`AgvSiteId`：任务与工位配置使用前者，
路径下发前统一转换为后者。运行站点缺失映射或多个节点重复绑定同一Robokit站点时禁止发布。
表格属性修改同样进入撤销栈；修改节点ID会联动所有路段端点，修改交通资源ID会联动节点、路段和
仿真封锁条件，避免重命名后产生隐蔽的悬空引用。“路径仿真”页可以针对当前未保存草稿选择起终点、
临时封锁任意资源，并展示业务节点路径、实际下发的Robokit站点路径、成本和占用资源；仿真只在内存
运行，不激活地图，也不会向AGV发送命令。

“发布前检查”会针对所有启用的工位、等待点和充电点建立有向路径矩阵，统计全部起终点组合的可达性，
并同时检查缺失或重复的Robokit站点映射。不可达项全部展示，可达项保留最多200条样例，避免大型厂区
把界面撑到数万行。“版本差异”会比较当前草稿和最近已发布版本，分别列出节点、路段、交通资源的
新增、删除和修改；任何后续编辑都会使旧的仿真及检查结果失效，必须重新计算，防止误用过期结果。

站点表支持`Ctrl/Shift`多选后整体平移、复制拓扑片段和批量删除。复制会保留所选站点之间的内部路段、
限速、方向和资源绑定，但清空新节点的`AgvSiteId`，防止直接制造重复厂家站点映射。路段编辑支持在
中点拆分并继承原路段属性，也支持一键反转单行方向；拆分后的新节点默认为Waypoint，发布前仍需
根据Robokit地图补齐必要的站点映射。

地图草稿使用独立`EditToken`进行乐观并发控制。两个操作员同时打开同一草稿时，第一个保存成功后会
刷新令牌；第二个仍持有旧令牌的窗口会被Service明确拒绝，项目元数据和拓扑更新在同一事务中回滚，
不会发生静默覆盖。收到冲突提示后应刷新地图，再基于最新草稿重新应用修改。

地图页面可以分别备份当前数据库草稿或已发布版本。备份始终由Windows Service读取确定版本生成，
保存在`Map:BackupPath`（默认`data/map-backups`），内容包含备份格式版本、导出时间、操作账号、地图
项目元数据、完整版本定义和校验报告；同时生成同名`.sha256`校验文件。未点击“保存草稿”的WPF
内存修改不会混入备份，便于审计、恢复和交付时确认文件未被改动。
Service生成的备份JSON可以直接填入地图导入框重新读取，但仍只会进入可撤销预览；操作员确认校验和
版本差异后，必须手动保存草稿并重新发布、激活，恢复过程不会绕过现有安全门禁。

地图画布提供互斥的“选择/拖动”模式：空白处拖框可以多选节点，拖动任一已选节点会整体移动当前
选择，并遵守网格吸附；一次完整拖动只写入一条撤销记录。鼠标滚轮按`50%~500%`缩放，中键拖动画布
平移，“复位视图”恢复100%和零偏移。绘点、连续画路、资源涂刷与选择拖动模式互斥，避免一次鼠标
动作同时修改两类地图对象。

Service持久化地图操作审计，记录地图、版本、操作账号、时间、成功状态和摘要。目前覆盖创建、保存、
保存并发冲突、发布、激活、CAD底图生成、坐标标定、导入预览和备份导出；WPF“操作审计”页可查看
当前地图最近300条记录。审计数据位于独立`map_operation_audit`表，不依赖普通文本日志。

“版本历史/恢复”页只先加载轻量版本摘要，选择版本后才通过Named Pipe读取完整不可变快照并计算与
当前编辑区的差异。点击“加载到编辑区”只替换本地可撤销内容，不直接更新数据库；仍需保存草稿、
校验、发布和激活。这样既可恢复旧发布版，也不会把历史查看误操作变成现场运行地图切换。

选中两个节点可执行合并：当前节点作为保留目标，其他节点的资源绑定和路段会迁移过来，自环与重复
路段自动清理，相关距离重新计算。若两个节点绑定了不同Robokit站点，系统拒绝合并，必须先人工确认。

交通资源支持可选多边形`Boundary`。选择至少三个地图节点后，可自动按中心角排序生成路口、窄道、
单行区或充电区边界，并在CAD底图上半透明显示；当前资源以橙色高亮。开启“边界顶点”模式后可直接
拖动当前资源的各个顶点，拖动遵守网格吸附且一次完整拖动只产生一条撤销记录。JSON使用坐标点数组，
CSV使用`x:y|x:y`格式并随导入比例一起缩放。发布校验会拦截少于三个点、非法坐标、零面积共线边界
和多边形自交；资源绑定的节点或路段中点落在边界外时给出覆盖告警，防止涂刷范围与图形范围不一致。
边界用于地图展示和发布校验，运行期路权仍以节点、路段显式绑定的资源ID为准，不根据图形碰撞临时猜测。

地图页会通过Named Pipe每秒读取Service中的AGV快照，并仅在当前项目与运行地图一致时叠加车辆位置和
航向。绿色表示定位正常，橙色表示数据陈旧或定位置信度偏低，红色表示超过允许的路网偏差；未激活
地图或查看非运行地图时不会叠加，避免把不同坐标系的数据画到一起。“实时车辆”页同时展示原始坐标、
角度、置信度、最近路段、偏差距离和判定原因。偏离路网判定由Service统一计算，因此后续报警和调度
门禁可以复用，WPF只负责显示。车辆图标叠加在可编辑草稿上仅供联调观察，不代表草稿已成为运行版本。

Service会把有效定位按“采样周期＋最小位移”降采样写入`agv_track_point`，每条记录固定关联车辆、运行
地图和运行版本，并保存定位置信度、最近路段、偏差距离和地图判定。静止车辆仍按较长心跳周期留点，
避免完全丢失停车区间；后台每天清理超过保留天数的轨迹。Named Pipe单次查询限制为24小时和最多
10000点，防止UI误操作拖垮服务。“实时车辆”页选择车辆后可加载最近若干分钟轨迹，蓝线表示正常区段，
红线表示偏航区段，并支持播放、暂停和清除。

偏航不会因单次定位抖动立即报警。只有连续超过`OffRouteAlarmDelaySeconds`才生成`AGV_OFF_ROUTE`
报警，恢复到允许偏差内后自动恢复报警。调度器在创建新任务前复用相同定位判定，仅从定位正常车辆中
选车；已经执行的任务不会被这一门禁直接取消，仍由既有任务状态机和人工处置流程控制。

`TrafficFlowMonitor`把定位正常的车辆投影到最近路段，形成Free、Occupied、Congested、Stalled四级
实时状态。有任务或上报阻挡的车辆持续小于最小移动距离才会进入Stalled，普通停车不会仅因坐标不变
被判定为死锁。同一路段多台滞留车辆持续超过阈值后生成`TRAFFIC_DEADLOCK_CANDIDATE`报警和处置建议；
系统不会据此自动删除路权。动态状态会生成非负路段代价，路径规划仍先遵守交通资源硬锁，再在可用
路径中绕开已占用、拥堵或滞留路段。“调度监控”页展示每条路段的车辆、状态持续时间、动态代价以及
潜在死锁建议，人工解锁由有权限的单个操作员现场确认后执行，并保留完整审计。

发布与激活分开：发布只产生经过校验的候选版本，不会立即改变运行路径；点击“激活运行地图”后，
后台才原子替换路径图、交通资源目录和持久化资源定义。存在活动任务或任何交通资源租约时禁止切换。
运行版本使用独立`ActiveVersionId`固定，即使继续发布下一版也不会偷换运行地图；服务重启后恢复同一
活动版本。通信诊断页面会显示活动地图和版本编号。

所有地图读写通过Named Pipe进入Windows Service，WPF不直接写SQLite。地图编辑账号需要
`View`和`ManageMap`权限；只有查看需求的账号只授予`View`。完成现场坐标标定、拓扑校验和
单车空载验证前，`Map:Enabled`、`Dispatching:Enabled`和`Agv:AllowCommands`仍应保持关闭。

### ProfControl 地图曲线功能对标（2026-10-08）

本次使用本地 XML 文档、地图样例和 ILSpy 定向检查参考地图功能，独立实现圆弧和三次贝塞尔路径。
功能对照与后续差距见 `../项目资料/ProfControl地图模块对标.md`。

进入“地图管理 → 打开地图编辑器”，在“基础绘制”选择直线、圆弧或三次贝塞尔，再使用连续画路。
新曲线先生成默认弯曲形状；点击“曲线控制柄”，点选路段后拖动黄色控制点调整。也可以在“路段”表
选择已有路段，选择形状后点击“设置当前路段形状”，再填写精确控制点坐标（单位米，格式 `x,y;x,y`）。
圆弧只有一个**弧上过点**，它不是圆心；三次贝塞尔有两个控制点。共线、重合或非有限坐标的圆弧会报错，
不能保存发布；控制点数量错误和无效数值也会被阻断。控制点操作纳入撤销/重做。

路段长度、路径成本、车辆偏差、路段交叉与资源边界检查共同使用曲线几何。复制、整体平移、90°旋转、镜像、
中点拆分及单向反转会保留曲线形状；拆分按参数中点生成两个等价子曲线，贝塞尔两段长度可能不同。
总览与编辑画布均显示曲线。同一对节点的不同几何路径在拓扑清理时会分别保留；发布时会阻断同方向
平行曲线，因为当前 Robokit 下发只使用站点序列。不同路径需增加独立中间站点后再发布。

标准 JSON 新增可选 `geometryKind`（`Line` / `CircularArc` / `CubicBezier`）与
`controlPoints: [{"x": ..., "y": ...}]`。旧文件缺少这两个字段时仍为直线。CSV 可选列使用
`GeometryKind` 与 `ControlPoints`，CSV 控制点格式为 `x:y|x:y`，与画布属性表格式不同。
导入比例尺同步缩放节点和控制点；曲线长度由几何重新计算。曲线样例为
`AgvDispatch.Service/config/map-template/curved-map.example.json`。

参考软件 `.rpj/.rpjx` 项目、多对象图层、切线连续性约束、点云与三维仿真尚未兼容。
实际导航仍通过已有 Robokit 站点路径协议，画布曲线应与机器人自身地图中的路径一致。
本次未修改现场通信开关。使用曲线地图需同时更新 WPF 与 Windows Service，避免旧服务丢弃新字段。

回归检查：`tools/MapEditorSmokeTest` 覆盖编辑、变换、控制柄与保存回读；
`dotnet run --project tools/MapTrafficSmokeTest -- --geometry-only` 覆盖曲线几何、导入、路径规划、校验及定位。

### 地图导入与上线校验

`MapImportService`支持两种明确格式：标准地图JSON，或包含`nodes.csv`、`edges.csv`、
`resources.csv`的目录。模板位于`AgvDispatch.Service/config/map-template`。CAD应先只保留AGV
中心线、站点和管制区，再按模板导出；如果CAD单位为毫米而调度坐标使用米，配置
`CoordinateScale=0.001`。Robokit地图应由厂家导出工具或转换脚本整理为相同标准JSON，当前不
直接猜测或解析未提供样例的厂家私有二进制地图。
`nodes.csv`和标准JSON均支持`AgvSiteId`字段，建议直接填写Robokit地图中的真实站点名；逻辑节点
编号可保持稳定，即使机器人地图站点重命名也只需更新映射。
地图页面可将标准JSON文件或三CSV目录导入当前编辑区。读取和校验由Windows Service执行并要求
`ManageMap`权限；导入结果先作为可撤销的本地预览，只有操作员再次点击“保存草稿”后才写入SQLite，
因此选错文件或坐标比例时可以直接撤销，不会覆盖当前发布版或运行版。

使用外部地图包时配置：

```json
{
  "Map": {
    "Enabled": true,
    "ImportPath": "config/factory-map",
    "ImportFormat": "CsvDirectory",
    "CoordinateScale": 0.001,
    "CoordinateTolerance": 0.01,
    "ValidationReportPath": "data/map-validation-report.json",
    "PositionStaleSeconds": 5,
    "MinimumLocalizationConfidence": 0.5,
    "MaximumRouteDeviationMeters": 0.75,
    "OffRouteAlarmDelaySeconds": 10,
    "TrackSamplingIntervalMs": 1000,
    "TrackMinimumDistanceMeters": 0.05,
    "TrackStationaryHeartbeatSeconds": 10,
    "TrackRetentionDays": 30,
    "TrafficMonitorIntervalMs": 1000,
    "CongestionVehicleThreshold": 2,
    "StationaryDistanceMeters": 0.05,
    "CongestionStationarySeconds": 10,
    "DeadlockDetectionSeconds": 30,
    "OccupiedEdgePenalty": 5,
    "CongestedEdgePenalty": 30,
    "StalledEdgePenalty": 120
  }
}
```

`MapValidationService`在地图投入调度前检查：重复或过近节点、无效路段、悬空引用、孤立节点、
多个断开的路网、单行方向导致站点不可达、重复路段、未保护的几何交叉、互斥资源重复圈选、
未使用资源、资源边界自交以及绑定对象越界。报告会写入`ValidationReportPath`；存在任何`Error`时
服务拒绝加载该调度地图。
这意味着导入成功不等于允许派车，必须先把校验报告中的阻断项处理完。

### 交通资源监控与人工解锁

“调度监控”页面现在每2秒读取交通资源租约，显示资源、AGV、任务、任务状态、获得时间、最近
心跳和过期状态。心跳超过`ExpiresAt`只显示“已过期（仍锁定）”，不会释放路权。服务启动时
会把当前地图资源定义同步到`traffic_resource_definition`，因此WPF可以同时识别空闲资源和
历史租约引用的旧资源。

WPF不再打开SQLite，也没有直接释放路权的接口。页面只通过本机Named Pipe连接Windows
Service；后台用管道连接的Windows登录身份做鉴权，客户端不能自行填写或伪造操作人。未知账号
默认没有任何权限。Named Pipe的Windows ACL允许已认证本机用户连接，但每项读写仍必须通过
应用权限校验。

人工解锁采用单人授权：拥有`SubmitManualRelease`权限的操作员选择明确租约、填写至少5字符
原因、确认AGV已完全离开并输入`RELEASE 资源编号`。后台事务内读取租约和任务，任务必须为
Completed、Faulted或Cancelled；活动任务、缺失任务、错误确认文本和重复解锁均拒绝。
保存操作人审计、删除租约和写入`Executed`历史在同一个SQLite事务中完成，任一步失败则全部
回滚。无需第二人批准。旧PendingApproval记录读取时失效；旧ReviewRelease接口保留兼容但
只返回“流程停用”，不会释放资源；必须重新进行现场确认。

权限在Service的`appsettings.json`中配置。开发配置仅给`CHAOCHAO\ChaoChao`授予
`View + ManageMap + ManageConnections`，不具备人工解锁、报警确认或管理员权限。生产部署前必须删除该开发账号，
改为真实的`域\账号`或现场计算机账号并按最小权限配置：

```json
"Security": {
  "PipeName": "AgvDispatch.Service",
  "ApprovalTimeoutMinutes": 15,
  "Users": [
    {
      "Enabled": true,
      "WindowsIdentity": "FACTORY\\agv-operator-a",
      "DisplayName": "AGV操作员甲",
      "Permissions": [ "View", "SubmitManualRelease" ]
    },
    {
      "Enabled": true,
      "WindowsIdentity": "FACTORY\\map-engineer",
      "DisplayName": "地图工程师",
      "Permissions": [ "View", "ManageMap" ]
    }
  ]
}
```

可用权限为`View`、`SubmitManualRelease`、`AcknowledgeAlarm`、`ManageConnections`、
`ManageMap`和`Administer`；`ApproveManualRelease`仅保留旧配置兼容，不再用于解锁。WPF只需要配置相同的
`Service:PipeName`，不再配置数据库路径。后台数据库路径仍由Service的`Database:Path`或
`AGV_DISPATCH_DATABASE_PATH`控制。

### 运行总览地图与车辆参数

顶栏高度已缩为52；直接显示上海时区日期、中文星期和秒级时间，不再折叠或展开日历；
启动先进入用户登录界面，通过验证后加载业务页面。右上角设置内含主题切换、当前账号/角色与
切换登录，不再显示系统连接状态。沿用Windows账号鉴权，新增管理员/操作员/监控员角色
模板，并将经Windows验证且被Service授权的身份用于后续Named Pipe请求。未配置的账号
不能登录调度系统，登录失败保留原身份；旧Permissions配置兼容且不自动提权。
现场角色配置及测试范围见[登录与角色配置](tools/Login-Roles.md)。

总览以后台当前激活地图的指定版本绘制站点和直线/圆弧/Bezier路段，每3秒刷新AGV位置、
朝向与状态。总览仅保留运行地图和当前业务任务两块；地图自适应占据除任务列表外的主要
空间，不显示额外统计卡片和通信卡片。地图内右侧在线AGV面板默认收起，收起后返还地图
宽度；展开按钮或点击在线车辆可查看参数。面板只列在线且未过期车辆，即使未定位也可选；显示电量、
定位置信度、位置、朝向、导航状态、车端任务/报警数量、急停、阻挡、心跳及响应耗时；
本次新增可选速度、角速度、充电、电压、电流和温度字段，按机器人实际返回显示。
失联/过期标记为灰色旧位置；后台不可用清空数据；草稿不作为运行地图，非米制地图不与
机器人米制坐标直接叠加。现场还需保证上位机拓扑与机器人地图坐标对齐。

协议核对及版本限制见[Robokit-Overview-API](tools/Robokit-Overview-API.md)。新增参数不增加
高频TCP请求、不采集激光点云，也不增加逐条状态日志。仍未开启任何现场控制开关。

### 通信诊断快照内容

“通信诊断”页面通过Named Pipe每2秒读取一次`SystemStatusSnapshot`，不直接调用PLC、AGV
客户端或SQLite。快照包含：Windows Service版本/PID/运行时间、数据库连通性和响应耗时、PLC
连接状态和轮询耗时、2445个点位的Good/Stale/Bad/Unknown/Disabled统计、每台AGV的连接质量、
心跳、电量、站点、导航状态、任务数和报警数、当前活动任务、Named Pipe请求统计及当前Windows
身份。页面同时显示PLC写入、AGV命令、地图和自动调度等安全开关，便于现场联调时确认系统仍处于
只读状态。

### 报警规则与生命周期

`AlarmRuleEngine`默认每2秒评估一次PLC连接和点位质量、工位故障/信号冲突、AGV连接/低电量/
急停/阻挡/厂家报警、故障任务以及交通资源租约心跳过期。PLC或AGV采集被配置为禁用时不会生成
相应离线报警，避免开发和停机状态产生报警风暴。

报警按`RuleKey`去重，同一故障持续存在时只更新最近观察时间；首次出现为`Open`，有
`AcknowledgeAlarm`权限的Windows账号可以填写处置说明并转为`Acknowledged`，但确认不会消除
报警。只有规则条件真正消失后，后台才自动转为`Recovered`。发生、确认、恢复时间和确认账号均
持久化到`alarm_record`，WPF“报警记录”页面可切换活动报警和历史记录。

### 任务中心、取消与现场恢复

任务中心通过本机 Named Pipe 查询和操作 Windows Service，不直接打开 SQLite。
列表支持任务状态筛选、任务/工位/车辆搜索、分页和自动刷新；离开页面取消请求并停止刷新。
详情显示九步骤时间线、站点、车辆动作、PLC 等待值与质量、厂家任务、命令重试及路权。
这些是观察快照，不能代替后台执行条件判断。

支持手动创建任务：选择工位和车辆、预览九步、逐步骤检查 PLC 信号映射、车辆动作与路线；
不完整配置禁止提交。任务参数页支持配置四段运输动作、取货与倒料站点及每步超时阈值，
保存复用后台校验与并发版本检查，未结束任务存在时禁止修改执行参数。
当前步骤显示耗时、等待原因和超时提示；超时提示本身不释放路权或推进物理步骤。

取消请求先冻结后续步骤，后台向 Robokit 查询并取消原运输任务，确认原运输结束后进入现场核对。
现场恢复复核车辆停车、箱体位置、PLC 握手和路权处置，并记录操作人、原因及证据。
人工操作由服务端鉴权与状态机门禁控制，不提供绕过核对的通用强制完成按钮。
周期号、容器编号及原运输关联已有接口契约和模拟采集适配，真实字段映射尚未接入；
身份采集与严格核验默认关闭。详细限制见 [PLC周期与容器身份接口契约](docs/architecture/PLC周期与容器身份接口契约.md)。

处置历史支持操作类别、时间范围、恢复批次筛选和分页，可按当前筛选导出历史与完整证据。
导出支持进度查询、主动取消及过期缓存清理。实现和验证范围见
[任务流程代码对标](docs/architecture/ProfControl任务创建代码对标.md)。

### AGV与设备监控

AGV管理与设备监控已接入后台只读快照，显示多车位置、电量、充电遥测、任务关联、厂家报警，
以及多 PLC 连接、工位状态、原始点位质量与倒料机握手。未知、禁用、断连和过期数据明确区分。
页面刷新复用缓存，不直接向车辆或 PLC 发起动作。充电遥测不表示已实现充电桩控制或自动充电。
现场配置与验收顺序见 [现场联调顺序](docs/commissioning/现场联调顺序.md)。

### 交通生命周期、地图联动与等待队列

调度监控显示路权申请、持有、等待和释放，关联任务、运输步骤、车辆、路线与阻塞资源。
地图联动高亮任务路线、持有资源和阻塞车辆，并按地图版本与定位质量限制显示。
等待队列展示等待起点、持续时间、任务优先级，支持阻塞资源与最低优先级筛选、排序和分页。
等待分级默认60秒预警、300秒长时间等待，通过后台`TrafficWaitingAlerts`配置；界面显示阻塞观察与最近解除时间。
未知起点及时钟异常不估算；解除为调度观察记录，超时提示不自动解锁。详见[等待分级与阻塞观察](docs/commissioning/交通等待分级与阻塞观察.md)。
排序只改变展示顺序，不代表已实现公平排队派车。

正常步骤交接和业务收尾按状态释放资源；车辆故障、取消待核对或租约心跳过期时保留占用。
几何动态区域锁、沿路径滚动申请与逐段释放仍待完善。细节见
[交通管制代码对标](docs/architecture/ProfControl交通管制代码对标.md)。

逐段释放已建立只读证据契约与评估门禁，65项模拟边界检查通过；未接真实进度源、未实际释放资源。
车体包络、厂家经过进度及定位误差界须核定，不能从站点或单点定位推导。见[证据契约](docs/architecture/逐段释放证据契约与门禁.md)。

### 导航与配置兼容

当前菜单入口均注册真实业务页面；通用占位页仅保留中性说明，不提供实时业务数据或功能承诺。
通信配置的 `FromLegacy` 仅从当前 S7/Robokit appsettings 初始化独立连接配置，不启用旧协议。
`DumperSite` 作为缺少 `DumperWaitingSite` 时的兼容后备值保留，倒料位仍需单独配置。
系统设置已提供多 PLC/AGV 连接配置；任务执行参数在任务中心编辑，其他设置以已实现页面为准。

### 九步任务与两车冲突模拟联调

`tools/DispatchIntegrationSmokeTest` 将真实 WPF 创建/查询、Named Pipe、后台编排、Outbox、路权与
Robokit TCP 模拟端串联，验证九步PLC条件、两车冲突等待、正常释放和继续执行。2026-10-13通过56项检查。
PLC仅在网关与缓存边界模拟，后台单轮受控执行，不代表真实S7或独立进程部署验收。
运行及证据见 [模拟联调记录](docs/commissioning/九步任务与两车冲突模拟联调.md)。

`tools/SeerFaultSmokeTest` 增加断连、无响应、半帧、迟到响应、错误序号以及Outbox确认失败重试检查，
2026-10-14通过39项。使用本机TCP模拟和隔离SQLite；未确认期间保持路权、重试沿用原步骤编号。
后续任务与联调限制见 [持续开发进度](docs/commissioning/持续开发进度.md)。

`tools/TaskCancellationProtocolSmokeTest` 通过真实3003/1110模拟取消确认延迟、丢响应、厂家拒绝与缺失记录，
2026-10-14通过101项；取消后九步冻结，原运输终结只进入现场核对，路权保持。
详见 [取消协议模拟联调](docs/commissioning/取消协议模拟联调.md)。

`tools/ServiceRestartSmokeTest` 使用实际后台独立进程、临时配置与数据库，验证重启恢复屏障、
原命令与路权保持；2026-10-14通过26项，设备通信关闭。
前端测试进程使用真实任务页面及Named Pipe；范围见 [独立进程重启恢复联调](docs/commissioning/独立进程重启恢复联调.md)。

`tools/HostedDispatchSmokeTest` 使用独立测试后台宿主及真实HostedService连续周期完成两车九步与冲突，
2026-10-14通过18项。该批修复后台循环继承启动数据库异步上下文导致的连接共享异常，
服务已更新发布；范围及回归见 [连续周期联调](docs/commissioning/连续周期独立进程模拟联调.md)。

### 系统设置：多PLC / 多AGV连接（2026-10-09）

“系统设置”已改为真实通信配置页面，分PLC连接、AGV连接、当前PLC连接/保存审计三个页签。
可增删PLC与车辆，编辑IP、端口、启用采集、PLC系列/Rack/Slot/TSAP、CSV点表和采集超时参数。
Robokit协议版本和AGV采集周期目前为全车共用；不支持一组配置混用不同协议版本。
已删除侧栏固定的“运行模式 / 联调准备”装饰卡片。

多PLC使用独立HSL实例、监督器、轮询循环和通信锁；公共缓存按全局唯一工位编号合并。
写信号依工位归属路由到目标PLC，一台PLC失败只改变所属工位的信号质量。
不同PLC可以使用同样的DB地址，但工位编号不能重复。每台PLC绑定单独CSV点表，路径必须在
Service的`config`目录中；启用前需有至少一个启用点位。当前配置校验上限为64个PLC和100台AGV，
不是经过现场压测验证的性能承诺。

读取/保存配置通过Named Pipe，保存与TCP测试需`ManageConnections`权限；开发账号仅增加该专项
权限，不获得管理员或人工解锁权限。服务端校验设备编号、地址、端口、周期、点表归属和配置版本。
存在未结束任务（含Faulted）时拒绝修改，操作账号由Windows管道身份确定，不接受客户端伪造。

保存采用临时文件+原子替换，文件位于数据库目录中的`communication-settings.json`，包含最近100条
保存审计。保存不会热切换正在运行的连接，也不会自动重启服务；页面显示是否需要重启，后台下次
启动加载此文件，优先于旧`Plc`与`Agv`连接参数。CLI修改旧`Plc`/`Agv`连接参数同样不会覆盖这个文件。
尚无文件时从旧appsettings配置初始化。PLC写入、AGV命令和自动调度安全开关仍独立由旧配置控制，
本页不提供开启动作/写入的按钮。重启前应确认处于安全停机状态。

“测试TCP连接”最多等待3秒，只建立并关闭TCP连接，不发送S7或Robokit报文。端口可达不等于PLC
授权、DB访问或车辆协议验证通过；真实通信质量仍须在通信诊断和现场只读联调中确认。
“刷新运行状态”保留未保存编辑；“重新加载”会丢弃未保存编辑。

`tools/CommunicationSettingsSmokeTest`覆盖多PLC归属、隔离质量、权限、配置冲突、保存审计和TCP
测试不发报文；`tools/ConnectionSettingsUiSmokeTest`使用`AgvDispatch.SettingsSmoke`隔离管道和临时
数据库验证加载、增添配置、保存不热切换、服务重启后恢复两PLC/六AGV配置及页面渲染。
这些属于模拟/只读配置验证，不是多台现场PLC/AGV通信验收。

### 日志与发布包同步

默认仅记录Info及以上，成功状态查询、页面导航和周期心跳为Debug且不落文件。
相同警告/错误60秒内去重。后台单文件约10MiB、客户端约5MiB轮转，各保留最多10个归档、
最长7天；异步写入及单条日志可能令文件略超过阈值，这不是磁盘配额。独立业务审计表不受文本日志去重影响。

先分别对Service和WPF执行`dotnet publish -c Release`，再运行`tools/Update-Release.ps1`。
脚本更新发布包程序和NLog配置，不删除旧文件，不覆盖appsettings、已有点表或运行数据。
本次旧发布包仅有Database配置，已补齐安全开发默认值；生产部署必须明确设置真实Windows账号，
且在现场联调前保持PLC/AGV通信、现场写入和自动调度关闭。

2026-10-08恢复专项测试依赖时，NuGet报告传递依赖`SixLabors.ImageSharp 3.1.12`存在安全告警。
需单独升级CAD图像依赖链并回归DWG/DXF转换；尚未在本次任务中心开发中解决。

## 开发运行

使用Visual Studio打开`AgvDispatch.sln`，将`AgvDispatch.Wpf`设为启动项目即可运行上位机。

后台调度服务在开发阶段可以直接以控制台方式启动：

```powershell
dotnet run --project AgvDispatch.Service/AgvDispatch.Service.csproj
```

逐段释放后台只读上下文检查已完成58项隔离SQLite验证，生产默认关闭；证据绑定原命令载荷、记录路线、地图版本及租约，采集期间状态变化拒绝结果。尚无现场源、公开检查端点或实际释放动作，发送时有序路线快照已保存并禁止常规UPDATE改写。

发送路线快照已在原Outbox准备事务持久化，并有SQLite更新保护；31项专项、58项只读检查及响应丢失/两车九步/重启回归通过。旧记录缺快照不补造历史，地图内容变化拒绝旧证据。实际逐资源释放仍待模拟事务与审计验证。

后台检查凭据已支持签发和只读查询，31项验证通过；最长两秒、原帧期限限制、重启/时钟回退/原状态变化失效，签发审计同事务。后台凭据消费事务已实现且默认关闭，未接公开IPC/自动调用；现场启用仍待真实证据及几何参数核定。

后台消费事务通过33项隔离模拟：新两帧、原租约时期精确删除、终点保留、审计/结果同事务及跨实例历史重放。生产开关固定默认关闭，无现场源或自动调用。

逐段释放现在同事务同步剩余请求资源，避免将他车后来占用已过路段误显示为原车阻塞。独立模拟进程31项、消费35项及检查器58项通过；生产开关关闭，未现场启用。

部分释放后的恢复已增加限制：缺重启后可信周期/进度核验时，当前原运输不能直接续接或放行；页面区分已释放与剩余。25项新验证和123项恢复回归通过，包含两次私有真实Service.exe重启，无现场连接。

几何区域锁前置只读预检通过45项：直线车体扫掠、曲线保守范围、接触/间隙、无效输入与预算截断。默认关闭，无自动加锁或释放，不代表已完成动态几何锁或现场避碰。
