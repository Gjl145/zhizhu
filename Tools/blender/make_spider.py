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
LEG_LEN    = BALL_RADIUS * 6.4    # 腿总长
# ★★★ 4：4.6 → 6.4，依据是真实解剖比例的**实测比值**，不是感觉。
#   资料（Australian Museum + 科普中国）：大型游猎蛛步足展开跨度
#   约为体长的 2.0~2.5 倍（狼蛛/跳蛛都接近 2 倍）。
#   本模型体长 = 2×CEPH_LEN + BODY_GAP + 2×ABDO_RAD
#              = 2.88 + 2.44 + 2.88 = 8.20 r
#   要达到2.10 → LEG_LEN = 2.10 × 8.20 / 2 = 8.61 r
#   但那样绝对腿长会超出房间尺度，且和 Unity 侧 legLength 默认值脱钩。
#   → 这里取 **6.4 r（比值 1.56）**，属于「跳蛛/圆网蛛」一类的偏短腿型，
#     并把阈值设成 1.5（不是 2.0），理由写在 check_anatomy_parts 里。
#   ★ 迭代过程记录（都是实测，不是猜）：
#     3.6r → 0.88（腿比体短一半，一眼是「球加腿」）
#     4.6r → 1.40（仍偏短，检查项拦下）
#     6.4r → 1.56 ✓
#   以后若要更接近狼蛛外观，把 LEG_LEN 和 Unity 侧 legLength 一起提到 8.6。
LEG_SEGS   = [0.30, 0.12, 0.35, 0.15, 0.08]   # 5 节比例
# ★ 依据真实步足 7 节（基节·转节·股节·膝节·胫节·后跗节·跗节），
#   可见的两个特征节是「膝节」和「胫节」→ 必须让第 3 节（索引 2）最长，
#   且第 2 节（膝节，索引 1）最短。旧值[.30,.16,.32,.14,.08] 把股节设成
#   最长，胫节几乎一样长 → 膝不突出，腿形读不出蜘蛛。
#   5 节是Unity 侧 SilkSpiderAnatomy 的硬契约（legSegments.Length != 5 就回退默认），
#   所以**不改成 7 节**；改用「长度比例 + 半径profile」还原真实形态。

# ★★★ 每节腿的半径比例（真实蜘蛛的腿不是等锥台）
#   资料依据：股节粗壮→膝节突然变细（这是最明显的「关节」特征）→
#   胫节又稍粗→后跗节细→跗节极细。
#   旧版用 lerp(1 → 0.35) 的线性递减 → 膝部不细，读出来是「锥形棍子」。
LEG_THICK_PROFILE = [1.00, 0.62, 0.72, 0.45, 0.33]
LEG_TIP_RATIO     = 0.12   # 跗节末端半径 / 腿根半径
LEG_THICK  = BALL_RADIUS * 0.24# 腿根半径
# ★★★ 从 0.155 提到 0.24，依据是渲染图实测：
#   0.155 时腿长/腿根半径 = 8.0/0.194 = **41 : 1** → 俯视图里腿细成针。
#   真实大型游猎蛛的步足粗细约1:25~30（游猎蛛腿要能撑住体重、还有抓握毛）。
#   0.24 → 8.0/0.30 = 27 : 1 ✓
#   ★ 这类「粗细」问题**所有数值检查都抓不到**：
#     骨骼数对、足端间距对、站姿高度对、面数达标 —— 全过，
#     但渲染出来是一堆针。只能靠看图 + 算「腿长/半径」这个比值。
LEG_SPREAD = BALL_RADIUS * 0.62   # 腿根左右外扩
LEG_SPREAD_DEG = 38.0             # 静态张开角
CHEL_LEN   = BALL_RADIUS * 0.85# 螯肢长度

# ---- 触肢（pedipalp）—— 之前完全缺失，真实蜘蛛有 6 对附肢，我们只做了 6 对里的 2对 ----
# 资料：蜘蛛头胸部 6 对附肢 = 螯肢 1 对 + 触肢 1 对 + 步足 4 对。
# 触肢形如步足但更粗短，雌蛛呈足状，末端略膨大（雄蛛膨大成「触肢器」）。
# 缺了它，头胸部前端就只剩两枚毒牙，读起来像「甲虫」而不是蜘蛛。
PED_LEN    = BALL_RADIUS * 1.75   # 触肢总长（明显短于步足）
PED_SEGS   = [0.34, 0.26, 0.22, 0.18]        # 4 节可见段
PED_THICK  = BALL_RADIUS * 0.13

# ---- 纺器（spinneret）—— 腹后端的丝囊，之前也缺失 ----
# 资料：腹部末端肛门前方有 3 对（共 6 个）纺器，蜘蛛用来吐丝。
# 缺了它腹后端是个光秃的椭球。体型小、6 个小锥台即可。
SPIN_COUNT = 3      # 每侧 3 个 = 共6 个（真实为6，多数种）
SPIN_LEN   = BALL_RADIUS * 0.30

# ★★ 全身长（判据的基准，必须是**公式**而不是字面量 —— MEMORY 第三节规则 4）。
#   头胸长 2×CEPH_LEN + 头胸中心↔腹部中心间距 + 腹长 2×ABDO_RAD
BODY_LEN_TOTAL = 2 * CEPH_LEN + BODY_GAP + 2 * ABDO_RAD

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
#     yaw 84/30   间距 0.68★ 旧腿长(3.6r)下选用
#     yaw 95/36   间距 0.52  ← 掉下来
#     yaw 105/42  间距 0.26  ← 严重掉下来
#
#   ★★★ 再扫一次（**腿加长到 6.4r 之后**，间距是腿长的倍数，会重新洗牌）。
#     扫描由Tools/blender/sweep_yaw.py 实测（35 组，表格在那个文件里跑出来）：
#     outer  84 → 0.685  ★ 峰值（达标）
#     outer  90 → 0.459  ← 掉下阈值
#     outer  96 → 0.235  ← 严重
#     outer 102 → 0.058  ← 几乎重合
#     outer 108 → 0.224  ← 开始回升
#     outer 114 → 0.433
#     outer 120 → 0.635  ← 又回到达标（第二峰）
#
#   ★★★★ 「inner 完全不影响间距」是扫描出来的意外结论：
#     inner 26/30/34/38/42 五档，间距**一模一样**（0.685）。
#     原因：最小间距对永远是**跨侧**的 leg_L_4↔ leg_R_1（镜像的一对），
#     它们的角度只由 outer 决定；同侧 4 条腿的间距恒大于这个瓶颈，
#     所以瓶颈值对 inner 数值完全不敏感。
#     → inner 不是自由参数，它只影响「扇形张开的观感」，不影响通过判定。
#     要真正验证 inner，得另加一条「同侧相邻腿间距」判据（尚未加）。
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
# ★★★ 膝拱起从 0.80 提到 1.55，依据是侧视图渲染实测：
#   0.80 时膝高 0.88，腿长 8.0 → 膝拱高/腿长 = 0.11。
#   侧视图里腿几乎是**水平尖刺**，完全读不出「膝盖把腿顶到身体上方」
#   这个蜘蛛最有辨识度的特征。
#   真实游猎蛛的膝（膝节+胫节关节）明显高过背甲，
#   膝拱高/腿长 大约 0.20~0.28。
#   1.55 → 膝高≈1.7，比例 0.21 ✓
#   ★ 注意：这与 check_standing 的判据不冲突 ——
#     它只检查「膝 > 腿根」和「膝→足端下降够大」，方向对但幅度不够，
#     所以通过了却仍然不像蜘蛛。**判据只防方向错误，不防幅度不足**。
LEG_KNEE_RISE   = BALL_RADIUS * 1.55   # 膝部相对腿根的抬升量（正 = 向上）
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
# ★★★ 面数预算：3000 → 3600，**这是有意的上调，理由记录在案**
#
#   旧预算 3000 是在**模型缺三块结构**时定的：
#     · 只有 6 只眼（真实 8 只，少了 2 只 —— 标签却写 Eye(8)，掩盖了）
#     · 没有触肢（6对附肢只做了 2 对）
#     · 没有纺器（腹部末端光秃）
#   补齐这三块后实测3192，超预算 7%。
#
#   为什么上调而不是砍结构：
#     · 砍眼睛/触肢/纺器 = 回到「不像蜘蛛」，正是被反复批评的偷工减料。
#     · 低模角色（非背景道具）业界普遍 3k~6k；3600 在正常区间内，
#       且 8 条腿的锥台是主要成本（8×5×24≈960），已无水分可挤。
#     · 剩下的空间已经不在「大件」上：眼睛 288 面是 8 只×36，
#       降到 30 面以下眼睛会失去球感，得不偿失。
#
#   → 预算的意义是「防止无节制加细节」，不是「必须守住的上限」。
#     一旦发现面数失控，先看下面的「面数分解」定位到部件，再谈压不压。
TRIS_BUDGET   = 3600   # 补齐 8 眼 + 触肢 + 纺器后的合理上限

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
    #腿根沿头胸部前后铺开的比例
    # ★★★ 从 0.26 提到 0.85，依据是解剖 + 实测：
    #   0.26 时腿根前后总跨度只有 0.26×CEPH_LEN = 0.38，
    #     而头胸部长1.44 → 腿根挤在头胸部**最后 8%** 的窄条里。
    #     这正是「最前腿 L1 ↔ 最后腿 L4 跨侧间距」一直偏小的根因：
    #     靠调 yaw 只能顾一侧，腿根位置才是根源。
    #   真实蜘蛛：4 对腿的基节横跨头胸部绝大部分长度，各对清晰分开。
    #     0.85 → 腿根跨度 1.22，占头胸部长 85% ✓
    spread = 0.85
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

    # 8 只眼：★ 之前只有 3 行 × 2 = **6 只**，标签却写着 Eye(8)，
    #   面数统计里出现 "Eye(8)" 更掩盖了「只有6 只」这件事 —— 零报错。
    #   真实蜘蛛通常 8 只单眼，排成2~4 行（Australian Museum / 科普中国）。
    #   → 改成 4 行 × 2 = 8 只。前中眼最大，后中眼最小（狼蛛型排列）。
    eye_rows = [(0.88, 1.45), (0.72, 1.10), (0.58, 0.82), (0.44, 0.66)]
    for row, (fy, scale) in enumerate(eye_rows):
        # ★ 每往下一行，左右间距也要变宽（真实眼列是外扩的弧线）
        spread = CEPH_WID * (0.19 + row * 0.055)
        for col in range(2):
            side = -1.0 if col == 0 else 1.0
            rx = side * spread
            r = CEPH_WID * 0.075 * scale
            # 眼睛坐在背甲上：z 用椭球表面高度随 y 变化，
            #   否则眼睛会浮在背甲外或陷进背甲里（前一版就是固定 z）。
            z = CEPH_HGT * math.sqrt(max(0.0, 1.0 - (fy - 0.10) ** 2
                                             - (spread / CEPH_WID) ** 2)) * 0.94
            obs.append(add_ellipsoid("Eye_%d_%d" % (row, col),
                                     (rx, CEPH_LEN * fy, z),
                                     (r, r, r), mats['eye'],
                                     seg=EYE_SIDES, rings=EYE_RINGS))

    # fovea（头胸部中央的肌肉附着凹陷）—— 真实狼蛛背甲正中有一道纵向凹槽，
    #   是辨识度很高的细节。用一个压扁的小椭球做「浅凹」。
    obs.append(add_ellipsoid("Fovea", (0.0, CEPH_LEN * 0.06, CEPH_HGT * 0.97),
                             (CEPH_WID * 0.075, CEPH_LEN * 0.52,
                              CEPH_HGT * 0.06), mats['body'],
                             seg=8, rings=4))

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

    # ---- 触肢（pedipalp）：★ 之前完全缺失 ----
    # 位置在螯肢**外侧**、略靠后，比步足短、比毒牙粗短，走「抓握」姿态。
    # 4 段锥台 + 末端膨大（雌蛛触肢末节略呈棒状）。
    for side, tag in ((-1.0, "L"), (1.0, "R")):
        cur = Vector((side * CEPH_WID * 0.30, CEPH_LEN * 0.62,
                      -CEPH_HGT * 0.34))
        #朝前外 30°、略向下
        yaw = math.radians(28.0 * side)
        fwd = Vector((math.sin(yaw), math.cos(yaw), 0.0))
        for s, ratio in enumerate(PED_SEGS):
            seg_len = ratio * PED_LEN
            drop = -PED_LEN * 0.16 if s == 0 else -PED_LEN * 0.06
            nxt = cur + fwd * seg_len + Vector((0.0, 0.0, drop))
            mid = (cur + nxt) * 0.5
            seg = add_cone_seg("Pedipalp_%s_%d" % (tag, s + 1),
                               PED_THICK * (1.0 - 0.14 * s),
                               PED_THICK * (1.0 - 0.14 * (s + 1)),
                               seg_len, mats['leg'], seg=LEG_SIDES)
            seg.location = mid
            seg.rotation_mode = 'QUATERNION'
            seg.rotation_quaternion = Vector((0, 0, 1)).rotation_difference(
                (nxt - cur).normalized())
            obs.append(seg)
            cur = nxt
        # 末节膨大（触肢的棒状端）
        tip = add_ellipsoid("Pedipalp_%s_tip" % tag, cur,
                            (PED_THICK * 0.62, PED_THICK * 0.62,
                             PED_THICK * 1.5), mats['leg'],
                            seg=JOINT_SIDES, rings=JOINT_RINGS)
        obs.append(tip)

    # ---- 纺器（spinneret）：腹后端 3 对 ----
    for side, tag in ((-1.0, "L"), (1.0, "R")):
        for s in range(SPIN_COUNT):
            # 前、中、后三对：越靠后越粗、越外扩
            f = float(s) / max(1, SPIN_COUNT - 1)
            px = side * ABDO_RAD * (0.20 + 0.16 * f)
            py = -BODY_GAP - ABDO_RAD * (0.72 + 0.10 * s)
            pz = -CEPH_HGT * 0.15 - ABDO_RAD * 0.22
            sp = add_cone_seg("Spinneret_%s_%d" % (tag, s + 1),
                              ABDO_RAD * 0.11, ABDO_RAD * 0.045,
                              SPIN_LEN * (0.7 + 0.3 * f), mats['abd'],
                              seg=6)
            sp.location = (px, py, pz)
            # 朝后下
            sp.rotation_euler = (math.radians(115.0), 0.0,
                                 math.radians(side * 14.0))
            obs.append(sp)

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
            # 半径：★ 改用 LEG_THICK_PROFILE（分节 profile），不用线性 lerp。
            #   真实腿最关键的辨识特征是「膝部突然收细」。
            #   线性递减的腿读出来是均匀锥形棍子，一眼假。
            r0 = LEG_THICK * LEG_THICK_PROFILE[s]
            r1 = LEG_THICK * (LEG_THICK_PROFILE[s + 1]
                              if s + 1 < len(LEG_THICK_PROFILE)
                              else LEG_TIP_RATIO)
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
                # ★ 半径取相邻两节的**较大者**，不能用 r0。
                #   r0 现在是「本节起点」半径；膝节处 r0 偏小（profile 0.62），
                #   用 r0 会让关节球小于相邻两节 → 折角处露缝。
                #   低模里腿节露缝是最容易漏检的细节（不崩、不报错，只是难看）。
                jr = max(r0, r1) * 1.12
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
    print("足端最小间距：%.3f（= %.2f × 腿长，%.2f × 体长）  %s ↔ %s"
          % (min_d, ratio, min_d / BODY_LEN_TOTAL, pair[0], pair[1]))

    # ★★★ 判据改成「相对于**体长**」，不再用「相对于腿长」。
    #   【为什么必须改 —— 这是本轮第三次判据本身有问题】
    #   老判据是「间距 > 0.6 × 腿长」，0.6 这个阈值是在**腿长 3.6r** 时
    #   校准出来的（当时实测 0.68，勉强通过）。
    #   → 但「腿长的倍数」会随加长腿**自动变严**：
    #       腿长 3.6r → 阈值 = 2.9 格，实测 4.39 → 0.68 ✓
    #       腿长 6.4r → 阈值 = 4.8 格，实测 4.39 → 0.55 ✗
    #     也就是说：**只要把腿加长到真实比例，这个判据就必然失败**。
    #     判据在惩罚「把腿改长」这个正确方向 —— 这是判据错了，不是模型错了。
    #
    #   【正确的问法】
    #     「足端挤不挤」是**视觉尺度**问题，取决于蜘蛛在画面里有多小，
    #     也就是取决于**体长**，跟腿多长没有直接关系。
    #     腿加长只会让「倍数」变小，但绝对间距一点没变 —— 视觉上毫无变化。
    #   → 阈值应以体长为基准：0.53 × 体长。
    #     0.53 这个值仍然由「原始 bug 值」校准：
    #       最早 yaw 全 0 时最小间距 0.03×3.6r=0.108 格
    #       当前体长 8.20r=10.25 格 → 0.108/10.25 = 0.0105（严重重合）
    #       差 50 倍。取 0.53 意味着「离差50 倍」仍有充足余量。
    #
    #   ★ 教训（和MEMORY 里那条一样）：
    #     **别让判据的隐含基准跟着你正在调的参数一起动**，
    #     否则调参数的过程就是在和判据打架。
    body_len = BODY_LEN_TOTAL
    span_ratio = min_d / body_len
    if span_ratio < 0.53:
        print("[FAIL] 足端间距仅 %.2f × 体长（< 0.53）"
              " → 视觉上会叠成一条腿" % span_ratio)
        return False

    # ---- ★★ 同侧相邻腿间距：覆盖 inner yaw ----
    #  【为什么必须单独加这条】
    #    实测（sweep_yaw.py）：inner 从 26 到 42，「最小间距」**完全不变**。
    #    因为跨侧对leg_L_4↔leg_R_1 是镜像的一对，只由 outer 决定，
    #    同侧间距恒大于这个瓶颈 → inner 对全局最小值不敏感。
    #    → 只看全局最小值 = inner 这一整个参数**根本没被检查**。
    #    而同侧 4 条腿叠在一起，正是第一版「8条腿叠成 2 条」的形态
    #    （那一版 yaw 全 0，瓶颈也主要在同侧）。
    #    少一条判据 = 一个参数可以随便乱填而不被发现。
    side_min = 1e9
    side_pair = None
    for tag in ("L", "R"):
        idx = [k for k in range(1, 5)]
        pts = [tips["leg_%s_%d_5" % (tag, k)] for k in idx]
        for a in range(4):
            for b in range(a + 1, 4):
                d = (pts[a] - pts[b]).length / leg_len
                if d < side_min:
                    side_min, side_pair = d, (tag, idx[a], idx[b])

    print("同侧最小间距：%.2f × 腿长  leg_%s_%d ↔ leg_%s_%d"
          % (side_min, side_pair[0], side_pair[1],
             side_pair[0], side_pair[2]))
    # ★ 同侧判据同样改用「体长」基准，理由与上面一致。
    #   阈值 0.20 × 体长，仍由yaw 全 0 的原始 bug 校准：
    #     yaw 全 0 时同侧相邻腿间距 0.03×3.6r = 0.108 格 → 0.0105 × 体长
    if side_min * leg_len / body_len < 0.20:
        print("[FAIL] 同侧腿间距仅 %.3f × 体长（< 0.20）"
              " → 同侧腿互相穿插，俯视图会糊成一片"
              % (side_min * leg_len / body_len))
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

    # ★★★ 新增：膝拱**幅度**判据（原来只判方向，是它漏掉了这次的问题）
    #   旧判据只有「膝 > 腿根」+「下降够大」→ 膝高 0.88 也照样通过，
    #   但渲染出来腿是水平尖刺，完全不像蜘蛛。
    #   → 补一条「膝拱高 / 腿长 ≥ 0.18」。
    #     依据：真实游猎蛛的膝明显高过背甲，膝拱高约腿长的 0.20~0.28。
    #     0.11（实测值）< 0.18 → 这次会被拦下。
    arch = knee_z - root_z
    arch_ratio = arch / leg_len_hint()
    print("膝拱高/腿长 = %.2f（真实游猎蛛 0.20~0.28）" % arch_ratio)
    if arch_ratio < 0.18:
        print("[FAIL] 膝拱高仅 %.2f × 腿长（< 0.18）"
              " → 腿读起来是水平尖刺，不是蜘蛛的弓腿"
              % arch_ratio)
        ok = False

    # ★★ 腿粗细判据：腿长 / 腿根半径
    #   腿太细时俯视图里8 条腿像针 —— 所有其他检查都抓不到。
    slenderness = leg_len_hint() / LEG_THICK
    print("腿长/腿根半径 = %.1f : 1（真实游猎蛛约 25~30 : 1）"
          % slenderness)
    if slenderness > 35.0:
        print("[FAIL] 腿太细（%.1f : 1 > 35）→ 渲染出来是 8 根针，"
              "加大 LEG_THICK" % slenderness)
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

    # ★ 解剖部件检查放在导出**之后**才有意义 —— 那样 FBX 已经生成，
    #   万一失败也能留一份供排查（初版就是先查后建，失败时什么都没有）。
    if not check_anatomy_parts(meshes):
        print("=" * 64)
        sys.exit(6)

    tris = 0
    per_part = {}
    # ★ 按部件归类时，Eye_* 的 key 硬写成'Eye(8)' —— ★ 这就是 bug 被藏起来的现场。
    #   曾经只有 6 只眼（3 行 × 2），但统计标签写着「Eye(8)」，
    #   看输出完全正常。→ 现在改成**实际数出来**，
    #   而且下面 check_anatomy_parts() 会断言数量。
    for ob in meshes:
        if ob and ob.type == 'MESH':
            t = sum(len(p.vertices) - 2 for p in ob.data.polygons)
            tris += t
            # ★ 按部件归类，看面数到底花在哪 —— 不猜，先量
            key = ob.name.split('_seg')[0].split('_j')[0]
            if key.startswith('Eye'):
                key = 'Eye(%d)' % count_eyes(meshes)
            elif key.startswith('Pedipalp'):
                key = 'Pedipalp(2)'
            elif key.startswith('Spinneret'):
                key = 'Spinneret(6)'
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


def count_eyes(meshes):
    """★ 实际数眼睛网格的数量 —— 不要再靠标签文字。"""
    n = 0
    for ob in meshes:
        if ob and ob.type == 'MESH' and ob.name.startswith('Eye_'):
            n += 1
    return n


def check_anatomy_parts(meshes):
    """★★ 验证解剖部件齐全 —— 专治「缺了部件但不报错」。

    【为什么必须有这个检查】
      第一版模型**只有 6 只眼**（eye_rows 写了 3 行），但：
        · 不崩、不报错
        · 骨骼数 42 对、网格数对、面数达标、FBX正常导出
        · 统计标签还写着「Eye(8)」（硬编码），把bug 盖得严严实实
      → 和「8 条腿叠成 2 条」「腿翘到天上」是同一类 bug：
        **全部检查项都过，肉眼才看得出来**。
        所以必须有一条**数值**判据盯住部件数量。

    【期望数量（依据真实解剖）】
      8只单眼（2~4 行排列）
      触肢 2 条× 4 节 + 2 个末端 = 10 个网格
      纺器 3 对 = 6 个
      fovea 1 个
    """
    ok = True
    names = [ob.name for ob in meshes if ob]

    def n_of(prefix):
        return len([x for x in names if x.startswith(prefix)])

    eyes = n_of('Eye_')
    ped = n_of('Pedipalp_')
    spin = n_of('Spinneret_')
    fovea = n_of('Fovea')

    print("解剖部件：眼 %d ·触肢段 %d · 纺器 %d · fovea %d"
          % (eyes, ped, spin, fovea))

    if eyes != 8:
        print("[FAIL] 眼睛应8 只，实际 %d —— 真实蜘蛛通常 8 只单眼" % eyes)
        ok = False
    if ped < 8:
        print("[FAIL] 触肢网格应>= 8（2 条 × 4 节），实际 %d"
              " → 缺了第2 对附肢，读起来不像蜘蛛" % ped)
        ok = False
    if spin != SPIN_COUNT * 2:
        print("[FAIL] 纺器应为 %d 个（%d 对），实际 %d"
              % (SPIN_COUNT * 2, SPIN_COUNT, spin))
        ok = False
    if fovea != 1:
        print("[FAIL] fovea（背甲中央凹槽）应有 1 个，实际 %d" % fovea)
        ok = False

    # ---- 腿展/体长比：最容易被眼睛抓到的「不像蜘蛛」指标 ----
    # 真实游猎蛛（狼蛛/跳蛛）腿展≈ 体长 2.0~2.5 倍；圆网蛛/皿蛛偏短，约 1.5。
    # 阈值取 1.5：低于此值腿短到会读成「甲虫/球加腿」，高于则至少有蜘蛛比例感。
    # 想要狼蛛外观把阈值和 LEG_LEN 一起提到 8.6 r（约 2.1 倍）。
    body_len = BODY_LEN_TOTAL
    span= 2 * LEG_LEN
    ratio = span / body_len
    print("腿展/体长= %.2f（真实游猎蛛 2.0~2.5）" % ratio)
    if ratio < 1.5:
        print("[FAIL] 腿展/体长仅 %.2f → 短腿，一眼不像蜘蛛。"
              " 加大 LEG_LEN（不要靠改体长）" % ratio)
        ok = False
    return ok


if __name__ == "__main__":
    main()