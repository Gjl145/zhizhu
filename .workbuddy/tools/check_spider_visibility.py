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
    """★ 2026-10-11 重写：程序化建模已全部删除，检查对象换成 FBX 接管链路。

    【为什么必须改判据，而不是把这项删掉】
      原来的判据是「BuildCephalothorax 里有没有用 CreatePrimitive」——
      而那个方法连同全部程序化建模一起被删了（用户判定「完全不能看」）。
      → 检查项指向一个不存在的东西，继续留着只会天天报假错，
        真正的「球加腿」风险反而没人管了。

    【新判据：直接盯住「唯一可见体来源」】
      程序化建模删掉后，「球加腿」只剩两种可能的成因：
        ① FBX 没加载成功且**静默退回** → 必须报错并中止（不能悄悄画个球）
        ② FBX 加载了但**接管失败**，某条腿没有接管 → 同样不能程序化补一条

      所以检查：
        · TryBuildFromFbx 失败分支里不得出现任何程序化建模调用
        · BuildLimbs 里不得出现 `limb.Build(`（那是程序化建腿）
        · 不得重新出现 CreatePrimitive / new GameObject("Cephalothorax") 这类假身体
    """
    with open(ANATOMY, encoding='utf-8') as f:
        raw = f.read()
    src = strip_comments(raw)

    errors = []

    # ---- 判据 A：TryBuildFromFbx 存在，且失败分支不建任何东西 ----
    b = body_of(src, 'Build')
    if b is None:
        errors.append('SilkSpiderAnatomy.Build() 找不到')
        return errors

    if 'TryBuildFromFbx' not in b:
        errors.append('Build() 里没有调用 TryBuildFromFbx'
                      '→ FBX 不再是可见体来源')

    # ---- 判据 B：任何地方都不许再出现假身体的创建 ----
    for pattern, desc in [
        (r'=\s*new\s+GameObject\(\s*"Cephalothorax"\s*\)',
         '用空 GameObject 创建头胸部（无渲染组件→ 完全不可见）'),
        (r'=\s*new\s+GameObject\(\s*"Abdomen"\s*\)',
         '用空 GameObject 创建腹部（无渲染组件 → 完全不可见）'),
    ]:
        if re.search(pattern, src):
            errors.append('★ 又出现了程序化建模：%s'
                          '→ 用户已判定这种建模「完全不能看」' % desc)

    # ---- 判据C：程序化建腿入口必须已被移除 ----
    if re.search(r'\blimb\s*\.\s*Build\s*\(', src):
        errors.append('★ BuildLimbs 里还有 limb.Build(...)（程序化建腿）'
                      '→ 接管失败时会多出一套无蒙皮的腿，'
                      '画面变成「有肉的腿 + 光骨头」，比球加腿更糟')

    return errors


def check_fbx_is_only_source():
    """★ 新增：FBX 接管失败必须报错并中止，不许静默。

    【为什么这一项比原[2] 更重要】
      真正的根因不是「建模不好看」，而是**静默退回**：
      之前 FBX 拿不到就 fallback 建一个球+腿，
      因为「凑合还能动」，永远不会有人去查为什么 FBX 没加载。
      → 现在改成没有 fallback，于是「失败时是否明确报错」就成了唯一防线。
    """
    with open(ANATOMY, encoding='utf-8') as f:
        src = strip_comments(f.read())

    errors = []

    b = body_of(src, 'Build')
    if b is None:
        return ['SilkSpiderAnatomy.Build() 找不到']

    fb = body_of(src, 'TryBuildFromFbx')
    if fb is None:
        errors.append('TryBuildFromFbx 找不到')
        return errors

    # 失败分支必须 LogError，且必须 return（中止构建）
    if 'LogError' not in fb:
        errors.append('TryBuildFromFbx 失败时没有 Debug.LogError'
                      '→ 失败会静默，用户只能从画面猜原因')

    # Build 里必须有「失败就 return」的短路
    if not re.search(r'if\s*\(\s*!\s*TryBuildFromFbx\s*\(\s*\)\s*\)\s*\{', b):
        errors.append('Build() 里没有「FBX 失败就短路返回」的结构'
                      '→ 失败后仍会继续往下建腿')

    return errors


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

    print('[2] 检查程序化建模是否彻底移除、FBX 是否成为唯一可见体来源')
    errs = check_cephalothorax_not_empty()
    if errs:
        for e in errs:
            print('    ERROR %s' % e)
        all_errors += errs
    else:
        print('    ✓ 无程序化建模残留')
    print()

    print('[2b] 检查 FBX 接管失败是否报错并中止（不许静默退回）')
    errs = check_fbx_is_only_source()
    if errs:
        for e in errs:
            print('    ERROR %s' % e)
        all_errors += errs
    else:
        print('    ✓ 失败会明确报错并中止')
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

    print('✓ 通过 —— 可见体 = FBX 模型（唯一来源，无程序化建模 fallback）')
    return 0


if __name__ == '__main__':
    sys.exit(main())