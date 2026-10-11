# 蜘蛛模型选型记录 —— Quaternius Spider（已入库，未接线）

> 日期：2026-10-11
> 用户原话：「蜘蛛选择你来决定，我事前说好蜘蛛是我设计的主角，所以最好是免费的越完整越好」「我没有国外的账号啊。你来吧，看看有没有替代品」

## 一、最终决定与理由

**选定：Quaternius「Spider」**（Animated Enemies 包，2022-07）
- 下载直链：`https://static.poly.pizza/list/a53OJwHrhh-zip--782929875.zip`（无需账号）
- **License：CC0 1.0** —— 无需署名、可商用、无任何限制。版权最干净。
- 作者：Quaternius（poly.pizza / quaternius.com，知名免费资产作者）

**为什么不是 Sketchfab 狼蛛（1 号候选）**：下载要登录账号，用户没有国外账号。
**为什么它反而更合适**：
1. CC0 比CC-BY 更干净（不用署名，主角模型零版权负担）
2. 自带 5 个动画（Walk/Attack/Death/Idle/Jump）——本项目程序化动画用不上，
   但将来做敌人（比如被蜘蛛击败的目标）可以直接复用
3. 骨骼命名规整（见下），接入工作量比预期小
4. Quaternius 是 Unity 社区验证多年的作者，FBX 干净

## 二、骨骼结构实测（Blender 5.2 无头导入，脚本 `Tools/blender/inspect_spider_fbx.py`）

### 总览
- armature `SpiderArmature`，**59 骨**，scale=1.0，尺寸 6.34(宽) × 8.875(长) × 4.332(高)
- 网格：单网格 `Cube`，1386 顶点 / 1380 三角面，带 ARMATURE 蒙皮修改器
- 动画 5 个：`Spider_Attack / Spider_Death / Spider_Idle / Spider_Jump / Spider_Walk`

### 8 条腿（每腿 3 节 + 1 端点骨 `_end`）
```
FrontLeg.L     → FrontLeg2.L     → FrontLeg3.L     → FrontLeg3.L_end
MidFrontLeg.L  → MidFrontLeg2.L  → MidFrontLeg3.L  → MidFrontLeg3.L_end
MidBackLeg.L   → MidBackLeg2.L   → MidBackLeg3.L   → MidBackLeg3.L_end
BackLeg.L      → BackLeg2.L      → BackLeg3.L      → BackLeg3.L_end
（.R 侧完全镜像）
```
腿根全部挂在 `Body` 下。左前腿根 head=(0.555, -0.305, 0.682)，足端 z≈0.02（贴地）。

### 身体链
```
Root → Body → Thorax → Head（+Head_end）
            → Abdomen（+Abdomen_end）
```

### 附赠骨骼（原 rig 的 IK 目标，parent=Root）
- `FrontFoot / MidFrontFoot / BackFoot`（×L/R，R 侧前腿叫 `FrontFoot2.R` ★ 命名不对称）
- `PoleTarget.L / PoleTarget.R`
- ★ 接入时这些**不用**——我们自己的 FABRIK 会驱动腿链骨骼

## 三、接入本项目要改的东西（★ 未动手，等诊断结果一起做）

| 项 | 现状 | 要改成 |
|---|---|---|
| 腿数 | `SilkSpiderLegs.Count=4` | **8**（用户 10-11 20:06 说改 4，今天原话「如果8条腿更好」→ 模型是 8 腿，用 8；步态相位表要同步改回八足） |
| 每腿节数 | `SegmentsPerLeg=5`（写死） | **3**（+可选 `_end` 做足端）→ 改成按模型配置 |
| 骨骼名契约 | `LegBoneNames()` 硬编码 `leg_L/R_N_S` | 改成 `{LegPrefix}.{Side}{Seg}` 模式匹配，或直接映射表（8×3=24 个名字） |
| 换模型开关 | `FbxResourcePath="spider_lowpoly"` | 指向 `spider_quaternius`（旧模型保留做对照） |
| 缩放 | — | 模型腿展 ≈6.3 单位，目标腿长 8（BALL_RADIUS×6.4）→ 缩放系数实测后定，不猜 |
| 朝向 | `orientationFixDeg=90`（推导值） | 实测定。Blender 里模型 -Y 是前进方向（Head 在 -Y），导入 Unity 后要实测 |
| 关节限位 (P3) | FABRIK 无限位 | 借这次换模型，从 rest pose 反推（本文件第二节的 head/tail 数据就是原料） |

## 四、文件位置
- 模型：`Assets/Resources/spider_quaternius.fbx`（1.2MB，已入 Unity 导入管线）
- 检查脚本：`Tools/blender/inspect_spider_fbx.py`
- 原始 zip + 解包：`_research/quaternius_spider/`（git 忽略范围外的话注意别提交 zip，
  只提交 fbx 与文档）
