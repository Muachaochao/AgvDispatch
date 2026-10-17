# 九步任务与两车冲突集成检查

从 DispatchingSystem 目录运行：

```powershell
dotnet run --project tools/DispatchIntegrationSmokeTest
```

测试创建唯一临时目录、独立 SQLite、唯一 Named Pipe 和两台只监听 `127.0.0.1` 随机端口的 Robokit 模拟车。没有读取生产 appsettings、点表或数据库，没有启用现场服务。结束时停止本测试管道并关闭模拟车，保留临时证据目录供复核。输出的 Evidence 路径包含任务运行/完成截图、发送与 PLC 写入记录和测试数据库。

真实组件：WPF TaskCenterViewModel/TaskCenterView、NamedPipeTrafficControlClient、TrafficControlNamedPipeServer、ManualTaskCreationService、TaskCreationCoordinator、DispatchTaskOrchestrator、DispatchTaskStepCoordinator、AgvCommandOutboxDispatcher、SeerAgvGateway/SeerRobotClient、SQLite仓储、路权和任务查询服务。

为确定性测试，通过反射调用后台组件的实际单轮方法；未启动 Windows Service 托管循环。前后台组件位于同一测试进程，通过真实 Named Pipe 通信，车辆指令经过实际 TCP 报文编解码。未使用的管道端点不注入服务，调用会失败，不提供虚假的实现。

PLC 仅在 IPlcGateway/IPlcSignalCache 边界模拟，不覆盖 HSL/S7 网络协议、授权或现场扫描时序。模拟输出写入在下一次 Scan 才进入缓存，验证步骤不会在同轮提前消除握手；它不代表真实电气响应。

覆盖：前端创建与九步预览、两工位两车辆、优先级与共享资源容量、等待起点保留、阻塞关联、等待期间无命令/无部分路权、四段3066运输与1110完成反馈、坏质量箱体信号、倒料Ready及允许离开阻断、空箱检测、PLC收尾、正常释放及另一辆车继续执行、最终界面显示与每段发送唯一性。

本检查不覆盖取消、TCP断连/丢响应、真实进程重启、严格机器身份、区域几何碰撞或公平滚动锁。相关模拟故障与独立进程部署联调应另行增加。


2026-10-16扩展：61项检查，新增创建前四段选路动作/PLC联动；result.json的Timeline记录4条配置预览与两车各9条执行快照，含PLC值/质量；preview-step-*与execution-step-*为真实WPF渲染图，当前步骤滚动入视。原确定性驱动/模拟边界不变。查看主工程commissioning的路段动作与PLC联动报告及离线HTML演示。
