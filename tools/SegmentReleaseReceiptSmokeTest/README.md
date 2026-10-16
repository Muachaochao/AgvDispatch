# 后台逐段释放检查凭据

运行 `dotnet run --project tools/SegmentReleaseReceiptSmokeTest`。31项，唯一隔离SQLite、模拟进度/包络、真实Inspector和ReceiptService，未连接设备。

验证仅接受StepId、服务生成随机编号及后台会话；绑定原任务/命令/路线/租约时期、两帧、包络、配置与采集时刻；最长两秒且不超过任一原样本新鲜度。签发头和Issued审计同事务；失败回滚、取消传播、重启失效、时钟回退/过期边界、门禁/换图/租约/配置变化失效，已撤销凭据不能复活；SQLite更新保护拒绝延长有效期或将签发改作完成。

本工具只测试签发/查询，不启用另行实现的后台Commit组件。Read仅复核保存证据时效、持久化上下文和门禁，不采集新的现场帧；Pending不代表车辆当前仍完全离开区域，不能作为解锁授权。后台Commit组件已重采最新权威进度/包络并在事务内重核对，默认关闭；提交结果与逐资源审计另见SegmentReleaseCommitSmokeTest。

生产Policy默认关闭且没有现场源，无Worker/公开IPC调用；无合格证据不写凭据。本轮复验Inspector58、Transaction41和ServiceRestart26，完整Debug编译0错误。证据见docs/commissioning/evidence/20261014-segment-receipt。
