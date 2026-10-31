# 单车充电目标识别探测

此工具只验证 `charger/c0001.charger` 的识别结果。不会导航到充电桩、控制电机或 DO、启动充电、抢占控制权、取消其他任务，不能将探测完成记为自动充电通过。

当前现场充电站参数、双触点观察和继续条件见[现场参数与对接观察](../../docs/commissioning/Kinco充电站现场参数与对接观察-20261031.md)。

需要 Python 3，仅使用标准库。默认 `inspect` 只查询机器人身份、状态、电池、机构脚本列表和充电识别文件目录。指定 `--charger` 仅建立并关闭 20207、20204 的 TCP 连接，不发送充电桩应用数据；端口连通不证明协议兼容。

```powershell
python .\tools\SingleCarCharging\probe_runner.py --robot 192.168.98.66 --charger 192.168.98.11
```

`probe` 会对同目录的固定 `charge_dock_probe.py` 计算 SHA256，只使用派生的 `charge_dock_probe_<sha12>.py` 名称。先核对硬件 ID 和空闲状态，再查询是否已存在该文件：已有文件须逐字节一致，否则拒绝；不存在时用 4021 上传。4022 下载校验成功后，再次读取身份和状态，最后只发送一次 3066 `SELF_POSITION`、`RecognizeOnly` 任务。无法选择其他脚本或任意动作。

```powershell
python .\tools\SingleCarCharging\probe_runner.py probe --robot 192.168.98.66 --expected-id 1ccbd798eee24ccc --evidence-dir .\artifacts\single-car-recognition
```

运行前应确认该 ID 仍对应测试车。前置检查要求：`task_status` 为 0 或 4、`script_status=0`、`task_id` 为空、未完成路径为空、底盘停止、车端报告未充电及未急停、控制权已释放、`dispatch_mode=0`；已返回的错误和严重错误也必须为空。仅对 `task_id` 以 `charge-recognition-` 开头的上次探测，允许状态 5/6/7，且必须再用新的 1110 应答确认唯一相同任务与相同终止状态；其他门禁全部保留。这不会清除、取消或重发上次任务。`manual_charge=false` 只说明车端未报告手动充电，不能单独证明插头已拔掉。若控制权不允许发送，保留拒绝结果，不自动抢权。

每次运行创建独立证据子目录，包含固定源码、精确请求报文、发送前意图、原始响应和本机接收时间。上传、执行遇到超时或损坏响应，结果视为未知并立即停止；不会自动重发。执行收到确认后，最多观察 30 秒，通过 1110 匹配原任务 ID，并优先解析 1100 的 `move_status_info`，兼容 `script_res`；两个原始通道均保留，`report_source` 标明实际采用的通道。只有任务完成且结果同时匹配探测标记和本次 `run_id`，并给出三次成功采样，才报告 `recognition_probe_completed`；充电验收状态始终为 false。

退出码：0 为查询完成或识别探测完成，2 为明确拒绝或失败，3 为结果未知。出现未知结果后，先核对证据和车辆状态，不直接重跑。结果中的相对位姿是识别参考系，不能据此直接让车辆移动同样距离；接触偏移、进退路线及桩侧握手仍需另行核定。

后续对接需单独标定识别参考系到车辆停靠中心的偏移，包括车辆触点位置、桩侧触点缩回位置与伸缩行程。地图上的当前站点名称、车体外形的前伸长度及识别返回距离均不能替代该偏移。接近点与最终对接点应分别配置，核定前保留现有业务站点。

本工具没有充电桩应用协议适配器。参考脚本向桩发送的 `1001 input/status` 含充电控制参数，不能当作只读状态查询；机器人 `6005` 断开继电器也不证明桩已停止输出或刷板已缩回。启用实际充电前，应取得当前桩适用的握手、接触确认、停止和回缩状态定义，以及有效的电池充电上限。`1007` 中 `max_charge_voltage/current` 的 `-1` 表示不支持，当前本地文档未定义 `0/-0.0` 的业务含义，不能将其解释为不限值或用参考脚本默认值补齐。

## 粗接近测试

`charge_dock_approach.py` 每次只执行一个“识别后低速停靠”的移动阶段。默认从当前 LM52 粗接近到桩前约 0.875 m；车辆先完成三次稳定识别，再校验当前中心距、横向偏差不超过 0.45 m、朝向误差不超过 0.35 rad、单次前进不超过 0.60 m。以首次现场识别值约 1.274 m 计算，第一段预计移动约 0.48 m，速度上限 0.03 m/s；到位后再次识别并停在粗接近位置。通过 `--standoff-m 0.675` 可在已到粗接近位置后执行第二段约 0.20 m 的接近，但不执行最终接触。脚本不控制充电继电器、不向充电桩发送应用数据。

2026-10-31 现场测试中，第一段从 `1.2747 m` 低速移动到 `0.8889 m` 后，三次识别结果稳定；第二段移动到约 `0.688 m` 后，近距离复测返回识别状态 3，但车辆已在规划点停稳、未充电且无错误。近距离阶段因此以有界路径到点作为完成条件，不再把近距离识别作为完成门禁；后续退出或重新接近仍必须重新识别。

执行前应确认现场人员位于急停按钮旁，并能观察双触点。命令必须显式填写车辆和当前站点，不能由脚本猜车：

```powershell
python .\tools\SingleCarCharging\approach_runner.py --robot 192.168.98.66 --expected-id 1ccbd798eee24ccc --expected-station LM52 --evidence-dir .\artifacts\single-car-approach --stage-only

python .\tools\SingleCarCharging\approach_runner.py --robot 192.168.98.66 --expected-id 1ccbd798eee24ccc --expected-station LM52 --evidence-dir .\artifacts\single-car-approach
```

第一条命令只上传并回读校验脚本，不下发导航；第二条才执行一次粗接近。出现未知结果时只保留证据，不自动重发。第一段完成后核对桩侧缩回触点与车侧自动触点间隙，再决定是否进行第二段 0.20 m 接近和最终 0.14 m 接触段；不能把本次约 0.875 m 粗接近记为充电通过。

完成粗接近后可执行一次不超过 0.10 m 的末端微进。`advance_runner.py` 会先核对车辆当前坐标和朝向，只允许沿当前车头方向前进，速度不得超过 0.01 m/s，并在路径到点后停止；它不识别、不横移、不控制充电继电器，也不与充电桩通信。

```powershell
python .\tools\SingleCarCharging\advance_runner.py --robot 192.168.98.66 --expected-id 1ccbd798eee24ccc --evidence-dir .\artifacts\single-car-advance --distance-m 0.10 --max-speed 0.01 --stage-only

python .\tools\SingleCarCharging\advance_runner.py --robot 192.168.98.66 --expected-id 1ccbd798eee24ccc --evidence-dir .\artifacts\single-car-advance --distance-m 0.10 --max-speed 0.01
```

第二条命令会移动车辆，只在现场已确认桩头缩回、路径无人且急停可操作后执行。同一位置不可自动连续补发多个 0.10 m 分段。
