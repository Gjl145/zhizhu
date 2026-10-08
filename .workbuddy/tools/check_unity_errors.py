#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""读 Unity 的 Editor.log，报告编译错误。

★ 为什么需要这个（用户 2026-10-08 的质问）：
  「不是让你自检吗？结果就是一个错误需要修复 3 遍？
    有没有办法你自己去看看编译报错呢？」

  —— 自检脚本（selfcheck / check_regressions）都是**文本启发式**，
  抓不到 CS0103 / CS1061 这类需要真正作用域分析的错误。
  而 **Unity 自己已经把答案写在日志里了**，而且带精确行号。
  ★ 所以第一件事应该是「去读它」，而不是「再写一个猜测的检查」。

用法：
    python .workbuddy/tools/check_unity_errors.py        # 报告最近一轮错误
    python .workbuddy/tools/check_unity_errors.py --all  # 报告全部历史错误

退出码：有 error 时返回 1（便于脚本化）
"""
import os
import re
import sys

# ★ 注意：Python 3.13 的 os 模块没有 os.dirname / os.abspath，
#   只能用 os.path.*。写成 os.dirname 会报
#   "module 'os' has no attribute 'dirname'"（报错信息很有误导性）。
_HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(_HERE))
os.chdir(ROOT)

# Unity 的 Editor.log 位置（Windows 标准路径）
LOG_CANDIDATES = [
    os.path.expanduser('~/AppData/Local/Unity/Editor/Editor.log'),
    os.path.expanduser('~/Library/Logs/Unity/Editor.log'),
]

ERR_RE = re.compile(
    r'(Assets[/\\][^:]+\.cs)\((\d+),(\d+)\): error (CS\d+): (.+?)(?:\r?\n|$)')


def find_log():
    for p in LOG_CANDIDATES:
        if os.path.exists(p):
            return p
    return None


def main():
    show_all = '--all' in sys.argv

    log = find_log()
    if not log:
        print('★ 找不到 Unity 的Editor.log')
        print('  试过：%s' % ', '.join(LOG_CANDIDATES))
        print('★ 这不代表没有编译错误 —— 只是日志路径没找到。')
        return 0

    # 日志可能有几十 MB，只读尾部
    size = os.path.getsize(log)
    with open(log, 'r', encoding='utf-8', errors='replace') as f:
        if size > 8 * 1024 * 1024:
            f.seek(size - 8 * 1024 * 1024)      # 只读最后 8 MB
            f.readline()                          # 丢掉被截断的半行
        text = f.read()

    # ★★ 关键：判断日志是「修复前」还是「修复后」的。
    #
    # Unity 不会主动编译 —— 我改完代码后，它要等用户切回编辑器窗口
    # 或点Refresh 才会重编。所以日志里的错误**可能已经过时**。
    #
    # 做法：比较「日志最后写入时间」与「代码文件最后修改时间」。
    #   日志比代码旧 -> 日志里的错误是**修复前的**，不可信。
    log_mtime = os.path.getmtime(log)
    newest_code = 0.0
    for root, dirs, files in os.walk('Assets'):
        dirs[:] = [d for d in dirs if d != '..']
        for fn in files:
            if fn.endswith('.cs'):
                fp = os.path.join(root, fn)
                if os.path.exists(fp):
                    newest_code = max(newest_code, os.path.getmtime(fp))

    stale = newest_code > log_mtime

    # 只取最后一次编译的错误（历史错误不能重复报）
    marks = list(re.finditer(
        r'(?:- Starting script compilation|Compilation failed|'
        r'Assets/[\w/]+\.cs\(\d+,\d+\): error)', text))
    if show_all or not marks:
        region = text
    else:
        region = text[marks[-1].start():]

    errors = {}
    for m in ERR_RE.finditer(region):
        path, line, col, code, msg = m.groups()
        path = path.replace('\\', '/')
        errors.setdefault((path, int(line), code), msg.strip())

    print('=' * 68)
    print('Unity 编译错误（来自 Editor.log，非猜测）')
    print('=' * 68)
    print('日志: %s' % log)
    import time as _t
    print('日志写入: %s' % _t.strftime('%H:%M:%S',
                                        _t.localtime(log_mtime)))
    print('代码修改: %s' % _t.strftime('%H:%M:%S',
                                        _t.localtime(newest_code)))
    if stale:
        print()
        print('★★ ★ ★ 日志已过时！**')
        print('  代码比日志新 —— Unity 还没重新编译。')
        print('  **下面列的是修复前的旧错误，不代表当前状态。**')
        print('  请在 Unity 里点「资源（Assets）> 刷新（Refresh）」'
              '或切回编辑器窗口触发编译。')
        print('  ★ 或者：别管这里，直接去 Unity 控制台（Console）看。')
    print()

    if not errors:
        print('[OK] 没有编译错误')
        print()
        print('★ 注意：如果刚改完代码，Unity 可能还没重新编译。')
        print('  在编辑器里点一下「资源（Assets）> 刷新（Refresh）」或等它自动编译。')
        return 0

    # 按文件 + 行号排序，根因（最小行号）排在最前
    for (path, line, code), msg in sorted(errors.items(),
                key=lambda kv: (kv[0][0], kv[0][1])):
        print('  [%s] %s:%d' % (code, path, line))
        print('         %s' % msg[:150])

    print()
    print('-' * 68)
    total = len(errors)
    print('共 %d 个错误' % total)
    if total > 1:
        print()
        print('★ ★ 多个错误通常是「一个根因 + 连锁反应」。')
        print('  **先修最小行号那个** —— 那是根因。')
        print('  本例中最小行号是 %d（%s）。'
              % (min(v[1] for v in errors), 'root cause'))
    return 1


if __name__ == '__main__':
    sys.exit(main())
