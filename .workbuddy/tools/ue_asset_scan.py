"""
UE .uasset / .umap 离线分析器
================================
在**没有 UE5** 的环境下，判断一个 UE资产包里有没有我们（Unity）能用的东西。

为什么需要它
------------
用户想「下载 Advanced Traversal System 学习一下，做一个 Unity 能用的」。
但 .uasset 是 UE 专有二进制，Unity 读不了。本脚本的作用是回答一个具体问题：

    **这个包里到底有没有 FBX / TGA / PNG 这类跨引擎通用资产？**

只要有 FBX，动画就能导进 Unity → 有学习价值。
全是 .uasset → 一无所获，直接告诉用户别折腾。

它做了什么
----------
1. 递归遍历目录，统计扩展名分布（按体积）
2. 对 .uasset/.umap 做「字符串抽取」——
   UE 的 name table 在 .uasset 里是明文可见的 ASCII/UTF-16 序列，
   所以我们能读出：类名、变量名、蓝图节点名、骨骼名、动画名、资产引用路径
   ★ 这不是「破解」，而是读公开的元数据表。
3. 交叉分析：对每个 uasset 抽出它**引用了什么资产**，
   从而判断它是「角色」「动画」「关卡」还是「材质」
4. 判定FBX 是否存在并列出骨骼/动画线索

用法
----
python ue_asset_scan.py <目录> [--json 输出.json]
"""

import sys
import os
import re
import json
import struct
from collections import defaultdict, Counter


# ---------------------------------------------------------------- 扩展名分类

# UE 专有 —— Unity 打不开
UE_ONLY = {
    '.uasset': 'UE 资产二进制',
    '.umap': 'UE 关卡二进制',
    '.ubulk': 'UE 大块数据',
    '.uexp': 'UE 扩展数据',
    '.uptnl': 'UE 子关卡',
    '.uplugin': 'UE 插件描述',
}

# 跨引擎通用 —— Unity 能用
CROSS_ENGINE = {
    '.fbx': '★ 骨骼动画/模型 —— Unity 可导入',
    '.obj': '模型 —— Unity 可导入',
    '.dae': '模型 —— Unity 可导入',
    '.gltf': '模型 —— Unity 可导入',
    '.glb': '模型 —— Unity 可导入',
    '.blend': 'Blender 工程 —— 需先导出',
    '.png': '贴图',
    '.jpg': '贴图',
    '.jpeg': '贴图',
    '.tga': '贴图',
    '.exr': 'HDRI 贴图',
    '.wav': '音频',
    '.mp3': '音频',
    '.mp4': '视频',
    '.ttf': '字体',
}

# 纯文本 —— 能直接读
TEXT = {'.txt', '.md', '.json', '.ini', '.csv', '.xml', '.html', '.pdf'}


def human(n):
    for unit in ('B', 'KB', 'MB', 'GB'):
        if n < 1024:
            return f'{n:.1f} {unit}'
        n /= 1024.0
    return f'{n:.1f} TB'


# ------------------------------------------------------- .uasset 字符串抽取

def extract_strings(path, min_len=4):
    """从 .uasset 抽明文字符串串（UE 的 name table是明文的）。

    UE 在 .uasset 偏移 0 里存了一个 name table：
    每个 entry 是 <长度:int32><ASCII 字符串><hash:int32>
    我们不严格解析结构，直接扫描连续可打印 ASCII，
    对 UE 资产来说召回率足够高（类名、变量名、资产路径都在里面）。
    """
    try:
        with open(path, 'rb') as f:
            data = f.read()
    except OSError:
        return []

    # --- ASCII 串（FName / ANSI 字符串）---
    out = set()
    cur = bytearray()
    for b in data:
        if 32 <= b < 127:
            cur.append(b)
        else:
            if len(cur) >= min_len:
                out.add(cur.decode('ascii', 'ignore'))
            cur = bytearray()
    if len(cur) >= min_len:
        out.add(cur.decode('ascii', 'ignore'))

    # --- UTF-16LE 串（FText /路径）---
    try:
        i = 0
        cur2 = bytearray()
        while i + 1 < len(data):
            lo, hi = data[i], data[i + 1]
            if hi == 0 and 32 <= lo < 127:
                cur2.append(lo)
                i += 2
            else:
                if len(cur2) >= min_len * 2:
                    out.add(cur2.decode('utf-16-le', 'ignore'))
                cur2 = bytearray()
                i += 1
        if len(cur2) >= min_len * 2:
            out.add(cur2.decode('utf-16-le', 'ignore'))
    except Exception:
        pass

    return [s for s in out if s.strip()]


# ------------------------------------------------------------ 资产角色判定

# 蓝图类名 -> 我们关心的东西
ROLE_RULES = [
    # (关键词, 判定, 说明)
    ('AnimBlueprint',      '动画蓝图',   '含动画状态机逻辑，★ 最有参考价值'),
    ('AnimInstance',       '动画实例',   '运行时动画驱动'),
    ('Blueprint',          '蓝图',       '★ 通用逻辑，看它引用什么'),
    ('SkeletalMesh',       '骨骼网格',   '角色/物体模型'),
    ('Skeleton',           '骨架',       '骨骼定义'),
    ('AnimSequence',       '动画序列',   '★ 动画片段'),
    ('AnimMontage',        '动画蒙太奇', '★ 连招/动作片段'),
    ('PhysicsAsset',       '物理资源',   '★ 刚体配置，破坏系统的参考'),
    ('Material',           '材质',       '美术资源'),
    ('StaticMesh',         '静态网格',   '关卡道具'),
    ('Level',              '关卡',       '关卡数据'),
    ('InputAction',        '输入动作',   '★ 键位设计参考'),
    ('InputMappingContext','输入映射',   '★ 键位设计参考'),
    ('DataTable',          '数据表',     '★ 参数配置表，最值得抄'),
    ('DataAsset',          '数据资产',   '★ 参数配置'),
    ('PhysicsConstraint','物理约束',   '★ 关节设置'),
]

# 只在这些库里找
CORE_LIB = ('/Engine/', '/Game/', 'ALS/', 'GASP/')


def classify_uasset(strings):
    """根据抽出的字符串判断这个 uasset 是什么角色。"""
    joined = '\n'.join(strings)

    # 抽资产引用路径 /Game/xxx 或 /ALS/xxx
    refs = set()
    for s in strings:
        for lib in CORE_LIB:
            idx = s.find(lib)
            if idx >= 0:
                tail = s[idx:]
                # 截到非法字符。
                # ★ 必须同时排除「十六进制 hash尾巴」——
                #   UE 的 name table entry 是 <len><name><hash:uint32>，
                #   hash 的十六进制文本会紧跟在名字后面，
                #   不排除就会把 "BP_AlsCharacter" + "D3" 拼成
                #   "BP_AlsCharacterD3" 这种不存在的资产名（变异测试抓到的真 bug）。
                out = []
                for idx_ch, ch in enumerate(tail):
                    if ch.isalnum() or ch in '/_.:-':
                        out.append(ch)
                    else:
                        break
                cleaned = ''.join(out)
                # 去掉尾部连续的十六进制串（UE name table 的 hash 尾巴）。
                # ★ 阈值必须 <=2：实测 hash 文本可能只有 2 位（如 "D3"），
                #   用 {3,} 会漏掉 → 变异测试抓到的第二个真bug。
                #   代价是极少数真资产名结尾恰好是 2 位 hex（如 "AB"）会被裁，
                #   这个代价远小于「几乎每个引用都带错误尾巴」。
                m = re.search(r'([0-9A-Fa-f]{2,})$', cleaned)
                if m:
                    trimmed = cleaned[:m.start()]
                    # 只有当剩余部分仍然像个资产名（含 / 或有 >=3 个字母）才裁
                    if '/' in trimmed or re.search(r'[A-Za-z]{3,}', trimmed):
                        cleaned = trimmed
                if len(cleaned) > 4:
                    refs.add(cleaned)
    refs = sorted(refs)

    # 判定角色
    roles = []
    for kw, role, note in ROLE_RULES:
        if kw in joined:
            roles.append((role, note, kw))

    return roles, refs


# ------------------------------------------------------------------ 主流程

def scan(root):
    stats = defaultdict(lambda: [0, 0])   # ext -> [count, bytes]
    ue_files = []
    cross_files = []
    text_files = []
    other_files = []

    for dirpath, dirnames, filenames in os.walk(root):
        for fn in filenames:
            fp = os.path.join(dirpath, fn)
            try:
                sz = os.path.getsize(fp)
            except OSError:
                continue
            ext = os.path.splitext(fn)[1].lower()
            stats[ext][0] += 1
            stats[ext][1] += sz

            if ext in UE_ONLY:
                ue_files.append((fp, sz))
            elif ext in CROSS_ENGINE:
                cross_files.append((fp, sz, ext))
            elif ext in TEXT:
                text_files.append((fp, sz, ext))
            else:
                other_files.append((fp, sz, ext))

    return stats, ue_files, cross_files, text_files, other_files


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 1

    root = sys.argv[1]
    out_json = None
    if '--json' in sys.argv:
        i = sys.argv.index('--json')
        if i + 1 < len(sys.argv):
            out_json = sys.argv[i + 1]

    if not os.path.isdir(root):
        print(f'✗ 目录不存在: {root}')
        return 1

    stats, ue_files, cross_files, text_files, other_files = scan(root)

    ue_bytes = sum(s for _, s in ue_files)
    cross_bytes = sum(s for _, s, _ in cross_files)

    print('=' * 72)
    print(f'  UE 资产包分析: {root}')
    print('=' * 72)

    # --- 总体结论（先给结论，不让人等）---
    print()
    print('【总体判定】')
    print('-' * 72)
    if cross_files:
        n_fbx = sum(1 for _, _, e in cross_files if e == '.fbx')
        print(f'  ★★ 发现 {len(cross_files)} 个跨引擎通用资产（{human(cross_bytes)}）')
        if n_fbx:
            print(f'  ★★★ 其中FBX {n_fbx} 个 —— **可以导入 Unity，有实质学习价值**')
    else:
        print('  ✗★ 未发现任何 FBX / 贴图 / 模型等通用资产')
        print('     → 这个包**无法用于 Unity**，只有 Unity 打不开的二进制')

    if ue_files:
        print(f'  · UE 专有文件 {len(ue_files)} 个（{human(ue_bytes)}）—— Unity 无法读取')

    # --- 扩展名分布 ---
    print()
    print('【扩展名分布 · 按体积】')
    print('-' * 72)
    print(f'  {"扩展名":<12} {"数量":>6} {"体积":>12}  说明')
    for ext, (cnt, byts) in sorted(stats.items(), key=lambda x: -x[1][1]):
        if ext in UE_ONLY:
            note = UE_ONLY[ext] + '  ✗ Unity 不支持'
        elif ext in CROSS_ENGINE:
            note = CROSS_ENGINE[ext]
        elif ext in TEXT:
            note = '文本 —— 可直接读'
        else:
            note = '其它'
        mark = '★' if ext in CROSS_ENGINE else ' '
        print(f' {mark}{ext:<12} {cnt:>6} {human(byts):>12}  {note}')

    # --- 跨引擎资产清单 ---
    if cross_files:
        print()
        print('【★ 可用于 Unity 的资产清单】')
        print('-' * 72)
        for fp, sz, ext in sorted(cross_files, key=lambda x: -x[1])[:60]:
            rel = os.path.relpath(fp, root)
            print(f'  [{ext:<6}] {human(sz):>10}  {rel}')
        if len(cross_files) > 60:
            print(f'  ... 另有 {len(cross_files) - 60} 个')

    # --- uasset 深挖 ---
    if ue_files:
        print()
        print('【uasset 内容深挖 · 每个文件能看出什么】')
        print('-' * 72)
        findings = []
        # 逐个抽字符串（大文件只抽前 8MB，控制耗时）
        for fp, sz in sorted(ue_files, key=lambda x: -x[1])[:40]:
            with open(fp, 'rb') as f:
                head = f.read(8 * 1024 * 1024)
            tmp = fp + '.part'
            try:
                with open(tmp, 'wb') as t:
                    t.write(head)
                strings = extract_strings(tmp)
            finally:
                try:
                    os.remove(tmp)
                except OSError:
                    pass

            roles, refs = classify_uasset(strings)
            name = os.path.relpath(fp, root)
            findings.append({
                'file': name,
                'size': sz,
                'strings_count': len(strings),
                'roles': [{'role': r, 'note': n, 'keyword': k} for r, n, k in roles],
                'refs': refs[:40],
            })

            tag = '、'.join(sorted(set(r for r, _, _ in roles))) or '未能判定'
            print(f'  · {name}')
            print(f'      {human(sz):>10} · 抽到 {len(strings)} 个字符串 · 判定: {tag}')
            if refs:
                print(f'      引用示例: {", ".join(refs[:4])}')

        # 汇总所有 uasset 里出现的「类名」和「资产引用」，看整体架构
        all_refs = Counter()
        all_roles = Counter()
        for fd in findings:
            for r in fd['refs']:
                all_refs[r] += 1
            for r in fd['roles']:
                all_roles[r['role']] += 1

        if all_roles:
            print()
            print('【全包资产类型统计】')
            print('-' * 72)
            for role, cnt in all_roles.most_common():
                print(f'  {role:<16} {cnt:>5} 个文件')

        if all_refs:
            print()
            print('【全包被引用最多的资产 · ★ 这些就是「系统构成」】')
            print('-' * 72)
            for r, cnt in all_refs.most_common(30):
                print(f'  {cnt:>3}x  {r}')

    # --- 文本文件 ---
    if text_files:
        print()
        print('【可直接阅读的文本文件】')
        print('-' * 72)
        for fp, sz, ext in sorted(text_files, key=lambda x: -x[1])[:30]:
            rel = os.path.relpath(fp, root)
            print(f'  [{ext:<6}] {human(sz):>10}  {rel}')

    # --- 写入json ---
    if out_json:
        result = {
            'root': root,
            'has_cross_engine': bool(cross_files),
            'cross_engine_count': len(cross_files),
            'fbx_count': sum(1 for _, _, e in cross_files if e == '.fbx'),
            'ue_only_count': len(ue_files),
            'stats': {k: {'count': v[0], 'bytes': v[1]} for k, v in stats.items()},
            'cross_engine_files': [
                {'path': os.path.relpath(fp, root), 'ext': e, 'size': s}
                for fp, s, e in cross_files
            ],
        }
        with open(out_json, 'w', encoding='utf-8') as f:
            json.dump(result, f, ensure_ascii=False, indent=2)
        print()
        print(f'→ JSON 已写入: {out_json}')

    print()
    print('=' * 72)
    return 0


if __name__ == '__main__':
    sys.exit(main())