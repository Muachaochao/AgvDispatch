# 厂家道路属性只读验证

在项目根目录执行：

```powershell
dotnet run --project tools/RobokitPathPropertiesSmokeTest -c Release
```

生成合成 SMAP 与规范地图 JSON，并输出 `vendor-path-result.json`。测试原始序号、属性保留、JSON 往返、版本差异和原文件归档；不连接车辆。

前端状态与双向比较测试在 `AgvDispatch.Web/tests/vendor-path.test.ts`。HTTP 集成使用已编译候选包：

```powershell
dotnet run --project tools/WebAdapterSmokeTest -c Release -- . --candidate-dir <包绝对路径> --vendor-path-only --vendor-map <生成的synthetic-path-properties.smap绝对路径>
```

集成工具只启动其自有本机隔离服务、管道和数据库，不调用现场控制。`--hold-ui` 保留临时界面供人工核查；按输出路径创建 `stop-ui` 文件后正常结束并清理本轮自有进程。
