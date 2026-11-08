# B07-1F 全厂 CAD 仿真地图构建器

`build_full_map.py` 根据核对后的下方有效 CAD 预览和固定渲染 ViewBox，
识别 305 个 CNC 点位，并以 CAD 原始毫米坐标除以 1000 建立米制导航拓扑。

## 建图口径

- 纳入 9 排 CNC：`26 + 36 + 37 + 36 + 36 + 37 + 24 + 37 + 36 = 305`。
- CAD 中前两排共 73 个重复设备图形不纳入已确认的 305 台数量。
- 五台 AGV、两个充电桩和倒料设备均使用 CAD 文字/设备锚点。
- 下方有效布局中的 10 个东侧码头、8 个 5T 点位、总控/调度及终端清洗、排屑、甩干、铝屑箱补充为 CAD 参考点；参考点不自动成为派车业务站。
- 横向取货通道位于背靠背 CNC 行之间；外围通道使用 CAD 机台区左右和南侧边界。
- 每台 CNC 向相邻通道偏移 1.1 m 建立容量 1 取货点。
- 路权资源包含 305 个 CNC、5 个待命位、1 个倒料位、1 个检修位、2 个充电位和交叉口；隔离演示主路段/主交叉口容量为 5，工位、等待位和倒料设备仍为容量 1。

CAD 未提供连续 AGV 中心线、单双向属性、转弯半径和真实障碍轮廓，因此生成的道路
用于隔离仿真拓扑分析，不能直接作为车端 SMAP、现场施工路线或实车地图传输依据。

## 回放视频

仿真报告产生 `replay.json` 后，可生成带全厂路网、车辆轨迹、当前步骤和等待原因的中文加速回放：

```powershell
python DispatchingSystem/tools/CadSimulationMap/render_replay_video.py `
  --replay <replay.json> `
  --report <report.json> `
  --output <simulation-replay.mp4>
```

视频用于快速复核，原始 JSON、控制器观察、数据库证据和日志仍是审计依据。

## 运行

在仓库根目录运行。该工具需要 `numpy`、`scipy` 和 `Pillow`。

```powershell
python DispatchingSystem/tools/CadSimulationMap/build_full_map.py `
  --cad-preview B07-1F下方有效布局_预览.png `
  --cad-dwg B07-1F下方有效布局_地图参考版.dwg `
  --cad-dxf B07-1F下方有效布局_地图参考版.dxf `
  --pdf 成都B07-1F-CNC数控铝屑自动回收系统-20260425.pdf `
  --output output `
  --generated-at 2026-11-06T17:45:00+08:00
```

后台发布并取得权威摘要后可追加：

```powershell
--map-id MAP-xxx --map-version-id MAPV-xxx --definition-hash SHA256
```

## 输出

- `cad-simulation-map-definition.json`：647 节点、1304 条有向道路、332 个资源。
- `areaAnnotations`：8 个业务分区、25 个 CAD 部署参考点和 1 个 CAD 原布局底图引用。
- `cad/b07-1f-lower-layout-reference.png`：随地图产物复制的下方有效 CAD 底图。
- `cad-station-points.json`：305 个 CNC 的预览像素、CAD 毫米和米制坐标。
- `cad-station-points.csv`：305 个 CNC 的 CAD 坐标、取货节点、资源、米制坐标和 PLC 起始地址。
- `cad-terminal-points.csv`：五车待命、双充电、倒料、检修和离位汇入节点的角色与坐标。
- `plc-cad-305-cnc-isolated.csv`：305 工位每站 8 信号，加倒料 5 信号，共 2445 行。
- `cad-simulation-configuration.template.json`：五车、305 工位、等待位、倒料位和点表绑定。
- `cad-simulation-scenario-305.json`：305 个工位进入一次排队积压。
- `cad-vector-map-preview.png`：完全由仿真地图数据绘制的矢量总览，不嵌入 CAD 图像层。
- `cad-full-map-preview.png`：CAD 点位、道路、五车待命、双充电和终端区叠加图。
- `source-manifest.json`：源文件摘要、ViewBox、识别数量和有向可达性结果。
- `cad-reference-points.json`：码头、5T、总控和终端工艺点的 CAD 坐标、实体句柄及“仅参考显示”权限。

PLC 点表全部标记为 `LabOnly`，只用于隔离仿真，不能作为现场 PLC 写入依据。
