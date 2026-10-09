# Advanced Traversal System v1.12 ——攀爬系统逆向分析

> 2026-10-09 · 源文件 `D:\新建文件夹`（已解压，1.7GB，5144 个 .uasset）
> ★ **本文推翻了「下载了也用不了」的预判。**
> 实际情况：目录结构、状态机结构、动画命名体系、**作者自己写的参数说明**
> 全部可读。**A 方案（照着功能做 Unity 版）成立。**

---

## 一、包的整体构成

| 项 | 数量 | 说明 |
|---|---|---|
| `.uasset` | 5144 个 / 1.7 GB | UE 二进制，Unity 读不了 |
| `.umap` | 8 个 / 15.5 MB | 关卡 |
| **FBX** | ★ **0 个** | **没有可直接导入的模型/动画** |
| PNG | 2 个（343 KB） | 启动图 + 封面，**无用** |

`EngineAssociation: 5.5`，依赖 `AdvancedLocomotionV4`（ALS 四代）。

### 13 个子系统（2800+ 文件）
```
Climbing_System    580 文件  ← ★ 今天要做的
PoleClimbing       380
Wall_Climbing      371
Ladder_System      335
Swimming_System    220
PushPull_System    203
ZiplineSystem      186
RopeClimbingSystem 178
WallRunning        159
NarrowPath          93
SlidingSystem       90
BeamWalk_System     86
_Common_            12
```

★ 用户明确「今天只做攀爬 + 平地移动」→ 只碰 `Climbing_System`（580 文件）。

---

## 二、★★ 攀爬系统的状态机（从蓝图节点树还原）

作者在 `Climbing_Component.uasset` 里的 EventGraph 结构
（`K2Node_Composite` 就是蓝图里的**折叠注释块**，一个块=一个子函数）：

```
EventGraph
├── Climb_Down_Ledge_Graph ......... 下攀（爬下去）
│   ├── Movement_Down
│   │   ├── Start_Transform
│   │   ├── Leg IK Start-End Space ...腿部 IK 保持贴合
│   │   └── While Moving
│   └── Set Climb Down
│
├── On_Ledge_Graph ................. ★ 挂在边缘上的主循环（核心）
│   ├── Check New Ledge ............ 检测新的可抓边缘
│   ├── Check_Mantle
│   │   └── Climb_Up_On_Top ......... ★ 翻上平台
│   ├── Check_Capsule_Hit ........... ★ 胶囊体空间检查（够不够位置站）
│   ├── Find_New_Ledge_Graph
│   │   └── While Moving
│   ├── Update_Transform ............ ★ 每帧更新位置（把角色贴到边缘上）
│   └── FreeHang_Switch_Side ....... ★ 悬挂时左右换手
│       └── Other Side Trace Params
│
├── Find_Ledge_To_Grab .............. ★ 找可抓的边缘
│   └── Set Grab
├── External In / Out ............... 从其它系统进出（跳入/跳出）
│   └── Set Ext In Grab
└── Move_To_Graph
    └── While Moving
```

### ★ 由此得出的状态清单（我们照这个做）

| 状态 | 触发 | 我们的实现 |
|---|---|---|
| `Idle` | 挂住不动 | 球贴墙静止 |
| `ClimbUp` | 按上/前 | 沿墙面上移 |
| `ClimbDown` | 按下/后 | 沿墙面下移 |
| `Shimmy` (横移) | 按左/右 | ★ 沿边缘横向移动 |
| `Mantle` (翻上) | 顶到可站立的边 | ★ 翻上平台 |
| `Corner` (转角) | 边缘有 90° 转角 | ★ 外角/内角 |
| `SwitchSide` (换手) | 悬挂时换边 | Freehang 特有 |
| `Reach` (够取) | 远距离够向边缘 | 预备动作 |
| `Grab` (抓) | 起手抓边缘 | 进入攀爬 |
| `Release` (松手) | 松开 | 退出攀爬 |
| `Leap` (跃出) | 蹬墙跳 | 退出攀爬给冲量 |
| `ExternalIn/Out` | 与其它系统衔接 | 我们暂不做 |

★ **Braced vs Freehang = 全系统最大的分支**（每个动画目录下都有这两套）：
- **Braced（支撑攀爬）**：脚蹬墙、手臂撑起，贴着墙
- **Freehang（悬挂）**：只有手挂着，身体悬空
→ 我们是球，没有手脚，**第一步只做Braced 的逻辑，Freehang 直接不做**。

---

## 三、★★★ 作者自己写的参数说明（这是最有价值的部分）

.uasset 里存着**开发者为每个参数写的 tooltip 原文**。这是真正意义上的
「一手设计资料」，比任何评论文章都可靠。摘录关键几条：

### 胶囊体空间检查
> **The character capsule radius used for Climbing.**
> It can be useful when you want to grab ledges where your character doesn't
> have enought space by default radius.
> **(Every grab point the collision is checked to make sure the character has
> enought space)**. If you change this value, you will need to change the
> CapsuleCollisionOffset value too.
> **(Radius = 30 to 10, Offset = 22 to 42)**. (CapsuleSpaceDebug)

★ 直接给出了配套关系：**半径 30 配偏移 22；半径改到 10，偏移要改到 42。**
（单位是 UE 的厘米，30cm ≈ 角色半径）

### 抓取点计算
> The distance between the character grab reference point and the ledge point
> to grab the ledge. (except when the character is already on ledge)
> The grab point is calculated by the **character location, character velocity,
> GrabDirect.Offset value and the GrabUpwardOffset value.**

★ 抓取点 = 角色位置 + **角色速度** + GrabDirect 偏移 + 向上偏移
→ **用了速度** —— 说明抓取会「预判」运动方向，不是死板地取手边。

### 动画速度倍率（4 种动作各一个）
> Climb movement speed multiplier. It changes the **Animation Speed (AnimBP),
> PreDelay, MovementTime and PostDelay (Timing Label)**.
> (MovementTime = 6 sec, Multiplier = 2, The MovementTime will be 3 sec).
> The multiplier is applied at BeginPlay.

四类动作各有独立倍率：**Climb / Corner / Grab / Jump Backward**
（Shimmy 用的是 `ShimmySpeed`，在Climbing Parameters 里）

★ 这是个重要的工程细节：**速度倍率同时改动画播放速度 + 三段延迟 + 移动时间**，
所以调手感要一起调，不能只改一个。

### 延迟体系（作者分了 5 种，命名很规范）
| 参数 | 用途 |
|---|---|
| `PreDelay` | 移动**开始前**的延迟。「动画开始了但根骨还没动」→ 给混合留时间 |
| `MovementTime` | 移动本体时长 |
| `PostDelay` | 移动**结束后**、输入恢复**前**的延迟 |
| `PostDelayHoldLong/Short` | 攀到边缘末端的停顿（区分长短）|
| `DelayTimeAtLedgeEnd` | 到达边缘后、搜索新边缘前的延迟 |

★ `PreDelay` 的存在理由值得记住：**UE 动画与位移是解耦的，动画要等根骨动。**
Unity 里我们用代码驱动位移，这个参数**基本不需要** —— 可以直接砍掉这一层。

### 边缘检测的角度补偿
> Angle offset value for new ledge detection reference normal.
> (climb or jump, except corners) Rotated on the **XY axis**.
> This way ledges rotated by **90 degrees** relative to the current ledge point
> can also be detected.

★ 边缘法线要做 90° 补偿，这样「垂直于当前边缘」的方向也能抓到新边缘
→ 否则角色面朝南墙时，检测不到东西向的墙。

---

## 四、动画命名体系（可直接照抄的分类法）

```
Climbing_System/Animations/
├── Climb/            {Braced, Freehang}    持续攀爬循环
│   └── MD_climb_{short|long}_{l|r}_{up|down}[_left|_right]
│   └── MD_climb_idle_l / _r
│   └── Climb_To_Ledge_{L|R}_BS              ← "_BS" = BlendSpace 混合空间
├── Corner/           {Braced, Freehang}    转角
│   └── MD_corner_{inner|outer}_{l|r}
├── Grab/             {Braced, FreeHang}    抓取起手
├── Release/          {Braced, Freehang}    松手
├── Reach/            {Braced/01,02, Freehang}/{In, Out}   够取
├── Shimmy/           {Braced/Lean, Freehang/Blocked}    ★ 横移 + 倾斜
├── Leap/             {Braced, Freehang}    蹬墙跳
├── SwitchSide/       {Braced, Freehang}    换手
├── Idle/             {Braced, Freehang/Blocked}        ★ Blocked = 被挡住
├── External/In|Out/  {Braced, Freehang}    跨系统衔接
├── Overlays/         Additives/{Bar,Braced} + Normal/{Aim,Stand}/{Ready,Bar,Braced}
├── Fall/                                掉落
└── Montages/         ClimbDown/{Braced,Freehang}
                      ClimbOnTop/{Braced,Freehang}/{ALS,Default,ThirdPerson}
```

### ★ 命名约定（★ 这套约定本身就值得学）
| 约定 | 含义 |
|---|---|
| `MD_` 前缀 | 蒙太奇驱动（Montage-Driven）？或 Motion Driven |
| `_BS` 后缀 | **BlendSpace**（混合空间，用于方向/速度混合）|
| `_l` / `_r` | left / right 左右手（攀爬分左右手！）|
| `short` / `long` | 短手/长手（左右手臂长度不同）|
| `inner` / `outer` | 转角的内侧/外侧 |
| `Braced` / `Freehang` | 支撑 / 悬挂（最大的分支）|
| `Blocked` | 被遮挡时的替代表现 |
| `In` / `Out` | 进/出（External 专用）|
| `ALS` / `TP` / `Default` | 对应不同角色骨架（ALS / ThirdPerson / 原版）|

★ **结论**：我们的球没有左右手之分，可以砍掉 `l/r` 这一整套 → **动画需求量直接减半**。

---

## 五、配套资源（我们能借鉴的思路）

| 资源 | 借鉴价值 |
|---|---|
| `Blueprints/AnimBP/Climbing_System_Anim_BP.uasset` | 动画蓝图结构 |
| `Blueprints/BPI/LedgeClimbing_BPI.uasset` | 蓝图接口 |
| `Blueprints/Modifier/LedgeClimbingCameraModifier.uasset` | ★ **攀爬时的相机处理** |
| `Blueprints/AnimNotifies/Ledge_AnimNotify.uasset` | ★ **动画通知**（决定何时切状态）|
| `Blueprints/Functions/LedgeClimbingSoundFunctions.uasset` | 音效触发点 |
| `Blueprints/Functions/LedgeClimbingParticleFunctions.uasset` | 粒子触发点 |
| `Blueprints/PhysicsAsset/ALS/LedgeClimbing_PhysicsAsset.uasset` | ★ **刚体配置** |
| `Audio/{Default,Jump,Metal,Rock,Wood}/` | ★ **按材质分类的音效**（5 套）|
| `Particles/Foot/*`, `Particles/Hand/*` | 手脚粒子（Dust/Ice 各一套）|
| `Meshes/Geometry/Grid_1~4` | ★ **网格辅助体**（调试用）|

★ **`LedgeClimbingCameraModifier` 值得单独研究** ——
我们之前刚踩过「相机穿墙」的坑（MEMORY 第四节），这里作者也用了同样的机制。

★ **`Audio/{Default,Jump,Metal,Rock,Wood}` 按材质分音效** ——
我们的球撞墙目前没区分材质，这个分类法可以直接抄。

---

## 六、A 方案判定：**成立，但要做取舍**

### ★ 结论：A 成立。可以照着它的功能做 Unity 版。

因为拿到的信息已经足够：

| A 的判定要求 | 拿到了吗 | 来源 |
|---|---|---|
| 有哪几个状态 | ✅ **14 个** | 蓝图节点树 + 动画目录 |
| 进入条件阈值 | ⚠️ **拿到参数「名称」和「作者的说明」，没拿到数值** | tooltip 原文 |
| 状态转移图 | ✅ **完整** | `On_Ledge_Graph` 的折叠块结构 |
| 用了哪些物理能力 | ✅ `Check_Capsule_Hit`/`Update_Transform`/`PredictProjectilePath` | 函数名 |

### ★★ 必须坦诚的一点：**数值拿不到**

.uasset 里的 float 是裸IEEE754 二进制，**没有字段偏移表**，
无法确定「哪个 float 对应哪个变量」。

→ 我们能知道作者有`GrabDirectOffset`、`ShimmySpeed`、`CapsuleRadius` 这些量，
→ **但不知道它们的值**（唯一例外是 tooltip 里作者手写举例的 Radius 30/10、Offset 22/42）。

★ **这不是缺陷**。这些阈值本来就应该按我们项目的尺度调
（我们是 5 格 = 1 米的 Z-up 世界，他们的是厘米单位的 Y-up 人形角色）。
**照抄数值反而是错的。**

---

## 七、我们的实现取舍（球体版）

### 第一版只做（今天的范围）
| 做 | 说明 |
|---|---|
| `Idle` | 球贴墙静止 |
| `ClimbUp` / `ClimbDown` | 沿墙上下 |
| `Shimmy` | 沿边横移 |
| `Mantle` | 翻上平台 |
| `Grab` / `Release` | 抓/松 |
| 边缘检测 | `SphereCast`/`BoxCast` 打边缘 + 法线判定 |
| 胶囊空间检查 | ★ 改成球体空间检查（`OverlapSphere`）|

### 直接砍掉
| 砍 | 理由 |
|---|---|
| **Freehang 全套** | 球没有手臂，悬挂无从表现 |
| `l` / `r` 左右手区分 | 球无左右 → **动画需求减半** |
| `PreDelay` / `PostDelay` | UE 动画与位移解耦的产物，我们代码驱动位移 → **不需要** |
| `Corner` 转角 | 复杂，第二版再说 |
| `SwitchSide` 换手 | Freehang 专属 |
| `External In/Out` | 我们没有其它系统要衔接 |
| 音效 / 粒子 | 等手感对了再加分 |

### 待定的关键问题
**球怎么表现「攀爬」？** MEMORY 已记录：球是 `PrimitiveType.Sphere`，无骨架。
方案 A（抽象表现）下最自然的做法：
- 球贴墙时**沿墙面滚动**（用 `visualRadius` 算贴墙距离）
- 攀爬时球表面加一点「抓附」的视觉暗示（贴图或形变）
- 相机稍微拉近+ 侧移，让玩家看清球在做什么

★ 这不需要骨骼，**今天就能做完**。

---

## 八、工具记录

新增两个工具（都可复用）：
- `.workbuddy/tools/ue_asset_scan.py` —— 整包扫描，判断有没有 FBX 等通用资产
- `.workbuddy/tools/ue_asset_deep.py` —— 单文件深挖，滤掉引擎噪音后抽
  状态名/参数名/函数名 + tooltip 说明

★ 两个工具都做了变异测试（人造仿真 uasset），抓到 2 个真bug：
1. `for unit` 写成 `forunit` → SyntaxError
2. UE name table 的 hash 尾巴被当路径（`BP_AlsCharacter` → `BP_AlsCharacterD3`），
   正则阈值 `{3,}` 漏掉 2 位的 hash → 改成 `{2,}`
★ **这是「检查条件过窄 = 掩盖问题」的第 N 次例证**（见 MEMORY 第九节）。

---

## 九、下一步

1. 用户确认方案 A（抽象表现，无骨骼）
2. 建测试关卡：一面带边缘的墙 + 地面
3. 按上面「第一版只做」清单逐步实现
4. 一次一个特性，每个特性配独立测试关卡（用户既定迭代循环）