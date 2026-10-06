import re
s = open('Assets/Silk/SilkBuilder.cs', encoding='utf-8').read()
t = re.sub(r'//[^\n]*', '', s)
t = re.sub(r'/\*.*?\*/', '', t, flags=re.S)
t = re.sub(r'@"(?:[^"]|"")*"', '""', t, flags=re.S)
t = re.sub(r'"(?:\\.|[^"\\])*"', '""', t)
t = re.sub(r"'(?:\\.|[^'\\])*'", "''", t)
print('braces', t.count('{'), t.count('}'),
      'parens', t.count('('), t.count(')'),
      'brackets', t.count('['), t.count(']'))

print()
print("=== 当前参数（按类分别抓取）===")
vals = {}


def grab(cls_marker, key):
    """在指定类区间内抓字段值。避免抓错同名字段：
    moveSpeed 在 SimpleOrbitCamera(FreeFly 飞行) 与
    SilkParkourController(跑酷) 里各有一个。"""
    i = s.index(cls_marker)
    j = len(s)
    m = re.search(r'\npublic class |\npublic static class ', s[i + 10:])
    if m:
        j = i + 10 + m.start()
    body = s[i:j]
    mm = re.search(r'public (?:float|Vector3) ' + key + r'\s*=\s*([^;]+);', body)
    return mm.group(1).strip() if mm else None


for k in ['jumpSpeed', 'gravity', 'camDistance', 'camHeight',
          'visualRadius', 'startPosition', 'terminalVel']:
    v = grab('class SilkParkourController', k)
    if v:
        vals[k] = v
        print('  %-16s = %s' % (k, v))

for k in ['moveSpeed', 'groundAccel', 'groundDecel', 'sprintMultiplier', 'momentumBleed']:
    v = grab('class SilkParkourController', k)
    if v:
        vals[k] = v
        print('  %-16s = %s   (SilkParkourController)' % (k, v))

print('  %-16s = %s   (SimpleOrbitCamera, FreeFly 飞行，不影响跑酷)'
      % ('camMoveSpeed', grab('class SimpleOrbitCamera', 'moveSpeed')))

print()
print("=== 手感核算 ===")


def num(x):
    return float(x.replace('f', ''))


g = num(vals['gravity']); v = num(vals['jumpSpeed']); sp = num(vals['moveSpeed'])
apex = v * v / (2 * g); airt = 2 * v / g
print('  跳跃最高 %.1f 格，在空中 %.2f 秒' % (apex, airt))
print('  跑速跳水平跨度 = %g x %.1f = %.0f 格' % (sp, airt, sp * airt))
print('  平台间隙 5~10 格 -> %s' % ('跨度合理、可控' if sp * airt < 80 else '!! 仍过大'))
print('  到全速 %.2f 秒 / 停止 %.2f 秒'
      % (sp / num(vals['groundAccel']), sp / num(vals['groundDecel'])))
print()
print('  %g 格/秒 ≈ %.0f km/h（1格≈1米）' % (sp, sp * 3.6))
print('  博尔特百米峰值约 12 格/秒 -> 当前是 %.1f 倍' % (sp / 12))
print('  对比：450 格/秒 = %.0f km/h（比跑车快 10 倍）' % (450 * 3.6))