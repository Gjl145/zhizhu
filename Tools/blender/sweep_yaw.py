#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""★ 实测扫描 LEG_YAW_DEG / 站姿，找出真实峰值，不靠猜。

【为什么要扫而不是拍】
  足端间距对 yaw 是**非单调**的（已踩过两次），而且间距是**绝对长度**、
  阈值是「腿长的倍数」→ 改LEG_LEN 会重新洗牌最优 yaw。
  所以每次改腿长都必须重扫。只靠「上次选的值应该还行」会静默退化。

【怎么扫】
  用 exec 加载 make_spider.py，改掉模块级常量后只跑 build_rig，
  直接量 8 个足端的最小间距。不生成网格、不导出 → 每次约 1 秒。

运行：blender --background --python Tools/blender/sweep_yaw.py
"""

import os
import sys

import bpy

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "make_spider.py")

BASE = {"inner": 30.0, "outer": 84.0}
LEG_LEN_SET = 6.4      # 与 make_spider.py 保持一致
DROP_RATIO = 1.05      # LEG_GROUND_DROP / BALL_RADIUS
RISE_RATIO = 0.80      # LEG_KNEE_RISE  / BALL_RADIUS


def run(inner, outer, leg_len):
    ns = {"__name__": "spider_probe", "__file__": SRC}
    with open(SRC, "r", encoding="utf-8") as f:
        code = f.read()

    # ★★★ 扫描时必须做源码文本替换 —— 但这有个坑，我自己踩了：
    #   替换目标串「LEG_YAW_DEG = [-96.0, -36.0, 36.0, 96.0]」是写这个脚本时
    #   那一版的值。我后来把 make_spider.py 里的 yaw 改成了 [-84.0, ...]，
    #   替换就**静默失效** → 扫描跑的一直是文件里的 yaw，
    #     而输出却打印了参数值 → 看起来在扫，实际全是同一个点。
    #   → 必须用正则匹配「整行」，与具体数值解耦。
    #   （对应 MEMORY 里那条：改代码/注释一律用 Edit，绝不用脚本批量替换。
    #     这里是自己写脚本替换自己，更该警惕。）
    import re

    code = re.sub(
        r"LEG_YAW_DEG\s*=\s*\[[^\]]*\]",
        "LEG_YAW_DEG = [%.4f, %.4f, %.4f, %.4f]"
        % (-outer, -inner, inner, outer),
        code, count=1)
    code = re.sub(r"BALL_RADIUS\s*\*\s*6\.4\b",
                  "BALL_RADIUS * %.4f" % leg_len, code, count=1)

    # ★★ 确认替换真的生效了 —— 不检查就会像上面那样「扫了个寂寞」
    if "[-%.4f, -%.4f," % (outer, inner) not in code:
        print("[FAIL] yaw 替换未生效 → 扫描结果无效")
        sys.exit(9)

    exec(compile(code, SRC, "exec"), ns)

    ns["clear_scene"]()
    _, _, world = ns["build_rig"]()

    tips = [t for name, (h, t) in world.items() if "_5" in name]
    if len(tips) != 8:
        return None, None

    min_d = min((tips[i] - tips[j]).length
                for i in range(8) for j in range(i + 1, 8))

    # ★★★ 归一化分母必须是**绝对腿长**，不是 r 的倍数系数。
    #   脚本报 0.685 而 make_spider 报 0.55（同一个绝对间距 4.386）：
    #     4.386 / 6.4 = 0.685  ←错，分母漏乘 BALL_RADIUS(1.25)
    #     4.386 / 8.0 = 0.548  ← 对，6.4 r 的绝对值就是 8.0
    #   → 漏乘导致所有比值虚高 25%，会把不合格的参数判成合格。
    denom = ns["LEG_LEN"]          # 直接读模块里的绝对值，别自己算
    return min_d / denom, min_d


def main():
    print("=" * 72)
    print("yaw 扫描 —— 腿长固定为 %.2f r" % LEG_LEN_SET)
    print("=" * 72)
    print("  outer  inner   间距/腿长   绝对间距")
    print("-" * 72)

    best = (-1.0, None)
    for outer in (84.0, 90.0, 96.0, 102.0, 108.0, 114.0, 120.0):
        for inner in (26.0, 30.0, 34.0, 38.0, 42.0):
            if inner >= outer:
                continue
            ratio, absolute = run(inner, outer, LEG_LEN_SET)
            if ratio is None:
                continue
            mark = ""
            if ratio >= 0.6:
                mark = "  <= 达标"
            if ratio > best[0]:
                best = (ratio, (inner, outer))
                mark += "  ★★ 峰值"
            print("  %5.0f  %5.0f%11.3f   %8.3f%s"
                  % (outer, inner, ratio, absolute, mark))

    print("-" * 72)
    print("峰值：inner=%.0f outer=%.0f  间距/腿长=%.3f"
          % (best[1][0], best[1][1], best[0]))
    print("=" * 72)

    # 再扫腿长，确认「改腿长要重扫 yaw」确实成立
    print()
    print("★ 交叉验证：不同腿长下的最优 yaw 是否不同")
    print("-" * 72)
    for leg_len in (3.6, 4.6, 6.4, 8.0):
        b = (-1.0, None)
        for outer in (84.0, 96.0, 108.0, 120.0):
            for inner in (30.0, 36.0, 42.0):
                if inner >= outer:
                    continue
                ratio, _ = run(inner, outer, leg_len)
                if ratio is None:
                    continue
                if ratio > b[0]:
                    b = (ratio, (inner, outer))
        print("  腿长 %.1f r → 最优 yaw inner=%.0f outer=%.0f，间距=%.3f"
              % (leg_len, b[1][0], b[1][1], b[0]))
    print("=" * 72)


if __name__ == "__main__":
    main()