# 当前工程向 ProfControl 地图桥接

该工具把本项目的规范化地图和工程方案转换为 ProfControl 公开 API 的 `APIV2MapType`，可在隔离的 ProfControl 测试实例中生成站点、车辆、直线和贝塞尔路径。

## 使用

只生成并核对转换包：

```powershell
powershell -NoProfile -File .\Convert-ProjectToProfControl.ps1 `
  -MapPath '..\..\..\发布包\运行数据\后台5199\merged-runtime-new-map.json' `
  -PlanPath '..\..\..\发布包\运行数据\engineering-center\draft.json' `
  -VehicleWidthMeters 0.52 `
  -VehicleLengthMeters 0.8
```

显式下发到隔离 ProfControl：

```powershell
powershell -NoProfile -File .\Convert-ProjectToProfControl.ps1 `
  -MapPath '..\..\..\发布包\运行数据\后台5199\merged-runtime-new-map.json' `
  -PlanPath '..\..\..\发布包\运行数据\engineering-center\draft.json' `
  -VehicleWidthMeters 0.52 `
  -VehicleLengthMeters 0.8 `
  -BaseUri 'http://127.0.0.1:10001' `
  -Send
```

`-Send` 默认要求目标 ProfControl 没有已有地图对象，避免重复添加。目标不是干净实例时必须先重启隔离实例；`setMap` 是追加接口，`-Force` 也不能覆盖非空地图；脚本现在一律在写入前拒绝非空目标。

也可以直接读取 `5199` 当前选中的完整仿真实例，不再手工指定地图和方案文件：

```powershell
powershell -NoProfile -File .\Convert-ProjectToProfControl.ps1 `
  -SimulationBaseUri 'http://127.0.0.1:5199' `
  -VehicleWidthMeters 0.52 `
  -VehicleLengthMeters 0.8 `
  -BaseUri 'http://127.0.0.1:10001' `
  -Send
```

该模式会从 `/api/simulation` 同时读取实际地图、地图版本、车辆数和各车起点。当前全厂方案生成 647 个可通行导航节点、305 个 CNC 业务工位和 5 辆车；补充 CAD 完整部署参考版还会把 25 个码头、5T、总控和终端工艺点作为不参与派车的参考站显示，再加 8 个业务分区标签，因此 ProfControl 可出现 680 个站点。三车四工位方案应生成 29 个导航节点、4 个业务工位和 3 辆车。

项目地图采用 Y 轴向上，ProfControl 地图采用 Y 轴向下；桥接默认自动反转 Y，并在报告中记录 `invertY=true`。如果以后导入的地图本身已经是 ProfControl 坐标系，可显式传入 `-InvertY $false`。

## 已转换

- 当前地图节点转换为 ProfControl 站点。
- `CadReferencePoint` 转换为无连线的参考站点，只显示 CAD 中的码头、5T、总控和终端设备，不代表可派车停站。
- 工程方案中的启用车辆转换为 ProfControl `Agv`，使用方案中的起点。
- 普通路段转换为 `Line`。
- 有两个控制点的 `CubicBezier` 转换为 `Bezier`。
- 输出源地图、方案、转换包 SHA256 和对象计数。
- 默认车辆尺寸使用用户确认的 `800 mm × 520 mm`；可通过参数覆盖。

## 未转换

- 交通资源多边形、容量和边界：ProfControl `setMap` 公开结构没有对应字段。
- 路段方向与单向/双向属性：公开结构没有对应字段。
- 当前工程的 PLC 点表、业务握手、正式调度器、任务协议和故障注入：地图桥接不会迁移这些内容。
- 车辆控制参数、加减速、电量与实际取放货动作：需要在 ProfControl 对象属性或仿真配置中继续绑定。

因此该桥接可用于地图、车辆初始位置和可视化核对；不能把“地图导入成功”当作业务仿真已经等价。

## 实时状态桥

按以下顺序操作：

1. 启动隔离 ProfControl，并确认 `http://127.0.0.1:10001/swagger/index.html` 可访问。
2. 用上面的地图桥接命令下发当前工程地图。
3. 运行 `发布包\启动最新版Web.cmd`，打开 `http://127.0.0.1:5199/#/simulation-center`。
4. 在“环境与地图”拉取当前工程，创建干净实例，再点击“启动仿真环境”。
5. 另开一个 PowerShell 窗口运行下面的状态桥。

状态桥命令：

```powershell
powershell -NoProfile -File .\Sync-SimulationToProfControl.ps1 `
  -SimulationBaseUri 'http://127.0.0.1:5199' `
  -ProfControlBaseUri 'http://127.0.0.1:10001' `
  -EvidenceDirectory '..\..\artifacts\profcontrol-bridge\runs'
```

状态桥每 500 ms 读取一次 `5199/api/simulation/live` 的 `device.cars`，同步：

- 当前世界坐标
- 当前角度
- 空载/载货状态
- 当前任务或等待原因
- 当前站点的简短车辆标签

车辆名称使用短标签，例如 `AGV-02 · 载货执行`、`AGV-03 · 等待`；空闲车辆只显示车号。任务 GUID 和站点不会铺满地图。状态桥只负责显示同步，不会向 5199 回传位置，也不会修改调度任务、PLC 握手或路权。

`/api/simulation/live` 只返回新鲜设备遥测和场景摘要，不重复序列化 647 节点地图、305 工位点表、历史报告和恢复指导，适合 305 工位满规模测试时持续刷新动画。旧版 WebHost 尚未提供该路由时，状态桥会自动回退到 `/api/simulation`。

每次运行会在 `EvidenceDirectory` 下创建独立目录：

- `metadata.json`：数据源、坐标比例及轴变换
- `state-events.jsonl`：车辆位置、角度、载荷、任务、等待原因和场景状态的时间序列
- `report.json`：场景结束或状态桥正常退出时保存的正式仿真报告

如果只需要在场景结束时停止状态桥，可加 `-ExitOnScenarioComplete`。

调试时可以使用单次输出，不修改 ProfControl：

```powershell
powershell -NoProfile -File .\Sync-SimulationToProfControl.ps1 `
  -InputPath '.\artifacts\status-sample.json' `
  -DryRun `
  -Once
```

默认 `ProfControlCoordinateScale=0.01`，对应 ProfControl 当前地图比例。状态桥只更新 `10001` 隔离实例，不参与正式派车、PLC 写入或路权判断。

车辆遥测 `device.cars.angle` 和目标 `changeAgvAngle` 均使用弧度；Y 轴反转时角度取反，不做度到弧度的二次换算。`Test-BridgeGeometry.ps1` 覆盖东南西北、跨周归一化及无效角度。缺失或非有限的位置/朝向不会写入显示端。

全厂隔离测试请把 `SimulationBaseUri` 指向该次控制台实际端口（例如 `59130`），记录地图和方案身份，不能把日常真车遥测混作虚拟车辆证据。使用当前 800×520 mm 车型时显式传入 `-VehicleLengthMeters 0.8 -VehicleWidthMeters 0.52`；地图转换报告记录本次使用的尺寸。

默认车体长 0.8 m、宽 0.52 m。`Test-ProjectConversion.ps1` 可在 Windows PowerShell 5.1 和 PowerShell 7 验证参考点/分区层、长宽、轴变换及稳定标识；只转换临时夹具，不写显示端。场景尚在运行时，单次状态桥保留采样，不提前请求不存在的最终报告。
