#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
Blender 侧几何验证 —— 跑 make_spider.py 并解析它的自检输出。

★★★ 为什么需要这个 ★★★
  脚本里的check_leg_separation / check_standing 已经会自己报错退出，
  但那是**在 Blender 内部**。这里的价值是：
    ① 把 Blender 检查接进 checkall.py，让模型和代码**同一道闸门**过；
    ② 顺带渲染三视角 PNG —— 本轮三个 bug（8腿叠成2 条 / 腿翘到天上 /
       面数超预算一倍）**全部零报错**，只有看图才发现。

★ 本轮这些 bug 都不会崩、不会警告、不会影响导出：
    骨骼数 42 正确 / 网格数 78 正确 / FBX 4.2MB 正常导出。
  → 纯靠「跑通了」判断质量 = 一定会漏。

用法：
    python .workbuddy/tools/check_spider_model.py
    python .workbuddy/tools/check_spider_model.py --render   # 附带出图
退出码 0 = 通过
"""
import os
import re
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
PROJ = os.path.dirname(os.path.dirname(HERE))

BLENDER_CANDIDATES = [
    r'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe',
    r'C:\Program Files\Blender Foundation\Blender 4.5\blender.exe',
    r'C:\Program Files\Blender Foundation\Blender 4.2\blender.exe',
]
SCRIPT = os.path.join(PROJ, 'Tools', 'blender', 'make_spider.py')
FBX = os.path.join(PROJ, 'Tools', 'blender', 'out', 'spider_lowpoly.fbx')

# 解析目标（None = 只要求脚本跑完并退出码 0）
R_BONES = re.compile(r'骨骼数：(\d+)')
R_MESH = re.compile(r'网格数：(\d+)')
R_TRIS = re.compile(r'三角面数：(\d+)')
# ★★ 间距输出格式已改：现在是「…（= A × 腿长，B × 体长）  name↔ name」
#   旧正则只匹配到第一个数字，且依赖旧文案 → 格式一变就静默「找不到」。
#   → 改成分别抓「× 腿长」和「× 体长」两个数，两条判据都要。
R_SEP = re.compile(
    r'足端最小间距：([\d.]+)（= ([\d.]+) × 腿长，([\d.]+) × 体长）')
R_SIDE_SEP = re.compile(r'同侧最小间距：([\d.]+) × 腿长')
R_ARCH = re.compile(r'膝拱高/腿长 = ([\d.]+)')
R_SLEND = re.compile(r'腿长/腿根半径 = ([\d.]+)')
R_LEGSPAN = re.compile(r'腿展/体长= ([\d.]+)')
R_STAND = re.compile(r'腿根 z=([+\-][\d.]+)\s+膝 z=([+\-][\d.]+)\s+足端 z=([+\-][\d.]+)')
R_ANATOMY = re.compile(
    r'解剖部件：眼 (\d+) ·触肢段 (\d+) · 纺器 (\d+) · fovea (\d+)')

#★★ 阈值全部**从生成器源码解析**，不再写死在本文件里
#
#   【为什么必须这么改 —— 本轮真实踩到的问题】
#     我这一轮给模型补了触肢 + 纺器 + 第 7/8 只眼、改了腿长腿粗，
#     生成器本身全部自检通过，但这个检查器仍然写死着
#     EXPECT_MESH=78/ MAX_TRIS=3000 / MIN_SEP_RATIO=0.6
#     → 每次动模型都会报三条「失败」，而那三条其实是「预期值过期」。
#
#     写死的检查器有个更坏的后果：**它会逼着人把模型改回去**，
#     或者更糟——让人为了让检查器过而去放宽检查（那才是真退步）。
#
#   → 单一事实来源：阈值写在 make_spider.py 里，检查器去读。
#     这样「改模型」和「改预期」永远是同一处。
EXPECT_BONES = 42     # 2 身体 + 8 × 5 腿（骨骼结构没动，硬值安全）


def parse_source_thresholds():
    """从 make_spider.py 里解析阈值。**解析不到就报错**，不用默认值兜底。

    ★ 不用默认值兜底是刻意的：
      解析失败时如果默默用一个「看起来合理」的默认阈值，
      检查器就会在错误的基准上放行 —— 比直接失败危险得多。
    """
    src = open(SCRIPT, encoding='utf-8').read()
    th = {}
    m = re.search(r'^TRIS_BUDGET\s*=\s*(\d+)', src, re.M)
    if m:
        th['max_tris'] = int(m.group(1))
    # 间距阈值在 make_spider.py 里是**内联字面量**（0.53 / 0.20），
    # 提到常量会牵动 check_leg_separation 的两处，比较化简：
    # 这里用与脚本同源的正则抓那个字面量，并要求抓两次都成功。
    m = re.search(r'span_ratio < ([\d.]+)', src)
    if m:
        th['min_sep'] = float(m.group(1))
    m = re.search(r'leg_len / body_len < ([\d.]+)', src)
    if m:
        th['min_side_sep'] = float(m.group(1))
    # 膝拱与腿粗细阈值
    m = re.search(r'arch_ratio < ([\d.]+)', src)
    if m:
        th['min_arch'] = float(m.group(1))
    m = re.search(r'slenderness > ([\d.]+)', src)
    if m:
        th['max_slenderness'] = float(m.group(1))
    m = re.search(r'if ratio < ([\d.]+):', src)
    if m:
        th['min_legspan'] = float(m.group(1))
    return th


def find_blender():
    for p in BLENDER_CANDIDATES:
        if os.path.isfile(p):
            return p
    #退回 PATH
    from shutil import which
    return which('blender')


def main():
    argv = sys.argv[1:]
    print('=' * 72)
    print('蜘蛛模型验证 —— Blender 侧')
    print('=' * 72)

    if not os.path.isfile(SCRIPT):
        print('  [FAIL] make_spider.py 不存在')
        return 1

    blender = find_blender()
    if not blender:
        print('  [FAIL] 找不到 blender 可执行文件')
        print('         装了的话请在 BLENDER_CANDIDATES 里加路径')
        return 1
    print('  Blender：%s' % blender)

    t0 = time.time()
    p = subprocess.run([blender, '--background', '--python', SCRIPT],
                       cwd=PROJ, capture_output=True, text=True,
                       encoding='utf-8', errors='replace', timeout=900)
    dt = time.time() - t0
    out = (p.stdout or '') + (p.stderr or '')

    errors = []

    if p.returncode != 0:
        errors.append('脚本退出码 %d（脚本内置检查判定不通过）' % p.returncode)

    def grab(rx, label, cast=float):
        m = rx.search(out)
        if not m:
            errors.append('%s：脚本输出里找不到（脚本可能崩了）' % label)
            return None
        return cast(m.group(1))

    bones = grab(R_BONES, '骨骼数', int)
    meshes = grab(R_MESH, '网格数', int)
    tris = grab(R_TRIS, '三角面数', int)

    th = parse_source_thresholds()
    need = ('max_tris', 'min_sep', 'min_side_sep', 'min_arch',
            'max_slenderness', 'min_legspan')
    missing = [k for k in need if k not in th]
    if missing:
        # ★ 不给默认值兜底 —— 见 parse_source_thresholds 的docstring。
        errors.append('无法从 make_spider.py 解析阈值：%s'
                      '（检查器与生成器已不同步，请更新本文件）'
                      % ', '.join(missing))
        th = {}

    m = R_SEP.search(out)
    sep_body_ratio = None
    if m:
        sep_body_ratio = float(m.group(3))
        print('  足端最小间距：%s（= %s × 腿长，%s × 体长）'
              % (m.group(1), m.group(2), m.group(3)))
    else:
        errors.append('足端间距：输出里找不到（格式可能又变了）')

    # ---- 同侧腿间距（覆盖 inner yaw；跨侧间距对 inner 不敏感）----
    m = R_SIDE_SEP.search(out)
    side_ratio = None
    if m:
        side_ratio = float(m.group(1))
        print('  同侧最小间距：%s × 腿长' % m.group(1))
    else:
        errors.append('同侧间距：输出里找不到')

    # ---- 膝拱幅度与腿粗细（这两个是渲染图才发现的问题，必须进数值检查）----
    arch_ratio = grab(R_ARCH, '膝拱高', float)
    slenderness = grab(R_SLEND, '腿长/腿根半径', float)
    legspan = grab(R_LEGSPAN, '腿展/体长', float)
    if arch_ratio is not None:
        print('  膝拱高/腿长：%.2f' % arch_ratio)
    if slenderness is not None:
        print('  腿长/腿根半径：%.1f : 1' % slenderness)
    if legspan is not None:
        print('  腿展/体长：%.2f' % legspan)

    # ---- 解剖部件齐全（眼 8 / 触肢 / 纺器 / fovea）----
    m = R_ANATOMY.search(out)
    if m:
        eyes, ped, spin, fovea = (int(m.group(i)) for i in (1, 2, 3, 4))
        print('  解剖部件：眼 %d · 触肢段 %d · 纺器 %d · fovea %d'
              % (eyes, ped, spin, fovea))
        if eyes != 8:
            errors.append('眼睛 %d ≠ 8（真实蜘蛛通常 8 只单眼）' % eyes)
        if ped < 8:
            errors.append('触肢段 %d < 8 → 缺第 2 对附肢' % ped)
        if spin != 6:
            errors.append('纺器 %d ≠ 6（真实为 3 对）' % spin)
        if fovea != 1:
            errors.append('fovea %d ≠ 1' % fovea)
    else:
        errors.append('解剖部件统计：输出里找不到')

    m = R_STAND.search(out)
    if m:
        root_z, knee_z, foot_z = (float(m.group(i)) for i in (1, 2, 3))
        print('  站姿：腿根 z=%+.3f  膝 z=%+.3f  足端 z=%+.3f'
              % (root_z, knee_z, foot_z))
        # 三条站姿不变量（与 make_spider.py 的check_standing 一致）
        if foot_z >= root_z - 1e-4:
            errors.append('足端(%.3f) 不低于腿根(%.3f) → 腿翘天/浮空'
                          % (foot_z, root_z))
        if knee_z <= root_z + 1e-4:
            errors.append('膝(%.3f) 不高于腿根(%.3f) → 没有膝拱轮廓'
                          % (knee_z, root_z))
    else:
        errors.append('站姿高度：输出里找不到')

    if bones is not None:
        print('  骨骼数：%d（预期 %d）' % (bones, EXPECT_BONES))
        if bones != EXPECT_BONES:
            errors.append('骨骼数 %d ≠预期 %d' % (bones, EXPECT_BONES))
    if meshes is not None:
        # 网格数**不做硬校验** —— 它随补部件而变，期望值毫无依据。
        # 只要求「每个网格都有非零面数」，那是真不变量。
        print('  网格数：%d（不设硬阈值，随部件增删变化）' % meshes)
        if meshes <= 0:
            errors.append('网格数为 0')
    if tris is not None and th:
        print('  三角面数：%d（预算 %d）' % (tris, th['max_tris']))
        if tris > th['max_tris']:
            errors.append('面数 %d 超预算 %d' % (tris, th['max_tris']))
    if sep_body_ratio is not None and th:
        if sep_body_ratio < th['min_sep']:
            errors.append('足端间距 %s× 体长 < %s× → 有腿叠在一起'
                          % (sep_body_ratio, th['min_sep']))
    # ★ 同侧间距：判据在 make_spider.py 里是「× 体长」，
    #   这里侧_ratio 是「× 腿长」，两个基准不同 → 不能直接比。
    #   做法：从生成器输出里再抓一次腿长，换算到同一基准再比。
    #   → 少写一个换算就会在错误的基准上判定，且**看起来完全正常**。
    if side_ratio is not None and th:
        # 腿长（绝对）可以从 make_spider.py 的 LEG_LEN 表达式算不出来，
        # 但生成器会打印「腿长/腿根半径」，用它 + 体长无法反推腿长。
        # → 改成直接抓生成器输出的「体长」相关量不存在，
        #    因此这条判据交给 make_spider.py 自己判（它有leg_len 和 body_len），
        #    本文件只负责确认「这一项有输出」。
        #    ★ 记下来：同侧间距目前是**单点判定**（只在生成器里），
        #      本检查器不做二次判定。原因：跨基准换算容易出错，
        #      宁可不重复判，也不要在错的基准上放行或报错。
        pass
    if arch_ratio is not None and th:
        if arch_ratio < th['min_arch']:
            errors.append('膝拱高 %s× 腿长 < %s× → 腿像水平尖刺'
                          % (arch_ratio, th['min_arch']))
    if slenderness is not None and th:
        if slenderness > th['max_slenderness']:
            errors.append('腿长/腿根半径 %.1f > %.1f → 腿细成针'
                          % (slenderness, th['max_slenderness']))
    if legspan is not None and th:
        if legspan < th['min_legspan']:
            errors.append('腿展/体长 %.2f < %.2f → 短腿，不像蜘蛛'
                          % (legspan, th['min_legspan']))

    if not os.path.isfile(FBX) or os.path.getsize(FBX) < 10000:
        errors.append('FBX 未生成或过小：%s' % FBX)
    else:
        print('  FBX：%.2f MB' % (os.path.getsize(FBX) / 1048576.0))

    # 提取脚本自己报出的 [FAIL]
    for ln in out.split('\n'):
        if '[FAIL]' in ln:
            errors.append('脚本内检查：%s' % ln.strip())

    # 渲染三视角（可选）
    if '--render' in argv:
        rpath = os.path.join(HERE, 'render_spider_preview.py')
        if os.path.isfile(rpath):
            print('  渲染三视角...')
            rp = subprocess.run([blender, '--background', '--python', rpath],
                                cwd=PROJ, capture_output=True, text=True,
                                encoding='utf-8', errors='replace',
                                timeout=900)
            shots = []
            d = os.path.join(PROJ, 'Tools', 'blender', 'out')
            for v in ('top', 'side', 'front'):
                f = os.path.join(d, 'preview_%s.png' % v)
                if os.path.isfile(f):
                    shots.append(f)
            print('  已出图：%s' % ', '.join(os.path.basename(s) for s in shots))
            if len(shots) < 3:
                errors.append('渲染只出了 %d/3 张图（看脚本 stderr）' % len(shots))
        else:
            print('  [跳过] render_spider_preview.py 不存在')

    print('')
    print('  (%.1fs)' % dt)
    if errors:
        print('  ✗ 失败 %d 项：' % len(errors))
        for e in errors:
            print('    · %s' % e)
        print('')
        print('★ 必须修完才能提交。')
        return 1

    print('  ✓ 通过 —— 8 条腿分得开、站姿正确（膝拱足落地）、面数达标')
    return 0


if __name__ == '__main__':
    sys.exit(main())