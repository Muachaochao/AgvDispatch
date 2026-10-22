# 候车位离位只读检查

运行：dotnet run --project tools/WaitingBayDepartureSmokeTest -c Release

使用新建私有SQLite、合成三点两路地图、原入站Outbox快照及模拟权威进度/核定包络接口。验证全车离位、全部候车租约及后续倒料资源、采集期间变化和只读不写库；不连接真实设备，不发释放许可。

输出waiting-bay-departure-result.json包含逐项结果、私有数据库路径及通过/缺资料样例。生产没有注册本工具的模拟源。
