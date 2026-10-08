#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""读 Unity 编译检查的结果（由 SilkCompileCheck.cs 写入）。

★ 用法：
    python .workbuddy/tools/read_compile_result.py

  它会提示你现在处于什么状态：
    · 没跑过编译检查 -> 告诉你点哪个菜单
    · 正在编译       -> 让你等一下再读
    · 有结果         -> 打印错误清单 + 提示「先修最小行号那个」

★ 为什么要有这个：
  Unity 不允许同一项目开两个实例 -> batchmode 被锁
  AI 无法直接触发编辑器内编译
  → 唯一可行的分工：**AI 写检查器，用户点一下，AI 读结果**
    这样用户不需要复制粘贴任何错误信息。
"""
import os
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(_HERE))
os.chdir(ROOT)

RESULT = os.path.join('Library', 'silk_compile_result.txt')


def main():
    if not os.path.exists(RESULT):
        print('=' * 64)
        print('还没跑过编译检查')
        print('=' * 64)
        print()
        print('请在 Unity 里点一下菜单：')
        print()
        print('    窗口（Window）> Silk 编译检查 (Compile Check)')
        print()
        print('点完之后我再读一次就行 —— 你不用复制粘贴任何报错。')
        print()
        print('★ 顺便说：Unity 切回前台时通常也会自动重新编译，')
        print('  所以「切一下窗口」也能触发。')
        return 2

    with open(RESULT, 'r', encoding='utf-8', errors='replace') as f:
        text = f.read().strip()

    print('=' * 64)
    print('Unity 编译检查结果')
    print('=' * 64)
    print()

    if text.startswith('STATUS: refreshing'):
        print('● Unity 正在编译中...')
        print()
        print('等几秒后再跑一次这个脚本。')
        return 2

    if text.startswith('RESULT: PASS'):
        print('[OK] 编译通过，无错误')
        return 0

    if text.startswith('RESULT: FAIL'):
        lines = text.split('\n')
        print('[FAIL] %s' % lines[1] if len(lines) > 1 else '[FAIL]')
        print()
        for ln in lines[2:]:
            if ln.strip():
                print('  %s' % ln.strip())
        print()
        print('★ 先修「line」最小的那个 —— 那是根因，其余是连锁反应。')
        return 1

    print('结果文件内容异常：')
    print(text[:500])
    return 2


if __name__ == '__main__':
    sys.exit(main())
