# Robokit文档示例验证

在DispatchingSystem目录运行：

```powershell
python tools/RobokitDocumentExamples/extract_examples.py
dotnet run --project tools/RobokitDocumentExamples -- docs/commissioning/evidence/20261014-robokit-document-examples
python tools/RobokitDocumentExamples/make_report.py
```

提取器不修改原始档案。JSON仅去零宽字符与字符串外注释，十六进制长度不匹配的报文留为文档／抓取问题。原文同页请求和响应独立组合，不假定是同一场景；计数包含重复页面。

工具引用生产Connectors.Agv及链接的生产Codec，模拟器只监听本机随机端口，不读取现场配置／数据库。响应报文独立构造、拆分发送；验证生产发包JSON、报文头及错误码。公共客户端业务验证中的请求编号替换、按字段表构造请求以及项目逐段编号策略明确记录在结果中，响应保持文档原值。

未接入类别通道不发送；结构或传输通过不能证明固件业务接受、公共接口或产品界面已实现全部API。1100没有完整响应夹具，不补造后称原样验证。当前工具不是交互模拟器，不是现场控制工具。

测试和报告都保存在docs/commissioning/evidence/20261014-robokit-document-examples，主报告位于docs/commissioning/Robokit协议文档示例测试报告-20261014.md。分API报告覆盖所有解析到请求编号的页面，没有可测试示例的API也明确标注未验证。
