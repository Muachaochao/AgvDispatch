# 地图传输记录与门禁验证

从DispatchingSystem运行`dotnet run --project tools/MapTransferLedgerSmokeTest`。

真实DatabaseInitializer/MapTransferLedger/MapTransferRecoveryBarrier/MapExecutionGate/MapSwitchService及车辆缓存参与。测试源图为官方6号样例副本，后台地图/车辆/厂家期望是明确模拟，观察使用FakeInspector；没有网络设备。只使用evidence内UUID私有SQLite和源副本，删除及故障触发器仅针对本测试数据。覆盖事务/身份保护、门禁、观察中状态变化、跨实例恢复与屏障。独立真实进程恢复另由增强的ServiceRestartSmokeTest验证。

2026-10-15扩展至55项：列表/时间/状态/地图筛选、稳定分页、132条历史、权限与只读处置契约；新证据目录20261015-map-transfer-history-ledger。
