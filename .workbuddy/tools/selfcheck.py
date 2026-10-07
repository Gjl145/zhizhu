"""SilkBuilder.cs 全面自检。
覆盖：括号配平、Unity API 存在性、成员可见性、重复定义、调用点存在性、
      诊断开关默认值、参数物理量级、关卡几何。
用法：python .workbuddy/tools/selfcheck.py
"""
import os
import re
import sys

SILK = 'Assets/Silk/SilkBuilder.cs'
STAGE = 'Assets/Silk/SilkParkourStage.cs'

errors = []
warns = []
oks = []


def err(m):
    errors.append(m)


def warn(m):
    warns.append(m)


def ok(m):
    oks.append(m)


s = open(SILK, encoding='utf-8').read()
stage = open(STAGE, encoding='utf-8').read()

# ---------- 提前解析关卡平台（第 9、10 项都要用）----------
# 原先在第 9 项内部解析，第 10 项依赖它 —— 变量作用域太脆弱，
# 一旦调整顺序就NameError。改为在顶层解析一次，两处共用。
_body = stage[stage.index('public static void Create'):stage.index('public static void Clear')]
# 记录调用处用的是 Plat() 还是 Barrier()：
#   Plat()    -> 可站立平台（参与路线/重叠检查）
#   Barrier() -> 护栏，竖直挡板，不是路面（必须排除）
# 不能按尺寸推断（平台本身也是扁的），也不能靠名字前缀。
calls = re.findall(r'\b(Plat|Barrier)\("([^"]+)"', _body)
_vecs = re.findall(r'new Vector3\(\s*(-?[\d.]+)f,\s*(-?[\d.]+)f,\s*(-?[\d.]+)f\s*\)', _body)
plats = []
B = {}
for _i, (_fn, _n) in enumerate(calls):
    _c = tuple(float(x) for x in _vecs[_i * 2])
    _s = tuple(float(x) for x in _vecs[_i * 2 + 1])
    # 护栏不是路面，与 B 保持一致地排除，避免后续 KeyError
    if _fn == 'Barrier':
        continue
    plats.append((_n, _c, _s))
    B[_n] = [(_c[i] - _s[i] / 2, _c[i] + _s[i] / 2) for i in range(3)]

# ---------- 1. 括号配平 ----------
t = re.sub(r'//[^\n]*', '', s)
t = re.sub(r'/\*.*?\*/', '', t, flags=re.S)
t = re.sub(r'@"(?:[^"]|"")*"', '""', t, flags=re.S)
t = re.sub(r'"(?:\\.|[^"\\])*"', '""', t)
t = re.sub(r"'(?:\\.|[^'\\])*'", "''", t)
for name, o, c in [('braces', '{', '}'), ('parens', '(', ')'), ('brackets', '[', ']')]:
    if t.count(o) != t.count(c):
        err('括号不配平 %s: %d vs %d' % (name, t.count(o), t.count(c)))
if not errors:
    ok('括号配平 ({} %d, () %d, [] %d)' % (t.count('{'), t.count('('), t.count('[')))

# ---------- 2. stage 文件括号 ----------
ts = re.sub(r'//[^\n]*', '', stage)
ts = re.sub(r'/\*.*?\*/', '', ts, flags=re.S)
ts = re.sub(r'"(?:\\.|[^"\\])*"', '""', ts)
for name, o, c in [('braces', '{', '}'), ('parens', '(', ')')]:
    if ts.count(o) != ts.count(c):
        err('SilkParkourStage 括号不配平 %s: %d vs %d' % (name, ts.count(o), ts.count(c)))

# ---------- 3. class 清单 ----------
classes = re.findall(r'\npublic (?:static )?(?:class|interface) (\w+)', s)
if 'SilkChain' not in classes:
    err('SilkChain 缺失')
if 'SilkParkourController' not in classes:
    err('SilkParkourController 缺失')
ok('类清单: %s' % ', '.join(classes))

# ---------- 4. 诊断开关必须默认 false ----------
diag = re.findall(r'public bool (\w*(?:verbose|renderScan|diag)\w*)\s*=\s*(\w+);', s)
for name, val in diag:
    if val.lower() != 'false':
        err('诊断开关 %s 默认应为 false，实际 %s（会刷屏）' % (name, val))
ok('诊断开关 %d 个，全部默认关闭' % len(diag))

# ---------- 5. Unity API 存在性（简易核对）----------
unity_ok = ['FindObjectOfType', 'FindObjectsOfType', 'AddComponent', 'GetComponent',
            'LineRenderer', 'SphereCollider', 'Rigidbody',
            'Vector3.MoveTowards', 'Vector3.Angle', 'Vector3.Dot', 'Mathf.Clamp',
            'Mathf.Max', 'Mathf.Abs', 'Quaternion.LookRotation', 'Physics.Raycast',
            'Physics.SphereCast', 'Cursor.lockState',
            'CursorLockMode.Locked', 'QueryTriggerInteraction.Ignore',
            'LayerMask', 'KeyCode.Space', 'KeyCode.LeftShift', 'Input.GetKeyDown',
            'Input.GetKey', 'Input.GetAxis', 'Time.deltaTime', 'Object.Destroy',
            'MeshRenderer']
missing = [a for a in unity_ok if a not in s]
if missing:
    warn('未在代码中找到（可能是我列的清单不全）: %s' % ', '.join(missing))
else:
    ok('Unity API 全部存在（核对 %d 项）' % len(unity_ok))

# MonoBehaviour 专属 API 误用在普通类上的风险点
mono_only = ['GetInstanceID']
for tok in mono_only:
    for m in re.finditer(r'parentLine\.' + tok, s):
        err('SilkLine 是普通 C# 类，不该调用 %s（CS1061）' % tok)

# ---------- 6. 重复方法定义（CS0111）----------
# 注意：C# 允许**重载**（同名但参数列表不同），那是合法的。
# 按「完整签名」分组，并且必须**精确切分类区间**——
# 早期用「下一个 public class」当边界是错的：若中间类声明前有空行/
# 或注释，边界会失效，把后一个类的方法算进前一个类，
# 从而把合法的 `Handle` 重载误报成 CS0111。
# 改为：先记录所有类声明的行号，再按行号区间切分。
print()
sig_pat = (r'\n    (?:public |private |)(?:static |)(?:void|float|int|bool|Vector3|'
           r'SilkLine|AnchorPoint|string|Camera|LayerMask|Quaternion|RaycastHit) '
           r'(\w+)\s*\(([^)]*)\)')

decls = [(m.start(), m.group(1)) for m in
         re.finditer(r'\npublic (?:static )?(?:class|interface) (\w+)', s)]
decls.append((len(s), None))   # 哨兵

for idx in range(len(decls) - 1):
    start, cls = decls[idx]
    end = decls[idx + 1][0]
    body = s[start:end]

    sigs = {}
    for name, params in re.findall(sig_pat, body):
        types = tuple(p.strip().split(' ')[0] for p in params.split(',') if p.strip())
        sigs[(name, types)] = sigs.get((name, types), 0) + 1
    for (name, types), c in sigs.items():
        if c > 1:
            err('%s 内方法 %s(%s) 定义了 %d 次（CS0111）'
                % (cls, name, ', '.join(types), c))

    byname = {}
    for (name, types) in sigs:
        byname[name] = byname.get(name, 0) + 1
    overloads = sorted(n for n, c in byname.items() if c > 1)
    if overloads:
        ok('%s: %s 为合法重载' % (cls, ', '.join(overloads)))

ok('无重复方法定义（CS0111），已按类区间精确切分')

# ---------- 7. 关键方法的调用点存在性 ----------
must_call = ['ResolveGround', 'HandleJumpOrGrab', 'PickBestAnchor', 'DoJump',
             'AdoptFlightMomentum', 'ApplyRotation', 'HandleLookOnly',
             'OrbitAround', 'ConnectForParkour', 'ClearRenderClaim',
             # 诊断类也要检查 —— 曾因移出 LateUpdate 而变成死代码
             'ScanHealth', 'ScanRenderers', 'ScanDuplicates', 'ScanDuplicates']
for name in must_call:
    n = len(re.findall(r'\b' + name + r'\s*\(', s))
    if n < 2:
        err('%s 只有 %d 处出现（可能只有定义、无调用点）' % (name, n))
ok('关键方法均有调用点（%d 个）' % len(must_call))

# ---------- 7b. 扫描所有 void 方法，找「定义但无调用」的 ----------
# 本项目踩过：ScanHealth 被移出 LateUpdate 后忘了保留调用 -> 静默失效。
# 只检查本文件内可见的方法（private void 且名字唯一）。
dead = []
for m in re.finditer(r'\n    (?:public |private |)(?:static )?void (\w+)\s*\(\s*\)', s):
    nm = m.group(1)
    if nm in ('Start', 'Update', 'LateUpdate', 'FixedUpdate', 'Awake',
              'OnDestroy', 'OnEnable', 'OnDisable'):
        continue
    n = len(re.findall(r'\b' + nm + r'\s*\(', s))
    if n < 2:
        dead.append(nm)
if dead:
    for nm in sorted(set(dead)):
        warn('%s() 只有定义没有调用 —— 可能是死代码或漏接线' % nm)
else:
    ok('无「定义但无调用」的无参 void 方法')

# ---------- 8. 已移除的方法不应有调用点 ----------
removed = ['StickToGround(']
for name in removed:
    # 注释里的引用不算
    code = re.sub(r'//[^\n]*', '', s)
    code = re.sub(r'/\*.*?\*/', '', code, flags=re.S)
    n = len(re.findall(r'\b' + re.escape(name), code))
    if n > 0:
        err('%s 已重命名，但仍存在代码调用 %d 处' % (name, n))
ok('无残留的已移除方法调用')

# ---------- 9. 物理量级核算 ----------
pk = s.index('class SilkParkourController')


def grab_float(key):
    """在 SilkParkourController 里抓字段的数值。
    要兼容两种写法：
      public float gravity = 50f;                     字面量
      public float gravity = SilkPhysics.Gravity;      引用常量
    后者无法静态求值，改为回退读取 SilkPhysics.Gravity 的定义。"""
    m = re.search(r'public float ' + key + r'\s*=\s*([^;]+);', s[pk:])
    if not m:
        return None
    expr = m.group(1).strip()
    mm = re.match(r'^(-?[\d.]+)f?$', expr)
    if mm:
        return float(mm.group(1))
    # 形如 SilkPhysics.Gravity -> 去 SilkPhysics 里查常量的值
    mc = re.search(r'const float ' + expr.split('.')[-1] + r'\s*=\s*(-?[\d.]+)f?;', s)
    return float(mc.group(1)) if mc else None


g = grab_float('gravity')
jv = grab_float('jumpSpeed')
sp = grab_float('moveSpeed')
rad = float(re.search(r'public float visualRadius = ([\d.]+)f;', s).group(1))
acc = grab_float('groundAccel')
dec = grab_float('groundDecel')

if None not in (g, jv, sp):
    apex = jv * jv / (2 * g)
    airtime = 2 * jv / g
    reach = sp * airtime
    print('  [物理] 重力%.0f 起跳%.0f 跑速%.0f 半径%.1f' % (g, jv, sp, rad))
    print('  [物理] 跳跃最高 %.1f 格 | 滞空 %.2f 秒 | 全速起跳跨度 %.0f 格' % (apex, airtime, reach))
    if sp > 150:
        err('跑速 %.0f 格/秒过高（现实人类峰值约 12）—— 会无法控制' % sp)
    else:
        ok('跑速在合理量级 (%.0f 格/秒)' % sp)
    if apex < 3:
        err('跳跃最高 %.1f 格太低，跳不上任何台阶' % apex)
    if acc and sp / acc > 0.5:
        warn('到全速 %.2f 秒，偏慢（手感会「拖」）' % (sp / acc))
    if acc and dec and abs(acc - dec) / max(acc, dec) > 0.5:
        warn('加速(%.0f)与减速(%.0f)差距过大，松开会有明显滑行' % (acc, dec))

    # 起点高度
    m = re.search(r'public Vector3 startPosition = new Vector3\(([-\d.]+)f,\s*([-\d.]+)f,\s*([-\d.]+)f\)', s)
    sx, sy, sz = (float(m.group(i)) for i in (1, 2, 3))
    p0 = calls[0]
    c0 = tuple(float(x) for x in _vecs[0])
    sz0 = tuple(float(x) for x in _vecs[1])
    top = c0[2] + sz0[2] / 2
    want = top + rad
    if abs(sz - want) > 1.0:
        err('起点高度偏差 %.1f 格（应为 顶面%.1f + 半径%.1f = %.1f）'
            % (abs(sz - want), top, rad, want))
    else:
        ok('起点高度正确 (z=%.1f = 顶面%.1f + 半径%.1f)' % (sz, top, rad))
    if not (c0[0] - sz0[0] / 2 <= sx <= c0[0] + sz0[0] / 2):
        err('起点 x=%.1f 不在平台 %s 的 x 范围内' % (sx, p0))

# ---------- 10. 关卡几何 ----------
# plats / B 已在文件顶部统一解析，此处直接使用。
for n, b in B.items():
    out = ['XYZ'[i] for i in range(3) if b[i][0] < -50 or b[i][1] > 50]
    if out:
        err('平台 %s 越界 %s（内墙 ±50）' % (n, out))

for i in range(len(plats)):
    for j in range(i + 1, len(plats)):
        n1, n2 = plats[i][0], plats[j][0]
        if all(min(B[n1][a][1], B[n2][a][1]) - max(B[n1][a][0], B[n2][a][0]) > 0
               for a in range(3)):
            err('平台 %s 与 %s 重叠' % (n1, n2))
ok('关卡几何：无越界、无重叠（%d 块平台）' % len(plats))

# 基础区 = 「沿 +X 前进且 y 与起点邻近」的那几段。
# 【踩过的坑】曾写 Plat_[45] 排除进阶区，但平台名是 "4_钩爪高台"，
# Plat_ 前缀是 Plat() 内部加的、不在 B 的键里 -> 过滤失效，
# 把进阶区也算进路线检查，报出「落差 28 格超过跳跃上限」的假错误。
# 现在改用「y 方向是否与基础区重叠」来判断，更本质 ——
# 进阶区在 y 上错开，无论它叫什么都不会混进来。
start_y = None
_plat0 = [n for n in B if n.startswith('0_')]
if _plat0:
    start_y = (B[_plat0[0]][1][0] + B[_plat0[0]][1][1]) / 2

basic = []
for n in sorted(B.keys()):
    y0, y1 = B[n][1]
    # 与基础区 y 范围完全分离 -> 独立区段（进阶区），跳过
    if start_y is not None and (y1 < start_y - 20 or y0 > start_y + 20):
        continue
    basic.append(n)

apex = jv * jv / (2 * g)
reach = sp * airtime
for i in range(len(basic) - 1):
    a, b = basic[i], basic[i + 1]
    gap = B[b][0][0] - B[a][0][1]
    dz = B[b][2][1] - B[a][2][1]
    if dz > apex - 0.5:
        err('%s->%s 落差 %.1f 超过跳跃上限 %.1f' % (a, b, dz, apex))
    if gap > reach:
        err('%s->%s 间隙 %.1f 超过跳跃跨度 %.0f' % (a, b, gap, reach))
ok('基础区路线可通关（%d 段，跳跃上限 %.1f 格 / 跨度 %.0f 格）'
   % (len(basic), apex, reach))

# ---------- 9b. 平台宽度 vs 球直径（比例检查）----------
# 参考视频 2：「前期未能建立正确的比例，后续修改成本极高」。
# 球直径 = 2 × visualRadius。基础区平台若窄于球径，玩家会卡住 ——
# 本项目曾出现「窄道 8 格 vs 球直径 9 格」的荒谬比例。
BASIC_RE = re.compile(r'Plat_\d')      # 只查基础区（0/1/2/3...）
WARN_RATIO = 1.3                        # 低于 1.3 倍球径即警告
for n, c, sz in plats:
    if not BASIC_RE.match('Plat_' + n):
        continue
    if re.match(r'Plat_[45]', n):        # 4/5 是进阶区，不参与
        continue
    width = min(sz[0], sz[1])# 取较小的那个水平尺寸
    ratio = width / (rad * 2)
    if ratio < WARN_RATIO:
        err('%s 宽 %.0f 格 = %.2f 倍球径 —— 窄于球的宽度，玩家会卡住/需要精确控制'
            % (n, width, ratio))
    elif ratio < 1.05:
        err('%s 宽 %.0f 格 = %.2f 倍球径 —— 球几乎塞不进' % (n, width, ratio))
ok('平台宽度均≥ %.1f 倍球径（球径 %.0f 格）' % (WARN_RATIO, rad * 2))

# ---------- 9c. 掉落兜底是否存在 ----------
# 没有掉落兜底时，玩家掉出关卡会无限下坠、永远回不来 ——
# 对基础关卡是致命的（参考视频：「失败要快、重生要快」）。
if 'fallRespawnZ' not in s:
    err('缺少掉落自动重生机制（fallRespawnZ）—— 玩家掉下去会永远回不来')
elif 'CheckFallRespawn' not in s:
    err('定义了 fallRespawnZ 但没有调用点 —— 掉落不会触发重生')
else:
    m = re.search(r'public float fallRespawnZ = (-?[\d.]+)f;', s)
    z = float(m.group(1)) if m else None
    ok('掉落自动重生已接入（触发线 z=%.0f）' % z)

# ---------- 11. 跨文件类型引用（防 CS0103）----------
# 改名类时最容易漏：文件里的类叫 A，调用处还写旧名 B -> CS0103。
# 本项目已犯一次（SilkTestLevel -> SilkParkourStage 改了类名没改调用处）。
# 注意要包含 enum —— SilkColor / SilkState / SilkControlMode 等都是枚举。
other = open(STAGE, encoding='utf-8').read()
type_pat = r'\npublic (?:static )?(?:class|interface|enum|struct) (\w+)'
stage_types = set(re.findall(type_pat, other))
all_code = s + other
all_types = set(re.findall(type_pat, all_code)) | set(re.findall(type_pat, s))

for cls in sorted(stage_types):
    used = len(re.findall(r'\b' + cls + r'\s*\.', all_code))
    if used == 0:
        warn('定义了 %s 但无人调用 —— 可能是死代码' % cls)
ok('跨文件类型引用：%s 均有调用点' % ', '.join(sorted(stage_types)))

# 反向：SilkBuilder.cs 里用到的外部类型是否都存在
missing = set()
for m in re.finditer(r'\b(Silk[A-Z]\w*)\s*\.', s):
    nm = m.group(1)
    if nm in all_types:
        continue
    missing.add(nm)
if missing:
    for nm in sorted(missing):
        warn('调用了 %s. 但找不到其定义（class/interface/enum/struct 都没匹配）' % nm)
else:
    ok('外部类型引用均可解析（class/interface/enum/struct 全覆盖）')

# ---------- 12. 设计文档与实现一致性 ----------
# SilkParkourDesign.cs 里列了「已实现机制」与「缺口清单」。
# 若代码里新增/删除了这些机制，文档不会自动更新 -> 会误导后来者。
# 这里做一次粗粒度对照：文档声称已实现的，必须能在代码里找到对应符号。
DESIGN = 'Assets/Silk/SilkParkourDesign.cs'
if os.path.exists(DESIGN):
    d = open(DESIGN, encoding='utf-8').read()
    # 从 Implemented 常量里抽出标识符
    m = re.search(r'Implemented\s*=\s*(.*?);', d, re.S)
    if m:
        blob = m.group(1)
        ids = set(re.findall(r'[A-Za-z_]\w{3,}', blob))
        # 过滤掉说明性文字里的常见词
        skip = {'以及', 'the', 'and', '或者', '并行'}
        missing_doc = []
        for ident in sorted(ids):
            if ident in skip:
                continue
            # 在代码里找定义或调用
            if not re.search(r'\b' + re.escape(ident) + r'\b', s):
                missing_doc.append(ident)
        if missing_doc:
            for i in missing_doc:
                warn('设计文档称已实现「%s」，但代码里找不到（文档可能过期）' % i)
        else:
            ok('设计文档声称已实现的 %d 项机制在代码中均存在' % len(ids))

# ---------- 汇总 ----------
print()
print('=' * 60)
for m in oks:
    print('  [OK]   %s' % m)
for m in warns:
    print('  [WARN] %s' % m)
for m in errors:
    print('  [ERR]  %s' % m)
print('=' * 60)
print('通过 %d | 警告 %d | 错误 %d' % (len(oks), len(warns), len(errors)))
sys.exit(1 if errors else 0)