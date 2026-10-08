# ProBuilder 安装与使用完整过程

> 针对本项目：**Unity 2022.3.62f3c1 + Built-in 内置管线**（非 URP/HDRP）
> 编写日期：2026-10-08

## ★ 你的项目情况（已核实，省掉一半步骤）

| 项 | 你的情况 | 影响 |
|---|---|---|
| Unity 版本 | 2022.3.62f3c1 | ✓ 满足 ProBuilder 6.0.9 的「2019.4+」要求 |
| 渲染管线 | **Built-in 内置管线** | ★ **不需要**导入 URP/HDRP 着色器 |
| URP/HDRP 包 | 未安装 | ★ 跳过「导入 Samples 着色器」这一步 |
| ProBuilder 已装 | 否 | 需Package Manager 安装 |

★ **因为用 Built-in 管线，下面第 3 步（导入着色器）可以完全跳过。**
官方文档原文：「If you are using either URP or HDRP, you also need to import
the corresponding shaders.」—— 你不属于这两种情况。

---

## 第 1 步：安装 ProBuilder

1. 打开 Unity 菜单：`Window > Package Manager`
2. 左侧栏点 **`Unity Registry`**（不是 My Assets）
3. 搜索框输入 **`probuilder`**
4. 在结果里找到 **`ProBuilder`**（发布者 Unity Technologies）
   - ⚠️ 注意别选成同名的第三方包
   - 右侧应显示版本 **6.0.x**、发布者 **Unity Technologies**
5. 点右下角 **`Install`**

★ 安装后 Package Manager 里会出现 `ProBuilder`，并且顶部菜单栏多出
**`Tools > ProBuilder`**。

---

## 第 2 步：启用实验性功能（★ 挖门洞必需）

ProBuilder 的 CSG 布尔运算属于**实验性功能**，默认关闭。

1. 菜单 `Edit > Preferences`（Mac 是 `Unity > Preferences`）
2. 左侧列表选 **`ProBuilder`**
3. 勾选 **`Enable Experimental Features`**
4. 关闭 Preferences 窗口

★ **Unity 会重新编译**，等编译完（看右下角进度条）。

★ 启用后 `Tools > ProBuilder` 菜单会多出：
- `New Bezier Shape`（曲线形状）
- `Vertex Component`（顶点组件）
- **`Experimental > Boolean (CSG) Tool`** ← 这就是我们要的

★ 官方警告这个开关「might reduce ProBuilder's stability」——
若遇到异常，可关掉此开关回到稳定状态。

---

## 第 3 步：跳过（你的项目不需要）

~~导入 URP/HDRP Samples 着色器~~

★ 只有 URP/HDRP 项目需要做这步。你的项目是 Built-in 管线，**跳过**。

---

## 第 4 步：画第一面墙

1. 菜单 `Tools > ProBuilder > New Shape > Cube`
   → 场景里出现一个白色立方体
2. 选中它，在 **Inspector** 里找到 **`ProBuilder`（或 `Behaviour`）** 组件
3. 设置 **Size**：
   - `X` = 墙厚方向厚度
   - `Y` = 墙的长度
   - `Z` = 墙的高度

★ **建议用 `Ctrl` + 拖动面**来拉伸（官方推荐的「暴力三连招」）：

| 操作 | 快捷键 | 效果 |
|---|---|---|
| 挤压面 | **`Shift` + 拖拽面** | 拉出墙体|
| 顶点捕捉 | **`V` + 拖拽顶点** | 对齐到网格点，避免缝隙 |
| 合并顶点 | **`Ctrl` + E** | 焊死多余顶点 |

★ 顶点数值建议保持整数（配合顶点吸附），否则容易出现裂缝。

---

## 第 5 步：勾选碰撞（★ 我们的球靠 SphereCast 撞墙）

**这一步不做的话球会直接穿墙。**

1. 选中 ProBuilder 物体
2. Inspector 点 **`Add Component`**
3. 搜索并添加 **`Mesh Collider`**

★ 我们项目里球的碰撞是 `Physics.SphereCast`（`SolveWallCollision`），
依赖场景物体有 Collider。没有 Collider = 球穿墙。

---

## 第 6 步：用 CSG 挖门洞（★ 核心操作）

### 6.1 准备两个形状

1. 已有墙（大Cube），宽 `X`（贯穿墙厚）
2. **新建** `Tools > ProBuilder > New Shape > Cube` 当「门框」：
   - `X` = **比墙的 X 大**（要贯穿墙厚，否则减不透）
     → 墙 X = 1.2，门框 X = 3.0
   - `Y` = 门洞净宽（我们的卧室门 0.90 米 = 4.5 格）
   - `Z` = 门洞净高（2.10 米 = 10.5 格，但门楣在上方，所以门框要**从地面往上**
     → 中心点 z 要下移一半）
2. 用 **`V` + 拖拽顶点** 让门框底边对齐地面

### 6.2 执行布尔运算

1. 菜单 `Tools > ProBuilder > Experimental > Boolean (CSG) Tool`
2. 在窗口里：
   - **左侧 Source A** 槽：把**墙**拖进去（或点 `⊙`选择）
   - **右侧 Source B** 槽：把**门框**拖进去
3. **Operations** 下拉选 **`Subtraction`**
   （A 减去 B，即「墙挖掉门框」）
4. 点 **`Apply`**

★ 结果：生成一个挖出门洞的新网格。**原墙和门框仍在场景里**，
按需要删掉或隐藏。

### 三种运算的用途

| 运算 | 结果 | 我们的场景 |
|---|---|---|
| **Subtraction** | A − B | ★ **挖门洞、开窗** |
| Intersection | 只留重叠部分 | 取交集造型 |
| Union | A + B 合并 | 合并墙体 |

---

## 第 7 步：试玩验证（你需求的「试玩」）

1. 确认场景里有 `SilkParkourController` 与球
2. 点 Unity 顶栏的 **`Play`**
3. 用 `Tab` 切到 Parkour 模式
4. 走向门洞，验证球能**穿过**而不卡住

★ 若卡住，说明门框比碰撞体大得不够 ——
我们球直径 2.5 格，卧室门 4.5 格，理论上有 2 格余量，应该很宽松。

---

## 第 8 步：量尺寸并「冻结」到代码

★ 这是**ProBuilder 与我们代码的衔接点**，也是你要的「量、冻结」。

### 量尺寸

1. 选中 ProBuilder 物体的某个面/边
2. 看 Inspector 里 ProBuilder 组件的 **Size** 数值
3. 换算成格：`格数 = 米数 × 5`（本项目 `UnitsPerMeter = 5`）

### 冻结到代码

把量好的尺寸写成 `HouseBlockout.cs` 里的常量。**这才是真正的「冻结」**——
因为关卡是运行时 C# 生成的（`HouseBlockout.Create()`），而不是手工摆的。

★ 例如门洞挖通验证成功后，把 `BuildDoorways()` 从「只做门楣」
改成「墙垛 + 门楣」，或改用 Subtraction 的思路生成。

---

## 常见问题

### Q1. 菜单里找不到 `Tools > ProBuilder`
- ProBuilder 没装成功 → 回第 1 步重装
- 或者装完没等编译完 → 等右下角进度条结束

### Q2. 找不到 `Boolean (CSG) Tool`
- ★ **没启用实验性功能** → 回第 2 步勾选 `Enable Experimental Features`
- 这是最常见的卡点

### Q3. Boolean 后模型消失 / 结果不对
- 两个形状**没真正重叠** → 检查 A、B 的位置与尺寸
- 门框必须**贯穿墙厚**（X 方向要比墙大）

### Q4. 布尔运算报错或 ProBuilder 崩溃
- 官方承认 Boolean 是实验性功能，可能不稳定
- 先保存场景（`Ctrl+S`），出问题可撤销
- 实在不行关掉实验开关，用「墙垛 + 门楣」的传统做法

### Q5. 球穿墙（没加 Collider）
- 回第 5 步，**Mesh Collider 是必需的**

### Q6. 想撤销 Boolean
- `Ctrl + Z` 撤销，或删掉生成的新物体

---

## 时间预估

| 步骤 | 耗时 |
|---|---|
| 1. 安装 | 5~10 分钟（首次可能下载慢）|
| 2. 启用实验功能 | 2 分钟 + 编译等待 |
| 4~5. 画墙 + 碰撞 | 5 分钟 |
| 6. 挖门洞 | ★ 第一次 15~30 分钟（熟悉操作）|
| 7~8. 试玩 + 量尺寸 | 10 分钟 |
| **合计** | **约 1 小时** |

★ 对比：下载 UE5 需**15~39 小时**（实测 0.94 MB/s）。

---

## ★ 最快的验证路径

如果你只想快速判断「ProBuilder 够不够用」，做这一步就够：

1. 装包 + 启用实验功能（10 分钟）
2. 画一个大 Cube 当墙（1 分钟）
3. 画一个小 Cube 当门框，穿过墙（2 分钟）
4. `Boolean (CSG) Tool` → Subtraction → Apply（2 分钟）
5. 看结果：**门洞出来了就成功**

★ 若这 15 分钟内跑通，「门洞没挖通」这个问题就有解了，
**完全不需要换引擎**。
