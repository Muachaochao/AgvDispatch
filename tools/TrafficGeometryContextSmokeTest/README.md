# 权威几何上下文与只读展示验证

运行 `dotnet run --project tools/TrafficGeometryContextSmokeTest`，56项验证。

独立临时SQLite，两条发送时原路线快照、真实上下文检查器及控制门禁，模拟核定最大载荷包络与误差界。缺源、旧快照、同版本地图修改、等待/未知发送、取消、旧租约、资料过期/缺失/变更/断连/不响应取消等拒绝；正常续期允许。原任务、步骤、Outbox、请求及占用均不由查询改变。

真实TrafficControlNamedPipeServer和NamedPipeTrafficControlClient在唯一管道往返；私有DispatchAsync验证View权限与无权限分支。生产管道当前使用LocalOperation本机操作模式，不声称已启用配置账户隔离。

WPF页面验证只发送两个步骤编号、过期/迟到/换选项/断连/离开页面清除，明暗主题与1100x700渲染，确认冲突列表有可见空间。生成context-result.json和geometry-*.png。没有设备通信、解锁动作或现场核定来源。
