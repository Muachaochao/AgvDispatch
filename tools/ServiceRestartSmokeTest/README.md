# 独立后台进程与重启恢复

从DispatchingSystem目录运行：`dotnet run --project tools/ServiceRestartSmokeTest`。2026-10-14完成26项检查，构建0错误。

测试复制当前Debug后台部署文件到唯一临时目录，生成专用appsettings、SQLite与Named Pipe名称，显式关闭AGV/PLC通信、命令/写入以及身份采集。调度、步骤和Outbox后台循环启用，用于观察恢复屏障。目录不带生产data/logs；复制的配置在启动前被测试配置替换。实际子进程运行原AgvDispatch.Service.exe，不注册Windows服务。

隔离数据库预置两个有路权的任务：一条运输已Sent/Executing，一条Sending且发送租约已过期。预置用于模拟断电前持久化状态，不声称这些命令已经发送到真实设备。

前端是独立测试进程，加载真实WPF TaskCenterViewModel/TaskCenterView，并通过真实NamedPipeTrafficControlClient读取独立后台进程。不是完整WPF Shell交互或实车验收。

两次启动验证：后台PID、通信安全开关、恢复条目/批次、拒绝未经核对放行、九步与原路权的IPC展示、原命令/步骤编号、尝试次数和状态、真实后台循环不会在屏障内回收/重发旧Sending命令。强制终止仅作用于本测试创建并持有Process对象的子进程，然后用相同数据库重启；从不按名称终止用户程序。测试保留临时部署目录、数据库、日志与截图供复核。

限制：本批没有启动TCP模拟车或PLC模拟采集，不验证独立进程内九步真实执行，也不验证正常Windows服务管理器停止、机器断电或实际固件重启。前一批同进程九步/TCP/PLC模拟和本批部署恢复测试覆盖不同边界，不能合并声称已做完整跨进程生产闭环。

后续先补专用测试宿主中的独立进程模拟设备闭环，再推进交通等待提示与滚动资源释放。

2026-10-15：地图传输历史扩展后45项，新增真实IPC列表、独立历史分页及固定禁止关闭/补发/放行的处置条件。证据evidence/20261015-map-transfer-history-process.log。
