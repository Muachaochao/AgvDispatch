# Robokit充电接口隔离验证

运行 `dotnet run --project tools/ChargingProtocolSmokeTest`。测试只连接随机本机端口上的合成TCP服务，不访问现场车辆、充电桩或日常数据库。

依据当前本地 `docs/vendor/robokit/SEER_Wiki_Tree-refresh-manifest.json`，467个档案文件已逐一核对SHA256一致。使用正文中的1007/11007完整电池查询（`simple=false`）和6005/16005充电继电器（`status=true/false`），端口依据`概述/03_API报文结构.md`。适配器不会把继电器ACK或`auto_charge`连接指示当成实际充电，也不定义文档未提供的充电桩握手。

覆盖比例转换、缺失字段、无效类型和越界值、厂家拒绝、分端口、明确开关payload、缺少写返回码、响应丢失不重发、响应序号不匹配、现场确认门禁、全局动作及单车通信关闭。该验证不证明现场充电电流符号、继电器与桩联动、精确对接、断开后的可行驶条件或充电桩型号兼容，必须随实车核定。

配置：`RobokitCharging:RelayControlConfirmed`默认`false`，必须对实际车桩确认6005适用性后启用；`RobokitCharging:OtherApiPort`默认`19210`。任务状态机仍需独立确认充电位配置、安全业务边界和正电流反馈。
