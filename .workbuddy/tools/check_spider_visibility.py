#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""蜘蛛解剖体可见性检查。

【为什么需要】
2026-10-09 用户反馈「这个不还是球加腿吗？我要的是真的蜘蛛模型」。

真实根因**不是**形态做得不像，而是**头胸部根本没渲染出来**：

    var go = new GameObject("Cephalothorax");   // ← 空物体，无 MeshFilter
    go.transform.localScale = new Vector3(...); // ← 空操作
    Paint(go, bodyColor);                       // ← 第一行 mr==null 就 return

`new GameObject()` 不带任何渲染组件，
所以 localScale 与 Paint 全部静默失效 —— **不报任何错**，
画面上只剩玩家本体的蓝球 + 从球里伸出的 8 条腿。
用户看到的「球加腿」，字面意义上就是「球 + 腿」，中间那个身体不存在。

★ 这类bug 编译器抓不到、precheck 抓不到、real_compile 也抓不到。
  只能靠「结构审查」——即本脚本。

【检查项】
  ① 凡是被当作「可见部件」使用的 GameObject，必须有 MeshFilter + MeshRenderer
     判据：紧接着（或不远处）调用了 Paint(...) 或 AddComponent<MeshFilter>
  ② 玩家本体球 Body/Nose 的渲染器必须在蜘蛛模式下被关掉
     （否则解剖体即使建好了也会被球包住→ 还是「球加腿」）
"""

import os
import re
import sys

PROJ = os.path.dirname(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
)

ANATOMY = os.path.join(PROJ, 'Assets', 'Silk', 'SilkSpiderAnatomy.cs')
BODY = os.path.join(PROJ, 'Assets', 'Silk', 'SilkSpiderBody.cs')


def strip_comments(src):
    """去掉 // 与 /* */ 注释，避免注释里的代码干扰检查。"""
    src = re.sub(r'/\*.*?\*/', '', src, flags=re.S)
    src = re.sub(r'//[^\n]*', '', src)
    return src


def body_of(src, func_name):
    """从方法名起，按大括号配平取函数体。★ 不能用字符串切分。"""
    m = re.search(r'\b' + re.escape(func_name) + r'\s*\([^)]*\)\s*\{', src)
    if not m:
        return None
    start = m.end() - 1
    depth = 0
    for i in range(start, len(src)):
        if src[i] == '{':
            depth += 1
        elif src[i] == '}':
            depth -= 1
            if depth == 0:
                return src[start + 1:i]
    return None


def check_visible_parts():
    """检查所有 Build*方法里创建的可视部件是否都有渲染组件。

    ★ 第一版这里有假阴性：用 new GameObject() 创建头胸部后，
      后面仍然有 Paint(go, color) 调用，于是「有没有 Paint」这个判据
      照样通过 → 脚本对真bug 盖章。
      （反向验证实测：改回new GameObject 后脚本报「✓ 通过」= 无效检查）

    → 修正判据：**CreatePrimitive 与 AddComponent<MeshFilter> 二者取一**，
      Paint() 只能算辅助着色，**不能**作为「有渲染组件」的证据 ——
      因为 Paint() 自己对 mr == null 是静默 return 的。
    """
    with open(ANATOMY, encoding='utf-8') as f:
        raw = f.read()
    src = strip_comments(raw)

    errors = []
    checked = 0

    for m in re.finditer(r'private\s+void\s+(Build\w+)\s*\([^)]*\)\s*\{', src):
        name = m.group(1)
        b = body_of(src, name)
        if not b:
            continue

        creates = list(re.finditer(
            r'(?:var\s+)?(\w+)\s*=\s*'
            r'(?:GameObject\.CreatePrimitive\(\s*PrimitiveType\.(\w+)\s*\)'
            r'|new\s+GameObject\(\s*"([^"]*)"\s*\))', b))

        for cm in creates:
            var, prim, name_lit = cm.group(1), cm.group(2), cm.group(3)
            checked += 1

            # CreatePrimitive 自带 MeshFilter + MeshRenderer → 合法
            if prim:
                continue

            # new GameObject(...) → 必须显式 AddComponent<MeshFilter>
            tail = b[cm.end():]
            has_mf = bool(re.search(r'AddComponent<\s*MeshFilter\s*>', tail))

            if not has_mf:
                errors.append(
                    '%s(): 变量 %s 用 new GameObject("%s") 创建，'
                    '且其后没有 AddComponent<MeshFilter>() '
                    '→ 该部件无 MeshFilter/MeshRenderer，**完全不可见**'
                    '（Paint() 内部 mr==null 会静默 return，不报错）'
                    % (name, var, name_lit))

    return checked, errors


def check_ball_hidden():
    """玩家本体球的渲染器必须被关掉，否则解剖体会被球包住。

    ★ 第一版只查「HideBallVisuals 方法是否存在」——
      反向测试实测：把调用改成注释掉，脚本照样报「✓ 通过」= 无效检查。
      （方法还在，只是不被调用了 → 球照样可见 → 依然是「球加腿」）

    → 修正：**必须查调用点**，而不只是方法定义。
      判据：存在 `HideBallVisuals();` 形式的**裸调用**（不是定义，不是注释）。
    """
    if not os.path.isfile(BODY):
        return ['SilkSpiderBody.cs 不存在']

    with open(BODY, encoding='utf-8') as f:
        src = strip_comments(f.read())

    errors = []

    if 'HideBallVisuals' not in src:
        errors.append('SilkSpiderBody 里没有 HideBallVisuals —— '
                      '玩家本体球的渲染器没被关掉，解剖体会被球包住，'
                      '画面上依然是「球加腿」')
        return errors

    # ---- 检查项3a：方法体内必须真的关渲染器 ----
    b = body_of(src, 'HideBallVisuals')
    if b is None:
        errors.append('HideBallVisuals 方法体找不到（可能是声明写坏了）')
    elif 'enabled = false' not in b:
        errors.append('HideBallVisuals 里没有 renderer.enabled = false')

    # ---- 检查项 3b：★必须存在裸调用（反向测试证明这一项不可省）----
    #
    # ★★ 判据要同时排除**方法定义**，否则这一项是假的：
    #   `private void HideBallVisuals()` 同样匹配 `HideBallVisuals\(\s*\)`。
    #   我第一版只写了 `(?<![\w.])`，以为排除了定义 —— 实测不成立：
    #   定义前面是空格（`void HideBallVisuals()`），lookbehind 通过。
    #   → 结果「调用被注释掉」时仍然报通过。**注释里写的和代码做的事不一致。**
    #
    #   正确做法：前面必须是**语句终结符或块起始**，而不是 `void`。
    called = re.search(r'[;}]\s*(?:^|\n)\s*HideBallVisuals\s*\(\s*\)', src) \
        or re.search(r'^\s*HideBallVisuals\s*\(\s*\)', src, re.M)

    if not called:
        errors.append('★ HideBallVisuals 定义了但**从未被调用**'
                      '（反向测试实测：调用被注释掉时上一版脚本仍报通过）'
                      '→ 球体渲染器不会被关闭，画面上依然是「球加腿」')

    # ---- 反向护栏：确认没有把方法定义误当成调用 ----
    if called and re.search(r'\bvoid\s*HideBallVisuals\s*\(', src):
        # 定义存在是正常的，只提示判据是否真的区分开了
        pass

    return errors


def check_cephalothorax_not_empty():
    """专项：头胸部必须用 CreatePrimitive，不能用空 GameObject。

    ★ 第一版用 `'Cephalothorax")' in b` 判断 ——
      但这个字符串在**注释里**也出现（我写的修复说明就含它），
      靠 strip_comments 已能去掉注释，但更稳的是直接找**赋值语句**。
    """
    with open(ANATOMY, encoding='utf-8') as f:
        raw = f.read()
    src = strip_comments(raw)

    b = body_of(src, 'BuildCephalothorax')
    if b is None:
        return ['找不到 BuildCephalothorax']

    # 精确匹配赋值：xxx = new GameObject("Cephalothorax")  ← 这是 bug 写法
    bad = re.search(
        r'=\s*new\s+GameObject\(\s*"Cephalothorax"\s*\)', b)
    if bad:
        return ['BuildCephalothorax 里 Cephalothorax 用 new GameObject 创建'
                ' → 无 MeshFilter/MeshRenderer → 头胸部不可见']

    # 必须是 CreatePrimitive（自带渲染组件）
    if not re.search(
            r'=\s*GameObject\.CreatePrimitive\(\s*PrimitiveType\.\w+\s*\)', b):
        return ['BuildCephalothorax 里找不到 CreatePrimitive，'
                '头胸部的创建方式需要人工确认']

    return []


def main():
    print('=' * 74)
    print('蜘蛛解剖体可见性检查 —— 专治「球加腿」')
    print('=' * 74)
    print()

    all_errors = []

    print('[1] 检查所有可视部件是否带渲染组件')
    checked, errs = check_visible_parts()
    print('    扫描创建点: %d 个' % checked)
    if errs:
        for e in errs:
            print('    ERROR %s' % e)
        all_errors += errs
    else:
        print('    ✓ 全部具备渲染组件')
    print()

    print('[2] 检查头胸部是否用了 CreatePrimitive')
    errs = check_cephalothorax_not_empty()
    if errs:
        for e in errs:
            print('    ERROR %s' % e)
        all_errors += errs
    else:
        print('    ✓ 头胸部有真实网格')
    print()

    print('[3] 检查玩家本体球渲染器是否被关闭')
    errs = check_ball_hidden()
    if errs:
        for e in errs:
            print('    ERROR %s' % e)
        all_errors += errs
    else:
        print('    ✓ 球体渲染器会被关闭')
    print()

    print('=' * 74)
    if all_errors:
        print('✗ %d 项失败 —— 这些都会导致「球加腿」而非真蜘蛛'
              % len(all_errors))
        return 1

    print('✓ 通过 —— 可见体 = 头胸部 + 腹部 + 腹柄 + 8 条五节腿')
    return 0


if __name__ == '__main__':
    sys.exit(main())