# -*- coding: utf-8 -*-
"""渲染蜘蛛三视角预览图 —— 用来看形态，不能只靠数字。

★ 为什么必须有这个
  本轮三个 bug **全部零报错**：骨骼数对、网格数对、面数达标、
  FBX 正常导出 4.2MB。只有看渲染图才发现：
    ① 8 条腿精确重叠成 2 条（所有腿朝向都是纯 ±X）
    ② 腿朝天上翘（足端比腿根高 1.28）
    ③ 面数超预算近一倍
  → 「跑通了」不等于「做对了」。形态问题只有眼睛能查。

★ 相机用**实测包围盒**定位 + Track To 约束自动对准
  —— 第一版手算 rotation_euler，相机对不准模型渲出一块灰板。

用法：
  blender --background --python .workbuddy/tools/render_spider_preview.py
输出：
  Tools/blender/out/preview_{top,side,front}.png
"""
import os
import sys
from mathutils import Vector
import bpy

PROJ = r'C:\Users\1\Desktop\wdyouxi\zhanchang\zhizhu'
SRC = os.path.join(PROJ, 'Tools', 'blender', 'make_spider.py')
OUT = os.path.join(PROJ, 'Tools', 'blender', 'out')


def load_spider_module():
    """把 make_spider.py 当模块加载，但**不执行它的 main()**。

    ★ make_spider.py 末尾是`if __name__ == "__main__": main()`，
      所以只要把 __name__ 设成别的值就不会触发导出。
      → 直接 exec，不做任何文本改写。

    ★★★ 不要再用 code.replace('\\nmain()\\n', ...) 这类文本替换：
      我在Tools/blender/sweep_yaw.py 里犯过同一个错 ——
      替换目标串（'\\nmain()\\n'）在源码格式一变就**静默失效**，
      而函数照常返回看起来正常的结果（那次扫描实际跑的是同一组参数，
      却打印出不同的参数值，差点据此改错模型参数）。
      → 判据要么用「语义」（__name__），要么用正则并**检查是否真的替换成功**。
    """
    code = open(SRC, encoding='utf-8').read()
    ns = {'__name__': 'spider_make', '__file__': SRC}
    exec(compile(code, SRC, 'exec'), ns)

    # 确认 main 没被执行（它会去写 FBX）
    if 'FBX_PATH' not in ns:
        raise RuntimeError('make_spider.py 没有按预期加载（找不到 FBX_PATH），'
                           '加载方式需要同步更新')
    return ns


def build_scene():
    ns = load_spider_module()
    ns['clear_scene']()

    mats = {
        'body': ns['make_material']('SpiderBody', (0.14, 0.11, 0.12)),
        'abd': ns['make_material']('SpiderAbdomen', (0.20, 0.13, 0.11)),
        'leg': ns['make_material']('SpiderLeg', (0.09, 0.08, 0.09)),
        'eye': ns['make_material']('SpiderEye', (0.85, 0.10, 0.08)),
    }
    arm, eb, world = ns['build_rig']()
    meshes = ns['build_body'](mats) + ns['build_legs'](mats, world)
    ns['skin'](arm, meshes, world)
    return ns, meshes


def measure(meshes):
    """实测世界包围盒 —— 不靠预设尺寸猜。"""
    lo = Vector((1e9,) * 3)
    hi = Vector((-1e9,) * 3)
    for ob in meshes:
        if ob and ob.type == 'MESH':
            for c in ob.bound_box:
                w = ob.matrix_world @ Vector(c)
                for i in range(3):
                    lo[i] = min(lo[i], w[i])
                    hi[i] = max(hi[i], w[i])
    return lo, hi


def setup_camera(ctr):
    cd = bpy.data.cameras.new('Cam')
    cam = bpy.data.objects.new('Cam', cd)
    bpy.context.collection.objects.link(cam)
    bpy.context.scene.camera = cam
    cd.lens = 45
    tgt = bpy.data.objects.new('Tgt', None)
    bpy.context.collection.objects.link(tgt)
    tgt.location = ctr
    # ★ Track To 约束：相机永远对准目标，不用手算欧拉角
    c = cam.constraints.new('TRACK_TO')
    c.target = tgt
    c.track_axis = 'TRACK_NEGATIVE_Z'
    c.up_axis = 'UP_Y'
    return cam


def setup_ground(z):
    bpy.ops.mesh.primitive_plane_add(size=80, location=(0, 0, z))
    gp = bpy.context.object
    gm = bpy.data.materials.new('Ground')
    gm.use_nodes = True
    gb = gm.node_tree.nodes.get('Principled BSDF')
    gb.inputs['Base Color'].default_value = (0.22, 0.23, 0.25, 1)
    gb.inputs['Roughness'].default_value = 0.95
    gp.data.materials.append(gm)


def setup_render():
    sc = bpy.context.scene
    sc.render.engine = 'BLENDER_WORKBENCH'   # 快，无需采样
    sc.render.resolution_x = 1100
    sc.render.resolution_y = 820
    sc.display.shading.light = 'STUDIO'
    sc.display.shading.color_type = 'MATERIAL'
    sc.display.shading.show_cavity = True
    sc.display.shading.cavity_type = 'BOTH'
    sc.display.shading.background_type = 'WORLD'


def main():
    ns, meshes = build_scene()
    lo, hi = measure(meshes)
    ctr = (lo + hi) * 0.5
    size = hi - lo
    radius = size.length * 0.5
    print('[preview] 包围盒中心 (%.2f, %.2f, %.2f)  尺寸 (%.2f, %.2f, %.2f)'
          % (ctr.x, ctr.y, ctr.z, size.x, size.y, size.z))

    setup_ground(lo.z - 0.15)      # 地面贴在最低点下方一点
    cam = setup_camera(ctr)
    setup_render()

    os.makedirs(OUT, exist_ok=True)
    views = [
        ('top', Vector((0.001, -0.001, 1.0))),
        ('side', Vector((1.0, 0.0, 0.18))),
        ('front', Vector((0.62, -0.72, 0.30))),
    ]
    for name, dirv in views:
        cam.location = ctr + dirv.normalized() * (radius * 2.9)
        sc = bpy.context.scene
        sc.render.filepath = os.path.join(OUT, 'preview_%s.png' % name)
        bpy.ops.render.render(write_still=True)
        print('[preview] preview_%s.png' % name)


main()