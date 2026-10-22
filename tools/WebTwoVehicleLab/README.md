# 两车 Web／Robokit TCP／共享 S7 端到端联调

使用正式 Service、WebHost、HTTP 任务接口、Outbox、SQLite、HSL S7 和两辆独立 TCP 虚拟车。正常任务不通过改数据库、改终态、移动坐标来完成。设备地址固定回环；每个新实验使用独立目录、数据库、管道和端口，拒绝覆盖原实验。需要本机 .NET 10、Python、已有 Web 构建和有效 HSL 授权；不保存授权信息。

沿用 `WebSingleVehicleLab` 维护的地图／点表／28阶段模板，目录前缀仍为 `web-single-vehicle-*`，由 `twoVehicleSimulation=true` 明确选择两车。该前缀是原隔离路径检查约束，不表示只有一车。

## 准备与运行

在主工程根目录，选一个**尚不存在**的新实验名：

```powershell
dotnet publish AgvDispatch.Service/AgvDispatch.Service.csproj -c Release -o artifacts/web-single-vehicle-你的新实验名/service
dotnet publish AgvDispatch.WebHost/AgvDispatch.WebHost.csproj -c Release -o artifacts/web-single-vehicle-你的新实验名/webhost
python tools/WebTwoVehicleLab/prepare.py --name web-single-vehicle-你的新实验名
python tools/WebSingleVehicleLab/start_runtime.py --root artifacts/web-single-vehicle-你的新实验名
python tools/WebSingleVehicleLab/configure_map.py --root artifacts/web-single-vehicle-你的新实验名
python tools/WebTwoVehicleLab/run.py --root artifacts/web-single-vehicle-你的新实验名 --setup
python tools/WebTwoVehicleLab/run_waiting_bay.py --root artifacts/web-single-vehicle-你的新实验名
```

发布顺序执行，避免同时修改共享编译输出。`--setup` 走正常安全切图、激活、放行、地图绑定、私有 Service 重启和启动恢复核对，不绕开拒绝。激活后需要新观测；恢复版本变化时刷新重新核对，不能强制成功。最终检查实际活动地图仍是目标版本，不能只看发布成功。

`ready.json` 给出本实验 Web 地址。浏览器可以观察两车；自动化提交和断言在正常 HTTP 接口执行，不能将其表述为每个前端按钮都已验收。

## 场景

1. 两车分别从取货 A/B 开始，用真实闭合支线取箱；共享一个虚拟 DB1，每车独立状态／导航 TCP 连接及运输历史。
2. 新模板等待节点绑定独立容量1资源R4，两车在首车仍取箱时接续提交。首个已接收送箱运输的资源持有车断连，设备模型继续原运输；后车载箱停在来源工位，不能生成进等待位的Outbox命令。
3. 原持有车到等待位后继续断连观察至少8秒；R4归属不丢失，正式等待队列能定位R4及占用任务。恢复连接后继续同一原任务。
4. 重连只核对原运输身份；各车四次 3066、九个后台步骤完成，原工位箱体及通知闭合，最终租约为零。
5. `observations.jsonl`、原始 3066、PLC 边沿、正常接口请求、步骤详情及只读数据库检查构成证据。

等待位采用保守整单保持：持有车完成原任务后，后车才取得R4并进等待位。资源在进站路径准备前原子申请；这不是几何自动避碰，也没有实现实际离开等待位后的提前释放。合成车体／坐标确认只对虚拟模型成立。`run.py`普通运行保留作历史未保护图的错峰证据，新模板明确拒绝该旧场景并提示使用`run_waiting_bay.py`；`run.py --setup`仍用于正常地图及恢复准备。

## 取消与重启：预期保留故障现场

正常两单完成后可单独运行：

```powershell
python tools/WebTwoVehicleLab/cancel_restart_probe.py --root artifacts/web-single-vehicle-你的新实验名 --confirm-synthetic-crash-test
```

新增两单，前车载箱进入倒料区的原运输执行中提交取消；相同请求号重放，查询原车确认后保留取消核对状态。受保护模板的后车保持在来源取箱点，不能进入等待位；历史未保护模板仍保留原等待位场景。随后只终止进程清单中且实际映像路径吻合的本实验 Service，设备模型继续，验证重启屏障、原路权身份、PLC 和 Outbox 保留。

该场景**有意停在人工恢复核对阶段**，不自动清 PLC、不释放未知占用、不批准恢复。数据库和载荷证据保留，不能将这两单标成正常完成。失败时也保留原记录；普通正常场景拒绝带着这些未闭合任务重新开始。

## 模型与回归边界

`multi_twin.py` 共享 PLC 内存，但各车自行按收到的路径运动；无命令不运动。网络断连不等于车辆急停。PLC Done 由物理模型到位及模拟倒料计时产生，不能代替真实机械、安全联锁或厂家固件验证。

`test_shared_plc.py` 核对共享内存、两车到位、许可消费和周期闭合；`TwoVehicleOutboxSmokeTest` 核对降级车队中离线车不领取命令、健康车继续及恢复后沿用原命令。`SeerFaultSmokeTest` 保留丢响应不重复发送、只读查询重连和发送结果不确定时保留路权的检查。

停止设备模型时在**本实验根目录**创建 `STOP`；只停止归属和实际映像路径均核对的私有 Service/WebHost。不要按进程名批量结束用户程序。工具拒绝在有旧设备状态时重新启动并伪装恢复，需另建新实验或另行逐项审查原故障。
