# 固定夹具回归

从DispatchingSystem目录执行：

```powershell
dotnet run --project tools/RobokitFixtureRegression
```

默认读取docs/examples/robokit-regression-v1。可指定其他本地夹具目录，但只连接工具自己创建的127.0.0.1随机监听；没有现场IP或端口参数。工具不读取生产通信设置或数据库，不写生产代码或发布配置。

生产SeerRobotClient、RobokitInspectionService、SeerAgvGateway及AgvStateCache参与。模拟活动地图与原任务链归属来自固定数据；不是持久化数据库或独立服务进程测试。状态／导航端口独立，模拟端检查请求字段及原段编号；响应报文独立构造、分块发送。1110按原文区分省略task_ids和显式空数组。

合成1100只涵盖项目使用字段，不代表厂家完整字段全集；文档1000/1301/1302/1303原样例单独留存并记录哈希，不组合成同一台设备。realDeviceReady始终false，真实样例仍缺。AllowCommands仅工具内用于验证模拟端的3066/3003，不影响生产开关。

结果、发包类型／端口、完整模拟交换JSON和报文头在docs/commissioning/evidence/20261014-robokit-fixed-fixtures。进程自动关闭自身监听，无需结束用户进程。

prepare_fixtures.py只用于从已审核的document-audit.json重建基准；不要对尚未审核的现场返回自动覆盖既有黄金夹具。真实样例填写单独模板并保留来源，确认后以新版本夹具导入，不把它标为厂家行为已经验证。

## 离线真实样例校验

取得真实完整脱敏文件后，从DispatchingSystem执行：

```powershell
python tools/RobokitFixtureRegression/validate_capture.py 真实样例.json --out docs/commissioning/evidence/robokit-received-001
python tools/RobokitFixtureRegression/test_validate_capture.py
```

输出目录必须尚不存在；既有证据不覆盖。提交文件按原始字节保存为submitted.json，并记录SHA256；review.json列错误、提示和待核定行为。待填模板会被拒绝，退出码2；schemaReady=true表示离线结构和关联校验通过，realDeviceValidated和behaviorVerified仍为false，不能自动发布成实车黄金夹具或开启现场控制。

实际采集后才能把origin改为real_device_read_only_capture，并按真实信息填写元数据。每份响应应保留完整脱敏对象，completeResponse=true仅为提交者声明，不是本工具证明字段全集完整。需前后两份1300及1022包围1301/1302/1303；省略、截取或换图批次不通过。厂家行为说明填写为含statement与source的对象，即便填写也仍需人工核定来源。

## 原图／车端／四段运输联合离线核对（2026-10-15）

新增`review_map_evidence.py`，输入只读采集包、原生二维Smap、本地导航图/草稿和核对计划，输出逐站坐标/朝向差、四段逐邻接路段和厂家行为待核定表。显式提供米制刚体变换，不猜坐标、厂家编号或MD5；未提供1303相邻直连观察不能用多点可达替代。默认拒绝模拟origin；明确合成演示必须加`--synthetic`，来源声明保持原值。

```powershell
python tools/RobokitFixtureRegression/review_map_evidence.py --capture docs/examples/robokit-map-cross-review-simulated/capture.json --smap docs/examples/robokit-map-cross-review-simulated/map.smap --local-map docs/examples/robokit-map-cross-review-simulated/local-map.json --plan docs/examples/robokit-map-cross-review-simulated/review-plan.json --synthetic --out docs/commissioning/evidence/my-map-review-001
python tools/RobokitFixtureRegression/test_review_map_evidence.py
```

输出目录不允许覆盖。逐项解释、模拟/负例报告及待填模板见[本批报告](../../docs/commissioning/Robokit原图车端与四段运输离线核对报告-20261015.md)。本工具只核对离线关联字段，不替代完整地图几何解析、安全验收或现场采集；全部设备验收/坐标对齐确认/行为核定/发送与上传许可保持false。原`validate_capture.py`命令行仍保持只接收声明真实来源，新增Python内部合成校验参数只供显式模拟核对调用。

已有报告现可在WPF系统设置→Robokit地图绑定→离线核对报告（只读）导入查看，保留伴随inputs，界面不重新计算地图、修改绑定或启用控制；使用及验证见docs/commissioning/地图绑定页离线报告只读导入与差异展示-20261015.md。
