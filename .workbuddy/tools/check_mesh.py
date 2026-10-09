# -*- coding: utf-8 -*-
"""
mesh 生成器的数值模拟 —— 在 Python 里复算一遍索引，越界当场发现。

★★★ 为什么需要这个 ★★★
  2026-10-09 的 IndexOutOfRangeException：
  `new int[sides * 6 + sides * 3]` 实际需要 `sides * 12`。
  **编译期完全查不出来** —— 数组长度是运行时表达式，
  越界只在 Play 时炸，且堆栈指向 TaperedMesh 内部，看不出是哪个长度算错。
  真编译（real_compile.py）也查不出 —— 它只做语义分析，不做数值模拟。

  ★ 所以这一类错误必须靠「把算法在Python 里重跑一遍」来防。

★★★ 检查什么 ★★★
  对每个生成 mesh 的函数，模拟其顶点/索引写入过程，
  逐个索引检查是否落在分配范围内。

用法：
    python .workbuddy/tools/check_mesh.py
"""
import sys

errors = []
checks = []


def sim_tapered_mesh(sides):
    """模拟 SilkSpiderLimb.TaperedMesh。

    ★ 用「真实的 Python 数组写入」而不是自己判断边界——
      这样模拟器会和 Unity 一样在越界时抛 IndexOutOfRangeException，
      抓 bug 的能力与运行时一致。
      初版我用自写write() + 只记错误不中断，结果用旧写法跑反而「通过」
      → **假阴性**。抓不出的模拟器比没有更危险，因为它给人虚假的安全感。
    """
    # 分配（已修复后的版本）
    n_verts = sides * 2 + 2
    n_tris = sides * 12

    verts = [None] * n_verts
    tris = [None] * n_tris
    t = 0
    steps = []

    def write(idx):
        nonlocal t
        #★★ 直接写真实数组 —— 越界就抛 IndexOutOfRangeException，
        #   与 Unity 的行为一致。
        #   初版我自写边界判断 + 只记错误不中断 → 用旧写法跑反而「通过」
        #   = **假阴性**。抓不出的模拟器比没有更危险，因为它给人虚假安全感。
        try:
            tris[t] = idx
        except IndexError:
            errors.append('TaperedMesh(sides=%d): 第 %d 次写入越界'
                          '（分配 %d，正在写 %s 的索引 %d）'
                          % (sides, t, n_tris,
                             steps[-1] if steps else '?', idx))
            raise
        t += 1

    # 底圈 + 顶圈顶点
    for i in range(sides):
        a = i / sides * 6.283185
        verts[i] = (a, 0, 0)
        verts[sides + i] = (a, 0, 1)
    c_bottom = sides * 2
    c_top = sides * 2 + 1
    verts[c_bottom] = (0, 0, 0)
    verts[c_top] = (0, 0, 1)

    # 侧面：每边 2 三角形 = 6 索引
    for i in range(sides):
        steps.append('side%d' % i)
        n0 = i
        n1 = (i + 1) % sides
        n2 = sides + i
        n3 = sides + (i + 1) % sides
        write(n0); write(n2); write(n3)
        write(n0); write(n3); write(n1)

    # 底面扇形：每边 1 三角形 = 3 索引
    for i in range(sides):
        steps.append('bot%d' % i)
        n1 = (i + 1) % sides
        write(c_bottom); write(n1); write(i)

    # 顶面扇形：每边 1 三角形 = 3 索引
    for i in range(sides):
        steps.append('top%d' % i)
        n1 = (i + 1) % sides
        write(c_top); write(sides + i); write(sides + n1)

    # 校验顶点索引也不越界（tris 里引用 verts）
    for i, v in enumerate(tris):
        if v is None:
            continue
        if v < 0 or v >= n_verts:
            errors.append('TaperedMesh(sides=%d): tris[%d] 引用顶点 %d 越界'
                          '（顶点分配 %d）' % (sides, i, v, n_verts))

    checks.append('TaperedMesh(sides=%d): 分配 tris=%d，实际写入 %d → %s'
                  % (sides, n_tris, t, '一致' if t == n_tris else '★ 不一致'))

    # 三角形数必须是 3 的倍数（否则 mesh.triangles 会报错）
    if t % 3 != 0:
        errors.append('TaperedMesh(sides=%d): 索引数 %d 不是 3 的倍数' % (sides, t))

    return t


def sim_joint_mesh():
    """模拟球关节 mesh 的使用：应复用同一份 mesh，不重复生成。"""
    checks.append('JointMesh: 复用 cachedJointMesh（不参与本模拟）')


def main():
    print('=' * 68)
    print('mesh 生成器数值模拟（专抓 IndexOutOfRangeException）')
    print('=' * 68)

    print('')
    print('--- TaperedMesh ---')
    # 项目实际用的 sides = 10；同时扫 3~32 覆盖各种情况
    for s in (3, 4, 5, 6, 8, 10, 12, 16, 20, 32):
        try:
            sim_tapered_mesh(s)
        except IndexError:
            # 错误已记录在 errors 里；中断这个 sides 的模拟，继续测下一个
            continue

    sim_joint_mesh()

    print('')
    for c in checks:
        print('  %s' % c)

    print('')
    if errors:
        print('---- 错误 %d 个 ----' % len(errors))
        seen = set()
        for e in errors:
            if e in seen:
                continue
            seen.add(e)
            print('  [ERR] %s' % e)
        return 1

    print('✓ 未发现数组越界')
    return 0


if __name__ == '__main__':
    sys.exit(main())