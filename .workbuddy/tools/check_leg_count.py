#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""腿数一致性检查（2026-10-11 建立，8 腿 → 4 腿时加）。

【为什么需要这个检查项】
把 8 条腿改成 4 条，字面量 `8` 散在三个文件里：
SilkSpiderFbxRig / SilkSpiderAnatomy / SilkSpiderBody。
**漏改任何一处都不会报编译错**，只会：
  · 少一条腿（数组长度 4 但循环跑 8 → 越界，或只显示前 4 条）
  · 骨骼名映射错位（legIndex 0..3 映射到 leg_L_1..4 → 左右全跑到左边）
  · HUD 步态图画出一半悬空的条带
  · 相位表长度 ≠ 腿数 → 有一条腿没相位，静默不动

★ 这些正是「编译 0 错误 + 全套检查通过，但画面是错的」的典型。
  2026-10-11 那次真编译全绿，但 `DrawGaitDiagram` 里还写着 `rowH * 8`，
  是人工 grep 才发现的 —— 所以这个检查项必须存在。

【检查项】
  ① SilkSpiderLegs.Count 是唯一的腿数定义处，其他类不得再写死 8
  ② 相位表长度 == 腿数
  ③ 互斥表索引假设（PerSide）与腿数一致
  ④ 骨骼名映射使用 PerSide 而不是硬编码 4
  ⑤ HUD 行列/高度由腿数推导，不得写死数字
"""

import os
import re
import sys

PROJ = os.path.dirname(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
)
SILK = os.path.join(PROJ, 'Assets', 'Silk')

FILES = {
    'legs': os.path.join(SILK, 'SilkSpiderLegs.cs'),
    'rig': os.path.join(SILK, 'SilkSpiderFbxRig.cs'),
    'anatomy': os.path.join(SILK, 'SilkSpiderAnatomy.cs'),
    'body': os.path.join(SILK, 'SilkSpiderBody.cs'),
    # ★ 测试关卡也必须纳入 —— 2026-10-11 的漏网就出在这里：
    #   HUD 里「步足 8 条」「着地≈ 8 × duty」「i < 8」全写死，
    #   而检查项只扫了 rig/anatomy/body 三个文件 → 漏掉了。
    #   ★ 教训：**新增文件必须同步加进检查范围**，否则检查形同虚设。
    'stage': os.path.join(SILK, 'SilkSpiderTestStage.cs'),
}


def strip_comments(src):
    src = re.sub(r'/\*.*?\*/', '', src, flags=re.S)
    src = re.sub(r'//[^\n]*', '', src)
    return src


def read(path):
    with open(path, encoding='utf-8') as f:
        return strip_comments(f.read())


def declared_count():
    """从 SilkSpiderLegs.cs 里读出实际腿数。"""
    if not os.path.isfile(FILES['legs']):
        return None
    src = read(FILES['legs'])
    m = re.search(r'Count\s*=\s*(\d+)\s*;', src)
    return int(m.group(1)) if m else None


def check_single_source():
    """① 其他类不得再写死8 作为腿数。

    ★ 2026-10-11 增补：原版只扫 `< 8` / `[8]` / `* 8` 三种形态，
      **漏了两种最常见的**（见实际漏网的两处）：
        · 直接写死的**下标**：staggerCache[4] ~ [7]
        · 与 4 比较的**索引判定**：i < 4 / i % 4
      第一处抛 IndexOutOfRangeException；第二处不崩但 4 腿下恒为真（逻辑静默错）。
    """
    errors = []
    for key in ('rig', 'anatomy', 'body', 'stage'):
        src = read(FILES[key])
        lines = src.split('\n')
        bad = []
        for m in re.finditer(r'new\s+\w+\[8\]', src):
            bad.append('new %s[8]' % m.group(1))
        for m in re.finditer(r'<\s*8\s*;', src):
            bad.append('循环上界 < 8')
        for m in re.finditer(r'\*\s*8\b', src):
            bad.append('乘法 *8')
        if bad:
            errors.append('%s.cs 仍有写死的 8：%s'
                          % (key, ', '.join(sorted(set(bad))[:6])))
    return errors


def check_hardcoded_index():
    """①b★ 硬编码下标 / 与 4 比较的索引判定。

    ★ 这一项是从真实崩溃里长出来的：
      用户跑起来报 `IndexOutOfRangeException at StaggerOffsets()`，
      根因是 `staggerCache[4] ~ [7]` —— 数组改成4 但赋值还按8 腿写。
      同时还查出两处 `i < 4` / `i % 4`：不崩，但 4 腿下恒为真。
    """
    errors = []
    # legCount 允许出现的数字（不该被当腿数相关）
    for key in ('rig', 'anatomy', 'body', 'stage'):
        path = FILES[key]
        with open(path, encoding='utf-8') as f:
            raw = f.read()
        src = strip_comments(raw)
        lines = src.split('\n')

        for ln, line in enumerate(lines, 1):
            # ① 直接写死的下标 4..7（1~3 允许，因为小腿数也要能用）
            for m in re.finditer(r'\w+\[\s*([4-9])\s*\]', line):
                errors.append('%s:%d 硬编码下标 %s[%s]'
                              '（数组长度随腿数变，写死必然越界或错位）'
                              % (key, ln, m.group(0)[:-1], m.group(1)))
            # ② 与 4/8 比较的索引判定
            for m in re.finditer(r'\b\w+\s*%\s*([48])\b', line):
                errors.append('%s:%d 索引取模写死 %s'
                              '（应 SilkSpiderLegs.PerSide）'
                              % (key, ln, m.group(0)))
            for m in re.finditer(r'\b(\w+)\s*<\s*([48])\b', line):
                # 排除 GUID 之类的误报：只看像索引的名字
                if re.match(r'^[a-z]{1,3}$', m.group(1)):
                    errors.append('%s:%d 索引比较写死 %s'
                                  '（应 SilkSpiderLegs.PerSide；'
                                  '4 腿时 i<4 恒为真）'
                                  % (key, ln, m.group(0)))
    # 去重
    seen = set()
    uniq = []
    for e in errors:
        if e not in seen:
            seen.add(e)
            uniq.append(e)
    return uniq


def check_phase_table(count):
    """② 相位表长度必须等于腿数。"""
    if count is None:
        return []
    src = read(FILES['body'])
    m = re.search(r'phaseOffsets\s*=\s*new\s+float\[\s*\]\s*\{([^}]*)\}', src)
    if not m:
        return ['找不到 phaseOffsets 数组字面量']

    body_txt = m.group(1)
    values = [v for v in re.split(r',', body_txt)
              if re.search(r'\d', v)]

    if len(values) != count:
        return ['★ phaseOffsets 有 %d 个值，但腿数是 %d'
                ' → 会有腿没有相位（静默不动）'
                % (len(values), count)]

    # 相位必须落在 [0,1)
    for i, v in enumerate(values):
        f = re.search(r'([\d.]+)f?', v)
        if f:
            val = float(f.group(1))
            if val < 0.0 or val >= 1.0:
                return ['phaseOffsets[%d] = %s 不在 [0,1) 区间' % (i, val)]

    # ★ 对角步态校验：左右对称的两条腿应该同相
    per = count // 2
    nums = [float(re.search(r'([\d.]+)', v).group(1)) for v in values]
    # L1(idx 0) 应与 R?(idx 2) 同相 —— 4 腿版是 L1/R2
    if count == 4:
        if abs(nums[0] - nums[3]) > 1e-6:
            return ['★ 4 腿对角步态被破坏：L1=%s 应与 R2=%s 同相'
                    % (nums[0], nums[3])]
        if abs(nums[1] - nums[2]) > 1e-6:
            return ['★ 4 腿对角步态被破坏：L2=%s 应与 R1=%s 同相'
                    % (nums[1], nums[2])]
        if abs(nums[0] - nums[1]) < 1e-6:
            return ['★ 4 腿步态退化：L1 与 L2 同相（应反相），'
                    '会出现两侧同步腾空']
    # ★ 8 腿交替四足步态校验（2026-10-11 换装 Quaternius 后启用）。
    #   规则来自 SilkSpiderBody.ComputePhase 注释的三条规律：
    #   ① 同侧隔条同相 ② 对角同相 ③ 相邻反相。
    #   ★ 只校验结构关系，不写死 0/0.5 —— 数组本身就是可调参数。
    if count == 8:
        L1, L2, L3, L4, R1, R2, R3, R4 = nums
        eps = 1e-6
        if abs(L1 - L3) > eps or abs(L2 - L4) > eps:
            return ['★ 8 腿步态规律①破坏：同侧隔条应同相（L1=%.2f L3=%.2f / '
                    'L2=%.2f L4=%.2f）' % (L1, L3, L2, L4)]
        if abs(R1 - R3) > eps or abs(R2 - R4) > eps:
            return ['★ 8 腿步态规律①破坏：同侧隔条应同相（R1=%.2f R3=%.2f / '
                    'R2=%.2f R4=%.2f）' % (R1, R3, R2, R4)]
        if abs(L1 - R2) > eps or abs(L2 - R1) > eps or abs(L3 - R4) > eps:
            return ['★ 8 腿步态规律②破坏：对角应同相（L1=%.2f R2=%.2f / '
                    'L2=%.2f R1=%.2f / L3=%.2f R4=%.2f）'
                    % (L1, R2, L2, R1, L3, R4)]
        if abs(L4 - R3) > eps:
            return ['★ 8 腿步态规律②破坏：L4=%.2f 应与 R3=%.2f 同相'
                    % (L4, R3)]
        if abs(L1 - L2) < eps:
            return ['★ 8 腿步态规律③破坏：相邻腿 L1 与 L2 同相（应反相），'
                    '会出现同侧两条腿同时腾空']
    return []


def check_neighbor_table(count):
    """③ 互斥表必须用 PerSide 而不是硬编码 4。"""
    src = read(FILES['body'])
    errors = []
    m = re.search(r'BuildNeighborTable\s*\(\s*\)\s*\{', src)
    if not m:
        return ['找不到 BuildNeighborTable']

    # 取函数体（大括号配平）
    start = m.end() - 1
    depth = 0
    body_txt = ''
    for ch in src[start:]:
        body_txt += ch
        if ch == '{':
            depth += 1
        elif ch == '}':
            depth -= 1
            if depth == 0:
                break

    if 'SilkSpiderLegs.PerSide' not in body_txt:
        errors.append('BuildNeighborTable 里没有用 SilkSpiderLegs.PerSide'
                      '→ 左右侧起点可能是写死的')
    if re.search(r'oppSide\s*=\s*left\s*\?\s*\d+\s*:\s*\d+', body_txt):
        errors.append('BuildNeighborTable 里 oppSide 写死了数字'
                      '（应从 PerSide 推导）')
    return errors


def check_bone_mapping():
    """④ 骨骼名映射必须用 PerSide。"""
    src = read(FILES['rig'])
    errors = []
    m = re.search(r'LegBoneNames\s*\(\s*int\s+legIndex\s*\)\s*\{', src)
    if not m:
        return ['找不到 LegBoneNames']
    start = m.end() - 1
    depth = 0
    body_txt = ''
    for ch in src[start:]:
        body_txt += ch
        if ch == '{':
            depth += 1
        elif ch == '}':
            depth -= 1
            if depth == 0:
                break

    if re.search(r'legIndex\s*<\s*\d+', body_txt):
        errors.append('LegBoneNames 里 legIndex 比较用了写死的数字'
                      '（应 SilkSpiderLegs.PerSide）')
    if re.search(r'legIndex\s*%\s*\d+', body_txt):
        errors.append('LegBoneNames 里 legIndex 取模用了写死的数字'
                      '（应 SilkSpiderLegs.PerSide）')
    return errors


def check_hud(count):
    """⑤ HUD 行列/条带高度必须由腿数推导。"""
    src = read(FILES['body'])
    errors = []
    m = re.search(r'DrawGaitDiagram\s*\(\s*\)\s*\{', src)
    if not m:
        return ['找不到 DrawGaitDiagram']
    start = m.end() - 1
    depth = 0
    body_txt = ''
    for ch in src[start:]:
        body_txt += ch
        if ch == '{':
            depth += 1
        elif ch == '}':
            depth -= 1
            if depth == 0:
                break

    if re.search(r'rowH\s*\*\s*\d+', body_txt):
        errors.append('★ DrawGaitDiagram 里条带高度写死了行数'
                      '（应 SilkSpiderLegs.Count）—— 4 腿时会画出一半悬空条带')

    # LegName 的左右判定
    m2 = re.search(r'LegName\s*\(\s*int\s+\w+\s*\)\s*\{', src)
    if m2:
        start2 = m2.end() - 1
        depth = 0
        t2 = ''
        for ch in src[start2:]:
            t2 += ch
            if ch == '{':
                depth += 1
            elif ch == '}':
                depth -= 1
                if depth == 0:
                    break
        if re.search(r'i\s*<\s*\d+', t2) or re.search(r'i\s*%\s*\d+', t2):
            errors.append('★ LegName 里左右判定/ 编号写死了数字'
                          '（应 SilkSpiderLegs.PerSide）—— 4 腿时 R1 会标成 R3')
    return errors


def check_hud_text():
    """⑥ HUD / 提示文案里的腿数必须由常量推导。

    ★ 这一项来自真实验收：截图里 HUD 显示「步足 8 条」，
      而代码早已改成 4条 —— 用户一眼就看出「没改干净」。

    判据：凡是把腿数写进**字符串**的地方，都必须有 SilkSpiderLegs.Count。
    ★ 这类漏网最刺眼：代码能编译、能运行、逻辑也对，
      唯独显示的数字是错的 —— 用户会以为整个改动没生效。
    """
    errors = []
    path = FILES['stage']
    if not os.path.isfile(path):
        return ['SilkSpiderTestStage.cs 不存在']
    with open(path, encoding='utf-8') as f:
        raw = f.read()
    src = strip_comments(raw)

    # ★★★ 这一项第一版写错过：正则的转义层数多了一层，
    #   实际匹配的是字面反斜杠+s，于是**反向测试注入了 bug 也报通过**。
    #   → 判据必须靠**反向测试**验证，不能「看着对」就算。
    #   （教训与「判据过窄会误报」同源，只是方向相反）

    # ① HUD 里用 (8 * ...) 算腿数
    if re.search(r'\(\s*8\s*\*', src):
        errors.append('HUD 里写死 (8 * ...) 计算腿数，应 SilkSpiderLegs.Count')

    # ② HUD 文案里出现「8 条」
    if re.search(r'8\s*条', src):
        errors.append('HUD 文案里写死「8 条」，应拼 SilkSpiderLegs.Count')

    # ③ 骨节统计循环写死 i < 8
    if re.search(r'i\s*<\s*8\b', src):
        errors.append('骨节统计循环写死 i < 8，应 SilkSpiderLegs.Count')

    return errors


def main():
    print('=' * 74)
    print('腿数一致性检查（8 腿 → 4 腿）')
    print('=' * 74)
    print()

    count = declared_count()
    if count is None:
        print('读不到 SilkSpiderLegs.Count —— 先建那个文件')
        return 1
    per_side = count // 2
    print('★ 当前腿数 SilkSpiderLegs.Count = %d（每侧 %d 条）' % (count, per_side))
    print()

    all_errors = []
    checks = [
        ('① 腿数单一来源（其他类不写死 8）', lambda: check_single_source()),
        ('①b 无硬编码下标 / 与4 8 比较的索引判定',
         check_hardcoded_index),
        ('② 相位表长度与对角步态', lambda: check_phase_table(count)),
        ('③ 互斥表索引假设', lambda: check_neighbor_table(count)),
        ('④ 骨骼名映射', check_bone_mapping),
        ('⑤ HUD 行列由腿数推导', lambda: check_hud(count)),
        ('⑥ HUD 文案里的腿数', check_hud_text),
    ]

    for label, fn in checks:
        errs = fn()
        if errs:
            print('[%s]' % label)
            for e in errs:
                print('    ERROR %s' % e)
            all_errors += errs
        else:
            print('[%s] ✓' % label)

    print()
    print('=' * 74)
    if all_errors:
        print('✗ %d 项失败 —— 腿数不一致会有「静默少腿 / 骨骼错位 / HUD 半截」'
              % len(all_errors))
        return 1
    print('✓ 通过 —— %d 条腿（每侧 %d），相位/ 互斥 / 骨骼 / HUD 全部对齐'
          % (count, per_side))
    return 0


if __name__ == '__main__':
    sys.exit(main())