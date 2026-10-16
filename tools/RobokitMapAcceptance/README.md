# Robokit地图验收工具

在DispatchingSystem目录运行`dotnet run --project tools/RobokitMapAcceptance`。

读取用户指定石墨文档链接的厂家GitHub七张官方示例及一张明确合成的混合类型图，生成标准navigation.json和软件可直接“打开本地”的.agvmap.json。真实MapImportService、LocalMapEditorStore、MapManagementViewModel及MapManagementView参与；链接已有测试代理但不执行其测试入口。仅隔离本地目录，无实际后台请求。普通点云、普通线、禁行线及高级区域为背景显示，不自动转导航或路权资源。

世界坐标单位米，显示时平移为正坐标；平移量、源SHA256、原点类型和不支持几何另存mapping-review.json。节点/路径Enabled=false，不能据此派车；方向、动作、属性和完整源图仍在原smap，不宣称完整编辑保存回smap。高级点LandMark兼容依据为官方示例实际类型，不泛化未知类名。StraightPath与三阶BezierPath用于预览；ArcPath控制点语义未核定，单列不转圆弧。3D ZIP Smap、RoundLine等不属于本批支持范围。

截图为实际WPF视图渲染（1440x900），不是绘制的网页替代品，也不是当前用户窗口的抓屏。逐站检查实际Canvas像素与原图米制坐标对应，保存后通过软件现有本地打开接口重载。

协议检查只使用本机随机端口，测试专用TCP发送4010/4011/2025并使用生产Codec；模拟端独立构造回应头、拆分发送。4010/2025成功JSON来自当前本地协议响应示例，请求为原始官方地图；4011响应直接返回整图。生产客户端没有配置这些类别通道，单独验证拒绝发送，不能称生产上传功能完成。未进行任何实车上传、切图或发车。

输出：docs/examples/robokit-map-acceptance/软件可打开及docs/commissioning/evidence/20261014-robokit-map-acceptance。生产代码/发布程序不修改。原样例源在docs/vendor/robokit/map-format-20261014/smap-master/maps；既有NU1902/NU1903依赖告警在构建日志保留。
