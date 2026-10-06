"""跑酷关卡可通关性核验。
用法：python .workbuddy/tools/check_stage.py
"""
import re

s = open('Assets/Silk/SilkBuilder.cs', encoding='utf-8').read()
stage = open('Assets/Silk/SilkParkourStage.cs', encoding='utf-8').read()


def num(x):
    return float(x.replace('f', ''))


def grab(key):
    """读取 SilkParkourController 的 float 字段。
    要兼容两种写法：
      public float moveSpeed = 50f;字面量
      public float gravity = SilkPhysics.Gravity;引用常量
    后者无法静态求值，回退去 SilkPhysics 里查该常量的值。"""
    i = s.index('class SilkParkourController')
    m = re.search(r'public float ' + key + r'\s*=\s*([^;]+);', s[i:])
    if not m:
        return None
    expr = m.group(1).strip()
    mm = re.match(r'^(-?[\d.]+)f?$', expr)
    if mm:
        return float(mm.group(1))
    mc = re.search(r'const float ' + expr.split('.')[-1] + r'\s*=\s*(-?[\d.]+)f?;', s)
    return float(mc.group(1)) if mc else None


g = grab('gravity')
v = grab('jumpSpeed')
sp = grab('moveSpeed')
r = num(re.search(r'public float visualRadius = ([^;]+);', s).group(1))

apex = v * v / (2 * g)          # 跳跃最高点
airtime = 2 * v / g             # 滞空时间
reach = sp * airtime           # 全速起跳的水平跨度

print('=== 物理参数 ===')
print('  重力 %.0f | 起跳 %.0f | 跑速 %.0f | 球半径 %.1f' % (g, v, sp, r))
print('  跳跃最高 %.1f 格 | 滞空 %.2f 秒 | 全速起跳跨度 %.0f 格' % (apex, airtime, reach))

# 解析关卡里的平台：Plat("名字", center, size, 色)
plats = []
# 只看 Create() 函数体，避免把别处的 Vector3 也算进来
body = stage[stage.index('public static void Create'):stage.index('public static void Clear')]
# 按「调用顺序」取出所有建几何物件的调用（Plat / Barrier），
# 再按出现顺序取成对的 Vector3（中心点 + 尺寸）。
# 注意：不能只用 Plat\\(" 匹配 —— Barrier(" 不含它但会混入；
# 也不能按尺寸推断哪个是墙（平台本身也是扁的）。必须都取出来再分类。
# 【关键】同时记录「用哪个函数建的」——
# Plat() 建可站立平台，Barrier() 建护栏（竖直挡板，不是路面）。
# 早先试过按尺寸推断（sz[2]<sz[1] 判墙），但平台本身也是扁的，
# 结果所有平台都被误判成墙；也不能靠名字前缀（Barrier 内部同样
# 加 Plat_ 前缀）。唯一可靠的方式是看调用处写的是哪个函数名。
calls = re.findall(r'\b(Plat|Barrier)\("([^"]+)"', body)
vecs = re.findall(r'new Vector3\(\s*(-?[\d.]+)f,\s*(-?[\d.]+)f,\s*(-?[\d.]+)f\s*\)', body)

if len(vecs) < 2 * len(calls):
    print('!! 解析到 %d 个建造调用但只有 %d 个 Vector3 —— 数量不匹配'
          % (len(calls), len(vecs)))
    raise SystemExit(1)

for i, (fn, n) in enumerate(calls):
    c = tuple(float(x) for x in vecs[i * 2])
    sz = tuple(float(x) for x in vecs[i * 2 + 1])
    plats.append((n, c, sz, fn))    # fn: 'Plat'=可站立, 'Barrier'=护栏


def bx(c, sz, i):
    return (c[i] - sz[i] / 2, c[i] + sz[i] / 2)


# 区分「可站立平台」与「护栏」：**必须按调用函数名，不能按尺寸推断**。
# （曾试过 sz[2] < sz[1] 判墙，但平台本身也是扁的 —— z 尺寸天然小于 y，
#   结果所有平台都被误判成墙，只剩护栏参与判定。）
# Barrier(...) 建的是竖直挡板，不是路面，不能参与路线/重叠检查。
# 只把 Plat() 建的算作可站立平台；Barrier() 建的是护栏，排除。
B = {}
BARRIERS = []
for n, c, sz, fn in plats:
    if fn == 'Barrier':
        BARRIERS.append((n, c, sz))
        continue
    B[n] = [bx(c, sz, i) for i in range(3)]

print()
print('=== 越界检查（内墙 ±50）===')
bad = False
for n, b in B.items():
    out = ['XYZ'[i] for i in range(3) if b[i][0] < -50 or b[i][1] > 50]
    if out:
        print('  !! %s 越界 %s' % (n, out))
        bad = True
if not bad:
    print('  全部通过')

print()
print('=== 重叠检查 ===')
ov = False
# 只在平台之间查重叠（B 已过滤掉护栏），避免护栏与相邻平台误报
names = sorted(B.keys())
for i in range(len(names)):
    for j in range(i + 1, len(names)):
        n1, n2 = names[i], names[j]
        if all(min(B[n1][a][1], B[n2][a][1]) - max(B[n1][a][0], B[n2][a][0]) > 0
               for a in range(3)):
            print('  !! %s vs %s 重叠' % (n1, n2))
            ov = True
if not ov:
    print('  无重叠')

print()
print('=== 基础区路线（沿 +X 前进）===')
seq = sorted(B.keys())
# 基础区= Plat_0..Plat_3；Plat_4/5 是刻意用y 方向隔开的摆荡进阶区。
# 注意平台名形如 "Plat_4_摆荡钩子"，不能startswith('4') 过滤。
basic = [n for n in seq if not re.match(r'Plat_[45]', n)]
ok_all = True
for i in range(len(basic) - 1):
    a, b = basic[i], basic[i + 1]
    gap = B[b][0][0] - B[a][0][1]
    yov = min(B[a][1][1], B[b][1][1]) - max(B[a][1][0], B[b][1][0])
    dz = B[b][2][1] - B[a][2][1]

    # y 方向完全分离 => 不是「同一路上的下一段」，
    # 而是刻意用空间隔开的独立区（如摆荡进阶区），不参与地面路线判定
    if yov <= 0:
        print('  %s -> %s: y 方向分离（%.0f..%.0f vs %.0f..%.0f）'
              ' -> 判定为独立区段，跳过地面路线检查'
              % (a, b, B[a][1][0], B[a][1][1], B[b][1][0], B[b][1][1]))
        continue

    # 判定：顶面落差要能跳上；x 间隙不能超出全速起跳跨度
    okz = dz <= apex - 0.5
    okx = gap <= reach
    tag = 'OK' if (okz and okx) else '!! 不通过'
    if not (okz and okx):
        ok_all = False
    print('  %s -> %s: 间隙%+.1f格 y重叠%.1f格 落差%+.1f格  [%s]' % (a, b, gap, yov, dz, tag))
print('  路线整体: %s' % ('可通关' if ok_all else '存在过不去的跳跃'))

print()
print('=== 起点 ===')
m = re.search(r'public Vector3 startPosition = new Vector3\(([-\d.]+)f,\s*([-\d.]+)f,\s*([-\d.]+)f\)', s)
sx, sy, sz = (float(m.group(i)) for i in (1, 2, 3))
p0 = basic[0]
# basic 的名字排序依赖命名，而过滤掉护栏后顺序可能变化。
# 用「x 坐标最小」来定位起点平台更符合语义 ——
# 关卡沿 +X 推进，起点必然在最左侧。
p0 = min(basic, key=lambda n: B[n][0][0])
inx = B[p0][0][0] <= sx <= B[p0][0][1]
iny = B[p0][1][0] <= sy <= B[p0][1][1]
top = B[p0][2][1]
want = top + r
print('  平台 %s 顶面 z=%.1f -> 球心应 z=%.1f' % (p0, top, want))
print('  startPosition=(%.1f, %.1f, %.1f)  z 差 %+.1f 格' % (sx, sy, sz, sz - want))
print('  x 在平台内: %s | y 在平台内: %s' % ('是' if inx else '否!!', '是' if iny else '否!!'))
print('  高度差 %.1f 格 %s' % (abs(sz - want), '（OK，地面吸附会修正）'
                              if abs(sz - want) < 1.0 else '（!! 偏差过大）'))