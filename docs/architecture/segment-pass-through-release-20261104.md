# 公共资源“通过即释放”实现说明

## 目标

共享道路资源继续保持容量 1。车辆进入下一路径段时，前一段只要同时满足以下条件，就可以在任务尚未整单完成时提前释放：

1. 原运输任务、步骤、车辆、厂家任务编号、命令、路线快照和地图版本一致。
2. Robokit 当前正在执行的指定路径段可以唯一映射到发送时路线。
3. 只把当前段之前的路段声明为已经通过；当前段和后续段仍视为未通过。
4. 两帧在线、同周期、序号递增且进度不倒退的定位样本证明整车及最大载荷包络已离开资源边界。
5. 资源在原路线上的最后一次使用已经通过，并且后续路线不会再次进入。
6. 签发短时凭据后再次采集更新两帧，仍满足同样的任务、地图、租约和包络条件。

终点资源继续保留到正常交接。取消、恢复屏障关闭、车辆报警、定位质量不足、包络未核定或证据读取超时都会保持原路权。

## 生产链路

- `AgvTrafficProgressReader` 从 Robokit 任务状态读取当前指定路径段，并从车辆缓存取得定位、置信度和采样序号。
- `TrafficSegmentReleaseInspector` 构造原发送路线、租约、地图和车体上下文，只做只读评估。
- `TrafficSegmentReceiptService` 为合格资源签发最长两秒的凭据。
- `RouteSegmentPassThroughReleaseWorker` 在后台周期内消费凭据；提交时会重新采样。
- 租约删除、逐资源审计、释放事件和剩余占用展示更新在同一个数据库事务中完成。任一条件失败，整笔事务回滚，原租约保留。

## 配置

```json
{
  "TrafficSegmentRelease": {
    "Enabled": true,
    "CommitEnabled": true,
    "PollIntervalMs": 750,
    "FrameWaitSeconds": 4,
    "MinimumObservationIntervalSeconds": 0.2,
    "MaximumAgeSeconds": 5,
    "MinimumConfidence": 0.9,
    "MaximumUncertaintyMeters": 0.25,
    "SafetyMarginMeters": 0.15
  }
}
```

`Enabled` 只允许只读评估；`CommitEnabled` 才允许后台删除租约。两项及任务、地图、恢复门禁必须同时有效。

## 六层监控

“交通路权”层新增：

- 已证明通过、等待原子释放的资源；
- 继续占用的资源和逐资源原因；
- 两帧已通过路段索引、定位坐标、误差界和整车包络；
- 最近实际释放的租约、资源、时间与证据摘要；
- “通过即释放评估”和“逐资源原子释放”两个独立开关。

## 验证边界

隔离测试使用真实 SQLite、逐段释放组件和模拟 Robokit 样本，覆盖两帧、车体包络、未来重入、事务回滚和跨进程重放。正式启用的前提仍是现场 Robokit 任务状态字段、地图资源边界、AGV-01/02 包络与 PLC 交接条件通过实车核对。
