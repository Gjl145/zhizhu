# spider_ik 参考实现分析 —— PhilS94/Unity-Procedural-IK-Wall-Walking-Spider

> 学习日期：2026-10-09
> 来源：`Docs/参考代码/spider_ik/`（git clone，3233 行 / 14 个 cs，MIT 协议，Asset Store 有新版）
> 性质：**Unity 2022 可直接对照的真实商业级程序化 IK 蜘蛛**
> ★ 价值等级：**A 类（直接可用）** —— 与 Locomotor 视频（UE5）互为印证，
>   但这个能**逐行对照**，因为引擎相同、算法同类。

---

## 一、为什么这份代码比视频更有价值

| | Locomotor 视频（UE5） | 本仓库（Unity） |
|---|---|---|
| 能看到的 | 参数面板数值、结果画面 | **全部源码** |
| 步态 | 商业插件黑盒 | `IKStepManager` 250 行明文 |
| IK 算法 | 只说是 FABRIK | `IKSolver` 315 行，有 CCD 完整实现 |
| 能否抄 | 不能（UE5 专有） | 思路可移植 |

★ 且它的 `StepMode` 枚举第一个值就是 **`AlternatingTetrapodGait`**，
**独立印证**了我从论文（Wiley 2019）推导的交替四足步态是对的。
两条独立来源指向同一结论 → 这不是我在自创。

---

## 二、六个我完全没做的机制（★ 本轮重构依据）

### ★★★ 1. 站位锚点 `defaultPositionLocal` —— **这就是「移动两步就需要重来」的病根**

参考代码把「这条腿应该待在哪」定义成**身体局部坐标里的一个固定锚点**：

```csharp
// IKStepper.Awake()
defaultPositionLocal = calculateDefault();
```

迈步时的完整逻辑（`calculateDesiredPosition`）：
```csharp
Vector3 start = ProjectOnPlane(endeffectorPosition, normal);   // 当前足端（投影到水平面）
return start + (defaultPosition - start) * defaultOvershootMultiplier;  // ★ 向锚点方向过冲 1.5 倍
```

然后：
```csharp
prediction = desiredPosition + endEffectorVelocity * stepTime;  // ★ 再加速度预测
TargetInfo newTarget = findTargetOnSurface();                  // ★ 打射线落到真实表面
```

**★ 我原来的做法（错的）**：
```csharp
bool outOfRange = distFromLocked > forceStepDistance;   // 单阈值
Vector3 projected = foot + predictVel;                  // 只加速度预测
```
我只在「离旧落点太远」时才换点，**但没有「腿应该回到哪里」的概念**。
→ 一旦某条腿被身体甩开，它的旧落点就永远在身后，越走越远，
  每帧都满足 `outOfRange` → **无限连续迈步** = 用户看到的「移动两步就需要重来」。

**正确解法（三段式，与参考代码一致）**：
```
1. 站位锚点 default（身体局部坐标，随身体走）
2. 过冲：start + (default - start) × 1.5     ← 总是朝锚点方向迈，不是被甩开就追
3. 速度预测：+ endEffectorVelocity × stepTime
4. 射线落地：findTargetOnSurface()          ← 必须在表面上
```

### ★★★ 2. 最小距离强制换点 `minDistance = 0.2 × chainLength`

```csharp
minDistance = 0.2f * chainLength;
...
else if (Vector3.Distance(rootJoint.getRotationPoint(), target.position) < minDistance)
    return true;   // ★ 腿收得太拢也强制换点
```

**★ 我完全没有这个判断。**
腿收拢时（爬窄缝、贴墙）足端会怼到腿根，FABRIK 在这种姿态下
要么拉直、要么抖动。**这是另一个独立的「步态崩掉」触发条件。**

### ★★★ 3. 步时与速度成反比（不是固定周期）

```csharp
// IKStepManager.calculateStepTime()
float k = stepTimePerVelocity * spider.getScale();
return velocityMagnitude == 0 ? maxStepTime
                              : Mathf.Clamp(k / velocityMagnitude, 0, maxStepTime);
```

★ **步时 ∝ 1/速度**。走得越快，步频越高 → 步幅保持恒定。

**★ 我用的是固定 `cycleTime = 0.9f`** —— 这是**严重错误**：
- 慢走时→ 每步跨得太远（脚够不到，出现「劈叉」）
- 快跑时 → 每步跨得太近（脚在地上拖，出现「滑步」）
→ **无论调cycleTime 怎么取值都不对**，因为它必须是速度的函数。

参考代码还给了 clamp 上限 `maxStepTime`，防止速度趋零时步时爆炸。

### ★★ 4. 分级多方向射线（12 条，有严格优先级）

参考代码 `updateCasts()` 建 12 条射线，分两组各 6 条，
**字典顺序 = 优先级**（注释原文："The order in which they appear in the dictionary is the order in which they will be casted. This order is of very high importance"）：

| 优先级 | 名字 | 作用 |
|---|---|---|
| 1 | Prediction Frontal | 从预测点朝前打（防踩空） |
| 2 | Prediction Out | 从预测点朝外打 |
| 3 | Prediction Down | 从预测点朝下打（主路径） |
| 4 | Prediction In Far | 朝身体中心远端 |
| 5 | Prediction In Mid | 朝身体中心中段 |
| 6 | Prediction In Close | 朝身体中心近端 |
| 7–12 | Default * | 同上但从站位锚点打（兜底） |

★ 还有**坡度过滤**：`Frontal` 射线只接受 `±65°` 以内的坡度，
太陡的坡不算落脚点（否则腿会插进悬崖）。

**★ 我只有一条射线。** 单条射线打不到 → 我的代码直接
`Phase = dutyFactor * 0.5f` 把相位压回支撑相 → **这条腿卡死不动**。

### ★★ 5. 站立时停止迈步

```csharp
[Range(0.0f, 2.0f)] public float stopSteppingAfterSecondsStill;
...
if (timeStandingStill > stopSteppingAfterSecondsStill) return false;  // stepCheck 里
```
注释原文："This fixes the indefinite stepping going on."

★ 我只有 `scaleLiftBySpeed`（静止不抬腿），
但**静止时相位仍在推进** → 原地踏步。

### ★ 6. IK 求解的两个保护

**a) 奇异半径 `singularityRadius`**（`IKSolver.cs`）
```csharp
if (toTarget.magnitude < singularityRadius) return;  // ★ 目标太靠近旋转轴 → 跳过该关节
```
当目标与关节旋转轴几乎重合时，`SignedAngle` 的解不唯一 → 关节会乱转。
**★ 这正是我在 `SilkSpiderLimb` 里遇到的「腿随机翻转」的同类问题**，
我当时用 `StableRotation` 兜住了症状，但根因（奇异）没处理。

**b) 最小变化量 `minimumChangePerIteration`**
```csharp
errorDelta = Mathf.Abs(oldError - error);
if (errorDelta < minimumChangePerIteration) break;   // ★ 误差不再下降 → 放弃
```
我只有 `TOL`，没有「不再改善就停」→ 会在解不出来时反复迭代。

**c) CCD 的关节顺序**：从末端往根解
```csharp
int k = mod((i - 1), joints.Length);   // ★ 从最后一节开始，倒序
```
★ CCD 必须倒序（末端影响最大，先定末端），正序会收敛慢且易抖。
我用的 FABRIK 是同时反向+正向，天然没这个问题。

### ★ 7. 足端朝向跟随表面法线

```csharp
// IKSolver: 最后���关节专门对齐命中点的法线
angle = footAngleToNormal + 90.0f
       - Vector3.SignedAngle(ProjectOnPlane(target.normal, rotAxis), toEnd, rotAxis);
```
`footAngleToNormal = 20°` —— 足端不完全垂直于法线，
留20° 倾斜，**符合 Locomotor 视频的 `Orient Foot to Ground Pitch 0.8`**。
★ 我完全没做足端朝向控制 → 脚是「插进去的」，不是「踩上去的」。

---

## 三、其他值得记的细节

| 项 | 参考值 | 说明 |
|---|---|---|
| `maxIterations` | 10 | CCD 迭代上限（我 FABRIK 用 6）|
| `footAngleToNormal` | 20° | 足端与法线夹角 |
| `defaultOvershootMultiplier` | 1.5 | 向锚点过冲倍数 |
| `minDistance` | 0.2 × 链长 | 强制换点的近距阈值 |
| `stepTimePerVelocity` | 可调 | 速度→步时的比例系数 |
| `tolerance` | 随 `lossyScale` 缩放 | ★ **所有阈值都应随缩放走，不能写死** |
| 求解触发 | `hasMovementOccuredSinceLastSolve()` | ★ **只在误差变化时才重解**，省性能 |
| 执行顺序 | `[DefaultExecutionOrder(+1)]` | ★ 步态必须在 IK 之后跑 |

★ **`tolerance` 随缩放缩放**这一条印证了我MEMORY 里的
「由别的参数推导出来的量必须写成公式」。

---

## 四、结论：本轮重构清单

| 优先级 | 机制 | 解决什么 |
|---|---|---|
| P0 | 站位锚点 + 过冲 | ★ **「移动两步就需要重来」** |
| P0 | 步时 ∝ 1/速度 | ★ 慢走劈叉 / 快跑滑步 |
| P1 | 最小距离强制换点 | 腿收拢时步态崩掉 |
| P1 | 站立停止迈步 | 原地踏步 |
| P1 | 分级多方向射线 | 单射线打不到 → 腿卡死 |
| P2 | 奇异半径 | 腿随机翻转（症状已兜住，根因未解）|
| P2 | 足端朝向跟随法线 | 脚「插进去」而非「踩上去」|
| P2 | 最小变化量提前退出 | 无解时反复迭代 |

★ **不需要换 IK 算法**：FABRIK 与 CCD 各有优势，
FABRIK 的优点是**只解世界位置再转局部**，天然绕开父子旋转叠加
（CCD 需要 `JointHinge` 这类带旋转轴/限位的组件，工程量大得多）。
**保留 FABRIK，把这 6 个机制作为外围补上。**