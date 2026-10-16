# 部分释放后的恢复与真实服务重启

从DispatchingSystem运行 `dotnet run --project tools/PartialReleaseRecoverySmokeTest`。25项；唯一临时SQLite，用真实消费组件先产生两条已释放资源审计和剩余终点。普通在线定位/原任务Running模拟证明直接续接仍被拒绝；最终放行也独立检查，不能绕过。界面数据ResourceSummary区分已释放与剩余，证据保留原提交、控制器周期及进度关联。

满足原有停在目标站和原任务完成条件仍可到站补记，下一PLC步骤保持恢复屏障。取消确认后关闭仍要求原人工路权处置，不自动释放剩余终点。

复制当前Debug Service.exe到唯一私有部署目录，覆盖私有配置并用命令行强制关闭AGV/PLC通信/写入及派单/发送/步骤/身份采集。两次实际启动通过唯一Named Pipe查询恢复状态，确认已释放/剩余记录、原命令及原租约保留，新恢复批次、未核对拒绝放行。只结束测试创建并持有的子进程，保留临时数据、boot JSON和日志。没有实车/S7或现场地址使用。

当前限制：恢复流程尚未接入重启后可信控制器周期/原命令进度及包络核验，存在当前运输步骤逐段释放历史时不支持ContinueOriginal，不能只靠重启前帧或普通Running/定位推导不会返回已开放路段。现场PLC身份采集/严格核验仍关闭；不把模拟字段当作厂家字段。

既有ExecutionRecoverySmokeTest 123项回归通过，完整Debug编译0错误。证据见docs/commissioning/evidence/20261014-partial-recovery。
