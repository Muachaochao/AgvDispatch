# WPF 全页面渲染检查

在 DispatchingSystem 目录运行：

```powershell
dotnet run --project tools/FullUiSmokeTest --artifacts-path .artifacts/ui-polish -p:NuGetAudit=false -m:1
```

使用真实 WPF 页面和 ViewModel，在浅深主题、正常和最小内容区尺寸下创建页面与页签，输出到 `docs/screenshots/wpf-full-20261017/`。同时检查指定页面的标题与工具栏没有重叠，并加载新建地图窗口。

客户端由进程内 DispatchProxy 模拟未连接状态，不访问实际后台或现场设备，不执行写操作。用于发现 XAML/资源加载和布局问题；成功渲染不代表全部交互、真实遥测内容或现场 DPI 已验收。填充数据及业务行为由现有监控、地图编辑、现场恢复和手动建任务等独立 smoke test 覆盖。
