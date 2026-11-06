# 当前工程向 ProfControl 地图桥接

该工具把本项目的规范化地图和工程方案转换为 ProfControl 公开 API 的 `APIV2MapType`，可在隔离的 ProfControl 测试实例中生成站点、车辆、直线和贝塞尔路径。

## 使用

只生成并核对转换包：

```powershell
powershell -NoProfile -File .\Convert-ProjectToProfControl.ps1 `
  -MapPath '..\..\..\发布包\运行数据\后台5199\merged-runtime-new-map.json' `
  -PlanPath '..\..\..\发布包\运行数据\engineering-center\draft.json' `
  -VehicleWidthMeters 0.8 `
  -VehicleLengthMeters 0.52
```

显式下发到隔离 ProfControl：

```powershell
powershell -NoProfile -File .\Convert-ProjectToProfControl.ps1 `
  -MapPath '..\..\..\发布包\运行数据\后台5199\merged-runtime-new-map.json' `
  -PlanPath '..\..\..\发布包\运行数据\engineering-center\draft.json' `
  -VehicleWidthMeters 0.8 `
  -VehicleLengthMeters 0.52 `
  -BaseUri 'http://127.0.0.1:10001' `
  -Send
```

`-Send` 默认要求目标 ProfControl 没有已有地图对象，避免重复添加。目标不是干净实例时必须先重启隔离实例；`-Force` 只用于明确理解重复对象风险后的测试。

## 已转换

- 当前地图节点转换为 ProfControl 站点。
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
  -ProfControlBaseUri 'http://127.0.0.1:10001'
```

状态桥每 500 ms 读取一次 `5199/api/simulation` 的 `device.cars`，同步：

- 当前世界坐标
- 当前角度
- 空载/载货状态
- 当前任务或等待原因
- 当前站点的简短车辆标签

车辆名称会显示为类似 `AGV-02 · 任务 TASK-001 | 载货 | @LM4`，任务、载荷和站点均来自 5199 的隔离仿真状态。状态桥只负责显示同步，不会向 5199 回传位置，也不会修改调度任务、PLC 握手或路权。

调试时可以使用单次输出，不修改 ProfControl：

```powershell
powershell -NoProfile -File .\Sync-SimulationToProfControl.ps1 `
  -InputPath '.\artifacts\status-sample.json' `
  -DryRun `
  -Once
```

默认 `ProfControlCoordinateScale=0.01`，对应 ProfControl 当前地图比例。状态桥只更新 `10001` 隔离实例，不参与正式派车、PLC 写入或路权判断。
