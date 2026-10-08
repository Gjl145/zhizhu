# Godot CSG Blockout vs Unity ProBuilder —— 关卡原型工具对比

> 调研日期：2026-10-08
> 起因：用户看到 Godot 的 CSG Blockout 插件能「画、量、试玩、冻结」做关卡原型，
> 想知道 **Unity 有没有同类工具**。
> **结论：完全有，而且更适合我们的项目。**

---

## 一、直接结论

| 问题 | 答案 |
|---|---|
| Unity 有类似工具吗？ | ★ **有：ProBuilder**，Unity **官方内置包** |
| 支持 CSG 布尔运算吗？ | ★ **支持**（Intersection / Union / Subtraction）|
| 版本兼容吗？ | ★ **兼容** —— ProBuilder 6.0.9 要求 Unity 2019.4+，我们是 2022.3 ✓ |
| 需要花钱吗？ | ★ **完全免费**，Package Manager 直接装 |
| 比 Godot 的更好吗？ | 对我们项目**更好**（见第四节）|

**结论：不换引擎，装 ProBuilder 即可。**

---

## 二、ProBuilder 是什么（官方文档核实）

来源：`https://docs.unity3d.com/Packages/com.unity.probuilder@6.0/manual/`

> 「You can build, edit, and texture custom geometry in Unity with the actions
> and tools available in the ProBuilder package. You can also use ProBuilder
> to help with **in-scene level design, prototyping, collision Meshes,
> and play-testing**.」

★ 官方原文明确写了四个用途：**关卡设计、原型、碰撞网格、试玩**——
与 Godot CSG Blockout 的定位完全一致。

| 项 | 值 |
|---|---|
| 包名 | `com.unity.probuilder` |
| 版本 | 6.0.9 |
| Unity 兼容性 | **2019.4 及以后**（我们是 2022.3 LTS ✓）|
| 获取方式 | Package Manager（**不是** Asset Store，3.0 起）|
| 价格 | **免费** |
| 官方定位 | 关卡设计 / 原型 / 碰撞 / 试玩 |

### CSG 布尔运算（正是 Godot CSG Blockout 的核心）

来源：`manual/boolean.html`

> 「Boolean operations is an **experimental feature**. To use them, you must
> enable experimental features.」
> 菜单：`Tools > ProBuilder > Experimental > Boolean (CSG) Tool`

三种运算：

| 运算 | 用途 | 我们的场景 |
|---|---|---|
| **Subtraction** | A 减去 B | ★ **挖门洞**（墙 − 门框）|
| Intersection | 只保留重叠部分 | 取交集造型 |
| Union | 合并为一体 | 合并墙体 |

★ 官方 Learn 教程也确认了这个流程（`learn.unity.com/tutorial/create-3d-models-with-round-faces`）。

### ⚠️ 需要注意的前提

1. **Boolean 是实验性功能**，需在
   `Edit > Preferences > ProBuilder > Enable Experimental Features` 打开
   → 官方警告「might reduce ProBuilder's stability」
2. **URP/HDRP 需额外导入 Samples 里的着色器**
   （我们在 URP 下，需走这一步，否则显示不正常）

---

## 三、对比 Godot CSG Blockout

| 维度 | Godot CSG Blockout 3.0 | Unity ProBuilder 6.0.9 |
|---|---|---|
| **CSG 运算** | 原生 CSG（引擎级） | ★ ProBuilder Boolean（实验性）|
| **上手** | 需懂 Godot | ★ ProBuilder 更直观（可视化拖拽）|
| **与我们的代码集成** | ✗ 完全两套 | ★ **可用 ProBuilder 的 Scripting API 生成网格** |
| **资产格式互转** | 需导出再导入 | ★ 无需导出（原生在 Unity 内）|
| **改完即时试玩** | 需要切场景 | ★ Scene 视图直接 Play |
| **成本** | 重下引擎 ~15~39 小时 | ★ **装一个包，10 分钟** |
| **学习曲线** | Godot 引擎 + 插件 | ★ 只需学 ProBuilder |

---

## 四、★ 为什么 ProBuilder 对我们**更好**（不只是「有」）

### 1. 它能直接被我们的代码调用

ProBuilder 有 **Scripting API**（官方文档明确写了）：
> 「ProBuilder also comes with a **Scripting API**, so that you can write C#
> scripts to make your own tools and customizations.」

★ 这意味着：**我们可以用 C# 生成 ProBuilder 几何体**，
把 `HouseBlockout` 里手搓的 `Slab()` / `Wall()` 换成 ProBuilder 生成，
既能手工调、又能代码生成 —— 这是 Godot CSG 做不到的。

### 2. 我们已在手搓薄板，改用 ProBuilder 收益明确

`HouseBlockout` 当前的困境（我上一轮发现的）：

```csharp
static void DoorInWall(...) {
    // ★ 目前只做了门楣，没做左右墙垛 -> 门洞没挖通
    Slab(tag + "_门楣", ...);
}
```

★ 用 ProBuilder 的 **Subtraction** 就能真正挖出门洞：
墙（大盒子）− 门框（小盒子）= 门洞。**这正是 Godot CSG 的核心用法。**

### 3. 社区实测评价（多方交叉验证）

来源：tsight.io、toxigon.com、kitchendemy.com 等 2026 年文章

> 「ProBuilder is great for **blocking out levels**, creating simple props,
> and making quick edits」
> 「**秒级反馈**，而传统外部软件导入是分钟级」
> 「**碰撞生成一键搞定**，无需手动处理」
> 「如果你在凌晨 2 点想调一下墙的宽度，ProBuilder 不用切软件」

★ 正是我们要的「**画、量、试玩**」工作流。

### 4. ProBuilder 的已知局限（诚实列出）

| 局限 | 对我们影响 |
|---|---|
| **面数 > 5 万性能下降** | ★ 白盒只有几百面，无影响 |
| 不适合有机形状 / 高模 | ★ 白盒本来是硬表面，无影响 |
| UV 工具基础 | ★ 白盒不上贴图，无影响 |
| 不支持骨骼绑定 | ★ 我们的球无骨架，无影响 |
| Boolean 是实验性功能 | ⚠️ 需开实验开关，有不稳定风险 |

★ **5 条局限里，4 条对我们完全无影响**（因为白盒就是硬表面、低面数、不上贴图、无骨架）。

---

## 五、具体安装与使用步骤

### 安装

1. `Window > Package Manager > Unity Registry`
2. 搜 `ProBuilder`，点 `Install`
3. 若用 URP（我们是）：在 ProBuilder 包详情页的 **Samples** 区
   点 URP 对应的 `Import`

### 启用 CSG（挖门洞必需）

1. `Edit > Preferences > ProBuilder`
2. 勾选 `Enable Experimental Features`
3. 等编译完成（会新增 Bezier Shape 等工具）

### 挖门洞的做法

1. 用 `New Shape > Cube` 画一面墙
2. 另建一个小 Cube 当「门框」（宽度 = 门洞宽，贯穿墙厚）
3. `Tools > ProBuilder > Experimental > Boolean (CSG) Tool`
4. 左边拖墙、右边拖门框，`Operation` 选 **Subtraction**
5. `Apply` → 得到挖出门洞的墙

### 勾选碰撞（★ 必须）

ProBuilder 的Collider 是独立的组件，不会自动加：
`Inspector > Add Component > Mesh Collider`

★ 我们的球靠 `SphereCast` 撞墙，**没有 Collider 就穿墙**。

---

## 六、★ 需要提醒的一件事

**ProBuilder 生成的是场景里的静态几何体，不是运行时生成的。**

而我们的 `HouseBlockout` 是**运行时用 C# 生成**整个住宅
（`HouseBlockout.Create(h)`），因为关卡可切换、可清空重建。

两者是**不同的工作方式**：

| | ProBuilder | 我们的 HouseBlockout |
|---|---|---|
| 创建时机 | 编辑器里手工 | ★ 运行时 C# |
| 关卡切换 | 手工保存 prefab | ★ 代码重建 |
| 参数化 | 改的是网格 | ★ 改的是常量 |

★ **两者不冲突，是互补的**：
- **手工调好布局** → 用 ProBuilder 试出满意的空间关系 → 量出尺寸
- 再把尺寸写成 `HouseBlockout` 的常量（按国标算）→ 保持代码化

★ 这也正是用户的诉求「**画、量、试玩、冻结**」：
用 ProBuilder「画」和「试玩」，量出好布局，然后「冻结」成代码常量。

---

## 七、结论与建议

### 结论
**Unity 完全能做这件事，工具就是 ProBuilder（官方内置、免费、兼容我们的版本）。**
不需要为了 CSG 白盒而换引擎。

### 建议的下一步

| 优先级 | 事项 | 时间 |
|---|---|---|
| 1 | **装ProBuilder + 启用 CSG** | 10 分钟 |
| 2 | 用 ProBuilder 试挖一个门洞（验证 Boolean 是否可用） | 30 分钟 |
| 3 | 若可用 → 把 `HouseBlockout` 的门洞改成「墙 − 门框」的正确做法 | 1~2 小时 |

★ 第 2 步是**决定性验证**：若 Boolean 在我们的 URP 下能用，
那么「门洞没挖通」这个问题就有了正解，且不需要换引擎。
