# 主工程数据接入契约

独立应用完整采用排策智演的 Vue 3、Element Plus、Three.js、ECharts 页面及 Java 算法后台。访问入口为 http://127.0.0.1:5270，Java API 为回环 5271。主工程数据代理固定使用 http://127.0.0.1:5199，路径前缀为 /dispatch-api。原生 API 仍使用 /api，原生实时通道使用 /ws。

## 页面与数据

“概览、车间地图、任务集、仿真运行、对比实验、用户管理、数字字典”采用原项目页面。“主工程接入”增加当前隔离仿真实例的三维观察、车辆、原任务详情、交通资源、方案启动、场景运行、终止编排及报告查询。轮询间隔为 1 秒，串行请求，离页取消。读取失败保留最后观察并显示断连，禁用写操作。图形坐标使用 X/Y 米制映射至 X/Z，主工程角度乘以 180/π 转为原组件使用的角度值。状态未知或失联单独显示。

“导入主工程地图”读取 /api/snapshot 及匹配活动版本的 /api/maps/{id}/workspace，要求 source=backend、连接有效、观察未过期、发布版本及摘要匹配、unit=m。仿真实例地图通过 /api/simulation 返回。米制完整定义经转换预览后另存为 Java 后台的独立路网。节点 ID 保留，agvSiteId 只用于别名匹配。禁用节点与道路跳过。未知节点类型映射为 JUNCTION，Station 映射为 WORKSTATION，Charging 映射为 CHARGER，Waiting 映射为 BUFFER。速度与长度必须大于零。原算法校验要求充电位和强连通路网，用户可显式选择实验初始停放点并在实验副本中标为 CHARGER，页面展示这一转换。强连通性及重复建边由原校验器检查。算法实验道路容量显式设为 1。

直线保持端点，贝塞尔与圆弧按 24 段生成独立实验节点及边，保持总距离和速度。转换结果记录原边到实验分段的映射。区域资源、厂家通行属性、PLC 条件、车体包络进入转换提示。来源、版本、摘要、原定义、转换提示及 ID 映射可下载为 JSON。三维观察使用原始地图的曲线采样，实时车辆保持反馈坐标。

原任务详情来自 /api/simulation/tasks/{id}。只提取已存在运输命令的路线端点，按当前导入图节点核对后预览为算法任务集。任务集保存采用 /api/taskset，保留原任务/命令 ID 的下载映射。九步任务详情在主工程接入页面按原数据展示。算法时钟 tick=100 ms，实验 makespan、waitTick 依旧显示原单位。PLC 循环耗时保留秒制。

## 主工程操作

GET /api/simulation、/tasks、/tasks/{id}、/snapshot、/resources、/report 读取隔离环境。POST /api/simulation/start、/stop 管理隔离后台，/scheme-start 提交已保存方案 ID，/run 提交页面选中的场景及轮次，/abort 中止场景后续编排。所有写操作使用页面显式按钮，操作期间禁用重复提交，操作失败展示后台原因。面板显示仿真实例名称与最后成功观察时间。

代理仅接受上述固定路由以及读取主工程活动地图所需的 /api/snapshot、/api/maps/{id}/workspace。原生 Java API 的 JWT 只发往 /api，主工程代理通过独立 fetch 调用。既有工程数据库、现场车辆通信配置与 5199 日常入口由主工程管理。

## 验证标准

<!-- runnable -->

```powershell
npm --prefix agv-frontend run type-check
npm --prefix agv-frontend run build
node --experimental-strip-types --test agv-frontend/tests/dispatch-adapter.test.ts
```

<!-- manual -->

登录后逐页核对原模板导航、地图编辑、任务集、暂停继续、实验图表和历史。主工程接入页面核对双车、弧度朝向、原任务身份和等待说明，导入预览核对曲线分段、摘要及未知字段提示。主工程不可达时应显示断连，操作保持禁用。转换后的算法实验结果按其输入副本解释。
