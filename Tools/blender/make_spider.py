#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""用 Blender 的 Python API 生成风格化低模蜘蛛（含骨架），导出 FBX。

【为什么用脚本而不是手动建模】
  · 绑定（armature + 每条腿 5 节父子链 + 权重）在 GUI 里要点几百次，
    脚本一次跑完、可重复、改参数即可重生成。
  · 形态比例必须与 Unity 侧 SilkSpiderAnatomy 的公式**严格对齐**
    （cephaloWidth = r*1.30 等），写成代码才能保证两边一致。
    手动点出来的数字不可能和代码公式一致。

【坐标约定 —— ★ 与项目一致】
  项目是 Z-up：X=左右，Y=前后，Z=高度（重力沿 −Z）。
  Blender 是 Y-up。
  → 建模时就按 **Z-up** 想（Z 是高度、Y 是前后），
    导出 FBX 时勾 "Y up → Z up"，Unity 自动转回来。

【命名 —— 必须与 Unity 侧对齐】
  腿：leg_L_1 .. leg_L_4、leg_R_1 .. leg_R_4
  每条腿 5 节：leg_<侧>_<序号>_<节号>，节号 1..5
  → Unity 侧靠这些名字把 IK 目标绑到对应骨骼。

运行：
  blender --background --python Tools/blender/make_spider.py
"""

import math
import os
import sys

import bpy
import bmesh
from mathutils import Vector

# ================================================================
#  参数 —— 与 SilkSpiderAnatomy 默认值严格对齐
# ================================================================
BALL_RADIUS = 1.25          # visualRadius（球直径 2.5 格 = 0.5m）

CEPH_LEN   = BALL_RADIUS * 1.15   # 头胸部长（前后）
CEPH_WID   = BALL_RADIUS * 1.30   # 头胸部宽（左右）
CEPH_HGT   = BALL_RADIUS * 0.62   # 头胸部高（法线）★ 最扁 = 蜘蛛的标志
ABDO_RAD   = BALL_RADIUS * 1.15   # 腹部半径
BODY_GAP   = BALL_RADIUS * 1.95   # 头胸中心↔ 腹部中心
LEG_LEN    = BALL_RADIUS * 3.6    # 腿总长
LEG_SEGS   = [0.30, 0.16, 0.32, 0.14, 0.08]   # 5 节比例（同 Unity）
LEG_THICK  = BALL_RADIUS * 0.155  # 腿根半径
LEG_SPREAD = BALL_RADIUS * 0.62   # 腿根左右外扩
LEG_SPREAD_DEG = 38.0             # 静态张开角
CHEL_LEN   = BALL_RADIUS * 0.85   # 螯肢长度

# ★★★ 每侧 4 条腿在水平面内的朝向（度，相对该侧的纯外侧方向）
#   index 0 = 最前腿（k=0），index 3 = 最后腿（k=3）
#   负 = 朝前（+Y），正 = 朝后（−Y）——真实蜘蛛 4 对足呈扇形辐射。
#   ★ 这个参数是 8 条腿能不能分开的关键，详见 build_rig 里的说明。
#   ★★ 数值来自实测扫描（不是估的）：
#   单扫 yaw（当时站姿还没修对）：
#     yaw=[-0,-0,0,0]      最小足端间距 0.03 × 腿长 ← 第一版的 bug，精确重合
#     yaw=[-42,-14,14,42]  0.41  ← 中间两条腿仍并在一起
#     yaw=[-58,-20,20,58]  0.57  ← 仍不达标
#     yaw=[-72,-26,26,72]  0.70  ← 但站姿修好后掉回 0.58
#   二维扫 yaw × 站姿（**修正站姿之后**重扫，结果和上面完全不同）：
#     yaw 84/30   间距 0.68  ★ 选用（唯一峰值）
#     yaw 95/36   间距 0.52  ← 掉下来
#     yaw 105/42  间距 0.26  ← 严重掉下来
#
#   ★★★ 「加大 yaw 就能把腿分开」是**错的**：
#     yaw 太大时腿转向身体下方，横向分量被腿长守恒压缩、纵向占比变大，
#     前腿互相穿插 → 间距反而急剧变小。
#     → 间距对 yaw 是**非单调**的，必须实测找峰，不能顺着方向一路加。
#     这是本轮第三次「以为单调其实不是」（前两次：扇形角、面数分解）。
#   阈值 0.6 由「原始 bug 值 0.03」校准，不是拍脑袋。
LEG_YAW_DEG = [-84.0, -30.0, 30.0, 84.0]

# ---- 站姿高度曲线（★ Z-up：+Z 是上方，向下是负值）----
# ★ 必须写成公式而不是字面量 —— 改 LEG_LEN 时这两项要跟着变，
#   否则腿会重新翘起来或陷进地里（MEMORY 第三节规则 4）。
#
#【符号约定，别再搞反】LEG_GROUND_DROP 是**负数**（向下）。
#   第一版我写成正值 → 足端跑到 z=+1.16（比腿根高 1.28），
#   整条腿朝天上翘。高度曲线里「向下」必须给负值。
#   → 代码里靠 assert 守住这个不变量，见 build_rig。
LEG_GROUND_DROP = -BALL_RADIUS * 1.05  # 足端相对腿根的下降量（负 = 向下）
LEG_KNEE_RISE   = BALL_RADIUS * 0.80   # 膝部相对腿根的抬升量（正 = 向上）
LEG_STAND_H     = BALL_RADIUS * 1.25   # 身体离地高度（运行时会按落点调整）

# 交替四足步态相位（0 / 0.5），与 SilkSpiderBody 一致
GAIT_PHASE = [0.0, 0.5, 0.0, 0.5]

# ---- 低模面数预算（★ 不是拍脑袋，是量出来的）----
#首版实测 5968 面，其中 8 条腿吃掉 4480（75%）。分解后逐项定：
#   锥台 seg=8 → 每边 4 面 × 8 = 32 面，seg=6 → 24 面（省 25%，肉眼看不出）
#   关节球 seg=8 rings=6 → 80 面；seg=6 rings=4 → 36 面（省 55%）
#   ★ 关节球只保留 s=1,2,3（见 build_legs）→ 8×3=24 个而不是 40 个
LEG_SIDES     = 6      # 腿锥台边数
JOINT_SIDES   = 6      # 关节球经向段数
JOINT_RINGS   = 4      # 关节球纬向环数
BODY_SIDES    = 12     # 身体椭球经向段数
BODY_RINGS    = 8      # 身体椭球纬向环数
EYE_SIDES     = 6      # 眼睛段数（眼睛很小，6 足够圆）
EYE_RINGS     = 4
TRIS_BUDGET   = 3000   # 硬预算：超过就说明有部件没压

HERE = os.path.dirname(os.path.abspath(__file__))
OUT_DIR = os.path.join(HERE, "out")
FBX_PATH = os.path.join(OUT_DIR, "spider_lowpoly.fbx")


# ================================================================
#  通用工具
# ================================================================
def clear_scene():
    bpy.ops.object.select_all(action='SELECT')
    bpy.ops.object.delete(use_global=False)
    for coll in (bpy.data.meshes, bpy.data.armatures,
                 bpy.data.materials, bpy.data.actions):
        for item in list(coll):
            if item.users == 0:
                coll.remove(item)


def make_material(name, rgb, rough=0.65):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    bsdf = m.node_tree.nodes.get("Principled BSDF")
    if bsdf:
        bsdf.inputs["Base Color"].default_value = (rgb[0], rgb[1], rgb[2], 1.0)
        if "Roughness" in bsdf.inputs:
            bsdf.inputs["Roughness"].default_value = rough
    return m


def _finish(bm, name, mat):
    me = bpy.data.meshes.new(name + "_me")
    bm.to_mesh(me)
    bm.free()
    me.validate()
    me.update()
    ob = bpy.data.objects.new(name, me)
    bpy.context.collection.objects.link(ob)
    if mat:
        ob.data.materials.append(mat)
    return ob


def add_ellipsoid(name, center, radii, mat, seg=16, rings=10):
    """椭球（UV 球缩放）。★ 用 bmesh 避免污染选择状态。"""
    bm = bmesh.new()
    bmesh.ops.create_uvsphere(bm, u_segments=seg, v_segments=rings,
                              radius=1.0)
    cx, cy, cz = center
    rx, ry, rz = radii
    for v in bm.verts:
        v.co.x = cx + v.co.x * rx
        v.co.y = cy + v.co.y * ry
        v.co.z = cz + v.co.z * rz
    return _finish(bm, name, mat)


def add_cone_seg(name, r0, r1, length, mat, seg=8):
    """锥台段：底半径 r0（−Z 端）→顶半径 r1（+Z 端），高 length，沿 +Z。

    ★ 与 Unity 侧 SilkSpiderLimb.TaperedMesh 的索引结构完全一致：
        侧面每边 2 个三角 + 底面 1 个 + 顶面 1 个 = 每边 12 个索引。
      （MEMORY 记录过：那里算成 9 个导致 IndexOutOfRangeException）
    """
    bm = bmesh.new()
    hz = length * 0.5
    lower, upper = [], []
    for i in range(seg):
        a = (i / seg) * math.pi * 2.0
        lower.append(bm.verts.new(
            (math.cos(a) * r0, math.sin(a) * r0, -hz)))
        upper.append(bm.verts.new(
            (math.cos(a) * r1, math.sin(a) * r1, hz)))
    c_low = bm.verts.new((0.0, 0.0, -hz))
    c_up = bm.verts.new((0.0, 0.0, hz))

    for i in range(seg):
        j = (i + 1) % seg
        bm.faces.new((lower[i], upper[i], upper[j]))
        bm.faces.new((lower[i], upper[j], lower[j]))
        bm.faces.new((c_low, lower[j], lower[i]))
        bm.faces.new((c_up, upper[i], upper[j]))

    bm.normal_update()
    return _finish(bm, name, mat)


def add_box(name, center, size, mat):
    hx, hy, hz = size[0] * .5, size[1] * .5, size[2] * .5
    cx, cy, cz = center
    v = [(cx - hx, cy - hy, cz - hz), (cx + hx, cy - hy, cz - hz),
         (cx + hx, cy + hy, cz - hz), (cx - hx, cy + hy, cz - hz),
         (cx - hx, cy - hy, cz + hz), (cx + hx, cy - hy, cz + hz),
         (cx + hx, cy + hy, cz + hz), (cx - hx, cy + hy, cz + hz)]
    f = [(0, 3, 2, 1), (4, 5, 6, 7), (0, 1, 5, 4),
         (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)]
    me = bpy.data.meshes.new(name + "_me")
    me.from_pydata(v, [], f)
    me.validate()
    me.update()
    ob = bpy.data.objects.new(name, me)
    bpy.context.collection.objects.link(ob)
    if mat:
        ob.data.materials.append(mat)
    return ob


def smooth(ob, angle=math.radians(40)):
    """平滑着色（但保留硬边）。"""
    me = ob.data
    for p in me.polygons:
        p.use_smooth = True
    # Blender 4.1+ 用自定义法线属性；这里用自动平滑的替代：直接平滑即可
    return ob


# ================================================================
#  骨架
# ================================================================
def build_rig():
    arm_data = bpy.data.armatures.new("SpiderRig_data")
    arm = bpy.data.objects.new("SpiderRig", arm_data)
    bpy.context.collection.objects.link(arm)
    bpy.ops.object.select_all(action='DESELECT')
    arm.select_set(True)
    bpy.context.view_layer.objects.active = arm

    eb = {}          # name -> edit_bone
    world = {}       # name -> (head_world, tail_world)

    bpy.ops.object.mode_set(mode='EDIT')

    def bone(name, head, tail, parent=None, connected=False):
        # ★ 必须写回 eb，否则后面 eb[parent] 一定KeyError。
        #   （edit_bones.new() 只在 Blender 里注册，不会进我们这个本地字典）
        b = eb.get(name)
        if b is None:
            b = arm_data.edit_bones.new(name)
            eb[name] = b
        b.head = Vector(head)
        b.tail = Vector(tail)
        if parent:
            if parent not in eb:
                raise KeyError("父骨骼不存在：%s（当前已建：%s）"
                               % (parent, sorted(eb.keys())))
            b.parent = eb[parent]
            b.use_connect = connected
        world[name] = (Vector(head), Vector(tail))
        return b

    # ---- 身体骨骼 ----
    # head 在原点，tail 指向 -Y（后方），这样骨骼 -Y 侧是「后」
    bone("head", (0, CEPH_LEN * 0.5, 0), (0, -CEPH_LEN * 0.5, 0))
    bone("abdomen", (0, -BODY_GAP * 0.45, 0),
         (0, -BODY_GAP * 0.45 - ABDO_RAD * 1.2, 0), parent="head")

    # ---- 8 条腿：每条 5 节 ----
    # 腿根沿前后集中在头胸部后半（真实蜘蛛前端留给口器/眼）
    rear_bias = 0.45
    spread = 0.26
    for i in range(8):
        left = i < 4
        side = -1.0 if left else 1.0
        side_tag = "L" if left else "R"
        k = i % 4
        along = (rear_bias + (k / 3.0 - 0.5) * spread) * CEPH_LEN
        root = Vector((
            side * (CEPH_WID * 0.42 + LEG_SPREAD),
            along,
            -CEPH_HGT * 0.15))

        #★★ 每条腿在水平面内有自己的朝向（yaw），这是 8 条腿能分开的**根本原因**
        #
        # 【踩过的坑】第一版所有腿的朝向都是 out = (±1, 0, 0)，即纯 ±X。
        #   → 同一侧 4 条腿的朝向完全相同，只是腿根 Y 差 0.37（体长才2.87）
        #   → 渲染出来 8 条腿精确重叠成 2 条，俯视图一眼就看出来了。
        #   ★ 真实蜘蛛的 4 对步足是**扇形辐射**：前腿朝前外、后腿朝后外。
        #
        # 【与 Unity 侧的关系】
        #   Unity 的 SilkSpiderBody 用 FABRIK 把足端拉到分散的落点，
        #   所以静态朝向再窄运行时也能散开 —— 这是**掩盖**不是没有。
        #   但 Blender 生成的是**静态骨架**（运行时我们自己解算关节角），
        #   窄朝向会直接固化成"两条腿"的模型。
        #   → 静态骨架必须自己给扇形，运行时 IK 再叠加上。
        yaw = math.radians(LEG_YAW_DEG[k] * side)
        out = Vector((side * math.cos(yaw), math.sin(yaw), 0.0))

        # 静止姿态：★「膝拱起、足落地」，不是水平平伸
        #【踩过的坑 —— 第二版】第一版每节只用 sin(ang) 一个抬升分量，
        #   结果腿像水平尖刺一样平伸：
        #     实测 leg_L_2：腿根 z=-0.12 → 足端 z=+0.95，**足端比腿根高 1.07**
        #   → 整条腿朝天上翘，悬浮着，根本没落地。侧视图一眼看出不对。
        #
        # 【真实蜘蛛的腿序】
        #   股节 向上外伸，把「膝」顶到身体上方
        #   膝部明显高于背 → 这是蜘蛛最有辨识度的轮廓
        #   胫节 向下外伸到地面
        #   足节 贴着地面
        # → 用「先上升、后下降」的**高度曲线**，而不是每节独立的 sin
        drop = LEG_GROUND_DROP          # 足端相对腿根下降多少（> 0 = 落地）
        rise = LEG_KNEE_RISE            # 膝部相对腿根抬多高
        # ★ zs 是**6 个骨端点**的高度（5 节骨 = 6 个端点，不是 5 个）：
        #   zs[0] = 腿根    (0)
        #   zs[1] = 膝      (最高，膝拱)
        #   zs[2] =回落
        #   zs[3] = 接近地面
        #   zs[4] = 落地       ← 足节起点
        #   zs[5] = 落地       ← 足端（与 zs[4] 同高 → 足节水平贴地）
        #   循环 s=0..4 里ver = zs[s+1] - zs[s]，s=4 时取zs[5]，所以必须 6 个。
        #   （第一版只给了 5 个 → IndexError: list index out of range）
        zs = [0.0, rise, rise * 0.45, drop * 0.80, drop, drop]
        # 每节的角度只控制**水平外扩**，不参与高度
        angles = [LEG_SPREAD_DEG,
                  -LEG_SPREAD_DEG * 0.55,
                  -LEG_SPREAD_DEG * 0.30,
                  -LEG_SPREAD_DEG * 0.30,
                  -LEG_SPREAD_DEG * 0.35]

        cur = root.copy()
        parent_name = "head"
        for s in range(5):
            seg_len = LEG_SEGS[s] * LEG_LEN
            ang = math.radians(angles[s])
            hor = seg_len * math.cos(ang)          # 水平外扩
            ver = zs[s + 1] - zs[s]                # 竖直：由高度曲线决定
            # 腿长守恒：斜向分量超长时按比例缩放，保证每节长度 = seg_len
            # ★ 用 scale 而不是复用 k —— k 是腿序号（i % 4），复用会串号。
            d = math.hypot(hor, ver)
            if d > 1e-9 and d > seg_len:
                scale = seg_len / d
                hor *= scale
                ver *= scale
            step = Vector((out.x * hor, out.y * hor, ver))
            nxt = cur + step

            bname = "leg_%s_%d_%d" % (side_tag, k + 1, s + 1)
            bone(bname, cur, nxt, parent=parent_name)
            parent_name = bname
            cur = nxt

    bpy.ops.object.mode_set(mode='OBJECT')
    return arm, eb, world


# ================================================================
#  身体网格
# ================================================================
def build_body(mats):
    obs = []

    # 头胸部：扁椭球（Z 最扁 → 蜘蛛的关键轮廓）
    obs.append(add_ellipsoid("Cephalothorax", (0, 0, 0),
                             (CEPH_WID, CEPH_LEN, CEPH_HGT), mats['body'],
                             seg=BODY_SIDES, rings=BODY_RINGS))

    # 前端收窄（盾形）
    obs.append(add_ellipsoid("CephaloSnout", (0, CEPH_LEN * 0.72, 0),
                             (CEPH_WID * 0.72, CEPH_LEN * 0.55,
                              CEPH_HGT * 0.80), mats['body'],
                             seg=BODY_SIDES, rings=BODY_RINGS))

    # 腹部：后方、略大、扁卵形
    obs.append(add_ellipsoid("Abdomen",
                             (0, -BODY_GAP, -CEPH_HGT * 0.15),
                             (ABDO_RAD * 1.05, ABDO_RAD * 1.30,
                              ABDO_RAD * 0.90), mats['abd'],
                             seg=BODY_SIDES, rings=BODY_RINGS))

    # 腹柄：★没有它两个球直接相连 = 读成「一个球」
    obs.append(add_cone_seg("Pedicel",
                            CEPH_WID * 0.17, CEPH_WID * 0.13,
                            BODY_GAP * 0.45, mats['body'], seg=LEG_SIDES))

    # 8 眼：前 1 大 + 中 2 中 + 后 2 小，左右两列
    eye_rows = [(0.84, 1.35), (0.70, 1.00), (0.56, 0.76)]
    for row, (fy, scale) in enumerate(eye_rows):
        for col in range(2):
            side = -1.0 if col == 0 else 1.0
            rx = side * (CEPH_WID * 0.26 + col * CEPH_WID * 0.08)
            r = CEPH_WID * 0.075 * scale
            obs.append(add_ellipsoid("Eye_%d_%d" % (row, col),
                                     (rx, CEPH_LEN * fy, CEPH_HGT * 0.58),
                                     (r, r, r), mats['eye'],
                                     seg=EYE_SIDES, rings=EYE_RINGS))

    # 螯肢：一对，向前下方
    for side, tag in ((-1.0, "L"), (1.0, "R")):
        bx = side * CEPH_WID * 0.15
        base = add_cone_seg("Chelicera_%s" % tag,
                            CEPH_WID * 0.12, CEPH_WID * 0.05,
                            CHEL_LEN, mats['body'], seg=LEG_SIDES)
        # 朝前（+Y）并略微下倾：绕 X 轴转 -90° 把 +Z 掰到 +Y
        base.rotation_euler = (math.radians(-80.0), 0.0, 0.0)
        base.location = (bx, CEPH_LEN * 0.78, -CEPH_HGT * 0.12)
        obs.append(base)

        fang = add_cone_seg("Fang_%s" % tag,
                            CEPH_WID * 0.055, CEPH_WID * 0.015,
                            CHEL_LEN * 0.55, mats['abd'], seg=6)
        fang.rotation_euler = (math.radians(-140.0), 0.0, 0.0)
        fang.location = (bx, CEPH_LEN * 0.92, -CEPH_HGT * 0.34)
        obs.append(fang)

    return obs


def build_legs(mats, world):
    """为每条腿的每节生成锥台 + 关节球，位置取自骨架世界坐标。"""
    obs = []
    for i in range(8):
        side = "L" if i < 4 else "R"
        k = i % 4
        for s in range(5):
            bname = "leg_%s_%d_%d" % (side, k + 1, s + 1)
            if bname not in world:
                continue
            head, tail = world[bname]
            length = (tail - head).length
            if length < 1e-6:
                continue
            # 半径：从腿根往足端递减（r0ForBone 的 Lerp(1, 0.35)）
            t0 = s / 5.0
            t1 = (s + 1) / 5.0
            r0 = LEG_THICK * (1.0 + (0.35 - 1.0) * t0)
            r1 = LEG_THICK * (1.0 + (0.35 - 1.0) * t1)
            mid = (head + tail) * 0.5
            direction = (tail - head).normalized()

            seg = add_cone_seg("%s_seg%d" % (bname, s + 1),
                               r0, r1, length, mats['leg'], seg=LEG_SIDES)
            # 锥台默认沿 +Z，旋到骨骼方向
            seg.location = mid
            seg.rotation_mode = 'QUATERNION'
            seg.rotation_quaternion = Vector((0, 0, 1)).rotation_difference(
                direction)
            obs.append(seg)

            # 关节球：盖住折角断口（真实蜘蛛关节本来就是圆的）
            #★ 只在 s=1,2,3 生成：
            #   s=0 的根部藏在头胸部里，看不见 → 纯浪费
            #   s=4 是足端末端，没有下一节 → 也不需要
            #   实测：省掉 16 个球 ×80 面 = 1280 面（占总面数 21%）
            if s in (1, 2, 3):
                jr = r0 * 1.12
                joint = add_ellipsoid("%s_j%d" % (bname, s + 1),
                                      head, (jr, jr, jr),
                                      mats['leg'], seg=JOINT_SIDES,
                                      rings=JOINT_RINGS)
                obs.append(joint)
    return obs


# ================================================================
#  权重
# ================================================================
def skin(arm, meshes, world):
    """给所有网格加 Armature 修改器 + 顶点权重。

    ★ 权重策略（简单但够用）：
      每个顶点归属到**空间上最近的骨骼**。
      这不是精确的蒙皮，但对「分节锥台 + 关节球」这种
      每段独立的结构足够 —— 段不会互相拉扯。
    """
    bpy.ops.object.select_all(action='DESELECT')

    bone_names = list(world.keys())
    # 预取骨骼中心与长度
    centers = {}
    for n, (h, t) in world.items():
        centers[n] = (h + t) * 0.5

    for ob in meshes:
        if ob is None or ob.type != 'MESH':
            continue
        mod = ob.modifiers.new("Armature", 'ARMATURE')
        mod.object = arm
        mod.use_vertex_groups = True

        # 建顶点组
        groups = {}
        for n in bone_names:
            groups[n] = ob.vertex_groups.new(name=n)

        me = ob.data
        for v in me.vertices:
            co = Vector(v.co)
            # ★ Blender 里对象可能带location/rotation，
            #   而 world 里的骨骼坐标是世界系 → 必须转换到同一空间
            world_co = ob.matrix_world @ co
            best, best_d = None, None
            for n in bone_names:
                d = (world_co - centers[n]).length
                if best_d is None or d < best_d:
                    best, best_d = n, d
            if best:
                groups[best].add([v.index], 1.0, 'REPLACE')


# ================================================================
#  导出
# ================================================================
def export_fbx(arm, meshes):
    os.makedirs(OUT_DIR, exist_ok=True)

    bpy.ops.object.select_all(action='DESELECT')
    arm.select_set(True)
    for ob in meshes:
        if ob:
            ob.select_set(True)
    bpy.context.view_layer.objects.active = arm

    # ★ axis_forward='-Z', axis_up='Y' → Blender(Z-up 建模) 导出成 Y-up FBX，
    #   Unity 导入时会转回 Z-up，与项目坐标系一致。
    bpy.ops.export_scene.fbx(
        filepath=FBX_PATH,
        use_selection=True,
        object_types={'ARMATURE', 'MESH'},
        use_mesh_modifiers=True,
        mesh_smooth_type='FACE',
        add_leaf_bones=False,
        primary_bone_axis='Y',
        secondary_bone_axis='X',
        axis_forward='-Z',
        axis_up='Y',
        bake_anim=False,
        path_mode='COPY',
    )


def check_leg_separation(world, leg_len):
    """★★ 验证 8 个足端真的分开了 —— 专治「8 条腿叠成2 条」。

    【为什么要这个检查】
      第一版所有腿的朝向都是 out = (±1, 0, 0)（纯 ±X），
      同一侧 4 条腿精确重叠，渲染出来只有 2 条腿。
      ★ 那个 bug **不会报错**：骨骼数42 正确、网格数正确、面数正确、
        FBX 正常导出 —— 只有肉眼看渲染图才发现。
      → 所以必须有一条**数值**判据，不能只靠眼睛。

    【判据】
      任意两条不同腿的足端间距 > 0.6 × 腿长（经验阈值，低于此值视觉上会糊在一起）。
      顺便打印最小间距，肉眼看图时能对照。
    """
    tips = {}
    for name, (head, tail) in world.items():
        if "_5" not in name:      # 只取每条腿的第 5 节尾端 = 足端
            continue
        tips[name] = tail

    if len(tips) != 8:
        print("[FAIL] 足端数量应为 8，实际 %d" % len(tips))
        return False

    min_d, pair = 1e9, None
    names = sorted(tips.keys())
    for i in range(len(names)):
        for j in range(i + 1, len(names)):
            d = (tips[names[i]] - tips[names[j]]).length
            if d < min_d:
                min_d, pair = d, (names[i], names[j])

    ratio = min_d / leg_len
    print("足端最小间距：%.3f（= %.2f × 腿长）  %s ↔ %s"
          % (min_d, ratio, pair[0], pair[1]))
    if ratio < 0.6:
        print("[FAIL] 有两条腿的足端几乎重合（< 0.6 × 腿长）"
              " → 视觉上会叠成一条腿")
        return False
    return True


def check_standing(world):
    """★★ 验证腿真的站得住 —— 专治「腿翘到天上」/「像水平尖刺」。

    【为什么要这个检查】
      第二版把LEG_GROUND_DROP 写成正值，足端跑到 z=+1.16，
      比腿根高 1.28 —— 腿朝天翘。**零报错**：骨骼 42 对、网格 78个、
      面数达标、FBX 正常。只能靠量高度发现。
      （同一版还犯过 zs 只有 5 个但需要 6 个的IndexError，
        那种至少会崩；这种不会崩，只会静默地看起来很怪。）

    【三条判据】
      ① 足端必须**低于**腿根（腿着地，不是浮空）
      ② 膝必须**高于**腿根（这是蜘蛛最有辨识度的轮廓）
      ③ 膝到足端的下降要够大（否则腿是水平尖刺而不是站姿）
    """
    root_z = world["head"][0].z          # 腿根所在平面的基准
    knee_z = world["leg_L_2_2"][0].z      # 第2节骨起点 = 膝
    tips = [t.z for name, (h, t) in world.items() if "_5" in name]
    if not tips:
        print("[FAIL] 取不到足端")
        return False
    foot_z = sum(tips) / len(tips)

    print("站姿高度：腿根 z=%+.3f  膝 z=%+.3f  足端 z=%+.3f" %
          (root_z, knee_z, foot_z))
    ok = True
    if foot_z >= root_z - 1e-4:
        print("[FAIL] 足端(%.3f) 不低于腿根(%.3f) → 腿浮空/翘天"
              % (foot_z, root_z))
        ok = False
    if knee_z <= root_z + 1e-4:
        print("[FAIL] 膝(%.3f) 不高于腿根(%.3f) → 没有膝拱轮廓"
              % (knee_z, root_z))
        ok = False
    drop = knee_z - foot_z
    if drop < leg_len_hint() * 0.15:
        print("[FAIL] 膝到足端只下降 %.3f → 腿像水平尖刺" % drop)
        ok = False
    return ok


def leg_len_hint():
    return LEG_LEN


def main():
    print("=" * 64)
    print("蜘蛛生成 —— Blender %s" % bpy.app.version_string)
    print("=" * 64)

    # ★ 不变量断言：站姿参数符号搞反过一次（足端翘到天上），
    #   而那个 bug 不会报错 —— 骨骼数/面数/FBX 导出全都正常，
    #   只有量高度才发现。→ 在这里挡住。
    if LEG_GROUND_DROP >= 0:
        print("[FAIL] LEG_GROUND_DROP 必须为负（Z-up里向下是负值），"
              "当前 %+.3f → 足端会翘到腿根上方" % LEG_GROUND_DROP)
        sys.exit(4)
    if LEG_KNEE_RISE <= 0:
        print("[FAIL] LEG_KNEE_RISE 必须为正（膝要高于背），当前 %+.3f" % LEG_KNEE_RISE)
        sys.exit(4)
    #膝顶起、足落地 → 膝必须高于腿根，且下降量要够
    if abs(LEG_GROUND_DROP) < LEG_KNEE_RISE * 0.6:
        print("[FAIL] 下降量太小（%.2f），腿会像水平尖刺而不是落地"
              % abs(LEG_GROUND_DROP))
        sys.exit(4)

    clear_scene()

    mats = {
        'body': make_material("SpiderBody", (0.13, 0.11, 0.12)),
        'abd':  make_material("SpiderAbdomen", (0.19, 0.14, 0.13)),
        'leg':  make_material("SpiderLeg", (0.10, 0.09, 0.10)),
        'eye':  make_material("SpiderEye", (0.85, 0.12, 0.10)),
    }

    arm, eb, world = build_rig()
    print("骨骼数：%d" % len(world))

    ok_legs = check_leg_separation(world, LEG_LEN)
    if not ok_legs:
        print("=" * 64)
        sys.exit(3)

    if not check_standing(world):
        print("=" * 64)
        sys.exit(5)

    body_obs = build_body(mats)
    leg_obs = build_legs(mats, world)
    meshes = body_obs + leg_obs
    print("网格数：%d" % len(meshes))

    skin(arm, meshes, world)
    export_fbx(arm, meshes)

    tris = 0
    per_part = {}
    for ob in meshes:
        if ob and ob.type == 'MESH':
            t = sum(len(p.vertices) - 2 for p in ob.data.polygons)
            tris += t
            # ★ 按部件归类，看面数到底花在哪 —— 不猜，先量
            key = ob.name.split('_seg')[0].split('_j')[0]
            key = (key if not key.startswith('Eye') else 'Eye(8)')
            per_part[key] = per_part.get(key, 0) + t

    print("-" * 64)
    print("导出：%s" % FBX_PATH)
    print("三角面数：%d（预算< %d）" % (tris, TRIS_BUDGET))
    print("-" * 64)
    print("面数分解（谁在吃面数）：")
    for k in sorted(per_part, key=lambda x: -per_part[x])[:12]:
        print("    %-22s %6d" % (k, per_part[k]))
    # ★ 超预算要**报错**而不是只打印一句 —— 静默超预算是低模优化里最常见的
    #   「以为压下去了其实没压」。非零退出码方便 CI / 外层脚本察觉。
    if tris > TRIS_BUDGET:
        print("[FAIL] 面数超预算 %d > %d" % (tris, TRIS_BUDGET))
        print("=" * 64)
        sys.exit(2)
    print("=" * 64)


if __name__ == "__main__":
    main()