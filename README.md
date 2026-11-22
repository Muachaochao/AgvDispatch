# 铝屑回收 AGV 调度系统

面向 CNC 车间的自动换箱与铝屑回收系统。工位发出满箱呼叫后，调度后台建立任务、分配 AGV，安排车辆取箱、倒料并把空箱送回原工位。操作员在 Web 页面查看车辆、任务九步、导航路线、PLC 反馈和路权等待。

主工程使用 C# / .NET 10、Vue 3 和 SQLite。车辆对接快捷 AGV，使用 **Robokit TCP**。工位与倒料设备通过 **西门子 S7 PLC** 交换业务信号。

## 两个使用入口

| 入口 | 地址 | 用途 |
| --- | --- | --- |
| **日常调度 Web** | `http://127.0.0.1:5199/` | 看车队和任务、维护工程与地图、核对设备信号、诊断异常 |
| **排策智演** | `http://127.0.0.1:5270/` | 配置仿真场景、发起模拟呼叫、观察车队和路权、查看报告与回放。Java 后端使用本机 `5271` |

日常环境通过 `发布包/启动最新版Web.cmd` 和 `发布包/停止最新版Web.cmd` 管理，版本由同目录的 `最新版Web.json` 指定。升级时先核对 `5199` 的实际运行版本，备份 `发布包/运行数据/后台5199` 和 `发布包/运行数据/Web工程方案`，再切换发布包。

## 一单业务怎么走

1. AGV 前往呼叫工位。
2. 取走满箱，核对机构和工位信号。
3. 运送满箱到倒料候车位。
4. 等待倒料设备就绪。
5. 取得路权后进入倒料位。
6. 执行倒料并等待完成反馈。
7. 将空箱送回原工位。
8. 放置空箱并确认到位。
9. 完成 PLC 握手收尾，结束业务周期。

**接单与发车。** `Global` 模式按当前车位、车辆状态和路线成本对待派任务与可用车辆做滚动匹配。推荐后还要核对路权和设备条件，再登记并发送命令。车队规模、同时放行的取箱任务数和单个资源的容量分别配置。

**行车与路权。** 每个运行窗口一次申请齐本窗口所需资源。有可用安全落脚点时，车辆可以分段通行，到边界按最新交通状态复核下一段。车辆通过后，后台按原命令和车体净空证据释放已通过的资源。缺乏安全落脚点的通道需要保护到可停位置，倒料出口也有通行保护。

**充电与异常。** 系统根据配置的低电量阈值安排充电。执行中的换箱业务按原任务、PLC 和路权条件收尾。车辆障碍、导航不确定或设备反馈异常时，后台核对原命令及车辆状态，再决定是否允许安全续行、受控绕行或转人工处理。

## 从源码开始

需要 Windows 和 .NET 10 SDK。构建 Web 前端时使用 Node.js 22.12+，运行排策智演还需要 Java 17 和 Maven。以下命令在仓库根目录运行。

```powershell
powershell -NoProfile -File tools/Initialize-LocalConfiguration.ps1
dotnet build AgvDispatch.sln -c Debug
npm.cmd ci --prefix AgvDispatch.Web
npm.cmd run build --prefix AgvDispatch.Web
```

初始化脚本只在缺少本地配置时生成示例。示例默认关闭现场通信、PLC 写入、车辆命令和自动调度。实际地图、PLC 点表、通信设置、授权和运行数据库由现场单独管理。

需要构建完整联调候选包时，在安装前端依赖后运行 `pwsh -File tools/Build-CommissioningBundle.ps1`。脚本生成独立目录，再用 [`Verify-CommissioningBundle.ps1`](tools/Verify-CommissioningBundle.ps1) 核对候选包。

## 在排策智演里验证

运行 `pwsh -File tools/Start-PaiceSimulation.ps1`，打开 `5270`。**原生算法**用于路网和算法对比，**主工程调度**通过 `5199` 使用独立实例、虚拟 PLC 和虚拟 Robokit，验证正式调度逻辑。

以 B07 五车十单为例，在「工程与地图」核对工程方案与五台虚拟车，在「场景与呼叫」配置十次呼叫并预览，随后从「任务与车队」看实际派车、九步进度、车辆目标和占用资源。回归比较应使用相同地图、车速、呼叫顺序及间隔，并记录建单到发车、路权释放到续行、最长阻塞和总完赛时间。完成后到「报告与回放」核对每单结果及异常证据。

仿真报告对应其记录的工程版本和运行条件。现场启用另需核对实际地图、设备反馈、车体净空和安全联锁。

## 仓库结构与文档

| 目录 | 职责 |
| --- | --- |
| `AgvDispatch.Web` / `AgvDispatch.WebHost` | Vue 页面、本机 Web 接入与后台桥接 |
| `AgvDispatch.Service` | PLC 建单、派车、九步编排、路权与异常恢复 |
| `AgvDispatch.Domain` / `AgvDispatch.Application` / `AgvDispatch.Infrastructure` | 领域模型、应用接口、SQLite 持久化和交通资源管理 |
| `AgvDispatch.Connectors.Agv` / `AgvDispatch.Connectors.Plc` | Robokit TCP 与 S7 PLC 适配 |
| `AgvDispatch.Wpf` | Windows 现场管理与维护工具 |
| `integrations/PaiceSimulation` / `tools/SimulationLab` | 排策智演、隔离实例管理、虚拟设备及场景验证 |

- [启动排策智演](integrations/PaiceSimulation/docs/how-to/启动独立工程仿真.md) · [运行主工程调度实验](integrations/PaiceSimulation/docs/how-to/运行主工程调度实验.md) · [任务与车队说明](integrations/PaiceSimulation/docs/reference/simulation-monitor.md)
- [全局匹配与六层执行链](docs/architecture/dispatch-rolling-optimization.md) · [滚动窗口与安全点交接](docs/architecture/滚动窗口安全点零占用交接-20261121.md)
- [地图离线编辑与在线发布](docs/architecture/地图离线编辑与在线发布.md) · [源码与本地配置范围](docs/architecture/GitHub源码范围与本地配置.md)
