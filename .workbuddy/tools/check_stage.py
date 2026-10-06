"""跑酷关卡可通关性核验。
用法：python .workbuddy/tools/check_stage.py
"""
import re

s = open('Assets/Silk/SilkBuilder.cs', encoding='utf-8').read()
stage = open('Assets/Silk/SilkTestLevel.cs', encoding='utf-8').read()


def num(x):
    return float(x.replace('f', ''))


def grab(key):
    i = s.index('class SilkParkourController')
    m = re.search(r'public float ' + key + r'\s*=\s*([^;]+);', s[i:])
    return num(m.group(1))


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
# 先取出所有 Plat("名字", ... 调用，再按出现顺序取成对的 Vector3
calls = re.findall(r'Plat\("([^"]+)"', stage)
# 只看 Create() 函数体，避免把别处的 Vector3 也算进来
body = stage[stage.index('public static void Create'):stage.index('public static void Clear')]
vecs = re.findall(r'new Vector3\(\s*(-?[\d.]+)f,\s*(-?[\d.]+)f,\s*(-?[\d.]+)f\s*\)', body)

if len(vecs) < 2 * len(calls):
    print('!! 解析到 %d 个 Plat 调用但只有 %d 个 Vector3 —— 数量不匹配'
          % (len(calls), len(vecs)))
    raise SystemExit(1)

for i, n in enumerate(calls):
    c = tuple(float(x) for x in vecs[i * 2])
    sz = tuple(float(x) for x in vecs[i * 2 + 1])
    plats.append((n, c, sz))


def bx(c, sz, i):
    return (c[i] - sz[i] / 2, c[i] + sz[i] / 2)


B = {n: [bx(c, sz, i) for i in range(3)] for n, c, sz in plats}

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
for i in range(len(plats)):
    for j in range(i + 1, len(plats)):
        n1, n2 = plats[i][0], plats[j][0]
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
inx = B[p0][0][0] <= sx <= B[p0][0][1]
iny = B[p0][1][0] <= sy <= B[p0][1][1]
top = B[p0][2][1]
want = top + r
print('  平台 %s 顶面 z=%.1f -> 球心应 z=%.1f' % (p0, top, want))
print('  startPosition=(%.1f, %.1f, %.1f)  z 差 %+.1f 格' % (sx, sy, sz, sz - want))
print('  x 在平台内: %s | y 在平台内: %s' % ('是' if inx else '否!!', '是' if iny else '否!!'))
print('  高度差 %.1f 格 %s' % (abs(sz - want), '（OK，地面吸附会修正）'
                              if abs(sz - want) < 1.0 else '（!! 偏差过大）'))