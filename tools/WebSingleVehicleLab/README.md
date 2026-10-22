# Web 单车闭环联调工具

通过现有 Web、正式 Service/WebHost、SQLite、Robokit TCP 和 HSL S7 驱动运行一辆本机虚拟车。没有新增模拟验收按钮，没有替换前端快照，没有直接写任务终态。任务由正常 Web 创建接口建立，四段运输由 Outbox 发送，虚拟设备通过协议反馈，九步由真实后台执行器推进。

## 范围

- 地图使用本机实际保存的规范化导航文件，走正常导入、草稿、发布、激活、坐标绑定和恢复门禁。当前默认输入是已有离线构造地图，不是实车测绘或厂家导出图。
- 虚拟车和虚拟总 PLC 只绑定 `127.0.0.1`；配置、数据库、日志、地图副本和证据位于独立 artifacts 目录。启动器拒绝非回环设备地址、目录外数据库及已占用端口。
- 后台使用正式 `SeerRobotClient`、`SeerAgvGateway`、地图逐段核验、HSL S7、路权、持久化握手及恢复服务。HSL 仍需本机有效授权，不读取或打印授权码。
- 新实验明确使用 `plc-28-stage-v2`：取箱后通知保持到返箱、实际出区后闭合倒料周期、返箱责任最后解除。设备模型从本实验配置读取契约；旧 `sender-owned-return-confirmed-v1` 仅保留原独立证据，不原地迁移、不与新版混用。
- 新模板的 `SIM-ONLY` 车体包络、定位误差与倒料边界只用于虚拟坐标系。`CalibrationVerified=true` 仅表示该合成模型参数已声明，不能复制到实车配置作为现场标定。
- 停靠、顶升、箱体、倒料和许可反馈都是虚拟设备模型；不能用于实车、安全或真实 PLC 梯形图验收。

## 组件

1. `prepare_lab.py`：首次准备导航副本、局部 PLC 点表、28阶段出区参数和回环配置。默认新目录为 `artifacts/web-single-vehicle-20261021-28stage`；可用 `--name web-single-vehicle-其他名称` 创建其他独立目录。已存在的实验不覆盖。
2. `configure_map.py`：通过现有 HTTP API 导入、创建、保存和发布地图，保存原始操作结果。激活、恢复、车辆编辑和任务提交仍使用现有入口。
3. `clone_runtime.py`：使用 SQLite backup API 获取一致性副本，不直接复制运行中的数据库主文件。`--fresh-devices` 使用全新回环端口和虚拟设备状态；原失败环境及其记录保留。
4. `start_runtime.py`：检查独立运行配置后以无窗口进程启动设备模型、Service 和 WebHost。第一次没有 `ready.json` 时启动本实验根目录，后续按同目录 `ready.json` 指向的运行副本启动。重复运行时拒绝占用端口，不停止其他进程。
5. `device_twin.py`：实现本次使用的 Robokit 查询、路径发送、查询和取消，以及最小 DB1 S7 读写模型。导航来自实际收到的 3066 路线，逐段更新坐标；没有命令不自行运动。无效方向、断链、重复任务编号及忙碌车辆拒绝接收。

本工具需要本地有效地图/点表、Windows .NET 10、Python，以及已构建的 Web 文件；现场输入不随 Git 分发。首次准备前先发布 Service 与 WebHost 到指定实验目录，再生成配置，避免 publish 覆盖实验配置。本机最终入口由所选实验目录的 `ready.json` 给出。

## 新版独立环境的使用顺序

在主工程根目录执行，以下路径均为新的隔离目录：

```powershell
dotnet publish AgvDispatch.Service/AgvDispatch.Service.csproj -c Release -o artifacts/web-single-vehicle-20261021-28stage/service
dotnet publish AgvDispatch.WebHost/AgvDispatch.WebHost.csproj -c Release -o artifacts/web-single-vehicle-20261021-28stage/webhost
python tools/WebSingleVehicleLab/prepare_lab.py
python tools/WebSingleVehicleLab/start_runtime.py --root artifacts/web-single-vehicle-20261021-28stage
python tools/WebSingleVehicleLab/configure_map.py --root artifacts/web-single-vehicle-20261021-28stage
```

`AgvDispatch.Web/dist` 应事先完成当前源码构建。启动后通过原有页面检查工程地图、激活版本、车辆及地图/MD5坐标绑定，并按正常恢复门禁核对；不能用改库代替这些操作。`configure_map.py` 使用正常 HTTP 创建和发布地图，已经完成配置时不必再次运行，以免重复建图。首次启动会生成通信设置；HSL 仍需本机有效授权。

准备独立重复运行副本时，在原实验已经完成正常地图和通信配置后执行：

```powershell
python tools/WebSingleVehicleLab/clone_runtime.py --root artifacts/web-single-vehicle-20261021-28stage --name runtime-repeat --fresh-devices
python tools/WebSingleVehicleLab/start_runtime.py --root artifacts/web-single-vehicle-20261021-28stage
python tools/WebSingleVehicleLab/collect_results.py --root artifacts/web-single-vehicle-20261021-28stage
```

采集脚本要求所有业务已完成、无未解释占用，未满足时保留原始证据并报失败。所有辅助脚本的 `--root` 必须指向同一实验，默认均为新版目录。查看或重用旧实验时显式传 `--root artifacts/web-single-vehicle-20261020`，保持它原有的契约、配置与历史；不能把新版设置覆盖到旧数据库。`clone_runtime.py --service-source` 仍只接受所选实验内的发布目录。

## 现场交互方式

使用原有车辆列表/车辆配置、地图管理/工程编辑、任务列表/创建任务、调度大屏和任务详情。`TaskCreation:AutomaticCreationEnabled=false` 仅关闭自动呼叫建单，保留手动任务的 PLC 应答处理；默认值 true 保持既有自动创建行为。`TaskCreation:Enabled=false` 仍关闭整个创建服务。

对虚拟工位发出满箱事件时，在当前设备目录写 `device-input.json`：

```json
{"sequence":"唯一输入编号","call":"CNC-001"}
```

也支持 `CNC-002`。这是外部虚拟传感器输入，不是任务状态改写。每次事件使用新 sequence；通过真实 S7 采集后，Web 再按实际条件允许创建任务。正常返程结束在原工位；同工位再次取箱时，首段沿已启用的真实相邻边离开并返回，在末点执行取箱动作。模板保留 `取货A→西下→取货A`、`取货B→西上→取货B` 两条独立进出路线；闭合路禁止经过等待点或倒料点。无合法通路时拒绝建单或等待资源，不能把虚拟车瞬移回停车点。

## 日志和停止

`device-events.jsonl` 保存虚拟设备请求、响应、PLC 位变化及导航阶段；`device-state.json` 保存诊断状态。Service 的日志、任务诊断 ZIP、地图操作结果和 SQLite 各自留存。它们不是跨设备原子快照。虚拟设备历史不会补造为厂家历史。

设备目录中的 `STOP` 文件只停止该设备模型；不要在运输中用它当作暂停功能。存在活动运输时，设备重启明确拒绝静默恢复；应保留原任务，通过正常恢复流程处理。已经完成的历史及位置可从状态文件恢复。Service/WebHost 应由其所属实例的运维流程停止，不按进程名称批量结束。

## 回归

新独立实例完成正常地图激活、通信绑定及服务重启后，可执行：

```powershell
python tools/WebSingleVehicleLab/run_rounds.py --root artifacts/web-single-vehicle-20261022-commissioning --rounds 2 --release-idle-recovery
```

该命令只接受明确`simulationOnly`及`plc-28-stage-v2`的回环实例。通过正常Web建单和独立虚拟传感器事件运行，同工位第二轮保留真实进出取货路线。保存每轮创建请求、阶段变化、完成详情、原始设备报文和28阶段电平投影；稳定重复电平阶段仍需结合运输及检查点核对。不会改数据库、补任务终态或移动设备。`--release-idle-recovery`仅允许无活动任务、虚拟车空载静止及PLC全闭合的合成全场启动核对，不处理活动任务恢复。实例未完成地图及恢复配置时明确拒绝运行。

`run_restart_probe.py --root <同一独立实验目录> --confirm-synthetic-crash-test`是明确选择的Windows进程崩溃测试：只允许合成回环设备，核对进程清单及PID实际映像路径后，仅终止该私有Service并无窗口重启，设备模型继续原运输。通过正常恢复接口核对新鲜停车、原车辆/原运输、机械完成及下一握手节点，再放行原任务；不重发原运输、不清PLC、不释放路权。需要进程环境中已有有效HSL授权，不打印或保存授权码。失败保留原任务及屏障；`--resume-evidence <原restart-probe目录>`可核对后继续同一测试，不另建任务。更换该合成Service后还须显式`--review-new-boot`，旧启动证据保留；不自动确认未知PLC写入。所有启动配置及保存的通信端点均须回环，其他进程和厂家实例不由脚本处理。

`python tools/WebSingleVehicleLab/test_probe_guards.py`验证保存配置出现非回环地址、PID复用映像不符及进程归属不唯一时拒绝测试；使用临时目录和伪内核，不终止任何进程。

`dotnet run --project tools/WebSingleVehicleLabSmokeTest -c Release` 验证数据库并发初始化、重复初始化不清活动资源、自动模式默认兼容，以及只手动建单时仍能正常应答。设备流程另由实际 Web 提交、正式后台运行和保存的原始报文验证；不能仅用本工具单元检查宣称端到端通过。

`dotnet run --project tools/PickupRepeatRouteSmokeTest -c Release` 验证真实闭合路线、预览/建单/派车/3066及当前地图核验；`python tools/PickupRepeatRouteSmokeTest/check_lab_template.py` 只在临时工作区验证新模板，`python tools/WebSingleVehicleLab/test_lab_paths.py` 验证新旧实验路径隔离，`python tools/WebSingleVehicleLab/test_start_runtime.py` 用模拟Popen验证首次启动和拒绝条件。它们不启动现场或已有实验实例。
