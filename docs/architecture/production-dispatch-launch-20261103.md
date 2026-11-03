# 正式 PLC 呼叫与全局调度入口

正式业务模式使用 `--production-dispatch --production-max-concurrent N`。N 为现场配置的并发上限，允许 1～1000，不依赖固定车号或固定双车数量。该模式通过原工位车辆池、车辆健康和合法路线选择车辆，关闭 FleetTest、单车业务测试、单次导航和独立 PLC 联调执行器。

正式模式同时启用任务创建、PLC 自动接单、业务编排、后续运输步骤、原命令 Outbox、PLC 握手、PLC 写入和 AGV 命令许可，并传入 `Dispatching:RequireGlobalMatching=true`。Global 策略通过现有 Web 调度优化接口保存；选择正式启动模式不会擅自改写优化参数，优化策略失效时不得静默退回逐任务派车。所有现场恢复、地图、PLC 许可、车辆健康、有效路权及原编号恢复复核继续有效。

日常固定入口仍为 `http://127.0.0.1:5199/`。根启动器传入 `--daily-launch-settings 发布包/最新版Web.json`，仅承接可选字段 `productionDispatch: true` 和整数 `maxConcurrentTasks`。缺省或 false 不授权正式执行；已授权但并发缺失、类型错误或越界时拒绝启动。此文件不保存单车导航、叉车升降或自动改道许可。显式测试模式替换本次正式模式，不能叠加权限。

`tools/Set-LatestWebRelease.ps1` 在更新日常版本时承接已有正式策略，首次授权使用 `-ProductionDispatch -ProductionMaxConcurrentTasks N`，撤销使用 `-DisableProductionDispatch`。候选构建仍默认停用设备执行；构建不会修改根入口和运行数据。进程 session 只记录实际启动模式与并发用于防止复用错误实例，不作为恢复动作权限的来源。

开启后只从有效 PLC 工位状态创建持久化任务，同工位已有未闭环任务时去重；持久化原任务、原步骤与 Outbox 不因切换模式重建。启动恢复屏障未放行、PLC 无有效采样或车辆不健康时仍不能发车。已存在的 PLC 高电平呼叫在恢复放行后可能被接单，应按当前真实现场条件复核，不把未知点值当作零。

验证命令：`dotnet run --project tools/ProductionDispatchLaunchSmokeTest`；`pwsh -File tools/ProductionDispatchLaunchSmokeTest/Test-ProductionConfiguration.ps1`。测试只验证启动解析、权限互斥、持久策略和无效配置拒绝，不启动后台或访问真实车辆/PLC。
