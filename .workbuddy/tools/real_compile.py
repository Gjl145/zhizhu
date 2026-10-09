# -*- coding: utf-8 -*-
"""
★★真编译检查器 —— 直接调用 Roslyn，拿到与 Unity 完全一致的错误列表★★

★★★ 为什么非要做到这一步 ★★★
  2026-10-09：用户反馈「现在很多报错」，但——
    · Unity 日志停在 12:35（编辑器已关闭，不再更新）
    · 沙盒内 Unity.exe -batchmode 被项目锁阻止，且**退出码仍为 0 → 假通过**
    · 静态正则查不出类型不匹配 / 成员不存在 / 签名不匹配
  → 我拿不到权威错误列表 = 无法定位用户看到的报错。
  ★ 唯一出路：**自己编译**。

★★★ 做法 ★★★
  Unity 自己编译时的完整参数被Bee 缓存下来了：
    Library/Bee/artifacts/1900b0aE.dag/Assembly-CSharp.rsp
  里面有：全部源文件、全部引用 DLL、全部 define 常量、
  langversion、nullable 设置……
  → 直接把这份 rsp 喂给 Roslyn，得到的就是 Unity 会得到的错误。

  编译产物写到临时目录，**绝不覆盖 Library/ScriptAssemblies**——
  那是 Unity 的地盘，污染它会让编辑器下次启动状态错乱。

★★★ 前置条件 ★★★
  需要 .NET 运行时。用 D:\\Unity\\...\\DotNetSdkRoslyn 的 csc.dll 时，
  配DOTNET_ROOT 或系统已装 dotnet。
"""
import os
import re
import subprocess
import sys
import shutil

PROJ = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

# ================================================================
#  rsp 自动发现
#  Unity 的编译参数放在 Library/Bee/artifacts/<hash>.dag/Assembly-CSharp.rsp
#  <hash> 每台机器/每个版本可能不同 → 硬编码路径会在换环境后失效。
#  做法：glob 找，找不到就退回按内容特征匹配（文件里有 -target:library 且
#  含Assembly-CSharp.rsp 字样）。
# ================================================================


def find_rsp():
    bee = os.path.join(PROJ, 'Library', 'Bee', 'artifacts')
    if not os.path.isdir(bee):
        return None
    cands = []
    try:
        for d in os.listdir(bee):
            p = os.path.join(bee, d, 'Assembly-CSharp.rsp')
            if os.path.isfile(p):
                cands.append(p)
    except OSError:
        return None
    if not cands:
        return None
    # 取最新修改的那个（最接近当前工程状态）
    cands.sort(key=lambda p: os.path.getmtime(p), reverse=True)
    return cands[0]

CSC_CANDIDATES = [
    r'D:\Unity\2022.3.62f3c1\Editor\Data\DotNetSdkRoslyn\csc.dll',
    r'C:\Program Files\dotnet\sdk\10.0.302\Roslyn\bincore\csc.dll',
]
DOTNET_CANDIDATES = [
    r'C:\Program Files\dotnet\dotnet.exe',
]

OUT_DIR = os.path.join(PROJ, 'Library', 'SilkCompileProbe')

DIAG_RE = re.compile(r'^(.+?)\((\d+),(\d+)\):\s*(error|warning)\s+([A-Z]+\d+):\s*(.*)$')


def find_csc():
    for p in CSC_CANDIDATES:
        if os.path.isfile(p):
            return p
    #兜底：再搜一次
    for root in (r'C:\Program Files\dotnet\sdk', r'D:\Unity'):
        if not os.path.isdir(root):
            continue
        for dp, dn, fn in os.walk(root):
            if 'csc.dll' in fn and 'Roslyn' in dp or 'DotNetSdkRoslyn' in dp:
                cand = os.path.join(dp, 'csc.dll')
                if os.path.isfile(cand):
                    return cand
    return None


def find_dotnet():
    for p in DOTNET_CANDIDATES:
        if os.path.isfile(p):
            return p
    return None


def main():
    RSP = find_rsp()
    if not RSP:
        print('★ 找不到 Unity 的编译参数文件（Library/Bee/artifacts/*/Assembly-CSharp.rsp）')
        print('  说明 Unity 从未编译过这个项目，或Library 被清过。')
        print('  → 解法：先用 Unity 打开一次项目并让它编译成功，之后本工具才能工作。')
        return 2

    csc = find_csc()
    dotnet = find_dotnet()
    if not csc or not dotnet:
        print('★ 找不到编译器或dotnet 运行时')
        print('  csc=%s' % csc)
        print('  dotnet=%s' % dotnet)
        return 2

    # 读 rsp，把输出路径改到临时目录
    with open(RSP, 'r', encoding='utf-8-sig', errors='replace') as f:
        rsp_lines = f.read().split('\n')

    new_rsp = []
    for ln in rsp_lines:
        s = ln.strip()
        if s.startswith('-out:') or s.startswith('-refout:') \
           or s.startswith('-doc:') or s.startswith('-pdb:') \
           or s.startswith('/out:') or s.startswith('/refout:') \
           or s.startswith('/doc:') or s.startswith('/pdb:'):
            continue
        # 调试相关可留
        new_rsp.append(ln)

    os.makedirs(OUT_DIR, exist_ok=True)
    tmp_rsp = os.path.join(OUT_DIR, 'probe.rsp')
    with open(tmp_rsp, 'w', encoding='utf-8') as f:
        f.write('\n'.join(new_rsp))
        f.write('\n-out:"%s"\n' % os.path.join(OUT_DIR, 'probe.dll').replace('\\', '/'))
        f.write('-nologo\n')
        f.write('-nowarn:0169,0414,0649,1591,0219,0162,0168,0108\n')

    print('=' * 70)
    print('真编译检查（Roslyn，与 Unity 同参数）')
    print('=' * 70)
    print('csc   : %s' % csc)
    print('rsp   : %s' % os.path.relpath(RSP, PROJ))
    print('产物  : %s（临时目录，不污染 Library/ScriptAssemblies）' % OUT_DIR)
    print('')

    cmd = [dotnet, csc, '@' + tmp_rsp.replace('\\', '/')]
    try:
        p = subprocess.run(cmd, capture_output=True, text=True,
                           encoding='utf-8', errors='replace', timeout=600)
    except subprocess.TimeoutExpired:
        print('★ 编译超时（600s）')
        return 2
    except Exception as e:
        print('★ 调编译器失败：%s' % e)
        return 2

    out = (p.stdout or '') + (p.stderr or '')

    errs = []
    warns = []
    for ln in out.split('\n'):
        ln = ln.rstrip()
        m = DIAG_RE.match(ln.strip())
        if not m:
            continue
        path, row, col, kind, code, msg = m.groups()
        rel = path
        try:
            rel = os.path.relpath(path, PROJ).replace('\\', '/')
        except ValueError:
            pass
        item = '%s(%s,%s): %s %s: %s' % (rel, row, col, kind, code, msg)
        if kind == 'error':
            errs.append(item)
        else:
            warns.append(item)

    # 未匹配 DIAG_RE 的行（通常是 fatal / 汇总）
    other = []
    for ln in out.split('\n'):
        s = ln.strip()
        if not s:
            continue
        if DIAG_RE.match(s):
            continue
        if s.startswith('error ') or s.startswith('fatal '):
            other.append(s)

    if errs:
        print('---- 错误 %d 个 ----' % len(errs))
        for e in errs:
            print('  %s' % e)
    else:
        print('✓ 0 个编译错误')

    if warns:
        # 只显示与我们改的文件相关的警告
        ours = [w for w in warns
                if 'SilkSpider' in w or 'SilkBuilder' in w
                or 'SilkCompileCheck' in w or 'SilkSphereCast' in w]
        print('')
        print('---- 警告 %d 个（其中相关文件 %d 个）----'
              % (len(warns), len(ours)))
        for w in ours[:30]:
            print('  %s' % w)

    if other:
        print('')
        print('---- 其他编译器输出 ----')
        for o in other[:20]:
            print('  %s' % o)

    print('')
    if errs:
        # 提示根因
        m2 = re.search(r'\((\d+),\d+\):\s*error', errs[0])
        first = errs[0]
        mm = re.search(r'\((\d+),(\d+)\)', first)
        if mm:
            print('★ 按C# 惯例，第一个（最小行号）通常是根因：%s' % first)
        return 1

    print('★ 这个结果与 Unity 将得到的错误列表一致（同一份 rsp + 同一个 Roslyn）。')
    return 0


if __name__ == '__main__':
    sys.exit(main())