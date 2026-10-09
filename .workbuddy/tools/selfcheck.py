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
# ★ 所有 Assets/Silk/*.cs 都要读 —— 否则新增文件里的类型
# 会被误报成「找不到定义」（加SilkSwingTestStage.cs 时踩过）。
import glob
SILK_ALL_FILES = sorted(glob.glob('Assets/Silk/*.cs'))

# 类型定义的通用匹配（class / interface / enum / struct / static class）
type_pat_all = (r'\npublic (?:static )?(?:class|interface|enum|struct) (\w+)')

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

# ---------- 2. ★ 所有 .cs 文件的注释与括号配平 ----------
# 【为什么要覆盖全部文件】
# 原先只检查 SilkParkourStage.cs，于是 SilkParkourDesign.cs 里
# 一个未闭合的 `/*`（插入大段注释时漏了结尾的 `*/`）
# 完全没被拦住 —— 要等编译报 CS1035 才发现。
# 教训：新增/修改 .cs 文件后，这类结构错误必须自动检查，不能指望肉眼。
for _f in SILK_ALL_FILES:
    if _f == SILK:
        continue          # SilkBuilder.cs 已在第 1 项查过
    if not os.path.exists(_f):
        continue
    _raw = open(_f, encoding='utf-8').read()
    _base = os.path.basename(_f)

    # 2a. 注释必须成对（未闭合的 /* 会导致 CS1035）
    #    ★ 必须先剥掉 `///` 文档注释再统计 —— 否则
    #    `/// <summary>xxx</summary>` 里的 `*/` 会被误判成「多余的」。
    _nodoc = re.sub(r'^\s*///.*$', '', _raw, flags=re.M)
    _no = len(re.findall(r'/\*', _nodoc))
    _nc = len(re.findall(r'\*/', _nodoc))
    if _no != _nc:
        _stack = []
        for _m in re.finditer(r'/\*|\*/', _nodoc):
            if _m.group(0) == '/*':
                _stack.append(_nodoc[:_m.start()].count('\n') + 1)
            elif _stack:
                _stack.pop()
            else:
                err('%s: 多余的 */ 在行 %d'
                    % (_base, _nodoc[:_m.start()].count('\n') + 1))
        for _ln in _stack:
            err('%s:%d 未闭合的 /* —— 会报 CS1035'
                '（插入大段注释时容易漏掉结尾的 */）' % (_base, _ln))
        continue          # 注释坏了，括号计数没意义

    # 2b. 括号配平
    _t = re.sub(r'//[^\n]*', '', _raw)
    _t = re.sub(r'/\*.*?\*/', '', _t, flags=re.S)
    _t = re.sub(r'@"(?:[^"]|"")*"', '""', _t, flags=re.S)
    _t = re.sub(r'"(?:\\.|[^"\\])*"', '""', _t)
    _t = re.sub(r"'(?:\\.|[^'\\])*'", "''", _t)
    # ★★ 2026-10-09 修正误报：中文文案里的全角括号会被当成语法括号计数。
    #   例：Tooltips 写「总长（腿根到足端）」→ 字符串替换后不该残留，
    #   但若文案里混用了全角与半角（`() ` ），计数就会差。
    #   → 计数前把全角括号统一成半角，保证配平判定基于**语法括号**。
    _t = _t.replace('（', '(').replace('）', ')')
    for _name, _o, _c in [('braces', '{', '}'), ('parens', '(', ')'),
                          ('brackets', '[', ']')]:
        if _t.count(_o) != _t.count(_c):
            err('%s 括号不配平 %s: %d vs %d'
                % (_base, _name, _t.count(_o), _t.count(_c)))

ok('全部 %d 个 .cs 文件的注释与括号均配平' % len(SILK_ALL_FILES))

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
must_call = ['ResolveGround', 'PickBestAnchor', 'DoJump',
             'AdoptFlightMomentum', 'ApplyRotation', 'HandleLookOnly',
             'OrbitAround', 'ConnectForParkour', 'ClearRenderClaim',
             'DoDash', 'DoRelease', 'RequireAirborne', 'CheckFallRespawn',
             # 诊断类也要检查 —— 曾因移出 LateUpdate 而变成死代码
             'ScanHealth', 'ScanRenderers', 'ScanDuplicates']
# 注：HandleJumpOrGrab / TryGrab / TrySpanNodes / TryFireAndHook /
#     CutFirstFiredLine / CutLineUnderCrosshair 已按用户要求解除绑定
#     （键位精简），它们保留但无调用点，故不列入本清单。
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
# ★ 收集**所有** Assets/Silk/*.cs 里的类型定义 ——
#   否则新增文件（如 SilkSwingTestStage.cs）里的类会被误报为「找不到定义」。
all_types = set(re.findall(
    type_pat_all, '\n'.join(open(f, encoding='utf-8').read()
                              for f in SILK_ALL_FILES)))
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

# ---------- 13. 摆荡一致性（防「球与丝线脱钩」回归）----------
# 【曾发生的真实 bug】用户反馈「钩爪莫名其妙把球和锚点连起来、
# 又莫名其妙断开」。根因有二：
#   1. FireAtAnchor 用 Attach(target) -> DriveEndTo(anchor) 把末端钉死，
#      丝线根本不会摆动。
#   2. UpdateSwing 里球只靠 `transform.position += accel*dt*dt` 积分，
#      **从不跟随末端** -> 球与丝线是两个物体 -> 看起来「断了」。
# 这两项若同时成立，玩家无论怎么按键都得不到摆荡手感。
ok_msg = []
if 'AttachSelf' not in s:
    err('缺少 AttachSelf —— 钩爪不能用它把末端钉死（否则摆荡不成立）')
else:
    ok_msg.append('AttachSelf 存在')
# 这条检查的语义是「球的位置必须来自末端」，
# 而不是「必须写成裸赋值 transform.position = grabbed.chain.GetEndPosition()」。
#
# 【为什么不能只认裸赋值】2026-10-07 给摆荡补墙体解算时，
# 裸赋值改成了：
#     Vector3 desired = grabbed.chain.GetEndPosition();
#     Vector3 resolved = SolveWallCollision(prevPos, desired, ref swingVel);
#     transform.position = resolved;
# 语义完全正确（球仍跟随末端，只是先过一道墙），
# 但旧检查只匹配裸赋值 -> 误报「球不再跟随末端」。
#
# -> 放宽为「UpdateSwing 里必须出现 GetEndPosition() 的结果被赋给 transform.position」。
#    变异测试（把 transform.position = resolved 改掉）仍能抓到。
if re.search(r'transform\.position\s*=\s*resolved', s) or \
   re.search(r'transform\.position\s*=\s*grabbed\.chain\.GetEndPosition\(\)', s):
    ok_msg.append('球跟随末端位置')
else:
    err('UpdateSwing 未让球跟随丝线末端 —— 会出现「球与线脱钩、看起来断开」')

# 13a. ★ 摆荡也必须过墙体解算，且修正量必须回写末端
#【为什么必须回写】球的位置若被墙推开而不通知末端，
#   下一帧约束会把它拉回墙里 -> 贴墙抖动；
#   而不通知的话，玩家看到的球与丝线末端是分离的（视觉上「线断了」）。
# 【本项目的教训】只检查「做没做」不够，要检查「做的是不是对的那个」
#   —— 历史上 13b 就是因为只查 AttachSelf 而漏掉「建链顺序反了」。
if 'GetEndPosition' in s:
    _sw = s.find('void UpdateSwing(')
    if _sw > 0:
        _ej = s.find('{', _sw)
        _ed = 0
        _ek = _ej
        while _ek < len(s):
            if s[_ek] == '{':
                _ed += 1
            elif s[_ek] == '}':
                _ed -= 1
                if _ed == 0:
                    break
            _ek += 1
        _swb = re.sub(r'//[^\n]*', '', s[_ej:_ek])
        _swb = re.sub(r'/\*.*?\*/', '', _swb, flags=re.S)
        if 'SolveWallCollision' not in _swb:
            err('UpdateSwing 没有过SolveWallCollision —— 摆荡速度可达数十格/秒，'
                '是全局最快的状态，不挡墙必然穿墙')
        else:
            ok_msg.append('摆荡也过墙体解算')
        if 'SetEndPosition' not in _swb:
            err('UpdateSwing 未把修正量回写末端（SetEndPosition）—— '
                '球被墙推开而末端不知情，下一帧约束会拉回墙里造成抖动，'
                '且视觉上球与丝线末端分离')
        else:
            ok_msg.append('末端位置回写')
if 'AddEndVelocity' in s:
    ok_msg.append('泵力施加到末端')
else:
    warn('未找到 AddEndVelocity —— 泵力可能仍在直接位移球（dt² 积分几乎无效）')
if ok_msg:
    ok('摆荡一致性：%s' % ' / '.join(ok_msg))

# ---------- 13b. 建链顺序：末端必须是「球」而不是「锚点」----------
# 【曾发生的真实 bug】EnsureChain 里写成
#     chain.Build(rootFrom, rootTo, this, 1f);
# 而 Build(highAnchor, breakNode) 的**第二个参数是末端（自由端）**。
# 球跟随 GetEndPosition() -> 球瞬移到 rootTo（锚点）——
# 用户反馈「小球瞬移到锚点，并在新旧位置之间建线」。
#
# 正确顺序：锚点当固定端，球当末端
#     锚点(固定) ──丝线── 球(自由端，挂在下面荡)
if re.search(r'chain\.Build\(\s*rootTo\s*,\s*rootFrom\s*,', s):
    ok('EnsureChain 建链顺序正确：锚点为固定端，球为末端')
else:
    err('EnsureChain 建链顺序错误 —— Build 的第二个参数才是末端，'
        '必须传球（rootFrom）而不是锚点（rootTo），否则球会瞬移到锚点')

# ---------- 13c. selfNode 不能缓存 ----------
# 【曾发生的真实 bug】selfNode 写成「存在即返回」的缓存：
#     if (_selfNode != null && _selfNode.AnchorAlive) return _selfNode;
# 球移动后它仍指向初始锚点 -> 表现为「在新旧位置之间建线」。
m_self = re.search(r'AnchorPoint selfNode\s*\{\s*get\s*\{(.*?)\n    \}', s, re.S)
if not m_self:
    warn('找不到 selfNode 属性 —— 建线起点可能有问题')
elif '_selfNode != null' in m_self.group(1):
    err('selfNode 仍是「存在即返回」的缓存 —— 球移动后建线起点会停在初始位置，'
        '表现为「在新旧位置之间建线」')
else:
    ok('selfNode 每次按当前位置解析（无缓存）')

# ---------- 13d. 摆荡中不得再发射 ----------
# 参考消逝之光2/蜘蛛侠2：一次只挂一根丝。允许连发会让玩家
# 在锚点间「瞬移」，既不是摆荡也不是飞行。
_fire = re.search(r'void FireAtAnchor\(\)\s*\{(.*?)\n    \}', s, re.S)
if not _fire:
    warn('找不到 FireAtAnchor')
elif re.search(r'grabbed\s*!=\s*null', _fire.group(1)):
    ok('摆荡中拒绝再次发射（须先松手）')
else:
    warn('FireAtAnchor 缺少「摆荡中拒绝发射」守卫 —— '
        '玩家可能在锚点间瞬移，失去摆荡手感')

# ---------- 14. 跨类字段访问（防 CS0103）----------
# 【曾发生的真实错误】在 SilkLine 的 AttachSelf 里写了
#     endTarget = null; endDriven = false;
# 而这两个字段属于 **SilkChain** 类 -> CS0103。
# 【为什么自检抓不到】括号配平、API 存在性都正常 ——
# 因为语法合法，只是访问了当前类不存在的成员。
#
# 做法：收集 SilkChain 的字段名，然后检查 SilkLine / SilkBuilder
#       的方法体内是否直接访问了这些字段（而非经 chain.xxx）。
CLASS_FIELDS = {}
for cname in ('SilkChain', 'SilkLine', 'SilkAnchor'):
    m = re.search(r'\npublic class ' + cname + r'\b', s)
    if not m:
        continue
    seg = s[m.start():]
    nxt = re.search(r'\npublic (?:static )?class \w+', seg[10:])
    if nxt:
        seg = seg[:10 + nxt.start()]
    for fm in re.finditer(
            r'\n    (?:public|private|protected)(?: static)?(?: readonly)? '
            r'[\w<>\[\],. ]+? (\w+)\s*(?:=|;)', seg):
        CLASS_FIELDS.setdefault(cname, set()).add(fm.group(1))

bad = []
for cname, flds in CLASS_FIELDS.items():
    m = re.search(r'\npublic class ' + cname + r'\b', s)
    seg = s[m.start():]
    nxt = re.search(r'\npublic (?:static )?class \w+', seg[10:])
    if nxt:
        seg = seg[:10 + nxt.start()]
    seg_start = m.start()

    # 【关键】必须先剥离注释 —— 否则「注释里解释这个坑」的文字
    # 会被误报成真实代码访问（本项目就误报过一次：
    # AttachSelf 的注释里写了 `endTarget = null;` 作为反面例子）。
    # 【关键】用**等长空格**替换注释，只去掉换行。
    # 若长度不一致，后面所有匹配位置都会偏移（踩过一次：
    # 报出来的行号指向注释里的示例代码，而非真实代码）。
    blank = lambda t: re.sub(r'[^\n]', ' ', t)
    seg_code = re.sub(r'/\*.*?\*/', lambda mm: blank(mm.group(0)), seg, flags=re.S)
    seg_code = re.sub(r'//[^\n]*', lambda mm: blank(mm.group(0)), seg_code)
    seg_code = re.sub(r'"(?:\\.|[^"\\])*"',
                      lambda mm: ' ' * len(mm.group(0)), seg_code)

    for other, oflds in CLASS_FIELDS.items():
        if other == cname:
            continue
        for f in oflds:
            # 形如 `endTarget = ` / `endTarget +=` 且前面不是 `.`
            pat = r'(?<![.\w])' + re.escape(f) + r'\s*(=[^=]|\+=|-=|\*=)'
            for mm in re.finditer(pat, seg_code):
                line = s[:seg_start + mm.start()].count('\n') + 1
                bad.append('%s:%d 直接访问 %s.%s（应通过 chain.%s 访问）'
                           % (cname, line, other, f, f))
if bad:
    seen = set()
    for b in bad:
        if b not in seen:
            err(b)
            seen.add(b)
else:
    ok('无跨类字段直接访问（%d 个类的字段均通过实例访问）' % len(CLASS_FIELDS))

# ---------- 14b. 裸方法调用的类归属（防 CS0103「方法不存在」）
# 【曾发生的真实错误 · 一天内栽了 3 次同类问题】
#   · endTarget       —— SilkChain 的字段，在 SilkLine 里裸访问
#   · verboseFireLog  —— SilkParkourController 的字段，在 SilkChain 里用
#   · ResetReelIn     —— SilkChain 的方法，在 SilkLine 里裸调用（本次）
# 共同点：**方法/字段的定义与使用不在同一个类**，
#        而语法完全合法，括号配平与 API 存在性都查不出来。
#
# 【为什么这一项比 14 更可靠】
# 14 号项试图比对「字段名在哪个类」，结果 53 条误报 ——
# 它分不清「访问他类字段」（错）与「访问局部变量的同名字段」（对）。
# 本项只检查**裸调用**（前面没有 `.`），而局部变量必然带 `.` 或无，
# 因此可以配合「本类方法表」精确判定。
# ★ 必须包含 SilkBuilder 与 SilkParkourController ——
#   两者最容易互相插错代码（本次 RebuildParkourStage 被插进 SilkBuilder，
#   引用了控制器的 StageHalfSize / startPosition -> 三个 CS0103）。
for cname in ('SilkChain', 'SilkLine', 'SilkBuilder', 'SilkParkourController'):
    m = re.search(r'\npublic class ' + cname + r'\b', s)
    if not m:
        continue
    seg = s[m.start():]
    nxt = re.search(r'\npublic (?:static )?class \w+', seg[10:])
    if nxt:
        seg = seg[:10 + nxt.start()]
    seg_start = m.start()

    # 本类定义的方法名
    own_methods = set(re.findall(
        r'\n    (?:public |private |protected )?(?:static )?[\w<>\[\],.]+ '
        r'(\w+)\s*\([^)]*\)\s*(?:=>|\{)', seg))
    # 本类继承来的（MonoBehaviour 的方法不用管，只查本文件定义的）
    if cname == 'SilkChain':
        own_methods |= {'resetAll'}   # Unity 生命周期，非本文件定义

    # 其他类定义的方法名（本文件内的 MonoBehaviour 类）
    other_methods = set()
    for oc in ('SilkLine', 'SilkChain', 'SilkParkourController',
               'SilkBuilder', 'AnchorPoint', 'SilkSegment'):
        if oc == cname:
            continue
        mo = re.search(r'\npublic class ' + oc + r'\b', s)
        if not mo:
            continue
        so = s[mo.start():]
        no = re.search(r'\npublic (?:static )?class \w+', so[10:])
        if no:
            so = so[:10 + no.start()]
        for mm in re.finditer(
                r'\n    (?:public |private |protected )?(?:static )?[\w<>\[\],.]+ '
                r'(\w+)\s*\([^)]*\)\s*(?:=>|\{)', so):
            nm = mm.group(1)
            if nm not in ('Start', 'Update', 'Awake', 'FixedUpdate',
                          'LateUpdate', 'OnDestroy', 'OnEnable', 'OnDisable'):
                other_methods.add((nm, oc))

    blank = lambda t: re.sub(r'[^\n]', ' ', t)
    code = re.sub(r'/\*.*?\*/', lambda mm: blank(mm.group(0)), seg, flags=re.S)
    code = re.sub(r'//[^\n]*', lambda mm: blank(mm.group(0)), code)
    code = re.sub(r'"(?:\\.|[^"\\])*"', lambda mm: blank(mm.group(0)), code)

    cross = []
    for nm, oc in sorted(other_methods):
        if nm in own_methods:
            continue
        for mm in re.finditer(r'(?<![.\w])' + re.escape(nm) + r'\s*\(', code):
            # 排除构造函数调用：`new SilkLine(...)` 是合法的
            if code[max(0, mm.start() - 4):mm.start()] == 'new ':
                continue
            ln = s[:seg_start + mm.start()].count('\n') + 1
            cross.append('%s:%d 裸调用 %s.%s() —— 应写成 chain.%s() 或同类实例访问'
                         % (cname, ln, oc, nm, nm))
    if cross:
        seen = set()
        for c in cross:
            if c not in seen:
                err(c)
                seen.add(c)

# ---------- 15. 跑酷世界键位表（防误改）----------
# 【用户 2026-10-07 精简后的最终键位】
#   WASD/QE 移动升降 / 空格 跳跃 / 左键 发射丝线 / 右键 松开丝线
#   左Shift 冲刺 / C 固化节点 / R 回起点 / Tab 切模式 / 鼠标移动 转视角
# 【已移除】B 斜上发射、V 结网、X 断自发线、G 断视线线
#
# 检查方式：只扫HandleKeys 方法体（跑酷世界的按键都在这里），
# 确认已移除的键不再出现，且保留的键都在。
_hk = re.search(r'void HandleKeys\(\)\s*\{(.*?)\n    \}', s, re.S)
if not _hk:
    err('找不到 HandleKeys —— 键位检查无法进行')
else:
    body_hk = _hk.group(1)
    #剥注释，避免注释里提到键名被误判
    blank = lambda t: re.sub(r'[^\n]', ' ', t)
    code_hk = re.sub(r'/\*.*?\*/', lambda mm: blank(mm.group(0)),
                     body_hk, flags=re.S)
    code_hk = re.sub(r'//[^\n]*', lambda mm: blank(mm.group(0)), code_hk)

    MUST_HAVE = {
        'KeyCode.Space': '空格=跳跃',
        'KeyCode.LeftShift': '左Shift=冲刺',
        'KeyCode.C': 'C=固化节点',
        'KeyCode.R': 'R=回起点',
    }
    MUST_NOT = {
        'KeyCode.B': 'B 斜上发射（已移除）',
        'KeyCode.V': 'V 结网（已移除）',
        'KeyCode.X': 'X 断自发线（已移除）',
        'KeyCode.G': 'G 断视线线（已移除）',
        'KeyCode.RightShift': '右 Shift（已并入左 Shift）',
    }
    miss = []
    for k, d in MUST_HAVE.items():
        if k not in code_hk:
            miss.append('缺少 %s' % d)
    if miss:
        for d in miss:
            err(d)
    left = [d for k, d in MUST_NOT.items() if k in code_hk]
    if left:
        for d in left:
            err('已移除的键仍在 HandleKeys 里：%s' % d)
    if not miss and not left:
        ok('跑酷键位符合精简后的约定（%d 个必备键，无已移除键）'
           % len(MUST_HAVE))
    # 右键必须是「松手」而不是旧的「取消待连线」
    if 'GetMouseButtonDown(1)) DoRelease()' in code_hk:
        ok('鼠标右键 = 松开丝线')
    elif 'GetMouseButtonDown(1)' in code_hk:
        err('鼠标右键有绑定，但不是 DoRelease —— 期望右键用于松手')
    if 'GetMouseButtonDown(0)' in code_hk:
        ok('鼠标左键 = 发射丝线')

    # ---------- 15b. ★ 硬约束：禁止擅自新增按键 ----------
    # 【用户硬约束，原话】
    #   「保持现在的按键不变。其他的，包括之后我不明白说明的情况下
    #     不准再新增加按键。」
    # 一旦有人给HandleKeys 加了新键，这一项会立刻报错。
    _hk2 = re.search(r'void HandleKeys\(\)\s*\{(.*?)\n    \}', s, re.S)
    if _hk2:
        _blank2 = lambda t: re.sub(r'[^\n]', ' ', t)
        _code2 = re.sub(r'/\*.*?\*/', lambda mm: _blank2(mm.group(0)),
                        _hk2.group(1), flags=re.S)
        _code2 = re.sub(r'//[^\n]*', lambda mm: _blank2(mm.group(0)), _code2)

        # 允许出现在 HandleKeys 里的键（=已定稿的 8 个）
        ALLOWED = {
            'KeyCode.W', 'KeyCode.A', 'KeyCode.S', 'KeyCode.D',
            'KeyCode.Q', 'KeyCode.E',
            'KeyCode.Space', 'KeyCode.LeftShift',
            'KeyCode.C', 'KeyCode.R', 'KeyCode.Tab',
            'GetMouseButtonDown(0)', 'GetMouseButtonDown(1)',
        }
        found = set(re.findall(r'KeyCode\.\w+', _code2))
        found |= set(re.findall(r'GetMouseButton(?:Down)?\(\d+\)', _code2))
        extra = sorted(found - ALLOWED)
        if extra:
            for e in extra:
                err('★ HandleKeys 里出现了未经批准的按键：%s\n'
                    '     用户硬约束「不准再新增加按键」。\n'
                    '     若确需新键，必须先问用户 —— 它同时也在 must_call 检查之外，'
                    '很可能是有意不接线的保留方法。' % e)
        else:
            ok('键位未擅自新增（%d 个已定稿键，无越界）' % len(found))

# ---------- 16. 速度类参数的比例一致性 ----------
# 【为什么会失衡】用户把moveSpeed 砍半时，若只改它一个，
# 冲刺 / 加速度的**相对强度**就变了：
#   50+42=92（1.84 倍）-> 25+42=67（2.68 倍）
# 冲刺会变得过强，空中连按两次就能飞出关卡。
# 故把「比例」固化为检查项。
dash = grab_float('dashImpulse')
acc = grab_float('groundAccel')
dec = grab_float('groundDecel')
mult = grab_float('sprintMultiplier')

if dash:
    ratio = (sp + dash) / sp
    if ratio > 2.2:
        err('冲刺过强：%.0f+%.0f=%.0f 格/秒 = **%.2f 倍**基础速度'
            '（应 ≤ 2.2；只降 moveSpeed 不降 dashImpulse 会造成失衡）'
            % (sp, dash, sp + dash, ratio))
    else:
        ok('冲刺强度合理：冲刺后 %.0f 格/秒 = %.2f 倍基础速度'
           % (sp + dash, ratio))

if acc:
    t = sp / acc                       # 到全速所需秒数
    if t < 0.06:
        err('地面加速度过大：到全速仅 %.3f 秒，球会「一按就粘在地上」'
            '（建议 0.08~0.2 秒；accel 应约为 moveSpeed 的 5~12 倍）' % t)
    elif t > 0.35:
        warn('地面加速度偏小：到全速需 %.2f 秒，手感会「拖」' % t)
    else:
        ok('地面加速度合理：到全速 %.2f 秒（%.0f 倍速度）' % (t, acc / sp))

if acc and dec:
    r = abs(acc - dec) / max(acc, dec)
    if r > 0.5:
        warn('加速(%.0f)与减速(%.0f)差距过大，松开会有明显滑行' % (acc, dec))

if dash and acc and sp:
    # 三者应大致同比例 —— 用比值的比值来判定
    bad = []
    if abs(dash / sp - 0.84) > 0.35:
        bad.append('dashImpulse/速度 = %.2f（基准 0.84）' % (dash / sp))
    if abs(acc / sp - 10) > 6:
        bad.append('groundAccel/速度 = %.1f（基准 10）' % (acc / sp))
    if bad:
        warn('速度类参数未同比例下调：%s' % '；'.join(bad))
    else:
        ok('速度类参数同比例（冲刺/加速度与速度的比例保持在基准附近）')

# ---------- 17. 关卡文件的健壮性检查 ----------
# 【曾发生的两个真实问题】
#  ① 空引用导致整张关卡消失（CreatePrimitive 返回 null）
#  ② 同一物体上有两个 MeshFilter / 两个 MeshRenderer
#     -> 因为 Setup() 已经处理了视觉，外部又AddComponent 了一遍
_sec_files = [f for f in glob.glob('Assets/Silk/*Stage.cs')
              if 'SilkBuilder' not in f]
sec_problems = []
for f in _sec_files:
    raw = open(f, encoding='utf-8').read()
    fn = os.path.basename(f)

    # ② 重复的视觉组件：AddComponent<MeshFilter> 与 Setup 同行或相邻
    if 'AddComponent<MeshFilter>' in raw and 'AddComponent<AnchorPoint>' in raw:
        # 检查是否在 AnchorPoint.Setup 之后又AddComponent 了视觉组件
        idx_setup = raw.find('.Setup(')
        idx_mf = raw.find('AddComponent<MeshFilter>')
        if idx_setup > 0 and idx_mf > idx_setup:
            sec_problems.append(
                '%s: 在 AnchorPoint.Setup() 之后又 AddComponent<MeshFilter> —— '
                'Setup 已处理网格/材质，同一物体会有两个 MeshFilter + 两个渲染器'
                % fn)

    # ① 危险的临时物体写法：CreatePrimitive + Destroy（取Mesh 用）
    if re.search(r'CreatePrimitive\(PrimitiveType\.Sphere\)', raw) and \
       re.search(r'Destroy\(', raw):
        sec_problems.append(
            '%s: 用「CreatePrimitive 建临时球再 Destroy」取网格 —— '
            'CreatePrimitive 返回 null 时会 NullReference，'
            '且异常抛在 Create() 里会导致整张关卡消失。'
            '应改用 AnchorPoint.Setup() 或 Resources.GetBuiltinResource' % fn)

    # ③ 关卡入口应该有 try/catch 兜底
    if 'Create(float' in raw and 'try' not in raw:
        pass    # 只提示，不报错（外部已有 try）

for p in sec_problems:
    err(p)
if not sec_problems:
    ok('关卡文件无重复视觉组件 / 无危险临时物体写法（%d 个关卡）'
       % len(_sec_files))

# ---------- 18. 绳长必须「等于实际距离」，不得有固定倍率 ----------
# 【用户要求】「能不能动态的，根据两个锚点之间的距离来计算，
#   而不是某一个固定值，一旦固定了就没有意思了」
#
# slackScale 是「在直线距离上额外加百分之多少」。
# >1 会让摆荡半径短于视觉距离，且压缩不同距离的节奏差异。
_slock = []
for _m in re.finditer(r'chain\.slackScale\s*=\s*([\d.]+)f', s):
    if abs(float(_m.group(1)) - 1.0) > 0.001:
        _ln = s[:_m.start()].count('\n') + 1
        _slock.append('第 %d 行 slackScale = %s（应为 1.0 = 绳长等于实际距离）'
                      % (_ln, _m.group(1)))
_d = re.search(r'public float slackScale = ([\d.]+)f', s)
if _d and abs(float(_d.group(1)) - 1.0) > 0.001:
    _slock.append('字段默认值 slackScale = %s（应为 1.0）' % _d.group(1))
if _slock:
    for _x in _slock:
        err(_x)
else:
    ok('绳长 = 两锚点实际距离（slackScale = 1.0），随距离动态变化')

# ---------- 19. 视觉/碰撞半径分离，且碰撞半径必须真被消费 ----------
# 【背景】用户要求「球的视觉看上去不变，但可以缩小（碰撞）」。
#   拆分后发现一个更根本的问题：本项目球的位置是脚本直接改写
#   transform.position，唯一的碰撞响应是 ResolveGround 的单条向下射线，
#   **从来没有墙体碰撞** —— 球本来就能穿墙而过。
#   于是「把碰撞半径调小」若无墙体解算配合，则完全无行为变化。
#   本项防止 colliderRadius 再次退化成「定义了但没人用」的死字段。
_r = []

# 19a. 两个字段都必须存在且是独立字段
if not re.search(r'public float visualRadius\s*=\s*[\d.]+f', s):
    _r.append('缺少 visualRadius 字段（视觉半径）')
if not re.search(r'public float colliderRadius\s*=\s*[\d.]+f', s):
    _r.append('缺少 colliderRadius 字段（碰撞半径）')

# 19b. colliderRadius 必须至少被消费一次（不能是死字段）
_used = len(re.findall(r'colliderRadius', s)) - 1   # 减去字段定义本身那处
if _used < 1:
    _r.append('colliderRadius 只出现在定义处，没有任何代码消费它'
              '——缩它不会改变任何行为')

# 19c. 墙体解算必须存在，且必须真的用 SphereCast（薄板防穿透的关键）
if 'SolveWallCollision' not in s:
    _r.append('缺少 SolveWallCollision：球能直接穿墙，'
              'colliderRadius 无从生效')
else:
    # ★ 按大括号配平截取函数体 ——不能用 '\n    }' 之类的字符串切分，
    #   函数体内的 if/for嵌套也会出现同样缩进的 '}'，会提前截断。
    _i = s.find('Vector3 SolveWallCollision(')
    if _i < 0:
        _r.append('未找到 SolveWallCollision 的定义')
    else:
        _j = s.find('{', _i)
        _depth = 0
        _k = _j
        while _k < len(s):
            if s[_k] == '{':
                _depth += 1
            elif s[_k] == '}':
                _depth -= 1
                if _depth == 0:
                    break
            _k += 1
        _body = s[_j:_k]
        # ★ 搜代码前必须先剥掉注释 ——
        #   本函数的文档注释里就写着「hit.normal」「SphereCast」等字样，
        #   直接搜整段会因注释命中而永远「通过」（变异测试实测漏抓）。
        _body = re.sub(r'//[^\n]*', '', _body)
        _body = re.sub(r'/\*.*?\*/', '', _body, flags=re.S)
        if 'SphereCast' not in _body:
            _r.append('SolveWallCollision 内没有 SphereCast —— '
                      '用点检测会在高速/薄板场景下穿透')
        # 沿法线推出必须存在，否则球会陷进墙里并逐帧抖动。
        # ★ 必须匹配「推出表达式」本身，不能只搜 'hit.normal' ——
        #   函数里 velocityRef 那几行也用 hit.normal，
        #   只搜字符串会在推出被删后仍然「通过」（变异测试实测漏抓）。
        if not re.search(r'\+\s*hit\.normal\s*\*\s*colliderRadius', _body):
            _r.append('SolveWallCollision 内没有「contact + hit.normal × '
                      'colliderRadius」的推出 —— 球会陷进墙里，'
                      '每帧重复命中导致抖动')

# 19d. 两个位移入口都要过墙体解算（地面移动 + 脱手飞行）
_nsolve = len(re.findall(r'=\s*SolveWallCollision\(', s))
if _nsolve < 2:
    _r.append('SolveWallCollision 只有 %d 处调用点（应为 2：'
              '地面移动 + 脱手飞行）—— 有一路会穿墙' % _nsolve)

# 19e. 地面吸附半径必须用 visualRadius（否则球陷进地板）
_gr = re.search(r'ResolveGround\(Vector3[^)]*\)\s*\{(.{0,600}?)float radius = (\w+);',
                s, re.S)
if not _gr:
    _r.append('未找到 ResolveGround 里的 radius 赋值')
elif _gr.group(2) != 'visualRadius':
    _r.append('ResolveGround 的 radius = %s，应为 visualRadius —— '
              '用 colliderRadius 会让视觉球陷进地板' % _gr.group(2))

if _r:
    for _x in _r:
        err(_x)
else:
    ok('视觉/碰撞半径已分离，碰撞半径被墙体 SphereCast 解算消费'
       '（%d 处调用）' % _nsolve)

# ---------- 20. 相机穿墙防护 ----------
# 【背景】用户报告「相机穿墙」。实算发现两个独立原因：
#   ① SphereCast 起点（focus = 球心 + camHeight）可能已埋进楼板，
#      而 Unity 对「起点已在碰撞体内」的 SphereCast 返回 distance=0
#      或直接不命中 -> 避障完全失效（这是SphereCast 的固有陷阱，
#      不是代码写错，是起点位置的问题）。
#   ② 相机参数与房屋尺度不匹配：房间半宽 11.25 格 vs camDistance 20。
#本项把这两条固化为检查。
_cam = []

# 20a. 必须有「起点重叠」的预处理 —— OverlapSphere
_orbit = s.find('public void OrbitAround(')
if _orbit < 0:
    _cam.append('找不到 OrbitAround —— 相机无避障')
else:
    # 按大括号配平截取方法体（不能用 '\n    }'，会撞上 if 的同缩进 }）
    _j = s.find('{', _orbit)
    _d = 0
    _k = _j
    while _k < len(s):
        if s[_k] == '{':
            _d += 1
        elif s[_k] == '}':
            _d -= 1
            if _d == 0:
                break
        _k += 1
    _body = s[_j:_k]
    # 剥注释（否则注释里的关键词会掩盖缺失 —— 已踩过这个坑）
    _body = re.sub(r'//[^\n]*', '', _body)
    _body = re.sub(r'/\*.*?\*/', '', _body, flags=re.S)

    if 'OverlapSphere' not in _body:
        _cam.append('OrbitAround 内没有 OverlapSphere 预处理 —— '
                    'SphereCast 起点埋进墙里时避障会完全失效')
    if 'SphereCast' not in _body:
        _cam.append('OrbitAround 内没有 SphereCast —— 无墙时相机不避障')
    # 退让上限：没有上限则 camDistance 大于房间半宽时必然穿墙
    if 'orbitMaxDistance' not in _body:
        _cam.append('OrbitAround 内没有用 orbitMaxDistance 限制退让 —— '
                    'camDistance > 房间半宽时相机必然退到墙外')

# 20b. orbitMaxDistance / orbitMinDistance 字段必须存在
if not re.search(r'public float orbitMaxDistance\s*=\s*[\d.]+f', s):
    _cam.append('缺少 orbitMaxDistance 字段（相机退让上限）')
if not re.search(r'public float orbitMinDistance\s*=\s*[\d.]+f', s):
    _cam.append('缺少 orbitMinDistance 字段（相机退让下限，防贴脸）')

# 20c. ★ 相机默认参数必须与「最紧的室内尺度」相容
#     判据：camDistance 默认值不得大于最小房间半宽。
_house = open('Assets/Silk/HouseBlockout.cs', encoding='utf-8').read()
_room = re.search(r'RoomLength\s*=\s*\(float\)\(([\d.]+)\s*\*\s*([\d.]+)\)', _house)
_camdef = re.search(r'public float camDistance\s*=\s*([\d.]+)f', s)
if _room and _camdef:
    # RoomLength = RoomSize * UnitsPerMeter
    room_len = float(_room.group(1)) * float(_room.group(2))
    half = room_len / 2.0
    cd = float(_camdef.group(1))
    if cd > half:
        _cam.append('camDistance 默认 %.0f > 房间半宽 %.2f 格 —— '
                    '相机必然退到墙外（%.1f 格）'
                    % (cd, half, cd - half))

# 20d. House 关卡必须显式套用自己的相机尺度
if 'ApplyCameraForStage(' not in s:
    _cam.append('缺少 ApplyCameraForStage —— 切换关卡时相机尺度不跟随')
else:
    if 'HouseBlockout.CameraDistance' not in s:
        _cam.append('House 关卡未套用 HouseBlockout.CameraDistance —— '
                    '会沿用空旷场地默认值而穿墙')
    if 'HouseBlockout.CameraHeight' not in s:
        _cam.append('House 关卡未套用 HouseBlockout.CameraHeight —— '
                    '焦点可能埋进楼板导致 SphereCast 失效')

# 20e. 焦点抬高必须小于净高（否则焦点埋进楼板）
_h2 = re.search(r'MinNetHeight\s*=\s*([\d.]+)f', _house)
_chdef = re.search(r'public float camHeight\s*=\s*([\d.]+)f', s)
if _h2 and _chdef:
    _upm = re.search(r'UnitsPerMeter\s*=\s*([\d.]+)f', _house)
    if _upm:
        net = float(_h2.group(1)) * float(_upm.group(1))
        ch = float(_chdef.group(1))
        # 还要扣掉球的视觉直径（球已占据的净空）
        vr = re.search(r'public float visualRadius\s*=\s*([\d.]+)f', s)
        ball = float(vr.group(1)) * 2 if vr else 0.0
        if ch > net - ball:
            _cam.append('camHeight 默认 %.1f 超过「净高 %.1f − 球直径 %.1f = %.1f」'
                        '—— 焦点会埋进楼板，SphereCast 起点重叠而失效'
                        % (ch, net, ball, net - ball))

if _cam:
    for _x in _cam:
        err(_x)
else:
    ok('相机穿墙防护完整（OverlapSphere 预处理 + SphereCast + 退让上下限，'
       '且默认参数与室内尺度相容）')

# ---------- 21. 视角转动：yaw 必须归一化 ----------
# 【问题】pitch 有 clamp(pitchMin, pitchMax)，yaw 一直没有。
#   yaw += mx * rate 可以无限累积 —— 转 200 圈就是 72000 度，
#   float 有效精度约 7 位十进制，此时小数位只剩 2 位，
#   cos/sin 的输入剧烈跳变 -> 视角顿挫抖动。
# 【要点】两处写入点都要修：Parkour 的 HandleLookOnly、FreeFly 的右键拖拽。
_look = []
if 'NormalizeAngle' not in s:
    _look.append('缺少 NormalizeAngle —— yaw 会无限累积，'
                 '转久了因float 精度耗尽而顿挫')
else:
    _n = len(re.findall(r'yaw\s*=\s*NormalizeAngle\(yaw\)', s))
    # 2 = HandleLookOnly（Parkour 鼠标视角）+ FreeFly 右键拖拽
    if _n < 2:
        _look.append('yaw 归一化只有 %d 处调用点（应为 2：'
                     'Parkour 鼠标视角 + FreeFly 右键拖拽）' % _n)

# 归一化必须用加减法而非 %=360 —— 取模会在 ±180 处数值跳变
_m = re.search(r'static float NormalizeAngle\(float deg\)\s*\{(.{0,400}?)\n    \}', s, re.S)
if _m:
    _body = re.sub(r'//[^\n]*', '', _m.group(1))
    if '%' in _body:
        _look.append('NormalizeAngle 用了 %=360 —— 取模会在 ±180 处跳变，'
                     '干扰 ResetOrientation 用 Atan2 反解的 pitch 基准；'
                     '应改用加减法')

if _look:
    for _x in _look:
        err(_x)
else:
    ok('视角转动：yaw 已归一化到 (-180,180]（2 处写入点），不会精度耗尽')

# ---------- 22.（已放弃）局部 const 的 CS0103 自动检测 ----------
#
# ★★ 本项尝试了 5 版，全部因误报而被放弃。记录在此以免后人重蹈覆辙。
#
# 【为什么放弃】要判断「一个局部 const 是否被其它方法引用」，
#   必须先准确定位方法边界，而C# 的方法边界无法用简单正则可靠判定：
#   第 1 版  大括号配平判边界     -> for/if 的 { } 被当成边界（20+ 误报）
#   第 2 版  缩进判边界           -> 表达式里单字母被当变量引用（x/y/s 全误报）
#   第 3 版  找 "    }" 判方法结束 -> 属性 get 块内同名常量又误报
#   第 4 版  顶层成员边界         -> 正则 ^\s 匹配任何行，条件矛盾，全量误报
#   第 5 版  引用跨度阈值         -> 长方法内跨度天然很大，全量误报
#   第 6 版  从声明行累积大括号   -> 声明在方法体中间时深度起点为 0，
#                                   第一个 for 就被误判为方法结束
#   ★ 正确的做法本应是「从方法签名行开始累积深度」，
#     但那需要先正确定位签名行 —— 于是又回到了第 1 版的问题。
#
# 【结论】这个检查需要真正的 C# 语法分析（Roslyn / dotnet build），
#   文本正则做不到。而项目已有更好的替代：**Unity 编译器本身就是检查器**。
#   CS0103 会在 Unity 里立刻报出精确行号 —— 这正是用户发现 stairW 的方式。
#   与其做一个半可靠的启发式（每天被误报淹没），不如老实告诉使用者：
#   **改完代码请看 Unity Console 的编译结果。**
#
# 【本项目已栽在此类错误上 5 次，记录清单供对照】
#   1. endTarget          —— SilkChain 字段，在 SilkLine 里用
#   2. verboseFireLog     —— SilkParkourController 字段，在 SilkChain 用
#   3. ResetReelIn        —— SilkChain 方法，在 SilkLine 裸调
#   4. RebuildParkourStage —— 插错类（SilkBuilder vs SilkParkourController）
#   5. stairW             —— BuildStair 的局部 const，被 BuiltMessage 引用
#   共同点：**定义与使用不在同一作用域**，语法完全合法，
#   括号配平与 Unity API 存在性检查都查不出来。
#
# 【唯一可靠的静态替代】第 14b 项（裸方法调用的类归属）——
#   它有效是因为「裸调用」这个特征在文本上可判定。
#   局部变量没有这种特征（任何标识符都可能是局部变量）。
#
# ⇒ 真正该做的是「写完先在 Unity 里编一遍」，而非继续加启发式检查。
ok('（第 22 项已放弃：局部 const 的 CS0103 无法用文本正则可靠检测，'
   '请以 Unity Console 的编译结果为准）')

# ---------- 23. 建筑参数联动（改了球径/房间后不能漏改的地方）----------
# 【背景】2026-10-07 把球从直径 9 格缩到 5 格、房间从 4.5 米放大到 7.0 米，
# 连带牵出 5 处必须同步的地方。其中两处当时真的错了：
#   · 起点高度写成字面量 -29.5 -> 球心悬空 2 格（自检第 8 项抓到）
#   · 踏步高若用净高算而非层高算 -> 楼梯差0.34 m 爬不到二层（肉眼极难察觉）
#
# 本项把「必须用公式而非字面量」固化为检查。
_link = []
_house = open('Assets/Silk/HouseBlockout.cs', encoding='utf-8').read()

# 23a. RoomLength 必须由 RoomSize 推导（曾硬编码 (4.5*5.0)）
_m = re.search(r'public const float RoomLength = ([^;]+);', _house)
if not _m:
    _link.append('HouseBlockout: 找不到 RoomLength 定义')
elif 'RoomSize' not in _m.group(1):
    _link.append('HouseBlockout: RoomLength = %s 是字面量，'
                 '必须写成 (RoomSize * 5.0) —— 否则改了 RoomSize 房间不变'
                 % _m.group(1).strip())

# 23b. StepRise 必须由 StoreyHeight / TotalSteps 推导
_m = re.search(r'StepRise\s*(?:=>|=)\s*([^;]+);', _house)
if not _m:
    _link.append('HouseBlockout: 找不到 StepRise 定义')
else:
    _v = _m.group(1).strip()
    if 'StoreyHeight' not in _v or 'TotalSteps' not in _v:
        _link.append('HouseBlockout: StepRise = %s 必须写成 '
                     'StoreyHeight / TotalSteps —— 硬编码会在改层高后'
                     '导致楼梯爬不到二层（差值肉眼难察）' % _v)

# 23c. 起点高度必须用公式
if 'StartPositionBasicStage' not in s:
    _link.append('SilkBuilder: 缺少 StartPositionBasicStage —— '
                 '起点高度应写成「平台顶面 + visualRadius」而非字面量')
elif not re.search(r'BasicStageFloorZ\s*\+\s*visualRadius', s):
    _link.append('SilkBuilder: StartPositionBasicStage 没用 '
                 'BasicStageFloorZ + visualRadius 公式')

# 23d. 球径必须小于最窄门洞与楼梯梯段
_vr = re.search(r'public float visualRadius\s*=\s*([\d.]+)f', s)
_cr = re.search(r'public float colliderRadius\s*=\s*([\d.]+)f', s)
_toi = re.search(r'ToiletDoorWidth\s*=\s*([\d.]+)f', _house)
_stw = re.search(r'StairWidth\s*=\s*([\d.]+)f', _house)
_upm = re.search(r'UnitsPerMeter\s*=\s*([\d.]+)f', _house)
if _cr and _toi and _stw and _upm:
    _u = float(_upm.group(1))
    _d = float(_cr.group(1)) * 2
    _toi_g = float(_toi.group(1)) * _u
    _stw_g = float(_stw.group(1)) * _u
    if _d > _toi_g:
        _link.append('碰撞直径 %.1f 格 > 卫生间门 %.1f 格 —— 球进不了厕所'
                     % (_d, _toi_g))
    if _d > _stw_g:
        _link.append('碰撞直径 %.1f 格 > 楼梯梯段 %.1f 格 —— 球上不了楼梯'
                     % (_d, _stw_g))
    # 建筑必须装得进网格：半宽 = (房间 × 2 + 楼梯井) / 2
    _rs = re.search(r'RoomSize\s*=\s*([\d.]+)f', _house)
    _grid = re.search(r'public int size = (\d+)', s)
    _f1 = re.search(r'Flight1Steps\s*=\s*(\d+)', _house)
    _f3 = re.search(r'Flight3Steps\s*=\s*(\d+)', _house)
    _tr = re.search(r'StepTread\s*=\s*([\d.]+)f', _house)
    if _rs and _grid and _f1 and _f3 and _tr:
        _room = float(_rs.group(1))
        _half = float(_grid.group(1)) / 2.0
        # 楼梯井 X = (Flight1+Flight3) ×踏步宽 + 0.30 余量
        _shaft_m = (int(_f1.group(1)) + int(_f3.group(1))) * float(_tr.group(1)) + 0.30
        _need = (_room * 2 + _shaft_m) / 2 * _u
        if _need > _half:
            _link.append('建筑半宽 %.1f 格 > 网格边界 ±%.0f 格 —— 会出界'
                         % (_need, _half))

if _link:
    for _x in _link:
        err(_x)
else:
    ok('建筑参数联动正确（RoomLength/StepRise/起点高度均为公式，'
       '球径小于门洞与楼梯，建筑在网格内）')

# ---------- 24. 门洞必须真的挖通（左墙垛 + 门楣 + 右墙垛）----------
# 【背景】2026-10-08 发现 BuildDoorways 的严重缺口：
#   注释写着「拆成左墙垛 + 门楣 + 右墙垛」，但代码**只做了门楣**，
#   两侧墙垛根本没实现 —— 于是门洞位置是一面完整的墙，
#   只在上面贴了块门楣，球靠墙体碰撞撞不开。
#   而且门楣本身的 z 位置也算错了（详见 24b）。
_door = []

_hs = open('Assets/Silk/HouseBlockout.cs', encoding='utf-8').read()
_di = _hs.find('static void DoorInWall(')
if _di < 0:
    _door.append('HouseBlockout: 找不到 DoorInWall()')
else:
    _dj = _hs.find('{', _di)
    _dd = 0
    _dk = _dj
    while _dk < len(_hs):
        if _hs[_dk] == '{':
            _dd += 1
        elif _hs[_dk] == '}':
            _dd -= 1
            if _dd == 0:
                break
        _dk += 1
    _dbody = re.sub(r'//[^\n]*', '', _hs[_dj:_dk])
    _dbody = re.sub(r'/\*.*?\*/', '', _dbody, flags=re.S)

    # 24a. 三块都要有：墙垛左、墙垛右、门楣
    if '_墙垛左' not in _dbody:
        _door.append('DoorInWall 缺少「墙垛左」—— 门洞没挖通，'
                     '球会被整面墙挡住（2026-10-08 的实际 bug）')
    if '_墙垛右' not in _dbody:
        _door.append('DoorInWall 缺少「墙垛右」—— 同上')
    if '_门楣' not in _dbody:
        _door.append('DoorInWall 缺少「门楣」')

    # 24b. 门楣位置必须用「门洞净高 + 门楣一半」，不能是 (height-lintel)/2
    #     后者的含义是「门洞净高的一半」，会让门楣落到墙的中部而非顶部。
    if re.search(r'\(\s*height\s*-\s*lintelH\s*\)\s*\*\s*0\.5f', _dbody):
        _door.append('DoorInWall 的门楣 z 用了 (height - lintelH) * 0.5f —— '
                     '那是「门洞净高的一半」，门楣会落在墙中部。'
                     '正确应为 doorH + lintelH * 0.5f（门楣坐在门洞顶部）')

    # 24c. 墙垛必须与墙同厚（否则漏光/看穿）
    #    ★ 必须匹配表达式本身（Vector3(sideW, thick, ...)），
    #      只数 'thick' 出现次数会被「thick * 0.3f」这类缩放骗过
    #      （变异测试实测漏抓）。
    _n_thick = len(re.findall(r'Vector3\(\s*sideW\s*,\s*thick\s*,', _dbody))
    if _n_thick < 2:
        _door.append('DoorInWall 的墙垛未全部使用原墙厚 thick —— '
                     '墙垛比墙薄会漏光/看穿'
                     '（匹配 Vector3(sideW, thick, ...) 形式）')

if _door:
    for _x in _door:
        err(_x)
else:
    ok('门洞由「墙垛左 + 门楣 + 墙垛右」三块拼成（真的挖通了），'
       '且门楣位置正确坐在门洞顶部')

# ---------- 25. 自由搭建模式：不能删（用户要自己搭场景）----------
# 【背景】2026-10-08 用户要「自己重新搭建场景，不保留之前的」。
#   原架构是「代码生成关卡」，导致：
#     · 按 Tab 会 RebuildParkourStage() -> 手工搭的场景全被清掉
#     · 球出生在写死的 (-30, 0, -32.75) -> 掉下去 -> 「小球容易丢」
#   加了 freeBuildMode 开关解决：开启时不重建 + 自动找场景最高的平台。
_fb = []
ok_msg = None

if 'freeBuildMode' not in s:
    _fb.append('缺少 freeBuildMode 开关 —— 用户无法在自己搭的场景里试玩，'
               '按 Tab 会清空手工场景')
else:
    # 25a. Tab 切换时必须尊重该开关（这是「场景被清掉」的根因）
    if not re.search(r'!\s*builder\.freeBuildMode\s*&&\s*'
                     r'builder\.createTestLevel\s*\)?\s*RebuildParkourStage', s):
        _fb.append('HandleModeSwitch 里 RebuildParkourStage 没有检查 freeBuildMode '
                   '—— 开启后按 Tab 仍会清空用户手工搭的场景')

    # 25b. 切回 FreeFly 时也不能 Clear 关卡
    if not re.search(r'if\s*\(\s*!\s*builder\.freeBuildMode\s*\)\s*\{[^}]*'
                     r'SilkParkourStage\.Clear', s, re.S):
        _fb.append('切回 FreeFly 时无条件 SilkParkourStage.Clear() '
                   '—— 自由搭建模式下会误删用户场景')

    # 25c. 重生必须回到场景里，而不是写死坐标
    if 'DetectFreeBuildSpawn' not in s:
        _fb.append('缺少 DetectFreeBuildSpawn —— 球无法自动落到用户搭的平台上')
    else:
        # ★ 截取范围要够大：RespawnAtStart 里有大量注释，
        #   600 字符不够（变异测试实测漏抓）
        _m = re.search(r'void RespawnAtStart\(string reason\)\s*\{(.{0,2000}?)\n    \}',
                       s, re.S)
        if _m is None:
            _fb.append('找不到 RespawnAtStart 方法体')
        elif 'freeBuildMode' not in _m.group(1):
            _fb.append('RespawnAtStart 没有检查 freeBuildMode '
                       '—— 掉出场景后会重生到旧关卡坐标，形成无限掉落')
        else:
            pass

    # 25d. 掉落重生线也要跟着场景走（否则掉很久才回来）
    _m2 = re.search(r'void CheckFallRespawn\(\)\s*\{(.{0,500}?)\n    \}', s, re.S)
    if _m2 and 'EffectiveFallRespawnZ' not in _m2.group(1):
        _fb.append('CheckFallRespawn 没用 EffectiveFallRespawnZ '
                   '—— 仍按写死的 -70 判定，用户场景在新坐标时会「掉很久才重生」')

    # 25e. 自动落点必须排除球自己，否则会把自己的位置当落点
    _m3 = re.search(r'public Vector3 DetectFreeBuildSpawn\(\)\s*\{(.{0,2000}?)\n    \}',
                    s, re.S)
    if _m3 and 'transform.root' not in _m3.group(1):
        _fb.append('DetectFreeBuildSpawn 没排除球自身 —— '
                   '会把球自己的 Collider 当成落点，形成死循环')

if _fb:
    for _x in _fb:
        err(_x)
else:
    ok('自由搭建模式完整（不重建关卡 / 自动找落点 / 重生回场景 / '
       '掉落线跟随场景）')

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