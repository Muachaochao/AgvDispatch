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
- 车辆控制参数、加减速、电量与任务动作：需要在 ProfControl 对象属性或仿真配置中继续绑定。

因此该桥接可用于地图、车辆初始位置和可视化核对；不能把“地图导入成功”当作业务仿真已经等价。
