"""Build traceable reports from completed test outputs; never modify vendor examples."""
from pathlib import Path
import json, csv, collections, hashlib

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / 'docs/commissioning/evidence/20261014-robokit-document-examples'
VENDOR = ROOT / 'docs/vendor/robokit'
audit = json.loads((OUT/'document-audit.json').read_text(encoding='utf-8'))
tests = json.loads((OUT/'example-test-results.json').read_text(encoding='utf-8'))
manifest = json.loads((VENDOR/'SEER_Wiki_Tree-refresh-manifest.json').read_text(encoding='utf-8-sig'))
index = json.loads((VENDOR/'SEER_Wiki_Tree-api-index.json').read_text(encoding='utf-8'))

def table_csv(path, fields, rows):
    with path.open('w', encoding='utf-8-sig', newline='') as f:
        w=csv.DictWriter(f, fields, extrasaction='ignore'); w.writeheader(); w.writerows(rows)

rows=tests['Results']
table_csv(OUT/'逐项测试结果.csv', ['Kind','Name','Page','Line','Type','RequestLine','ResponseLine','Source','Status','Detail'],rows)
issues=[dict(Page=p['path'],Sha256=p['sha256'],**i) for p in audit['pages'] for i in p['issues']]
table_csv(OUT/'文档示例异常.csv', ['Page','Sha256','line','kind','message'],issues)
coverage=[]
for p in audit['pages']:
    matched=[r for r in rows if r.get('Page')==p['path'] or r.get('Source','').split(':')[0]==p['path']]
    coverage.append({'Page':p['path'],'Sha256':p['sha256'],'RequestAPIs':','.join(map(str,p['requestNumbers'])),
       'JSONExamples':len(p['jsonExamples']),'RawFrames':len(p['rawFrames']),'Issues':len(p['issues']),
       'Results':len(matched),'Scope':'见逐项结果' if matched else '无自动配对测试；不能视为通过'})
table_csv(OUT/'逐页覆盖清单.csv',list(coverage[0]),coverage)

api_dir=OUT/'分API报告'; api_dir.mkdir(exist_ok=True)
numbers=sorted({n for p in audit['pages'] for n in p['requestNumbers']})
api_rows=[]
for n in numbers:
    pages=[p for p in audit['pages'] if n in p['requestNumbers']]
    page_names={p['path'] for p in pages}
    rs=[r for r in rows if r.get('Type')==n or (r.get('Kind')=='raw_frame' and r.get('Page') in page_names)]
    counts=collections.Counter(r['Status'] for r in rs)
    source_lines=['- '+p['path']+'；SHA256 `'+p['sha256']+'`' for p in pages]
    names=sorted({d[1] for p in pages for d in p['apiDefinitions'] if d[0]==n})
    lines=[f'# Robokit API {n} 文档示例测试报告', '', '日期：2026-10-14。使用本地新版档案；仅本机模拟，没有实车结论。',
       '', '原文API名称：'+', '.join(names), '', '## 来源', '', *source_lines, '', '## 结果', '',
       '| 分类 | 项数 |','|---|---:|',*[f'| {k} | {v} |' for k,v in sorted(counts.items())], '',
       '无结果表示没有可自动配对的完整示例，不代表通过。原始帧按页附列，可能同时含请求、响应及同页其他报文；以其Type为准。',
       'PASS_HEADER_STRUCTURE仅核对报文头结构；PASS_TRANSPORT_EXAMPLE仅证明当前通道JSON收发与错误码处理；PASS_BUSINESS_ASSERTION才包含记录中的业务断言。',
       '', '## 逐项证据', '', '| 类别 | 原文行／来源 | 报文Type | 状态 | 说明 |', '|---|---|---|---|---|']
    for r in rs:
        location=r.get('Source') or str(r.get('Line') or (str(r.get('RequestLine',''))+' → '+str(r.get('ResponseLine',''))))
        lines.append('| '+' | '.join(str(x).replace('|','\\|').replace('\n',' ') for x in [r['Kind'],location,r.get('Type',''),r['Status'],r.get('Detail','')])+' |')
    lines+=['', '## 本页文档／抓取异常', '']
    lines += [f"- {p['path']}:{i['line']}：{i['kind']}，{i['message']}" for p in pages for i in p['issues']] or ['没有检测到提取器所识别的异常；不等于文档全部正确。']
    lines+=['', '完整原始请求、响应及配对依据见同级上目录vectors.json；没有把不同示例解释为同一次机器人事务。']
    (api_dir/f'API-{n}.md').write_text('\n'.join(lines)+'\n',encoding='utf-8')
    api_rows.append(f'| [{n}](分API报告/API-{n}.md) | {len(pages)} | {counts.get("PASS_TRANSPORT_EXAMPLE",0)} | {counts.get("PASS_BUSINESS_ASSERTION",0)} | {counts.get("NOT_IMPLEMENTED_CHANNEL",0)} | {counts.get("DOC_INVALID_LENGTH",0)} |')
(OUT/'分API报告索引.md').write_text('# 分API报告索引\n\n按原文请求编号归类；重复页面合并展示，页数不是独立API数。无测试项不表示通过。\n\n| API | 原文页数 | JSON收发通过 | 业务断言通过 | 未接入通道 | 原始帧长度异常 |\n|---|---:|---:|---:|---:|---:|\n'+'\n'.join(api_rows)+'\n',encoding='utf-8')

summary=tests['Summary']; counts=collections.Counter(r['Status'] for r in rows)
unique_transport=sorted({r['Type'] for r in rows if r['Status']=='PASS_TRANSPORT_EXAMPLE'})
text=f'''# Robokit协议文档示例测试报告（2026-10-14）

本轮已完成新版协议归档核对、全部Markdown自动扫描，以及实际生产客户端参与的本机请求／响应模拟。测试发现0项实现断言失败；这不等于全部厂家API已接入，也不等于实车验收通过。

## 档案与执行环境

- 来源：`F:/Industrial_AGV_System/docs/protocols/SEER_Wiki_Tree`。
- 当前档案：`DispatchingSystem/docs/vendor/robokit/SEER_Wiki_Tree`，467文件（465 Markdown、2 JSON），逐文件SHA256匹配；见[刷新清单](../vendor/robokit/SEER_Wiki_Tree-refresh-manifest.json)。
- 原项目版本已保留在`{manifest['PreviousArchive']}`。源目录移动受到Windows占用锁阻止，因此采用复制；源目录仍保留，未结束用户进程。
- 仅监听127.0.0.1随机端口，不读取生产通信配置、不访问现场PLC／AGV、不操作现场地图和数据库。没有生产代码变更，没有更新发布程序，自动开发任务保持暂停。
- `tools/RobokitDocumentExamples`引用实际Connectors.Agv，原始报文头使用链接的生产SeerProtocolCodec；收发使用实际SeerRobotClient。模拟响应报文头独立构造，拆分发送头和正文，核对生产请求字段、长度、编号、响应正文及非零ret_code拒绝行为。
- 工具构建0错误、0警告，运行退出码0。构建／运行日志见下方证据目录；本轮没有重复未变化的既有大套件。

## 测试结果

| 验证层 | 实际结果 | 可得结论 |
|---|---:|---|
| 原文Markdown扫描 | 465页 | 扫描全体本地MD；不是人工逐条验收 |
| 提取JSON示例 | 417段 | 含重复页面；按页面与语义局部去重 |
| 提取原始十六进制帧 | 385段 | 包括请求、响应及非JSON内容 |
| 生产报文头结构核对 | {summary['FramePass']}通过 | 核对前10字节同步／版本／序号／长度／类型；不证明保留区、压缩或二进制业务支持 |
| 原始帧长度异常 | {summary['DocumentInvalidFrames']}处 | 声明长度与提取正文不符；未修补后冒充原样测试，可能是文档或抓取缺损 |
| 同页请求／响应候选组合 | 199组 | 不同示例独立配对，只作通用收发测试，不证明因果一致 |
| 实际客户端JSON收发 | {summary['TransportPass']}组通过 | 覆盖{len(unique_transport)}个请求编号；不证明这些API有产品操作入口 |
| 项目未接入类别通道 | {summary['UnsupportedChannels']}组 | 单独列为未测试，未新增控制／配置／其他通道 |
| 公共客户端业务断言 | {summary['BusinessPass']}项通过 | 下表所列任务解析、取消字段与逐段发包 |
| 实现断言失败 | {summary['ImplementationFailures']}项 | 当前测试范围未发现失败 |
| JSON提取失败 | 51处 | 语法／占位符／片段或提取歧义，未强行修复；需回原文检查 |

319个结构通过＋130组通用收发通过＋10项业务通过，共459项通过记录；另外66个长度异常和69个未接入通道记录分别统计。51处JSON提取失败是扫描异常，不混入实现失败计数。重复页、独立示例组合均计数，因此不能宣称459个独立API通过。

## 业务层具体核对

| 场景 | 项数 | 输入与断言 |
|---|---:|---|
| 1110整批任务解析 | 2 | 保留原响应，按响应实际编号构造请求；编号、status、type全部准确 |
| 1110单任务查找 | 3 | 响应原值不改，只替换请求编号；准确找到指定原任务 |
| 1110不匹配整链拒绝 | 2 | 请求SEER78909，响应SEER78914／15；原值不改，项目必须拒绝整链确认 |
| 3003定向取消 | 2 | 按原文字段表构造smartCancel=false／true和taskId；响应ret_code=0；不等于取消完成 |
| 3066逐段发包 | 1 | 文档LM1→LM2→AP1、末段JackHeight=0.2；中间编号按项目策略改为12344322-01，末段12344322保持；路径与动作准确 |

3066文档原请求的两个编号分别为12344321和12344322；通用测试保持原请求，业务测试则明确使用项目确定性编号策略。没有悄悄修改样例后宣称字节完全一致。取消确认延迟、部分接收、级联规则、历史保留、断电重启仍不能由ret_code=0模拟响应推导。

## 重点差异与缺口

1. **1110示例编号不同**：查询请求与响应并非同一编号集合。若用于整链确认，应拒绝额外编号。此次拒绝断言已通过；不能要求实现接受不匹配响应。
2. **1302示例MD5仅31位**：`9cffdb59dd1f7adc4c41fb425f4abdb2`。通用JSON可以传输该值，但当前地图绑定要求32位十六进制，不应为适配此例降低校验。真实MD5需只读取得并人工确认。
3. **1303原始请求长度为0但附正文**：报文结构存在矛盾。旁边JSON注释作为独立文本请求测试，原始帧仍记长度异常；请求LM1→LM10与返回LM954→LM951也不能作同一线路验收。
4. **3003无参数示例与定向字段表并存**：通用层保留无数据示例；项目公共方法保持明确taskId。两种形式在本车固件的取消范围及级联行为必须厂家核定，不能直接用无参样例取消现场车辆。
5. **3051复杂字段只证明传输**：原文含`direction `末尾空格及PGV字段拼写差异；项目公共方法仅实现当前所需动作字段。传输通过不等于全部运动参数、脚本和动作已接入或被车端接受。
6. **1100缺完整响应样例**：提取到请求但没有可配对完整响应，因此本轮没有将1100轮询标成文档原样端到端通过；需要本车真实脱敏返回补齐。
7. **1301子对象与整响应区别**：单站点字段对象不能冒充完整stations响应，本轮配对已过滤此类局部示例。
8. **未接入通道与其他协议**：当前项目使用状态与导航TCP通道；控制、配置、其他端口以及Web／Modbus等页只能留档／结构扫描，不视为已实现。无需为通过全库测试而增加现场控制范围。
9. **离线资料仍有缺失**：新版索引检测到{index['summary']['remoteImageReferences']}处远程图片引用、{index['summary']['blobImageReferences']}处blob引用、{index['summary']['pagesWithBlankTableCells']}页空表格单元格和{index['summary']['pagesWithLoadingMarker']}页加载提示；未完整留存附件及外链地图格式。原目录捕获236节点的检查来自上一批，不代表新版官方全库覆盖已证明。

## 逐份报告与原始证据

- [分API报告索引](evidence/20261014-robokit-document-examples/分API报告索引.md)：{len(numbers)}个解析到请求编号的API分别留报告，包含没有可执行配对的API，明确其未验证状态。
- [逐项测试结果CSV](evidence/20261014-robokit-document-examples/逐项测试结果.csv)：每项结果、请求／响应原文行号和原因。
- [逐页覆盖CSV](evidence/20261014-robokit-document-examples/逐页覆盖清单.csv)：465页逐一SHA256、示例数、测试数、未覆盖说明。
- [文档示例异常CSV](evidence/20261014-robokit-document-examples/文档示例异常.csv)：117处自动检测异常（66长度＋51JSON提取），不作为117个厂家实现缺陷。
- [机器结果JSON](evidence/20261014-robokit-document-examples/example-test-results.json)、[提取审计](evidence/20261014-robokit-document-examples/document-audit.json)、[实际测试向量](evidence/20261014-robokit-document-examples/vectors.json)、[运行日志](evidence/20261014-robokit-document-examples/example-run.log)、[构建日志](evidence/20261014-robokit-document-examples/build.log)。

原文请求／响应通过语义JSON比较：去除零宽字符及字符串外注释，不改字段、编号或数值；重新序列化后模拟，不能称原文逐字节重放。十六进制帧则独立审计原始字节。分类和同页配对为自动提取，存在遗漏和歧义，应结合来源行复核；未把AI摘要或空表格当字段契约。

## 复现与后续

在DispatchingSystem目录依次执行：

```powershell
python tools/RobokitDocumentExamples/extract_examples.py
dotnet run --project tools/RobokitDocumentExamples -- docs/commissioning/evidence/20261014-robokit-document-examples
python tools/RobokitDocumentExamples/make_report.py
```

下一批优先补1100完整返回和本车1000版本、1301站点、1302地图MD5、1303实际路线的脱敏样例，形成固定回归夹具；然后核定1110未知任务／历史清除、3066部分接收及3003级联规则。当前已有资料可以继续做隔离模拟；正式现场只读联调需要确认实际固件与连接信息，生产控制仍保持关闭。
'''
(ROOT/'docs/commissioning/Robokit协议文档示例测试报告-20261014.md').write_text(text,encoding='utf-8')
print(json.dumps({'apiReports':len(numbers),'resultRows':len(rows),'coveragePages':len(coverage),'issues':len(issues)},ensure_ascii=False))
