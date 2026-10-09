#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
一键全检 —— 按正确顺序跑完所有检查，并在任何一项失败时立刻停下。

★★★ 为什么不手动逐个跑 ★★★
  2026-10-09：因为只跑了「自检+回归」就提交，
  结果漏掉 3 个真实编译错误，用户看到一堆报错。
  → 根因不是「没检查」，是**检查顺序没固定**，
    靠我每次记得跑，必然会漏。
  → 正确解法：把顺序写死在脚本里，让我无法跳过。

用法：
    python .workbuddy/tools/checkall.py
退出码 0 = 全通过；1 = 有失败项
"""
import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
PROJ = os.path.dirname(os.path.dirname(HERE))

# (脚本名, 是否关键失败即停, 说明)
STEPS = [
    ('real_compile.py',      True,  '真编译（Roslyn，与 Unity 同参数）'),
    ('precheck.py',          True,  '括号 / 属性特性 / CS0103 / 死变量'),
    ('selfcheck.py',         False, '39 项结构与参数联动自检'),
    ('check_regressions.py', False, '历史错误回归'),
]

PY = sys.executable


def main():
    print('=' * 72)
    print('一键全检 —— zhizhu')
    print('=' * 72)

    results = []
    for script, critical, desc in STEPS:
        path = os.path.join(HERE, script)
        if not os.path.isfile(path):
            print('  [跳过] %s 不存在' % script)
            continue

        print('')
        print('--- [%s] %s ---' % (script, desc))
        t0 = time.time()
        try:
            p = subprocess.run([PY, path], cwd=PROJ,
                               capture_output=True, text=True,
                               encoding='utf-8', errors='replace',
                               timeout=900)
            out = (p.stdout or '')
            errout = (p.stderr or '')
            dt = time.time() - t0
            # 只打印结论行，避免刷屏
            tail = []
            for ln in out.split('\n'):
                s = ln.strip()
                if not s:
                    continue
                if ('错误' in s or 'ERR' in s or '通过' in s
                        or '✓' in s or 'ERROR' in s
                        or 'WARN' in s and '提示' not in s):
                    tail.append(s)
            for s in tail[-4:]:
                print('   %s' % s)
            #★ 关键：子脚本自己崩了时stdout 是空的，
            #   只有 stderr 有内容。不打出来就只看到「退出码 2」，
            #   完全不知道发生了什么（本轮踩过）。
            if p.returncode != 0 and errout.strip():
                print('   --- stderr ---')
                for ln in errout.strip().split('\n')[-8:]:
                    print('   %s' % ln)
            print('   (%.1fs, 退出码 %d)' % (dt, p.returncode))

            failed = p.returncode != 0
            results.append((script, desc, failed, critical))
        except subprocess.TimeoutExpired:
            print('   ★ 超时')
            results.append((script, desc, True, critical))

    print('')
    print('=' * 72)
    failed_critical = [r for r in results if r[2] and r[3]]
    failed_other = [r for r in results if r[2] and not r[3]]

    for script, desc, failed, _ in results:
        mark = '✗ 失败' if failed else '✓ 通过'
        print('  %-6s %s' % (mark, script))

    print('')
    if failed_critical:
        print('★★★ 关键项失败，必须修完才能提交：')
        for script, desc, _, _ in failed_critical:
            print('   · %s —— %s' % (script, desc))
        return 1

    if failed_other:
        print('★ 有非关键项失败，请看一眼：')
        for script, desc, _, _ in failed_other:
            print('   · %s —— %s' % (script, desc))
        return 1

    print('全通过。可以提交。')
    return 0


if __name__ == '__main__':
    sys.exit(main())