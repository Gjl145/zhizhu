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
/// 【★ 2026-10-11 深夜再改 8 → 4（用户原话：「现在变成4条腿吧，8条可能有点多」）】
///   · 8 腿在画面上确实密，4 条干净得多（用户拍板）。
///   · 骨骼映射取 Quaternius 每侧**前 2 对**：FrontLeg + MidFrontLeg
///     （前腿最像蜘蛛的「手」，视觉上合理）。
///   · 4 腿对角步态相位表、StaggerOrder4、检查器 4 腿校验分支全部现成。
public static class SilkSpiderLegs
{
    /// <summary>★ 全身腿数。改这一个数字即可切换 4 腿 / 8 腿。</summary>
    public const int Count = 4;

    /// <summary>每侧腿数（左右各半）。</summary>
    public const int PerSide = Count / 2;

}