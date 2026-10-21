# PLC 握手时序实验台

独立 WPF 工具，保留用户于2026-10-21再次确认使用的 **28阶段 / 10信号** 表。支持离线演示，以及连接WebSingleVehicleLab的本机虚拟PLC。该表不自动替换主工程握手执行器。

## Web联调（2026-10-21）

1. 启动已有WebSingleVehicleLab实例。运行仓库根目录 `Start-PlcHandshakeLab.cmd`，读取本机 `artifacts/web-single-vehicle-20261020/ready.json` 自动选择同一个设备实例；也可直接启动新发布的程序，通过“选择目录”“连接虚拟PLC”连接。
2. 选择CNC-001或CNC-002，点击“触发满箱呼叫”（或满箱呼叫卡片的0按钮）。只发布置1事件，不修改任务、车辆位置、数据库或H输出。
3. 等待“满箱呼叫已反馈为1”，在Web创建任务表单中刷新条件、选择车辆、核对路线、提交。工位呼叫满足不代表首段路线必然可执行，车辆当前就在取料点时现有路线校验可能拒绝零长度首段。
4. 实验台每500ms读取虚拟设备实际状态，显示呼叫应答、箱体检测、返箱责任、等待申请、倒料周期、Ready和Done。设备快照超过3秒、停止、损坏或不可读取时显示未知并禁止输入，不沿用旧值。
5. H侧输出只读；箱体、Ready、Done由现有设备模型产生。联调模式关闭离线播放、跳步和标准值恢复，不通过28阶段表替后台推进任务。返回离线模式仅切换观察，不停止设备或清零点位。CSV分别导出当前模式事件。

联调通道复用设备已有 `device-input.json` / `device-state.json`：实验台发布带唯一编号的外部呼叫，设备模型应用到自己的DB1，正式后台仍经真实S7驱动读取；后台H写入由同一设备快照回显。不存在另一份PLC电平，也不直接改写设备状态文件。单个实例同时使用一个输入控制器，避免多个工具争用单槽输入文件；输入文件写成不代表已生效，必须观察设备反馈。输入按钮在反馈前禁止再次触发，5秒未观察到反馈则提示核查日志。

只接受PLC和AGV均为127.0.0.1、关闭自动建单的独立实例。工位故障、下游离线、无箱或本轮责任未闭合时不触发新呼叫；这些限制不影响离线28阶段学习。原28阶段取箱通知长保持、倒料实际出区即清等口径与当前sender-owned-return-confirmed-v1执行器仍有差异，实时模式如实显示执行器，不宣称已把整套28阶段应用于主工程或完成现场验收。

发布与只读UI检查：

```powershell
dotnet publish tools/PlcHandshakeLab -c Release -r win-x64 --self-contained true -o artifacts/PlcHandshakeLab-live
dotnet run --project tools/PlcHandshakeLab -- --lab <设备实例绝对目录> --live-smoke-test <绝对PNG路径> --station CNC-002
```

`--live-smoke-test`默认只读，检查10信号映射、H只读及离线隔离并渲染窗口；显式附加`--trigger-call`才测试实际虚拟呼叫事件，必须选未呼叫工位，不能拿已有高电平当新输入成功。原`--smoke-test`仍只测离线UI。二者均为命令行检查，不增加Web验收按钮。

## 使用

- 自动演示：按所选间隔播放；默认末步停止，勾选循环后从初态重新开始。
- 上一步、下一步、点击表格行：暂停播放，加载目标阶段的标准模拟值。跳转是假定该阶段条件成立，不验证物理动作。
- 亮灯代表1，暗灯代表0；本次变化的灯闪烁。表内↑/↓对应相邻标准阶段的电平变化，0/1表示两种电平均有效。
- 点击点位卡片右侧的0/1，翻转本地电平并暂停播放；红框及偏差文字标出与当前阶段的差异。偏差未修正时禁止自动推进。可以手动改回或点击“恢复本步标准值”。
- 第22步箱体检测允许0或1，进入该步保留当前检测值；第23步要求为1。信号相同的相邻阶段仍逐步显示，因为操作条件不同。
- 重置只重置模拟周期，不清事件历史；日志保留最近500条，导出为带UTF-8 BOM的CSV，包含时间、阶段、来源、点位及前后值。

离线模式P/H为责任方，`0.0` / `610.0` 等为用户表内标识；联调模式显示虚拟实例明确的DB1映射（CNC-001为0字节，CNC-002为2字节，共享倒料为610字节），不把它当现场地址核定。本工具没有现场写入或AGV下发功能。离线值不与主工程共享；联调值来自同一虚拟设备，不能证明真实固件或现场联锁通过验收。

到站、任务持久化、设备独占、许可消费、Done旧低基线、真实退出区域和返箱动作等仅显示为阶段条件，演示时人工假定成立。阶段12明确保留“Ready回低后PLC内部设备保留继续”的语义；此工具未实现PLC内部设备状态机或故障恢复协议。

后续接入真实点位需要先核定PLC型号、连接参数、工位/设备归属、实际DB地址及访问方向。建议先实现只读监视、数据质量和新鲜度，再实现独立于演示器的受控H侧单点测试；P侧点位不能作为上位机可写位。现场写操作不得由自动演示定时器驱动。

## 开发与验证

在仓库根目录执行（Windows，.NET 10 SDK）：

```powershell
dotnet run --project tools/PlcHandshakeLab/PlcHandshakeLab.csproj
dotnet run --project tools/PlcHandshakeLabSmokeTest/PlcHandshakeLabSmokeTest.csproj -c Release
dotnet build tools/PlcHandshakeLab/PlcHandshakeLab.csproj -c Release
dotnet publish tools/PlcHandshakeLab/PlcHandshakeLab.csproj -c Release -r win-x64 --self-contained true -o artifacts/PlcHandshakeLab
```

发布目录中启动 `PlcHandshakeLab.exe`，不依赖主程序服务或用户安装.NET运行时。开发项目未加入主解决方案，保持工具独立。

可执行文件支持 `--smoke-test <绝对PNG路径>`：执行窗口加载、偏差提示、自动开始/暂停及关键阶段检查，渲染窗口到PNG，生成旁边的 `.result.txt` 并退出。它只用于可重复的离线UI验证。
