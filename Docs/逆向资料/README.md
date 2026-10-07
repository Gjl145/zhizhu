# 官方技术资料 —— 已验证可访问

## ★ 核心资料：Insomniac《Marvel's Spider-Man》移动系统

| 项 | 内容 |
|---|---|
| **标题** | Concrete Jungle Gym: Building Traversal in 'Marvel's Spider-Man' |
| **演讲者** | Doug Sheahan（Lead Gameplay Programmer, Insomniac）|
| **会议** | **GDC 2019**（不是 2024 —— 之前的记录有误）|
| **页数** | **264 页** |
| **本地副本** | `Docs/逆向资料/Insomniac_ConcreteJungleGym_GDC2019.pdf`（11.9 MB）|
| **全文提取** | `Docs/逆向资料/Insomniac_ConcreteJungleGym_全文.txt` |
| **官方 URL** | `https://media.gdcvault.com/gdc2019/presentations/Sheahan_Doug_ConcreteJungleGym.pdf` |

### ⚠️ 关于链接的重要更正

网络上流传的 `ubm-twvideo01.s3.amazonaws.com/...` 旧域名**已失效（403）**，
但搜索引擎仍缓存其全文索引，容易误判为「找不到资料」。

**只有 `media.gdcvault.com` 有效。**

规律：`https://media.gdcvault.com/gdc{年份}/presentations/{Lastname}_{Firstname}_{Title}.pdf`

GDC Vault 的播放页（`gdcvault.com/play/1026084/`）**HTML 里没有 Slides 链接**，
必须按上述规律反推 CDN 路径。

---

## 已核实的一手技术内容

以下全部来自官方 PDF，标注了页码。

### 一、摆荡的力学模型（P68–71）

> P68: "The two basic forces involved in the pendulum are gravity and tension."
> "tension is a factor of line length, angle, and gravity in the direction of the line"

> P69: "We also break gravity up into it's two component vectors here,
>      one parallel to the line, one perpendicular"
> "The **perpendicular portion of gravity represents the restoring force**.
>  This is the part that causes the pendulum to oscillate back and forth"

> P70: "When actually calculating this in code, we **set mass to one** for simplicity
>      as we aren't dealing with variable mass systems."
> "After a bit of simplification, we apply the remaining forces of tension and
>  perpendicular gravity to our velocity"

> **P71: "In order to increase the accuracy of the simulation,
>        we do four iterations each frame to run at a total of 120Hz"**

> **P71 BONUS（关键实现细节）：**
> "In actual implementation, we apply these forces **only to the portion of
>  velocity that are tangent to the line**. We then apply **full gravity to the
>  remaining velocity** and recombine for a final velocity."

#### 可直接照抄的公式

```
张力      T = m·v² / L          （m 设为 1）
重力分解  G_para = Project(G, L)   沿绳方向
          G_perp = G − G_para     垂直绳方向（恢复力）
施力对象  只施加速度**切向分量**；其余速度接受完整重力
迭代      每帧 4 次迭代 → 总计 120 Hz
```

### 二、锚点选择（P22、P59–60）

> **P22（为什么放弃射线检测）：**
> "The biggest issue is that **ray casts simply did not provide enough resolution**.
>  Our line lengths would often **exceed 50m** and even with a respectable density
>  of ray casts we were getting **20m square gaps at full range**."

> **P59–60（评分机制）：**
> "With all of our individual scores calculated we then do a **weighted sum**
>  and the **highest score wins**."
> "Using a weighting scheme on the **normalized element scores** helped us to
>  quickly adjust one elements influence versus another's
>  without needing to mess with the individual elements."

#### 评分结构（可直接照抄的架构）

```
每个评分项先归一化到 [0,1]
    ↓
加权求和（权重可独立调整）
    ↓
取得分最高的点
```
★ 权重具体数值**未公开**，但**「归一化 + 加权」这个架构**是可照抄的。

### 三、速度管理（P80、P84–88）

> **P80（入摆时的速度混合）：**
> "To improve, we **blend the incoming velocity towards the tangent direction
>  of the swing arc a little bit each iteration**.
>  This helps maintain healthier line lengths and improves expected behavior
>  in angular velocity."

> **P84：**
> "the two biggest influences on the speed for any given swing will be
>  the **amount of speed the hero brings in** and the **gravity applied**"

> **P86–87（水平终端速度）：**
> "We start be deciding what our **horizontal terminal velocity** is for any given swing.
>  As a baseline, this is done by **translating fall speed into max speed
>  while never letting it slow you down**."
> "Then we let **normal swing physics accelerate you up to that max speed**."

> **P88（只限制水平速度）：**
> "To enforce the terminal velocity, we **only cap the hero's horizontal speed**.
>  This can have an odd side effect where you can actually slow down in 3D
>  through the downswing but it helps you get through a long swing arc much faster"

### 四、流式加载约束（P85）

> "Due to streaming considerations we also need to stay below an
>  **average speed of 30 m/s** to avoid loading stalls."

★ 这是公开的**硬性数字**。

---

## 三款游戏的参数公开情况（已逐一核查）

| 游戏 | 状态 |
|---|---|
| **Insomniac 蜘蛛侠** | ★★★ **有 264 页官方幻灯片**，见上|
| Treyarch 蜘蛛侠2（2004）| 只有 3 个数字（重力 10 倍、跳 5 层、射线选点）。**阻尼/绳长/松弛全部未公开** |
| 镜之边缘（DICE）| **DICE 从未公开任何参数** |
| 刺客信条（Ubisoft）| **Ubisoft 从未发表过 AnvilNext 移动系统的技术资料** |
| Rocket League | 有官方幻灯片（见下）|

---

## 第二份已验证的官方资料：Rocket League 载具物理

| 项 | 内容 |
|---|---|
| 标题 | It Is Rocket!|
| 演讲者 | Jared Cone, Psyonix |
| 会议 | GDC 2018 |
| URL | `https://media.gdcvault.com/gdc2018/presentations/Cone_Jared_It_Is_Rocket.pdf` |
| 页数 | 182 |

明确的简化摩擦模型：

```
Ratio         = SideSpeed / (SideSpeed + ForwardSpeed)
SlideFriction = Curve(Ratio)
GroundFriction = Curve(GroundNormal.Z)
Friction      = SlideFriction * GroundFriction
Impulse       = Constraint * Friction
```
物理引擎：Bullet，120 Hz 固定 tick，60 Hz 逻辑步。

---

## 未找到的（别再花时间）

| 项目 | 状态 |
|---|---|
| Treyarch 演讲幻灯片 PDF |试了 15 种命名变体全 403，**不存在公开 slides** |
| Insomniac 各评分项的具体权重 | 演讲说「custom define」但未给值 |
| Insomniac 理想坡度/理想线长的角度与米数 | 只说概念，未给数值 |
| Insomniac FOV / 相机跟随距离曲线 | 只定性说「随速度提升」 |
| 镜之边缘全部物理参数 | **DICE 从未公开** |
| 刺客信条全部移动系统参数 | **Ubisoft 从未公开** |
| Unity 官方的绳索推荐参数 | **Unity 从不给推荐数值**（只有属性语义）|

### Box2D 官方推荐参数（另一个引擎，但有明确数值）

来源：Box2D 官方 RopeJoint 源码（v2.4.0 tag）
⚠️ 注意 **RopeJoint 在 Box2D v3 已被移除**，只存在于 2.4.1

```
hertz        = 2.0f        （频率）
dampingRatio = 0.5f        （阻尼比）
                           dampingRatio = 1.0 = 临界阻尼，完全不振荡
硬约束      hertz < 时间步频率 / 2   （Nyquist，60Hz 步长则 hertz < 30）
子步数推荐   4            原文：「long joint chains will stretch less
                            with more sub-steps」
            示例 8 子步 @ 1/60s = 480 Hz
时间步       1/30 s 或更小，1/60 s 为高质量
```

### 「绳索缓慢下沉」的官方解释

不是 bug，是三处容差叠加：

1. **Jolt 官方**：「we integrate physics using an explicit Euler scheme,
   **there is always energy loss**」
2. **Box2D 源码**：剩余误差 < `b2_linearSlop = 5mm` 时，
   求解器**主动停止修正并返回成功**
3. **Box2D**：每帧最多修正 `b2_maxLinearCorrection = 0.2m`，长链收敛慢

### Unity 内置 PhysX 没有子步进

想要子步只能 `Simulation Mode = Script` + 手动多次 `Physics.Simulate`。
⚠️ 官方警告：子步进下**每帧写 velocity 会得到非预期结果**，
应改用 `AddForce`（Box2D 原文：「velocity adjustments no longer exist
after the first sub-step」）。

---

## 明确排除的不可信来源

搜索中出现的以下内容**全部未采纳**：

- thegadgetdigest 的「Super Mario Galaxy GravityEngine 技术分析」
  （含「187.6m」「3.2ms」「0.25×–3.0×」等看似精确的数字）—— **AI 生成的 SEO 垃圾**
- mycplus.com / vectree.io 关于 AnvilNext 的描述 —— **AI 生成的 SEO 内容**
- csdn / 博客园里的「Unity 绳索推荐 spring=50 damper=100」—— **内容农场，无出处**
- GDC 官方幻灯片的旧 S3 域名 —— **已失效，但搜索引擎仍缓存索引**

---

## 尚未探索的 Vault ID（可能还有资料）

| Vault ID | 标题 |
|---|---|
| 1035867 | Grappling with Success |
| 1034283 | Higher Faster Farther |
| 1025208 | Parkour: How to Improve Freedom |
| 1029003 | Companion Traversal in God of War |
| 1019687 | Evolution of Sonic Dashing |

按 CDN 规律 `https://media.gdcvault.com/gdc{年}/presentations/{Lastname}_{Firstname}_{Title}.pdf` 尝试。
---

# 第二轮：批量发现的官方资料（2026-07-07 补充）

## ★★ 正确的发现方法（比猜文件名可靠得多）

**GDC Vault 播放页的媒体代理端点无需登录，会 302 到真实文件：**
```
https://www.gdcvault.com/play/mediaProxy.php?sid={PLAY_ID}
```
用 play ID 直接提取 PDF 直链，比按命名规律猜测靠谱。

### ⚠️ 命名规律其实是错的
`{Lastname}_{Firstname}_{Title}.pdf` 大部分不成立。实测 1310 个变体只命中1 个已知文件。

### CDN 路径目录名不统一（踩过的坑）
| 年份 | 目录 |
|---|---|
| 2019 | `presentations/` |
| 2016–17 | `Presentations/` |
| 2008–12 | `slides/` |
| 2022 | `GDC+2022/Speaker+Slides/` |
| 2023–26 | `Slides/` 或 `Slides/GDC+slide+presentations/` |

### S3 的错误行为
对「文件不存在」返回 **403 AccessDenied**（不是 404），
所以**不能靠 403 判断权限或存在性**。

---

## 已验证的 8 份官方资料（全部 HTTP 200 + `%PDF` 头）

| # | 演讲 | 演讲者 | 会议 | 页/大小 | 本地文件 |
|---|---|---|---|---|---|
| 1 | Concrete Jungle Gym: Building Traversal in Marvel's Spider-Man | Doug Sheahan, Insomniac | **GDC 2019** | 264 页 / 11.9 MB | `Insomniac_ConcreteJungleGym_GDC2019.pdf` |
| 2 ★ | **Higher, Faster, Farther: Evolving Traversal in Marvel's Spider-Man 2** | Doug Sheahan, Insomniac | **GDC 2024** | 62 页 / 7.2 MB | `SpiderMan2_GDC2024_HigherFasterFarther.pdf` |
| 3 ★ | **Building a Better Jump** | Kyle Pittman | **GDC 2016** | 2.4 MB | `BuildingABetterJump_GDC2016.pdf` |
| 4 ★ | **Free Reign: Building VFX for Player Agency in Just Cause 3**（抓钩）| Fred Hooper, Avalanche | **GDC 2016** | 10.5 MB | `JustCause3_GrappleHook_GDC2016.pdf` |
| 5 | Character Control with Animation | Daniel Holden, **Ubisoft** | GDC 2018 | 5.8 MB | `CharacterControlWithAnimation_Ubisoft_GDC2018.pdf` |
| 6 | Vault, Slide, Mantle（Brink）| Arne Olav Hallingstad, ZeniMax | GDC 2012 | 3.2 MB | `VaultSlideMantle_Brink_GDC2012.pdf` |
| 7 | Subway Surfers（无尽跑酷）| Celia Zimmermann, Kiloo | GDC 2023 | 6.1 MB | `SubwaySurfers_GDC2023.pdf` |
| 8 | Obstacle Traversal in the Organic World of Pandora | Joel Nilsson, Massive | GDC 2024 | 3.7 MB | `ObstacleTraversal_Pandora_GDC2024.pdf` |

另有一份**超大**（293 MB，含大量视频截图，未入库但可下）：
```
https://media.gdcvault.com/gdc2017/Presentations/vanGrinsven_Paul_PlayerTraversalMechanics.pdf
```
（Horizon: Zero Dawn 玩家移动机制，Guerrilla，293 页/293 MB）

---

## Spider-Man 2 (GDC 2024) 的气动公式 —— 已核实原文

来自 `SpiderMan2_GDC2024_HigherFasterFarther.pdf` P21–23：

```
升力:  F_L = C_L · r · (V²/2) · A
       C_L = 升力系数, r = 空气密度, V = 速度, A = 翼面积
       原文注：「Ignore air density and wing area as non-variable,
              bake into coefficient」

阻力:  F_D = C_D · r · (V²/2) · A
       C_D = 阻力系数, V = 相对运动速度, A = 迎风面积

重力:  F_G
推力:  F_T

加速度:
  A_L = F_L · D_L
  A_D = F_D · |-V|
  A_G = F_G · -U

积分:
  V_F = V_I + (A_L·dt) + (A_D·dt) + (A_G·dt)
  V_A = (V_F + V_I) / 2← 平均速度（梯形积分）
```

### 设计要点（P21/P26/P27/P32/P41 提到）
- **升力**产生前向速度、抵消下坠速度、可当**天然刹车**
- **重力**增加总速度
- 辅助功能设计：
  · **垂直减速制动** —— 快速降低下落速度 → 转为水平
  · **最低速度助推**
  · **近地面短时反重力泡**
  · 向开阔处微推 + 靠墙阻尼转向

⚠️ **具体的 C_L / C_D 数值未公开**，演讲只给了公式结构。

---

## 失败的探索（别再重复）

| 目标 | 结果 |
|---|---|
| Treyarch 蜘蛛侠2 (2004) 幻灯片 |试了 15 种命名变体全 403，**不存在公开 slides** |
| Ratchet & Clank hoop 摆荡 | 仅视频 |
| Apex Legends / Titanfall 2 跑墙 | 该主题**无演讲**；命中的都是纹理流送/服务器扩展话题 |
| Rainbow Six Siege（AnvilNext）| 无移动系统演讲 |
| Journey / Abzû / Sonic Frontiers | GDC Vault **无对应演讲** |
| Vault ID 1035867 / 1034283 / 1025208 / 1029003 / 1019687 | **只上架了视频，幻灯片未公开**（302 → blazestreaming） |

注：1034283「Higher, Faster, Farther」的**姊妹页 1034327** 幻灯片是公开的，
即上表第 2 项。

---

## 明确排除的不可信来源（AI 生成 / 内容农场）

- thegadgetdigest 的「Super Mario Galaxy GravityEngine 技术分析」
  （含「187.6m」「3.2ms」「0.25×–3.0×」等伪精确数字）—— **AI 生成 SEO 垃圾**
- mycplus.com / vectree.io 关于 AnvilNext 的描述 —— **AI 生成 SEO 内容**
  （含虚构细节如「AC Unity 有 1550 万行 C++」）
- csdn / 博客园的「Unity 绳索推荐 spring=50 damper=100」—— **内容农场，无出处**

**识别方法**：出现非常精确的数字却给不出处，优先怀疑 AI 生成。
