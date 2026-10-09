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
R_SEP = re.compile(r'足端最小间距：([\d.]+)（= ([\d.]+) × 腿长）')
R_STAND = re.compile(r'腿根 z=([+\-][\d.]+)\s+膝 z=([+\-][\d.]+)\s+足端 z=([+\-][\d.]+)')

EXPECT_BONES = 42     # 2 身体 + 8 × 5 腿
EXPECT_MESH = 78      # 身体 18 + 腿 40 锥台 + 24 关节球
MIN_SEP_RATIO = 0.6   # 足端最小间距 / 腿长（由原始 bug 值 0.03 校准）
MAX_TRIS = 3000


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

    m = R_SEP.search(out)
    sep_ratio = None
    if m:
        sep_ratio = float(m.group(2))
        print('  足端最小间距：%s（= %s × 腿长）' % (m.group(1), m.group(2)))
    else:
        errors.append('足端间距：输出里找不到')

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
        print('  网格数：%d（预期 %d）' % (meshes, EXPECT_MESH))
        if meshes != EXPECT_MESH:
            errors.append('网格数 %d ≠ 预期 %d' % (meshes, EXPECT_MESH))
    if tris is not None:
        print('  三角面数：%d（预算 %d）' % (tris, MAX_TRIS))
        if tris > MAX_TRIS:
            errors.append('面数 %d 超预算 %d' % (tris, MAX_TRIS))
    if sep_ratio is not None:
        if sep_ratio < MIN_SEP_RATIO:
            errors.append('足端间距 %s× 腿长 < %s× → 有腿叠在一起'
                          % (sep_ratio, MIN_SEP_RATIO))

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