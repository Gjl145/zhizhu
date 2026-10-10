# UE5.5 高级运动系统（GASP / ALS / Lyra）技术提炼

来源：B站 `BV1UkHB6CEMr`（Beardman，5:23，源文件分享系列）+ 交叉核实 GASP 内部实现

---

## 零、先说视频本身值不值

**视频本身：低价值。** 0:00–2:30 全是作者推销自己的资产包（367 个动画、FCS 战斗框架、
一个月内把 GASP 塞进去、两个月内加游泳/攀爬）。技术信息只有三句话：

| 时间 | 内容 |
|---|---|
| 0:00–0:20 | Epic 目前没出 comparable 的东西；这套系统「运动基本是活生生的」 |
| 0:20–0:30 | Epic 做了两套：default和 Lyra 用的那套；5.5 起支持多人复制 |
| 0:30–1:35 | **选型判据**（见下） |

**但它提出的问题是真问题**，而且顺着「GASP 到底怎么做的」挖下去收获很大。

---

## 一、★ 选型判据（视频里唯一值钱的东西）

作者给的四条，本质是在问「你有多少动画资产」和「团队有多少动画能力」：

| 条件 | 选谁 | 代价 |
|---|---|---|
| 团队有动画师，懂曲线、懂混合，手里有**一堆姿势相近的动画** | **ALS** | 需要知识和时间，不是靠原始动画量|
| 只需要**一套**运动系统，或大团队能**批量产动画** | **GASP**（Motion Matching） | 动画数量爆炸 |
| 其他所有情况 | **状态机** | 简单、能跑，永远可以往上加攀爬和 IK |

> 原文：「ALS —— you need knowledge and time over raw animation data.」
> 「GASP —— if you can afford to mass produce animations.」
> 「State machines —— they work, they're simple.」

### 三者的本质区别

- **ALS / Lyra：状态机 + 程序化扭曲**。有一份有限的状态集（Idle/Walk/Run…），
  方向差异靠 BlendSpace 或 Warping 补。**动画是「离散 + 预定义」的**。
- **GASP：Motion Matching**。不播「Run 状态」，而是在**每一帧**从动画数据库里检索
  「与当前速度、朝向、轨迹最匹配的那一帧」播放。因为库里总有最合适的那帧，
  所以任意速度、任意角度都能平滑接上。

GASP 的检索栈：

```
Animation Database（上百个片段，每帧预先提取特征）
  ↓特征向量 = 脚部位置 / 速度 / 朝向 / 加速度 / 运动相位
加权欧氏距离
  ↓ K-D 树 / ANN 加速
最匹配的那一帧
```

★ **运动相位必须作为特征输入**，否则会匹配到「腿脚位置相同但左右脚相反」的帧。

---

## 二、★ 六个可借鉴的具体技术（按价值排序）

### 1. Rotation Offset（Yaw Offset）—— ★★★ 最高价值

**思路：能用原动画就用原动画，程序化扭曲只补小角度。**

做法：算「移动方向与期望朝向的夹角」，按扇区查曲线拿一个偏置角。
例：期望朝向与运动方向差 +135°，当前在B（后）扇区 → 采样得到 −45° 偏置。

为什么有效：

| 方案 | 结果 |
|---|---|
| 角度差 180°（正好与原动画一致） | 偏置 = 0，**直接播原动画** |
| 角度差 45°（小角度） | Orientation Warping 会把身体扭得很难看 → 改用曲线偏置 |

作者实测：强行让所有扇区都吃偏置，45° 小角度下反而出现「胸口和头朝正前方，
但身体朝斜前方移动」的怪异感。所以**偏置和 Warping 是互补的，不是叠加的**：

```
角度差大 → Rotation Offset（用原动画，动画师K 好的姿势大概率更好）
角度差小 → Orientation Warping（扭曲幅度小，可接受）
```

GASP 里Rotation Offset 的曲线还有 `AngleThreshold = 135°`：
超过 135° 会被反转成「向背面 Warp 45°」造成错误，所以要卡阈值。

### 2. 分扇区 + Dead Zone —— ★★★ 直接对应多足步态

**问题：BlendSpace 混合时脚步交叉打绕（leg crossing）。**
本来向右走是左脚在前，切回向后是右脚在前，直接融合腿就穿了。

**解法：分扇区 + Dead Zone。**

```
把 360° 切成 4 或 6 个扇区（F / B / L / R或 F/B/LL/LR/RR/RL）
  ↓ 每个扇区内的动画，都保证「同一只脚在前」
  ↓ 每个扇区的范围适度扩大，形成 Dead Zone
  ↓ 结果：扇区内混合时前后脚关系不变 → 不打绕
```

命名规则值得抄：`RL` = **朝右移动，胸口朝左**（第一个字母是移动方向，第二个是朝向）。
和 ALS 的 `RF`/`RB` 一个思路。

最新版本还开放了实验性开关：Aim 和 Strafe 可以分别选扇区风格（4 扇区 / 2 扇区 / 1 扇区）。
在 F/B 模式下 FL和 BL 值相同，就根本不会进 L 扇区。

### 3. 衔接动画的分类学 —— ★★

GASP 里 Run 的衔接动画密密麻麻，靠 Motion Matching + Chooser 选。类型定义：

| 类型 | 朝向变化 | 运动方向变化 | 轨迹 |
|---|---|---|---|
| **Spin** | 变 | 不变 | 原地转 |
| **Box** | 不变 | 变 90° | L 形 |
| **Pivot** | 不变 | 变 180° | — |
| **Turn** | 变 | 变 | 两者始终一致 |
| **Switch** | 不变 | 不变 | 胸部朝向左右切|

### 4. 三个参数决定选哪个衔接动画 —— ★★★ 极精妙

状态机状态（StateMachineState）只能告诉你「正在转入 Locomotion」，
不足以区分上面几十个动画。真正区分靠**三个基于未来轨迹预测的参数**：

| 参数 | 含义 | 区分什么 |
|---|---|---|
| **FutureFacingDelta** | 当前 Root 朝向 → 期望朝向的差值（**预测未来**） | 朝向变没变（Spin/Turn vs Switch/Box/Pivot）；转向角度、左右 |
| **IsPivoting** | 未来轨迹与当前运动方向的夹角 > Ground（75°）且有速度 | 运动方向变没变 |
| **MovementDirectionRecent** | 上一个**稳定保持 0.1s 以上**的扇区 | 两者都不变时怎么区分（Switch 等） |

`MovementDirectionRecent` 的「0.1 秒去抖」是关键：
快速来回抖动时Recent 不更新 → 选不出衔接动画 → **跳过衔接直接回Loop**，
避免「明明没转向却播了一段转身动画」。

### 5. Foot Lock / Foot Placement —— ★★

所有调整旋转/位移的做法都会产生滑步。解法：**脚落地时锁住它**，不让它随整体旋转滑动。

实现：给动画打曲线标记记录哪个时刻脚步落下，在对应时刻把脚钉住。
GASP 实现在 **ControlRig** 里（不是蓝图）。

### 6. ★★ Steering 节点（缩放动画旋转 +弹簧补偿）

问题：只有一个 90° 的转身动画，怎么转到 60°？

```
预测实际需要转的角度 / 动画自身转的角度 → 缩放系数
  例：动画 90°，实际要转 60° → 每帧只应用 66.7% 的旋转量
  ↓
Max/Min ScaleRate 限幅（否则会算出 3 倍这种荒谬值）
  ↓
弹簧平滑补偿残差
```

★ **实测数字很有说服力**：
预测时长设2s，动画 3s 转 90°（前 2s 转 20°，最后 1s 转 70°，先慢后快）。

- 预测到动画转 20°，实际要转 60° → 理论缩放 3 倍
- 但Max ScaleRate 限到 1.5 倍 → 前 2s 转了 30°
- 后 1s 缩到 0.5 倍限幅 → 转了 35°
- **最终转了 65°，期望60°，偏差 5°**

→ 和 Distance Matching 的 AdvanceTime 是同一个性质的偏差，**所以必须配 Foot Lock 兜底**。

还有个细节：预测时长不够长 → 缩放不均匀 → 破坏动画原本的旋转节奏。
所以 `AnimatedTargetTime` 要给够。

### 7. Distance Matching（距离匹配）—— ★★

**用位移驱动播放进度，而不是用时间。**

| 节点 | 行为 | 代价 |
|---|---|---|
| `DistanceMatchToTarget` | 直接跳到合适位置 | **丢失前面的动画** |
| `AdvanceTime` | 先播，但调播放速度让剩余距离/角度追上 | 有 PlayRateClamp → 残差 → 滑步 |

两者对比的实测结论：

- **动画位移 > 实际位移**：ToTarget 丢前段动画；AdvanceTime 先**加速**动画，
  缩小动画剩余位移去追实际剩余位移。
- **动画位移 < 实际位移**：ToTarget 丢前段动画；AdvanceTime 先**减速**等实际位移追上来。

想要「从头播 + 对上角度」→ 用 AdvanceTime；但限幅必然产生偏差 → 配 Foot Lock。

---

## 三、三个方案的转身实现对比

| | ALS | Lyra | GASP |
|---|---|---|---|
| Actor 朝向 | 不用 CMC 的 `ControllerDesiredRotation`/`RotationToMovement` | **始终跟随 ControlRotation** | 由输入的 OrientationIntent 决定 |
| 触发条件 | Control 与 Actor 夹角超阈值（Strafe 还要求保持一段时间） | — | Aiming 下超 60° 才更新 OrientationIntent；`FutureFacingDelta` > 50° 触发 Turn |
| 怎么转 | 每帧旋转量存进动画曲线，按实际角度调播放速度并缩放曲线值 → **一个动画对应多个角度** | 暴力直接播 90° 动画，转完用 AO 对齐目标 | **Steering 节点**缩放+ 校正 |
| Mesh 怎么不跟着转| — | ABP 里算差值**反向应用到 Root** | **`OffsetRootBone`** 节点，Accumulate 模式 |
| 已知缺陷 | 低帧率时会跳过曲线点导致旋转不够；强行按差值倍数缩放 → 最终偏差 | 不适配不同角度 | Trigger 角度与 Intent 更新角度不匹配时会「滑过去永不触发 Turn」 |

**Lyra 那个「反向应用差值给 Root」的做法被GASP 继承并封装了**——
这正是记忆里那条相机穿墙陷阱的同源思路：**父级动了而子级要保持不动时，
要把子级反向补偿，而不是让子级跟着动**。

---

## 四、★ 行业量级数据（可直接用的数字）

### 1. PlayRate / Stride Warping 调整上限

> 「industry standard of playrate/stride warp adjustments is **15-20% max** increase/decrease.
> Values beyond that result in **significant visual quality drops**,
> and it's often better to allow foot sliding.」

**超过 ±20% 就该放手，宁可允许滑步。**

### 2. Pose Warping = 三者之和

```
Pose Warping = Stride Warping + Orientation Warping + Slope Warping
```

- **Stride Warping**：调脚步 IK + 盆骨位置，让步幅匹配速度（不是调播放速度）
- **Orientation Warping**：调腰骨/脊柱朝向，下半身朝运动方向、上半身不变
- **Slope Warping**：按地面倾角调腰与脚 IK。**⚠️ 不适用于楼梯**，楼梯要传统 FootIK 或 FBIK

★ **Slope Warping 的分级策略**（低成本/高成本权衡）：

| 场景 | 用什么 |
|---|---|
| 移动中、或相机远（不明显） | Slope Warping（低成本） |
| 停下、或相机近（很明显） | 标准 Foot IK（高成本） |

### 3. 朝向阈值

| 参数 | 值 |
|---|---|
| Orientation Warping `AngleThreshold` | 135° |
| Turn In Place 触发 `FutureFacingDelta` | 50° |
| Aiming 下更新 OrientationIntent | 60° |
| `IsPivoting` 的 Ground 角度 | 75° |
| `MovementDirectionRecent` 去抖窗口 | 0.1s |
| Idle → Turn 融合时间 | 0.4s（会导致转角不足，关闭才对） |

### 4. 动画资产量级

| 系统 | 量级 |
|---|---|
| GASP | 上百个片段（有人统计为 **367 个**，Beardman 的 FCS） |
| 状态机路线 | 几十个 |

---

## 五、Foot IK 权重淡入淡出

不是全程开IK：

```
低速时淡入IK（精度重要）→ 高速时淡出（脚本来就离地，看不出来）
```

配合 Full Body IK：脚部 effector 由射线打地形驱动，身体 effector 维持在两脚平均高度之上。

---

## 六、Lyra 的动画分层架构（值得单独抄）

Lyra 用 **Animation Linked Layers + 共享 Animation Linked Interface**，
把「动画逻辑」和「动画资产」彻底解耦，四个角色：

| 组件 | 职责 | 含动画资产？ |
|---|---|---|
| **Animation Linked Interface** | 共享协议，定义函数契约 | — |
| **Animation Blueprint** | 决定当前该是什么状态、在哪个虚拟挂点取 pose | **❌ 一个都没有** |
| **Animation Linked Layer Base** | 实现每个 ALI 函数，动画资产是变量 | **❌ 全是变量** |
| **Animation Linked Layer** | 实际注入的资产容器（继承 Base，无需写逻辑） | ✅ 全部在这 |

**收益**：加 20 种武器，动画蓝图不加载任何资产；不用重复逻辑；好调试；团队协作不冲突。

Locomotion 本身：状态机 + distance matching + stride/orientation warping + turn-in-place
+ 逐骨骼 IK 修正，通过 `BlueprintThreadsafeUpdateAnimation` 线程安全驱动。

---

## 七、Lyra 官方迁移文章里的一条判断

把 Lyra 从 strafing改成 forward-facing 时遇到三个问题：play rate、snapping、rotation speed。
官方的处理顺序值得学：**先修 play rate → 再修 snapping → 最后修 rotation speed**。

以及一句重要的现实主义判断：

> 「It is not uncommon to allow some foot sliding…
> often it's better to allow foot sliding.」

**允许少量滑步是行业常态，不是 bug。**