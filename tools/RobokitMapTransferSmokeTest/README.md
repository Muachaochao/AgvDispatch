# 地图传输内核隔离模拟

在DispatchingSystem运行`dotnet run --project tools/RobokitMapTransferSmokeTest`。

生产RobokitMapTransferSession/SeerRobotClient参与；三个仅监听本机随机端口的模拟器检查完整地图JSON、类别、长度、序号和分块回应。固件及预期车端MD5为明确模拟值，不作为厂家字段核定。WritesEnabled仅测试进程打开，生产没有DI/IPC/界面注册，不改现场配置。

结果位于docs/commissioning/evidence/20261014-map-transfer。该检查不证明实际持久化或载入、源文件MD5算法、车队任务/路权互斥、进程重启防重或完整生产上传功能；相应集成仍需后续完成。
