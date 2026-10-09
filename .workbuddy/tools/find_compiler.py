# -*- coding: utf-8 -*-
"""定位 Unity 自带的 Roslyn 编译器（DLL 形式，可用托管方式调用）。"""
import os

CANDIDATE_ROOTS = [
    r'C:\Program Files\Unity Hub',
    r'C:\Program Files\Unity',
    r'C:\ProgramData\Unity',
    r'D:\Program Files\Unity Hub',
    r'D:\Unity',
    r'D:\ai',
    r'C:\Program Files',
]

WANT = ('csc.dll', 'csc.exe', 'Microsoft.CodeAnalysis.CSharp.dll')

hits = []
scanned = 0
for root in CANDIDATE_ROOTS:
    if not os.path.isdir(root):
        continue
    # 深度限制，避免全盘扫描
    for dirpath, dirnames, filenames in os.walk(root):
        depth = dirpath[len(root):].count(os.sep)
        if depth > 6:
            dirnames[:] = []
            continue
        # 跳过明显无关的大目录
        dirnames[:] = [d for d in dirnames
                       if d not in ('node_modules', 'Temp', 'Cache', 'Cache_Data')]
        scanned += 1
        if scanned > 40000:
            break
        for fn in filenames:
            if fn in WANT:
                hits.append(os.path.join(dirpath, fn))
        if len(hits) > 20:
            break
    if len(hits) > 20:
        break

print('扫描目录数: %d' % scanned)
if not hits:
    print('★ 没找到 Unity 自带编译器。')
    print('  可能原因：Unity 装在别的盘，或 Hub 只装了启动器。')
else:
    for h in hits:
        try:
            sz = os.path.getsize(h)
        except OSError:
            sz = -1
        print('%-12d %s' % (sz, h))