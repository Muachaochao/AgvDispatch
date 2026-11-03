# 六层派车执行链实现与验证（2026-11-03）

主工程采用快捷 AGV / Robokit TCP。依据本轮用户确认，将已存在的滚动匹配能力整理为实际六层服务，并贯穿首段派车、后续运输与发送恢复。现场原始配置、通信数据和工程方案不提交到代码仓库。

## 实现与界面

1. PLC 呼叫队列：沿用原持久化任务及步骤，去重后保留原呼叫时间、优先级和 PLC 应答责任；窗口读取由 `DispatchQueueWindowService` 负责。
2. 候选筛选：`DispatchCandidateFilter` 执行健康、电量、车辆池、区域、路线与占用约束，只输出候选和估算，不改变路权或命令。
3. 滚动全局匹配：纯计算 `DispatchAssignmentOptimizer`，保留 2～5 秒周期、有限窗口、候选裁剪、稀缺车辆保护和路线缓存；已派任务及实际路权责任固定。
4. 交通路权：`DispatchTrafficReservationService` 协调实际资源申请、容量、公平排队和交接。只读候选查询不再隐式释放；完成清理由执行周期显式调用。
5. 安全复核：`IDispatchPreSendSafetyService` / `DispatchPreSendSafetyService` 同时用于传统派车、全局派车、后续运输登记前及 Outbox 领取前。复核真实地图、原任务和步骤、车辆、PLC 条件及当前步骤的实际路权。
6. 执行与恢复：原子登记时再次确认车辆未被其他任务占用；同时间戳下只有真正更新成功的调用可以认领发送；超时恢复只回收原领取。已有物理发送尝试沿用原步骤编号向厂家核对，不以车辆仍在原起点或已消耗的 PLC 条件阻断原编号核对。

滚动派车页面新增固定六层状态、观察时间、数量、原因、相关入口和任务受阻层。无观察、断连、过期、停用和阻止分别显示。持久化等待请求不会因没有已授予路权而被误标为空闲。

同一任务同一车辆的其他步骤路权不能替代当前步骤路权；最终复核明确校验 `OwnerStepId`、任务、车辆、地图、资源和路径申请身份。

## 验证

所有自动化动作均在独立 SQLite、合成地图、假网关或隔离浏览器中执行，未用真实车辆/PLC 验证发送。

| 检查 | 结果 |
| --- | --- |
| `AgvDispatch.sln` Release 构建 | 通过，0 错误；保留既有 ImageSharp NuGet 安全公告警告，未在本次调度变更中更换图像依赖 |
| `DispatchPlanningLayersSmokeTest` | 34 项通过，含六层状态、并发观察、持久化等待、只读边界及错误步骤路权拒绝 |
| `DispatchPreSendSafetySmokeTest` | 30 项通过，含实际全局编排至安全层至 Outbox、PLC 改变、原编号恢复及原子去重 |
| `DispatchOptimizationIntegrationSmokeTest` | 72 项通过 |
| `DispatchTrafficFairnessSmokeTest` | 43 项通过，含公平性、实际路权和纯读/显式清理 |
| `TrafficLifecycleSmokeTest` | 41 项通过，含交接、事务、完成清理、IPC 及布局 |
| `MapSwitchSmokeTest` | 93 项通过 |
| `ExecutionRecoverySmokeTest` | 228 项通过 |
| `TwoVehicleOutboxSmokeTest`、`DispatchWorkflowSmokeTest` | 通过，旧封路夹具改为独立车辆，避免复用尚未闭环的车辆责任 |
| Web 单元测试 | 231 项通过 |
| Web 隔离浏览器检查 | 15 项通过，截图检查通过 |

## 日常入口升级

升级前备份 `发布包/运行数据`，继承 `后台5199` 数据库与通信设置及 `Web工程方案`。只用原入口自身的拥有者记录停止旧进程，不结束其他程序。日常仍使用 `http://127.0.0.1:5199/`，根启动器、停止器及 `最新版Web.json` 指向同一批次。

发布后检查实际版本、后台连接、六层 API、真实页面和数据保留结果；不因发布自动授予现场发车、PLC 写入或单次导航许可。模式保持现有影子比对配置；全局匹配由策略配置启用。

实际升级结果：

- 发布目录 `发布包/SixLayerDispatch_20261103_r2`，实际批次 `commissioning-20261103-091049`。发布校验 1226 个初始文件 SHA256 及三组件版本一致；固定入口已指向此目录。
- 数据备份 `发布包/运行数据备份/before-six-layer-20261103-091347`，包含全部原运行数据、SQLite 一致性备份及原根入口。
- 升级前后均为 87 个任务（67 完成、20 取消）、783 个步骤、317 条 Outbox 命令、9 个地图工程、50 个地图版本、54410 个轨迹点、0 个实际路权租约；SQLite `quick_check=ok`。
- 14 份原通信/任务等 JSON 配置及 Web 工程方案文件哈希逐一一致，工程方案存储标识未变。
- 5199 `/api/config` 返回新批次，`/api/snapshot` 为已连接，`/api/dispatch-optimization` 返回正确顺序的六层及 `Shadow`；持久化队列为空，业务派车及发送仍按原配置停用。
- 真实时间的本机无头浏览器只读打开页面，核对六层文案、现场门禁和后台已连接；截图已检查。首次虚拟时间截图出现人为推进时钟造成的“数据过期”，最终证据改用真实时间采集。

本地证据保存在 `artifacts/six-layer-*` 及 `.codex-tmp/live-six-layer-realtime-*`，不随源码提交。本轮自动化结果不替代真车及现场 PLC 验收。
