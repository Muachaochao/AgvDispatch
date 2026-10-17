# 持久化握手隔离检查

在主工程目录执行 `dotnet run --project tools/HandshakeRuntimeSmokeTest`。

使用临时SQLite、真实握手执行器和存储；PLC网关/缓存、车辆定位是测试替身，运输终态由夹具设置。不会连接现场或读写生产数据库，不能替代PLC程序及机械动作验收。

覆盖版本绑定、模式关闭不降级、写入结果未知后重启不重发、revision冲突、Ready消费、Done旧高电平、设备独占、保守返程定位核对、阶段收尾、信号去重/固定分页、不可改事件和联调ZIP逐文件哈希。当前37项通过。

恢复核对、原Outbox模式冲突另见 `tools/ExecutionRecoverySmokeTest`；真实Service→管道→HTTP隔离接入见 `tools/WebAdapterSmokeTest`。包含Service引用的构建应顺序执行，避免共享输出目录发生文件锁竞争。
