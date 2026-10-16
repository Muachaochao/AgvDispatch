# Robokit 分端口与只读核验模拟

在DispatchingSystem目录运行：

```powershell
dotnet run --project tools/RobokitReadOnlySmokeTest
```

两个仅绑定127.0.0.1随机端口的模拟服务器，分别只接受状态/导航API，错端口返回错误。生产SeerRobotClient/Gateway/RobokitInspectionService及真实活动地图参与验证；不使用现场地址或数据库。

覆盖分端口和旧Port兼容、通信设置保存、请求节流、原任务状态、导航响应丢失确认、状态与导航等待/断线隔离、版本/地图/站点/控制权/路径只读采集、地图变化/载入未完成/缺站点/重复映射/错误码/关闭采集/主动取消。无参数只读API要求零长度；只读核验期间禁止出现>=2000的API。

老SeerFault/取消/Hosted模拟器显式设置NavigationPort=Port保留原有故障场景；它们不能证明实车分端口，分端口由本工具独立验证。控制器epoch、真实固件兼容、3066部分接收原子性和车体退出资源均不属于此模拟证明范围。
