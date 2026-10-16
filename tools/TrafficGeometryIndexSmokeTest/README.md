# 几何空间索引验证

从 DispatchingSystem 运行 `dotnet run --project tools/TrafficGeometryIndexSmokeTest`。

只使用内存地图和合成核定包络，不连接设备或数据库。固定种子混合直线/Bezier与朴素遍历比对，覆盖长路段、负坐标、切触、圆弧保守范围和退化点；验证5000段稀疏路线、1000段密集路线及比较/遍历/报告预算。时间仅记录，不作为性能通过条件，不代表现场地图验收。

索引是平衡包围盒树，不按网格中心分配长边；父节点包围盒包含所有子边，使用与朴素模型相同epsilon，因此完整遍历不会遗漏叶子交叠。候选按原路线顺序输出。UseSpatialIndex=false保留参考遍历；Comparisons统计精检候选，IndexVisits统计树查询次数。索引树规模受MaximumEdgesPerRoute限制，遍历另受MaximumIndexVisits限制；预算耗尽Complete=false且不能判定分离。
