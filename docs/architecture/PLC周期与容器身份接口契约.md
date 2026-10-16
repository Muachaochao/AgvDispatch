# PLC周期与容器身份接口契约

日期：2026-10-13。本文定义AGV侧拟接入的内部契约，不是已经确认的现场PLC地址或厂家API字段。现场尚未提供字段名、类型和返回样例，当前没有启用采集映射，没有新增PLC写入或车辆指令。

## 1. 字段定义

身份记录独立于现有布尔PLC信号缓存，不能把完成位、箱体存在位或界面人工输入转换成可靠身份。

| PLC记录字段 | 类型 | 含义与校验 |
| --- | --- | --- |
| SchemaVersion | int | 当前为1，其他版本拒绝自动核验 |
| StationId | string | AGV配置中的PLC工位编号，精确匹配 |
| TaskId | string | AGV主业务任务编号；适配器必须有可靠关联依据 |
| DeviceSessionId | string，最多64字符 | 控制器启动/运行世代编号；重启或计数器回绕必须换世代，不能使用上位机本次启动号替代 |
| CycleId | string，最多64字符 | 原动作周期编号；与DeviceSessionId组合后标识周期 |
| ContainerId | string，最多128字符 | 本周期关联的容器编号，不代表容器内货物是满是空 |
| OriginalBusinessStepId | string，最多64字符 | 原运输的AGV步骤StepId，厂家业务关联号使用该值；与主任务TaskId不同 |
| OriginalVendorTaskId | string? | 厂家原运输编号；没有可靠关联则不能达到自动核验状态 |
| Quality | SignalQuality | 必须Good；断连、读失败保留未知/错误质量，不能刷新旧值冒充新读取 |
| ObservedAt | DateTimeOffset | 上位机实际读取完成时间，带时区；不是设备时钟或业务更新时间 |
| IsCoherent | bool | 整组字段为同一周期的稳定读取；默认不能声称一致 |

车辆容器记录VehicleContainerIdentity使用SchemaVersion、VehicleId、LoadState、ContainerId、OriginalBusinessStepId、OriginalVendorTaskId、Quality、ObservedAt、IsCoherent。其中LoadState为Unknown=0、Empty=10、Loaded=20。Loaded表示车上承载容器，必须返回容器编号和原运输关联；Empty表示明确没有承载容器，ContainerId必须为空。Unknown包括未接入或不可判定，不能解释为空载。空载车辆仍需原运输状态、PLC握手和人工现场核对。

所有编号按原始字符串区分大小写、精确比较；周期号使用不透明字符串，避免整数截断或大小比较。上位机不猜测PLC计数器的含义。现场若没有控制器世代编号，需要先补可靠的世代协议；只用循环计数值无法区分断电重启后的旧动作。

## 2. 采集和关联要求

只读适配器要求整组身份一致读取；若PLC只能逐点读取，必须使用覆盖所有身份字段变化的快照版本锁/握手方案，读取前后确认世代、周期及快照版本不变且均处于稳定状态。只检查周期计数相同不能排除同周期内字段更新。只有现场协议能保证整组一致时才标记IsCoherent=true。主任务号、StepId、厂家编号不得由当前选中任务或当前最新命令猜测；必须来自设备实际关联或已验证的原运输映射。

恢复原运输优先使用取消请求固定的原StepId/厂家编号，否则使用当前运输步骤或当前PLC步骤之前最近的原运输步骤。工位选择沿用现有九步流程的PlcStation规则，不新增地址推测。适配器应发布对应原动作周期，不能返回一个无关的“最近动作”。

以下是拟议内部样例，不是现场返回值；枚举按当前默认JSON数值表示，Quality使用对应的Good值：

```csharp
new PlcExecutionIdentity("PLC-01", taskId, "BOOT-20261013-A", "1042", "BOX-001",
    originalStepId, originalVendorId, SignalQuality.Good, hostReadCompletedAt, true);
new VehicleContainerIdentity("AGV-01", ContainerLoadState.Loaded, "BOX-001",
    originalStepId, originalVendorId, SignalQuality.Good, hostReadCompletedAt, true);
```

ExecutionIdentityCache是内部采集边界；已有通用只读适配器框架，尚无现场协议驱动、远程写入接口或用户手填身份入口。确认现场协议后由只读驱动接入统一采集器，再发布到缓存。读取失败应发布非Good质量，不能仅停止刷新而永久保留有效状态。

## 3. 原执行基线和恢复门槛

ExecutionEvidenceJournal在已有AgvSendIntent/PlcWriteIntent写入设备之前，将当前PLC/车辆身份作为ExecutionIdentityBaseline附加到原意图JSON。记录CapturedAt，即使没有身份样本也记录缺失。原证据及既有JSON字段保留。

恢复读取当前步骤的首条执行意图；该步骤还没有意图时可读取其原运输的首条发送意图。SQLite按追加rowid选择首条，不依赖可能相同或回退的墙钟时间/随机GUID。首条缺失基线就维持缺失，后续重试的新样本不能补造原动作身份。历史数据库不会迁移生成虚假的基线。

核验状态：

| 状态 | 行为 |
| --- | --- |
| NotRequired | 完全未开始且无执行意图的请求沿用原安全判断，不用身份字段证明动作 |
| Unavailable | 身份未接入、关联缺失、载荷未知或原意图无基线；明确保留人工核对 |
| Blocked | 已接入样本质量/一致性/时间无效，或工位、任务、运输、车辆、周期、容器冲突；人工勾选不能越过 |
| Verified | 当前关联字段与原执行基线一致；仅证明身份对应，不证明动作完成、不自动勾选现场核对、不自动放行 |

当前样本必须属于本次恢复开始之后，不能晚于主机时间1秒以上，且满足现有MapSwitchSafetySettings.StateMaxAge。原基线必须在当时CapturedAt满足同样的采集质量、原子性和最大年龄。PLC启动世代、周期号、容器号变化均阻止沿用原动作。即使PLC与车辆一起变成另一容器，也拒绝匹配旧基线。

车辆明确承载容器时，处置表单的MaterialReference必须精确填写设备ContainerId；空载或接口未接入时继续填写现场核对说明。后台保留车辆停止/位置、原厂家任务、PLC握手、路权和人工三项核对门槛，字段匹配不代替这些门槛。取消关闭仍需车辆原任务结束确认、PLC握手清理与路权核对。

身份语义变化或质量变差撤销旧确认、增加修订号并保存VerificationRevoked审计。只有ObservedAt刷新且身份语义不变时不反复撤销。提交处置及最终放行都会重新读取身份，不能依赖旧界面摘要。恢复页面显示核验状态与设备报告的容器，冲突禁用处置按钮；JSON证据随现有原始证据导出保存。

## 4. 配置与联调顺序

Service/appsettings.json中RecoveryIdentity.RequireMachineIdentity默认false，当前维持未启用现场映射的人工流程；已有身份出现冲突时，即使false也拒绝恢复。true要求身份和原执行基线完整，缺失就阻止处置/放行。因此不能在没有现场字段和历史意图基线时直接启用严格模式。全场条目仅检查已接入样本及现场状态，不把有几条身份记录当成已自动核对整个现场。

下一步先收集PLC/AGV字段名、数据类型、周期产生/复位时机、设备世代规则、容器编码规则、业务StepId/厂家运输映射以及脱敏样例。实现只读适配器，检查原子读取、断连质量和主机时间。新任务在发送/写入前产生可靠基线后，再使用隔离现场联调并决定启用严格核验。旧任务缺基线维持显式人工处置或现场专项流程，不用重试自动填补。


## 5. 只读采集适配器框架与模拟联调（2026-10-13）

用户确认仍没有真实现场样例，因此本批实现协议无关的只读适配器和模拟联调，不填写任何PLC地址、AGV请求类型或厂家JSON字段，不注册模拟源到生产服务，不改变现有HSL/SEER采集及写入流程。生产配置如下：

```json
"ExecutionIdentityAcquisition": {
  "Enabled": false,
  "PollIntervalMs": 1000,
  "ReadTimeoutMs": 2000,
  "CoherentReadAttempts": 3
},
"RecoveryIdentity": { "RequireMachineIdentity": false }
```

采集轮询范围100–60000ms，读取超时50–60000ms，一致读取最多1–5次。源编号必须非空且同类型唯一。Enabled=true但未注册任何经确认的源时明确拒绝构造采集器，不会将“已配置开启”误认为有真实采集数据。只注册一种来源时仍无法自动核验缺失的另一来源；有源也不代表所有现场设备已覆盖。

### 只读边界与一致性

IPlcExecutionIdentityReader仅暴露StationId、ReadStampAsync和ReadFieldsAsync。驱动返回的PlcIdentityStamp包含DeviceSessionId、CycleId、SnapshotVersion、IsStable、Quality。适配器依次读取前版本、身份字段、后版本；两次版本完全一致、处于稳定态并质量Good，才可以发布IsCoherent。任何字段变化都必须使SnapshotVersion改变，版本在一次采集范围内不能发生ABA回绕。PLC写方必须在修改字段之前先标记更新中，完成后再发布新稳定版本；可使用奇偶序列锁或现场等价协议。驱动不能仅凭周期号、解析成功或三次碰巧读取相同就声称具备这一保证。

读途中改变版本会丢弃本次组合并有界重读。达到次数限制后发布Bad/IsCoherent=false。错误工位响应在预期工位下发布无效状态，不向其他工位缓存写入。字段版本不支持、质量不好或必要字段缺失也不能发布Good。

IVehicleContainerIdentityReader仅暴露VehicleId和ReadAtomicAsync，返回VehicleIdentityFrame。IsAtomic必须来自现场协议对整个容器/任务关联快照的保证，不能由JSON解析成功推导。错误车辆、空载但有容器、承载缺编号/StepId、未知枚举或非原子快照均无效。Unknown作为真实读取状态可以记录，但不能被恢复核验解释成Empty。关联原任务/运输由恢复核验服务和原意图基线检查，采集器不使用UI选中任务替设备补值。

两个读接口没有Write/Send/Cancel方法。它们应由未来PLC/AGV连接器在真实协议确认后实现；现有布尔PLC接口不够表达身份和一致性，当前不强行扩展其读写地址。主机ObservedAt在完成采集时生成，不接收设备时钟值。

### 失败、超时及服务生命周期

ExecutionIdentityAcquisition统一处理发布、失败和超时，ExecutionIdentityPollingService在后台按周期读取注册源；不同设备独立读取，一个源失败不阻止其他源。日志仅在状态变化时报告源编号和通用原因，不输出容器/任务原始响应。

断连、异常或超时把已有身份质量降为Bad并清除一致性，保留原编号与最后成功读取时间用于追溯，不刷新旧值冒充新样本。首读失败时只建立无效、未知的样本，不猜测容器或周期。服务取消/停止或运行中的采集配置关闭，将已运行源标记Disabled。

超时使用取消令牌及有界等待；每个源有独立读取门闩。驱动忽略取消时，不等待其返回阻塞全场轮询，也不会重复进入同一设备读取。该驱动返回之前后续轮询将超时；迟到响应不会写回缓存，只有下一次新的成功采集可恢复质量。真实驱动仍须实现连接超时、取消与断开释放，框架不会强杀第三方阻塞线程。恢复原周期质量后也不会自动恢复旧人工核对。

### 接入前必须确认

先提供真实字段及脱敏样例，确认SnapshotVersion覆盖全部字段、稳定态规则、世代与计数器复位/回绕、容器空载含义和原StepId/厂家编号关联。还需确认动作周期何时生成：如果PLC只在执行后才生成新周期，执行前的当前周期不能充当即将发起动作的基线，必须先约定预分配周期或可靠的指令关联握手。不能为满足核验而在驱动中推测未来周期。

实现现场只读驱动后通过DI注册IPlcExecutionIdentityReader/IVehicleContainerIdentityReader，先使用隔离采集检查实际映射、超时、断连和原子性。确认新任务在第一次发送/写入前取得可用基线，并完成真实现场联调后，才考虑开启RecoveryIdentity.RequireMachineIdentity。没有基线的旧任务仍走明确的专项人工流程。


### 本批验证结果

ExecutionIdentityAcquisitionSmokeTest共45项通过：默认关闭不读设备、启用无源/重复源/无效配置拒绝、PLC一致读取与有界重试、更新中版本拒绝、设备世代/周期复位、错误工位与车辆、载荷形态与版本质量、主机采集时间、断连与质量恢复、超时与不重入、迟到响应保护、驱动自行取消分类、服务取消/停止/运行关闭、采集中途停止后的发布屏障、DI默认构造与实际后台模拟轮询。

ExecutionRecoverySmokeTest共123项通过（上一批115项+本批8项），新增隔离数据库闭环验证：只读适配器发布身份、原发送意图保存基线、启动后重读关联、严格身份与人工确认共同通过、采集断连阻止放行并撤销旧确认、重新读取不自动重新确认、错误原运输关联拒绝、重启世代/周期复位拒绝、审计持久化且不发送/取消设备命令。ManualTaskCreationSmokeTest137项及原九步执行回归通过。Debug全解决方案0错误，现有ImageSharp依赖告警仍在。所有设备读取均来自测试模拟源，没有生产服务启动、PLC/实车读写或真实现场联调。
