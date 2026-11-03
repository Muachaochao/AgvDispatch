# 六层监控界面验证记录（2026-11-03）

本轮增加可逐层检查的监控界面及只读接口，原策略页继续保留推荐、成本和配置。没有增加现场启用或发送动作。

## 验证结果

| 范围 | 结果 |
| --- | --- |
| `AgvDispatch.sln` Release 构建 | 0 错误；保留已有 ImageSharp NuGet 安全公告警告 |
| `DispatchPipelineInspectionSmokeTest` | 25 项通过：六层与开关、准确总量及 100 条采样、未来重试、原命令/步骤/车辆责任、PLC 原采样与业务评估时间、未计算/未复核/过期、当前地图门禁、全过程数据库只读 |
| Web 单元测试 | 239 项通过，含新增监控辅助逻辑 8 项 |
| 六层监控隔离浏览器检查 | 25 项通过：逐层切换与深链、实际依据、搜索/异常筛选、冻结/历史/导出/恢复实时、缺失/未来/过期/断连及窄屏；全部请求无写操作 |
| 原优化集成回归 | 74 项通过 |
| 原发送前安全回归 | 30 项通过 |
| 测试工程引用兼容 | MapSwitchSmokeTest 与 ExecutionRecoverySmokeTest 各补两条已有服务源码链接，编译通过；未借此修改执行逻辑 |

后台隔离检查对读取前后的原任务、步骤、命令、租约、申请及事件逐项比对，确认没有创建、释放、认领或增加发送尝试。页面检查只访问合成 HTTP 夹具，未连接现场设备。

桌面、公共资源明细、执行恢复明细、冻结历史、窄屏及窄屏详情截图均已检查。当前记录时间、历史快照时间和任务原身份分别保留。

本地证据：`artifacts/pipeline-inspection-backend-tests.log`、`artifacts/pipeline-inspection-browser-final.log`、`artifacts/pipeline-monitor-solution-final.log`、`artifacts/pipeline-monitor-*SmokeTest.log` 及 `.codex-tmp/dispatch-pipeline-browser-1793689170532`，不随源码上传。
