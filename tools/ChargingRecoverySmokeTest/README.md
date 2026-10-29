# 充电启动恢复隔离验证

运行 `dotnet run --project tools/ChargingRecoverySmokeTest`。仅使用临时SQLite、独立JSON和合成车辆快照，验证真实TrafficResourceManager与ExecutionRecoveryService的集成，不连接现场设备。

独立充电会话不需要伪造DispatchTask；启动冻结保留原充电位、道路和车辆归属，心跳过期不能自动释放。恢复通过无运行时依赖的IChargingRecoveryEvidence读持久文件，启动审计保存原完整会话和租约；仅在本次启动后由充电入口完成现场停止及实际离位核对、原车辆/步骤/资源/地图路线全部一致且没有残余租约和Outbox时专项核定。不会只凭CHARGE前缀或资源消失放行，不发送车辆或PLC动作。

覆盖正常核对关闭、过期占位互斥、未完成会话、遗留租约、孤立编号、错误原车辆/步骤/地图/资源、持久记录车辆变更、启动快照缺失、现场依据缺失、旧关闭证据、遗留Outbox、实际未退出和仍在充电。需要另用ChargingSmokeTest检验充电状态机及充电入口的人工关闭门禁。
