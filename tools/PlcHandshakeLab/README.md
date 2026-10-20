# PLC 握手时序实验台

独立 WPF 离线工具，依据用户 2026-10-20 提供的 **28阶段 / 10信号** 表。该表用于本工具，不自动替换主工程握手执行器或此前待核定契约。

## 使用

- 自动演示：按所选间隔播放；默认末步停止，勾选循环后从初态重新开始。
- 上一步、下一步、点击表格行：暂停播放，加载目标阶段的标准模拟值。跳转是假定该阶段条件成立，不验证物理动作。
- 亮灯代表1，暗灯代表0；本次变化的灯闪烁。表内↑/↓对应相邻标准阶段的电平变化，0/1表示两种电平均有效。
- 点击点位卡片右侧的0/1，翻转本地电平并暂停播放；红框及偏差文字标出与当前阶段的差异。偏差未修正时禁止自动推进。可以手动改回或点击“恢复本步标准值”。
- 第22步箱体检测允许0或1，进入该步保留当前检测值；第23步要求为1。信号相同的相邻阶段仍逐步显示，因为操作条件不同。
- 重置只重置模拟周期，不清事件历史；日志保留最近500条，导出为带UTF-8 BOM的CSV，包含时间、阶段、来源、点位及前后值。

P/H是责任方，`0.0` / `610.0` 等仅为用户表内标识，**不是已核定的S7 DB映射**。本工具不含网络通信、PLC驱动、现场写入或AGV下发功能，可与主程序独立启动。没有与主工程共享状态，也不证明真实固件或现场联锁通过验收。

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
