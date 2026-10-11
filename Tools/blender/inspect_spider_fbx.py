# 无头检查 Quaternius Spider.fbx 的骨骼结构与网格 —— 为接入 SilkSpiderFbxRig 做证据
import bpy, sys

fbx = r"C:\Users\1\Desktop\wdyouxi\zhanchang\zhizhu\_research\quaternius_spider\extracted\Spider\Spider.fbx"
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=fbx)

print("==== 对象清单 ====")
for ob in bpy.data.objects:
    print(f"[{ob.type}] {ob.name}  parent={ob.parent.name if ob.parent else None}")

print("==== 骨骼树（armature） ====")
for ob in bpy.data.objects:
    if ob.type == 'ARMATURE':
        print(f"armature: {ob.name}  bones={len(ob.data.bones)}")
        for b in ob.data.bones:
            parent = b.parent.name if b.parent else "ROOT"
            head = tuple(round(v, 3) for v in b.head_local)
            tail = tuple(round(v, 3) for v in b.tail_local)
            print(f"  bone {b.name:24s} parent={parent:20s} head={head} tail={tail}")

print("==== 网格 ====")
for ob in bpy.data.objects:
    if ob.type == 'MESH':
        print(f"mesh {ob.name}  verts={len(ob.data.vertices)}  tris={len(ob.data.polygons)}  armature_modifier={any(m.type=='ARMATURE' for m in ob.modifiers)}")

print("==== 动画 ====")
for a in bpy.data.actions:
    print(f"action: {a.name}  frames={a.frame_range}")

print("==== 尺寸 ====")
for ob in bpy.data.objects:
    if ob.type == 'ARMATURE':
        print(f"armature scale={tuple(round(v,4) for v in ob.scale)} dim={tuple(round(v,3) for v in ob.dimensions)}")
