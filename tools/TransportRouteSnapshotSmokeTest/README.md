# 原运输路线快照

运行 `dotnet run --project tools/TransportRouteSnapshotSmokeTest`。31项检查，唯一隔离SQLite，无设备I/O。验证准备事务写入映射路径/有序逻辑路段及地图内容、重试/确认不改变快照、SQLite拒绝改写原载荷/车号/版本、准备失败完整回滚、旧结构升级保留原命令且不回填历史、新数据库连接读取一致。LegacyOutboxEntity仅为升级前结构测试夹具。

快照更新保护不是数据库管理员防篡改机制。生产逐段评估开关仍关闭；没有释放动作。证据见docs/commissioning/evidence/20261014-route-snapshot。
