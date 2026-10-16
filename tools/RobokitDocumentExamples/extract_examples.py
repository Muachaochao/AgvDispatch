"""Extract traceable examples without modifying vendor files or repairing their semantics."""
from pathlib import Path
import re, json, hashlib, struct, collections

ROOT = Path(__file__).resolve().parents[2]
DOCS = ROOT / "docs/vendor/robokit/SEER_Wiki_Tree"
OUT = ROOT / "docs/commissioning/evidence/20261014-robokit-document-examples"
OUT.mkdir(parents=True, exist_ok=True)
pages, vectors = [], []

def uncomment(text):
    return re.sub(r'"(?:\\.|[^"\\])*"|//[^\n]*|/\*[\s\S]*?\*/',
                  lambda m: m.group(0) if m.group(0).startswith('"') else '', text)

for p in sorted(DOCS.rglob('*.md')):
    raw = p.read_bytes()
    s = re.sub(r'[\u200b-\u200f\ufeff]', '', raw.decode('utf-8-sig'))
    api = [(int(m[0]), m[1], m[2]) for m in re.findall(r'编号[:：]\s*(\d+)[\s\S]{0,160}?名称[:：]\s*(robot_\w+_(req|res))', s)]
    request_numbers = sorted(set(n for n, _, role in api if role == 'req'))
    blocks, frames, issues = [], [], []
    seen = set()
    for marker in re.finditer(r'(?:JSON复制|```json\s*\n)', s):
        tail = s[marker.end():]
        # Code labels may contain comments/blank lines before the JSON object.
        start = re.match(r'(?:\s|//[^\n]*\n)*([\{\[])', tail)
        if not start: continue
        offset = marker.end() + start.end() - 1
        candidate = s[offset:offset+100000]
        clean = uncomment(candidate)
        try:
            value, end = json.JSONDecoder().raw_decode(clean)
        except json.JSONDecodeError as error:
            signature = ("invalid", candidate[:120])
            if signature not in seen: issues.append({"line": s.count('\n',0,offset)+1,"kind":"JSON_PARSE_ERROR","message":str(error)})
            seen.add(signature)
            continue
        canon = json.dumps(value, ensure_ascii=False, sort_keys=True)
        definitions = list(re.finditer(r'名称[:：]\s*robot_\w+_(req|res)', s[:offset]))
        role = definitions[-1][1] if definitions else 'unknown'
        recent = list(re.finditer(r'(请求示例|响应示例)', s[:offset]))
        if recent and (not definitions or recent[-1].start() > definitions[-1].start()):
            role = 'req' if recent[-1][1] == '请求示例' else 'res'
        signature = (role, canon)
        if signature in seen: continue
        seen.add(signature)
        blocks.append({"role":role,"line":s.count('\n',0,offset)+1,"payload":value,
                       "commentNormalization":True,"json":clean[:end]})
    lines = s.splitlines()
    i = 0
    while i < len(lines):
        if not re.fullmatch(r'\s*5[Aa](?:\s+[0-9A-Fa-f]{2}){15,}\s*', lines[i]): i += 1; continue
        first = i
        hex_lines = [lines[i].strip()]; i += 1
        while i < len(lines):
            line = lines[i].strip()
            if not line or line.startswith('//'): i += 1; continue
            if re.fullmatch(r'[0-9A-Fa-f]{2}(?:\s+[0-9A-Fa-f]{2})*',line): hex_lines.append(line); i += 1; continue
            break
        data = bytes.fromhex(' '.join(hex_lines))
        key = ("hex", data.hex())
        if key in seen: continue
        seen.add(key)
        version, seq, size, kind = data[1], *struct.unpack('>HIH', data[2:10])
        body = data[16:]
        valid = size == len(body)
        frame = {"line":first+1,"hex":data.hex(),"version":version,"sequence":seq,"type":kind,
                 "declaredBytes":size,"actualBytes":len(body),"lengthMatches":valid}
        if not valid: issues.append({"line":first+1,"kind":"FRAME_LENGTH_MISMATCH","message":f"type={kind}, declared={size}, actual={len(body)}"})
        if valid and body:
            try: frame['payload'] = json.loads(body.decode('utf-8'))
            except (ValueError,UnicodeError): frame['binaryOrInvalidJson'] = True
        frames.append(frame)
    # Supplement JSON bodies from valid raw examples, keeping raw type as authority.
    for frame in frames:
        if frame['lengthMatches'] and 'payload' in frame:
            role = 'req' if frame['type'] < 10000 else 'res'
            if not any(b['role']==role and b['payload']==frame['payload'] for b in blocks):
                blocks.append({"role":role,"line":frame['line'],"payload":frame['payload'],"fromHex":True})
    # Explicit JSON comments beside hex examples are a separate textual example, not a repaired raw frame.
    for match in re.finditer(r'//\s*(\{[^\n]+\})', s):
        try: value=json.loads(match[1])
        except ValueError: continue
        if 'source_id' in value or 'task_ids' in value or 'map_names' in value:
            if not any(b['role']=='req' and b['payload']==value for b in blocks):
                blocks.append({"role":"req","line":s.count('\n',0,match.start())+1,"payload":value,"fromComment":True})
    page = {"path":p.relative_to(DOCS).as_posix(),"sha256":hashlib.sha256(raw).hexdigest(),
            "requestNumbers":request_numbers,"apiDefinitions":api,"jsonExamples":blocks,"rawFrames":frames,"issues":issues}
    pages.append(page)
    if len(request_numbers) != 1: continue
    number = request_numbers[0]
    requests = [b for b in blocks if b['role']=='req']
    responses = [b for b in blocks if b['role']=='res']
    if number == 1301:
        responses=[b for b in responses if isinstance(b['payload'],dict) and ('stations' in b['payload'] or 'ret_code' in b['payload'])]
    parameterless = any(f['type']==number and f['declaredBytes']==0 and f['lengthMatches'] for f in frames)
    if not requests and parameterless: requests = [{"line":0,"payload":None,"parameterless":True}]
    for request in requests:
        for response in responses:
            # Pairing means generic transport compatibility, not that two independent examples form a scenario.
            vectors.append({"page":page['path'],"sha256":page['sha256'],"type":number,
                            "request":request,"response":response,"pairing":"same-page independent examples"})
summary = {"pages":len(pages),"apiPages":sum(bool(p['requestNumbers']) for p in pages),
           "jsonExamples":sum(len(p['jsonExamples']) for p in pages),"rawFrames":sum(len(p['rawFrames']) for p in pages),
           "documentIssues":sum(len(p['issues']) for p in pages),"candidatePairs":len(vectors)}
(OUT/'document-audit.json').write_text(json.dumps({"summary":summary,"pages":pages},ensure_ascii=False,indent=2),encoding='utf-8')
(OUT/'vectors.json').write_text(json.dumps(vectors,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(summary))
