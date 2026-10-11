/// <summary>
/// ★★ 蜘蛛全身的「腿数」唯一来源（2026-10-11 建立，8 → 4）
/// </summary>
///
/// 【为什么单独建一个类，而不是放在 SilkSpiderFbxRig 里】
///   `LegCount` 被三个类同时引用：
///     · SilkSpiderFbxRig（骨骼名映射 leg_X_N_M）
///     · SilkSpiderAnatomy（limbs / LegRoots / FootPositions）
///     · SilkSpiderBody（legState / footWorld / rootWorld / footNormals /
///       互斥表 / 相位表 / HUD 条带 / 全部遍历循环）
///   放在 FbxRig 里的话，另外两个类都要写 `SilkSpiderFbxRig.LegCount`，
///   且**形成依赖方向问题**（Anatomy 与 Body 本来不依赖 FBX 层）。
///   → 单独一个无依赖的静态常量类，谁都能引用，谁都不被牵连。
///
/// 【★ 改腿数时必须同步检查的地方（漏一处就是静默 bug）】
///   1. **相位表** `SilkSpiderBody.phaseOffsets`
///      长度必须 == LegCount，且数值要符合该腿数的步态。
///      4 腿版 = { L1:0, L2:0.5, R1:0.5, R2:0 }（对角同相、同侧反相）。
///   2. **互斥表** `SilkSpiderBody.BuildNeighborTable()`
///      索引假设（每侧几条、左右起点）必须随腿数变。
///   3. **FBX 骨骼名** `SilkSpiderFbxRig.LegBoneNames()`
///      每侧取前 N 对（4 腿时只取 leg_L_1/2、leg_R_1/2，不碰 3/4）。
///      → ★ 不需要重跑 Blender，FBX 里本来就有 4 对。
///   4. **HUD 步态条带** 行列数由 LegCount 推导（原本写死 8 行）。
///   5. **落点初始化/ 换点 / 摆动裁决** 里所有循环上界。
///
/// 【★ 2026-10-11 改回 8 条（4 → 8，换装 Quaternius Spider）】
///   · 用户选定的新主角模型 Quaternius Spider 是**真 8 腿**骨骼
///     （FrontLeg / MidFrontLeg / MidBackLeg / BackLeg × L/R，每腿 3 节），
///     只用 4 条会浪费一半腿，画面上是残废蜘蛛。
///   · 用户原话：「免费的就行，4条腿的都行，如果8条腿更好」→ 8 条更好。
///   · 步态/互斥/起步顺序的全部 8 腿数据结构在 4 腿改造时都保留了下来
///     （StaggerOrder8、交替四足相位表 {L:0,0.5,0,0.5 / R:0.5,0,0.5,0}），
///     现在直接启用。
///   · 与 IK 教程（B站 BV12F411P71f）的四足对照仍是有效参照——
///     交替四足步态 = 8 腿分两组各 4 条交替，正是 UE5 教程（BV1X88c6iETw）的做法。
public static class SilkSpiderLegs
{
    /// <summary>★ 全身腿数。改这一个数字即可切换 4 腿 / 8 腿。</summary>
    public const int Count = 8;

    /// <summary>每侧腿数（左右各半）。</summary>
    public const int PerSide = Count / 2;

}