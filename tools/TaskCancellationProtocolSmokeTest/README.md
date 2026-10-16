# 取消确认延迟与异常路权保留

运行：`dotnet run --project tools/TaskCancellationProtocolSmokeTest`。2026-10-14完成101项检查，构建0错误。

使用真实 TaskCancellationService、SeerAgvGateway/SeerRobotClient、SQLite任务/步骤/Outbox仓储、共享控制门和交通资源管理器。模拟车只监听127.0.0.1随机端口，每个场景使用独立临时数据库。不读取或修改生产通信配置，不连接现场设备。

本次按单轮调用取消服务的 ProcessAsync，即 TaskCancellationWorker 每轮调用的同一方法；未启动实际Windows服务托管周期，也不覆盖客户端取消入口。请求与3003/1110均使用原任务编号。

七个场景：

- 3003接受后1110仍显示运行，延迟确认取消；期间不重复发送3003。
- 3003已接收并终结原任务，但响应连接断开；重建取消服务后从1110确认，不重发取消。
- 3003响应断开且原任务仍运行；保留退避期，再查原任务后重试。
- 厂家返回ret_code=1；保留冻结与重试，成功接受仍等待结束状态。
- 1110返回404或未知状态；不误认成功、不盲发取消。
- 原运输已经Completed或Failed；不额外发送3003，仅进入现场核对。

所有场景均验证：九步冻结、正常调度无Ready/Executing工作、无后续Outbox可发送、请求重放幂等、原路权保持、结束确认审计只写一次、3003目标是原StepId、没有新运输。任务终结不证明车辆已物理停车，不自动完成步骤或解除现场核对要求。

Evidence临时目录保留test.db与result.json；模拟端结束时自动关闭。项目内证据位于docs/commissioning/evidence/20261014-cancel-protocol。没有完成真实进程重启、S7采集、实际停车/箱体核验或厂商控制器重启验收。
