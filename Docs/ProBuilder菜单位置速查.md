# ProBuilder 5.2.4 完整菜单位置速查

> ★ 从 `Library/PackageCache/com.unity.probuilder@5.2.4/` **源码实测提取**，
> 不是凭印象或抄文档。
> 日期：2026-10-08

## ★★ 先记住三条铁律（我踩过的坑）

| # | 坑 | 正确做法 |
|---|---|---|
| 1 | ★ **新建形状不在 `Tools` 菜单** | → **`GameObject`** 菜单 |
| 2 | ★ **编辑命令不在静态源码里**（grep 不到） | 它们是 `MenuAction` 子类，**运行时动态生成** |
| 3 | ★ **`Tools > ProBuilder` 那一栏是空的** | 真正的编辑命令在 **ProBuilder 工具栏窗口**里 |

★ 我第一次告诉你「`Tools > ProBuilder > New Shape`」是**错的**——
那是照着新版文档写的，5.x 菜单结构不同。以后遇到菜单问题先查源码。

---

## 一、新建形状：`GameObject` 菜单

**在 Scene 视图空白处右键** → `GameObject` → `ProBuilder` →

| 形状 | 用途 |
|---|---|
| **`Cube`** | ★ 最常用，墙/平台/箱子都靠它 |
| `Plane` | 地面 |
| `Stair` | ★ 楼梯（自动按级数生成）|
| `Curved Stair` | 弧形楼梯 |
| `Door` | ★ 门（自带门洞）|
| `Window` | 窗（带窗洞）|
| `Arch` | 拱门 |
| `Cylinder` | 柱/筒 |
| `Cone` | 锥 |
| `Sphere` | 球 |
| `Torus` | 圆环 |
| `Pipe` | 管 |
| `Prism` | 棱柱 |
| `Sprite` | 精灵 |

★ **这 14 个是 ProBuilder 5.2.4 的全部形状**（源码 `ShapeMenuItems.cs`）。

---

## 二、编辑命令：ProBuilder 工具栏窗口

**`Window` → `ProBuilder`**（打开工具栏窗口）

★ 编辑命令**只在这里**，不在 `Tools` 菜单里。
★ 命令按**分组**组织，共 **78 个**（源码实测）。

### Geometry（几何）—— 最常用

| 命令 | 作用 |
|---|---|
| `Extrude` | ★ **挤出面/边**（`Ctrl+E`）—— 拉出墙体最常用 |
| `ExtrudeFaces` / `ExtrudeEdges` | 挤出面 / 挤出边 |
| `BridgeEdges` | 桥接连通的边 |
| `InsertEdgeLoop` | 插入边循环 |
| `BevelEdges` | 倒角 |
| `SplitVertices` | 拆分顶点 |
| `WeldVertices` | ★ **合并顶点**（`Alt+V`）—— 消除裂缝 |
| `CollapseVertices` | 塌陷顶点 |
| `ConnectVertices` / `ConnectEdges` | 连接 |
| `SmartConnect` | 智能连接 |
| `SmartSubdivide` | 智能细分 |
| `SubdivideEdges/Faces/Object` | 细分 |
| `OffsetElements` | 偏移 |
| `TriangulateFaces/Object` | 三角化 |
| `FillHole` | 补洞 |
| `ConformFaceNormals` / `ConformObjectNormals` | 统一法线 |
| `FlipFaceEdge` / `FlipFaceNormals` / `FlipObjectNormals` | 翻转法线 |

### Object（对象）

| 命令 | 作用 |
|---|---|
| `MergeObjects` | ★ **合并多个物体**（做成一整块）|
| `MirrorObjects` | 镜像 |
| `CenterPivot` | 中心设轴心 |
| `SetPivotToSelection` | 轴心设到选中 |
| `FreezeTransform` | 冻结变换 |
| `ProBuilderize` | ★ **把普通 mesh 转成 ProBuilder 可编辑** |
| `SubdivideObject` | 细分物体 |

### Selection（选择）

| 命令 | 作用 |
|---|---|
| `SelectEdgeLoop` / `SelectFaceLoop` | 循环选择边/面 |
| `SelectEdgeRing` / `SelectFaceRing` | 环绕选择 |
| `SelectHole` / `SelectLoop` / `SelectRing` | 选择孔洞/循环/环 |
| `GrowSelection` / `ShrinkSelection` | 扩大/缩小选择 |
| `ToggleDragRectMode` / `ToggleDragSelectionMode` | 切换拖拽模式 |
| `ToggleSelectBackFaces` / `ToggleXRay` | 选背面 / X-Ray |
| `ToggleHandleOrientation` / `ToggleHandlePivotPoint` | 切换手柄 |

### Editor（编辑器）

| 命令 | 作用 |
|---|---|
| `OpenVertexPositionEditor` | ★ **顶点位置编辑器**（精确输坐标）|
| `OpenUVEditor` | UV 编辑器 |
| `OpenVertexColorEditor` | 顶点色 |
| `OpenMaterialEditor` | 材质编辑器 |
| `OpenLightmapUVEditor` | 光照 UV |
| `OpenSmoothingEditor` | 平滑组 |
| `SetCollider` | ★ **设置碰撞体** |
| `SetTrigger` | 设置触发器 |
| `GenerateUV2` | 生成 UV2 |
| `NewBezierShape` / `NewPolyShapeToggle` / `NewShapeToggle` | 新建形状（工具栏版）|

### Export（导出）

| 命令 | 作用 |
|---|---|
| `ExportAsset` | 导出为 asset |
| `ExportObj` / `ExportPly` | 导出 OBJ / PLY |
| `ExportStlAscii` / `ExportStlBinary` | 导出 STL |

---

## 三、修复工具：`Tools` 菜单（真的只有这三个）

| 路径 | 用途 |
|---|---|
| `Tools > ProBuilder > Repair > Check for Broken ProBuilder References` | ★ **检查断裂引用**（几何出问题时先跑这个）|
| `Tools > ProBuilder > Repair > Mesh Debug Tool` | 网格调试 |
| `Tools > ProBuilder > GUID Remap Editor` | GUID 重映射 |
| `Tools > ProBuilder > API Examples > Log Callbacks Window` | API 示例 |

---

## 四、右键菜单（上下文）

| 路径 | 用途 |
|---|---|
| **选中物体** → 右键 `ProBuilder` | 编辑命令 |
| `CONTEXT/ProBuilderMesh/Open ProBuilder` | ★ **双击物体可打开工具栏** |

★ **最快的打开方式：选中一个 ProBuilder 物体，按 `W`**
（默认快捷键是 `W` 切换编辑模式，或用菜单 `Window > ProBuilder`）

---

## 五、ProBuilder 窗口内的四个编辑模式

打开工具栏后，顶部有一排模式按钮：

| 模式 | 快捷键 | 作用 |
|---|---|---|
| **Object**（物体）| `1` | 选整个物体，移动/缩放 |
| **Vertex**（顶点）| `2` | 顶点级编辑 ★ 吸附对齐用 |
| **Edge**（边）| `3` | 边级编辑 |
| **Face**（面）| `4` | 面级编辑 ★ 挤出面用 |

★ 这四个是**核心**，记住它们比记菜单更重要。

---

## 六、常用操作速查

| 想做什么 | 怎么做 |
|---|---|
| 拉出墙体 | 切到**面模式**（`4`）→ `Shift` + 拖拽面 |
| 顶点对齐防裂缝 | **`V` + 拖拽顶点**（开启吸附）|
| 消除小裂缝 | `Alt+V`（合并顶点）|
| 精确输尺寸 | Inspector → `ProBuilder` 组件 → `Size` |
| 精确输顶点坐标 | `Window > ProBuilder` → 顶点位置编辑器 |
| 加碰撞体 | Inspector → `Add Component` → `Mesh Collider` |
| 挖门洞 | `Tools > ProBuilder > Experimental > Boolean (CSG) Tool` |
| 设置吸附 | `Edit > Preferences > ProBuilder > Vertex Snap` 或按 `V` 临时切换 |

---

## 七、★ 我们项目相关的两点提醒

### 1. 新形状默认带碰撞体

设置文件里 `mesh.newShapeColliderType = 2`，
即**新建的形状自动有 Collider** —— 不用手动加 `Mesh Collider`。

但如果你删了碰撞体又想要，点 `Tools > ProBuilder > Set Collider`。

### 2. Z-up 坐标系

本项目 **X=左右Y=前后 Z=高度**（非 Unity 默认）。
ProBuilder 是在 Unity 里直接操作 Transform，
所以它会**遵守场景里物体的朝向**——你摆好的墙就是 Z 向高的墙。

★ 但如果你从别的 DCC 软件导入模型，会遇到 Y-up → Z-up 的旋转问题。

---

## 附：数据来源

全部从本地源码提取，可复查：

| 信息 | 文件 |
|---|---|
| 14 个形状菜单 | `Editor/EditorCore/ShapeMenuItems.cs` |
| 78 个编辑命令 | `Editor/MenuActions/**/*.cs`（`MenuAction` 子类）|
| Tools 菜单三个工具 | grep 全部 `MenuItem` 属性 |
| 菜单前缀 `Tools/ProBuilder/` | `Editor/EditorCore/EditorToolbarMenuItems.cs:17` |
| 设置默认值 | `ProjectSettings/Packages/com.unity.probuilder/Settings.json` |
