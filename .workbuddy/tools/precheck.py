# -*- coding: utf-8 -*-
"""
定向编译预检 —— 专查本轮新增/修改的代码会踩的坑。

为什么需要：沙盒内无法 batchmode 编译（项目被编辑器锁住），
Unity 又要用户手动刷新才能编译 → 我在提交前必须自己先筛一遍。

★ 覆盖本项目历史上真实犯过的错误类型（见 MEMORY.md 第六、九节）：
  1. 括号不配平（CS1026 / CS1513）
  2. 标识符使用早于声明（CS0103）——本项目已栽 9 次
  3. 未使用局部变量（CS0219警告，但会暴露「以为接了线其实没接」）
  4. 属性特性写法错误（CS1026，Tooltip缺右括号那4 处）

★ 局限性（必须坦诚承认）：
  文本正则**不等于编译器**。本项目第 22 项检查（自动检测 CS0103）
  尝试 6 版全因误报失败，已放弃。
  → 本脚本只查「文本上可判定」的高置信度问题，
     语义级错误（类型不匹配、成员不存在）**查不出来**，
     最终必须以 Unity Console 为准。
"""

import os
import re
import sys

BASE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ROOT = os.path.join(os.path.dirname(BASE), 'Assets', 'Silk')

errors = []
warnings = []


def strip_code(src):
    """剥离注释与字符串字面量，但**保持行数不变**。

    ★ 必须保持行数 —— 否则报错行号会漂移，无法定位。
      （历史教训：整块 re.sub 删注释导致行号漂移 100+ 行）
    """
    out = []
    in_block = False
    in_verbatim = False
    for ln in src.split('\n'):
        s = ln
        res = []
        i = 0
        in_str = False
        while i < len(s):
            if in_block:
                if s.startswith('*/', i):
                    in_block = False
                    i += 2
                else:
                    i += 1
                continue
            if in_str:
                if s[i] == '\\' and not in_verbatim:
                    i += 2
                    continue
                if s[i] == '"':
                    if in_verbatim and i + 1 < len(s) and s[i + 1] == '"':
                        i += 2
                        continue
                    in_str = False
                i += 1
                continue
            if s.startswith('//', i):
                break
            if s.startswith('/*', i):
                in_block = True
                i += 2
                continue
            if s.startswith('@"', i):
                in_verbatim = True
                in_str = True
                res.append('  ')
                i += 2
                continue
            if s[i] == '"':
                in_str = True
                res.append(' ')
                i += 1
                continue
            if s[i] == "'":
                # 字符字面量（含转义）
                j = i + 1
                if j < len(s) and s[j] == '\\':
                    j += 2
                if j < len(s) and s[j] == "'":
                    res.append(' ')
                    i = j + 1
                    continue
            res.append(s[i])
            i += 1
        out.append(''.join(res))
    return out


def check_brackets(name, lines):
    pairs = {'{': '}', '(': ')', '[': ']'}
    closers = {'}': '{', ')': '(', ']': '['}
    stack = []
    for idx, ln in enumerate(lines, 1):
        for ch in ln:
            if ch in pairs:
                stack.append((ch, idx))
            elif ch in closers:
                if not stack:
                    errors.append('%s:%d 多余的 %s' % (name, idx, ch))
                    return
                top_ch, top_ln = stack[-1]
                if pairs[top_ch] != ch:
                    errors.append('%s:%d 的 %s 与第 %d 行的 %s 不匹配'
                                  % (name, idx, ch, top_ln, top_ch))
                    return
                stack.pop()
    for ch, ln in stack:
        errors.append('%s:%d 的 %s 未闭合' % (name, ln, ch))


def check_attr_brackets(name, raw):
    """专查 [Attribute("..."] 少右括号的写法。

    这是本轮真实踩过的 4 个错误（CS1026: ) expected）。
    特征：'[' 之后是标识符 '(' ... 结尾却是 '"]' 或 '"' 而没有 ')']'
    """
    for idx, ln in enumerate(raw.split('\n'), 1):
        s = ln.strip()
        # 匹配 [Something(".....")   结尾缺 )
        m = re.match(r'^\[[A-Za-z_]\w*\(\s*"', s)
        if not m:
            continue
        if s.endswith('")]'):
            continue
        if s.endswith('")') or s.endswith('")') :
            continue
        if s.endswith(']'):# 结尾是 '"]' → 缺 ')'
            errors.append('%s:%d 属性特性可能缺右括号：[Foo("..."] '
                          '应为 [Foo("...")]' % (name, idx))


def check_use_before_decl(name, lines):
    """查「局部变量使用早于声明」（CS0103）。

    ★★ 必须用**词法作用域树**，不能用「括号深度」当作用域。
      【踩过的坑 —— 本轮实测误报】
        旧实现 sid = 累加 (每行 { 减 })，于是两个**平级方法**
        （BuildNeighborTable 与 HideRenderersUnder）拿到同一个 sid，
        第 320 行的 `var t` 与第 618 行的 `Transform t` 被当成同一个变量
        → 报「变量 t 在 618 行才声明」，纯误报。
        真编译 0 错误已经证明是误报。
      → 现在给每个 `{` 分配**全局唯一 id**，用栈维护「当前所在块」。
        这样平级方法的 id 天然不同。
    ★ 声明行本身要排除；只报「声明确实在后面」的情况。
    """
    # ---- 词法作用域：每个 { 拿一个唯一 id，栈顶 = 当前所在块 ----
    stack = []
    next_id = [1]
    sid_of_line = []
    for ln in lines:
        # ★ 本行的 sid 取**进入本行之前**的栈顶 —— 这样「某行同时开括号」
        #   属于外层作用域，语义上更保守（少报错）。
        sid_of_line.append(stack[-1] if stack else 0)
        for ch in ln:
            if ch == '{':
                stack.append(next_id[0])
                next_id[0] += 1
            elif ch == '}' and stack:
                stack.pop()

    decl_re = re.compile(
        r'^\s*(?:readonly\s+|const\s+)?'
        r'(?:var|bool|int|float|double|string|Vector2|Vector3|Vector4|'
        r'Quaternion|Matrix4x3|Matrix4x4|Color|Ray|RaycastHit|Object|'
        r'SilkSphereCast|SilkSpiderAnatomy|SilkSpiderSurfaceMove|'
        r'SilkParkourController|Collider|Transform|Camera|LayerMask|'
        r'System\.Collections\.IEnumerator|IEnumerator)'
        r'\s+([A-Za-z_]\w*)\s*(?:=[^;]*)?;')

    declared = {}   # (scope_id, name) -> 行号
    for idx, ln in enumerate(lines, 1):
        m = decl_re.match(ln)
        if m:
            declared.setdefault((sid_of_line[idx - 1], m.group(1)), idx)

    # 收集裸使用（排除声明行本身）
    use_re = re.compile(r'(?<![.\w])([A-Za-z_]\w*)\s*(?=[,)\];=+\-*/]|\s*==|\s*!=|\s*<|\s*>)')
    for idx, ln in enumerate(lines, 1):
        sid = sid_of_line[idx - 1]
        if decl_re.match(ln):
            continue
        for m in use_re.finditer(ln):
            name2 = m.group(1)
            key = (sid, name2)
            if key in declared and declared[key] > idx:
                errors.append('%s:%d 变量 %r 在第 %d 行才声明（CS0103）'
                              % (name, idx, name2, declared[key]))


def check_unused_locals(name, lines):
    """查未使用的局部变量 —— 暴露「以为接线了其实没接」。"""
    decl_re = re.compile(
        r'^\s*(?:readonly\s+|const\s+)?'
        r'(?:bool|int|float|double|string|Vector2|Vector3|Vector4|'
        r'Quaternion|Color|RaycastHit|Collider|Transform|Object)'
        r'\s+([A-Za-z_]\w*)\s*(?:=[^;]*)?;')
    for idx, ln in enumerate(lines, 1):
        m = decl_re.match(ln)
        if not m:
            continue
        var = m.group(1)
        if var in ('true', 'false', 'null'):
            continue
        # 在整个文件里找使用（排除声明行自身）
        cnt = 0
        pat = re.compile(r'(?<![.\w])' + re.escape(var) + r'(?![A-Za-z0-9_])')
        for j, l2 in enumerate(lines, 1):
            if j == idx:
                continue
            cnt += len(pat.findall(l2))
        if cnt == 0:
            warnings.append('%s:%d 局部变量 %r 声明后从未使用'
                            % (name, idx, var))


TARGETS = ['SilkSpiderBody.cs', 'SilkSpiderLimb.cs',
           'SilkSpiderAnatomy.cs', 'SilkSpiderTestStage.cs']

print('=' * 66)
print('定向编译预检（文本启发式，**不等于编译器**）')
print('=' * 66)

for fn in TARGETS:
    path = os.path.join(ROOT, fn)
    if not os.path.isfile(path):
        errors.append('%s 文件不存在' % fn)
        continue
    with open(path, 'r', encoding='utf-8-sig') as f:
        raw = f.read()
    lines = strip_code(raw)
    check_brackets(fn, lines)
    check_attr_brackets(fn, raw)
    check_use_before_decl(fn, lines)
    check_unused_locals(fn, lines)

print('')
for w in warnings:
    print('  [WARN] %s' % w)
print('')
if errors:
    for e in errors:
        print('  [ERR]  %s' % e)
    print('')
    print('共%d 个错误' % len(errors))
    sys.exit(1)
else:
    print('通过 0 错误| %d 警告' % len(warnings))
    print('')
    print('★ 提醒：本脚本查不出类型不匹配 / 成员不存在 / CS1061 等语义错误。')
    print('  最终以 Unity 控制台（Console）为准 —— 需要你点一下')
    print('  「资源（Assets）> 刷新（Refresh）」触发编译。')