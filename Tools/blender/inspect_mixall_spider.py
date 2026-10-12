# 无头检查 mixall 蜘蛛 FBX 的骨骼结构与网格 —— 蓝色捕鸟蛛接入前验骨
import bpy, sys

# Blender --python xxx.py -- <fbx路径>
fbx = sys.argv[sys.argv.index("--") + 1] if "--" in sys.argv else \
    r"C:\Users\1\Desktop\wdyouxi\zhanchang\zhizhu\Assets\Spiders - characters with animations\Spiders - characters with animations\Models\blue tarantula.fbx"

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=fbx)

print("==== 对象清单 ====")
for ob in bpy.data.objects:
    print(f"[{ob.type}] {ob.name}  parent={ob.parent.name if ob.parent else None}")

print("==== 骨骼树（armature） ====")
for ob in bpy.data.objects:
    if ob.type == 'ARMATURE':
        print(f"armature: {ob.name}  bones={len(ob.data.bones)}  scale={tuple(round(v,4) for v in ob.scale)}  dim={tuple(round(v,3) for v in ob.dimensions)}")
        for b in ob.data.bones:
            parent = b.parent.name if b.parent else "ROOT"
            head = tuple(round(v, 3) for v in b.head_local)
            tail = tuple(round(v, 3) for v in b.tail_local)
            print(f"  bone {b.name:28s} parent={parent:22s} head={head} tail={tail}")

print("==== 网格 ====")
for ob in bpy.data.objects:
    if ob.type == 'MESH':
        print(f"mesh {ob.name}  verts={len(ob.data.vertices)}  tris={len(ob.data.polygons)}  armature_modifier={any(m.type=='ARMATURE' for m in ob.modifiers)}")

print("==== 动画 ====")
for a in bpy.data.actions:
    print(f"action: {a.name}  frames={tuple(a.frame_range)}")
