/* ============================================================================
 *  摆荡单元关卡（SwingUnitStage）—— 最小可玩单元
 * ============================================================================
 *
 *  【设计意图 —— 用户明确要求，勿扩张】
 *  「我搭建的这个正方体的目的不是完全搭建一个完整的关卡，
 *    而是一个部分，一个小部分就行了……
 *    我不用过于执着于构建一个、或者完美复现一个关卡。
 *    但是需要完美复现一个节点，然后继续下一个节点，
 *    最后让玩家摆荡一次就到达一个新节点，最后达成跑酷的效果」
 *
 *  ★ 这**不是**复刻任何游戏的关卡，只是验证「摆荡链」这个玩法成立：
 *    节点（正方体）→ 摆荡一次 → 下一个节点 → …… → 终点
 *
 * ============================================================================
 *  ★ 尺寸是怎么算出来的（不是拍脑袋）
 * ============================================================================
 *
 *  已知物理参数：
 *      重力 gravity = 98  （Insomniac GDC 2019 官方值，10 倍地球重力）
 *      起跳 jumpSpeed = 32
 *      跑速 moveSpeed = 25
 *
 *  推导：
 *      跳跃最高点 apex  = 32² / (2×98) = **5.2 格**
 *      全速起跳跨度     = 25 × 0.65      = **16.3 格**
 *      理想绳长         = **22 格**
 *
 *  ★ 两条硬性要求：
 *      ① 相邻节点高差 **> 5.2 格** —— 否则直接跳上去，摆荡没意义
 *      ② 高差 **< 15 格** —— 摆荡最多爬升约「绳长 - 起跳高度」，
 *         超过这个数玩家钩得到但荡不上去
 *
 *  → 本关卡：**高差 12 格、水平间距 20 格**
 *      高差 12 > apex 5.2 ✓跳不上去
 *      高差 12 < 15 ✓ 摆得上去
 *      间距 20≈ 理想绳长 22 ✓ 钩得到
 *
 * ============================================================================
 *  ★ 相机约束（这个坑踩过两次，必须先算）
 * ============================================================================
 *
 *  相机：camDistance = 20，camHeight = 10（在球后方）
 *
 *  ① 节点间距必须 ≥ camDistance，否则相机退到下一个节点里被挡
 *     → 本关卡间距 20 格，正好
 *
 *  ② 整条链沿 **+X** 延伸，相机退向 **-X**（起点后方是空旷的）
 *
 * ============================================================================
 *  关卡结构：4 个节点，一条直线
 * ============================================================================
 *
 *    z↑                ┌─────┐  节点3（终点）
 *     │                └─────┘
 *     │        ┌───────┐
 *     │        │节点2  │          每级顶面 +12 格
 *     │   ┌────┴─────┐
 *     │   │ 节点1    │
 *     │   └────┬─────┘
 *     │┌──────┴───┐
 *     ││  节点0   │← 出生点
 *     └┴──────────┴──────────────────→ x
 *
 *   节点顶面：-40 → -28 → -16 → -4（每级 12 格）
 *   水平间距：20 格
 *
 * ============================================================================
 *  锚点（按官方 markup 方案）
 * ============================================================================
 *
 *  官方 GDC 2019 P22：raycast 精度不足（50m 线长下有 20m 空洞），
 *  改用「把建筑包进box volume，在每个面上放点」的 markup 方案。
 *
 *  本关卡的正方体就是 volume，锚点贴在其**顶面边缘** ——
 *  玩家要钩的是「目标节点上方偏前」的点，荡过去正好落在平台上。
 *  官方明确放弃过「垂直天空的蛛丝」：
 *  P5: 「we didn't want the webs to be attached to the sky」
 *
 * ============================================================================
 */

using UnityEngine;

public static class SilkSwingUnitStage
{
    /// <summary>创建关卡。h = 立方体半高（本项目 50）。</summary>
    public static void Create(float h)
    {
        float k = h / 50f;

        /* ---- 参数（全部经过核算，见文件头）---- */
        const float gapX = 20f;      // 水平间距 ≥ camDistance 20
        const float riseZ = 12f;      // 顶面高差 > apex 5.2，< 15
        const float nodeSize = 16f;   // 节点边长
        const float startX = -10f;
        const float baseZ = -40f;     // 节点0 的顶面高度

        /* ---- 地面 ----
         * 覆盖相机可能到达的全部范围（相机退向 -X），
         * 否则球掉下去就没有参照物。*/
        Box("地面", new Vector3(startX + gapX, 0f, baseZ - 2f) * k,
            new Vector3(90f, 60f, 4f) * k, C.Ground);

        /* ---- 四个节点 ---- */
        for (int i = 0; i < 4; i++)
        {
            float cx = startX + i * gapX;
            float top = baseZ + i * riseZ;        // 该节点的**顶面**高度
            float cz = top - nodeSize * 0.5f;      // 中心 =顶面 - 半高
            float cy = 0f;

            Box("节点" + i, new Vector3(cx, cy, cz) * k,
                new Vector3(nodeSize, nodeSize, nodeSize) * k,
                i == 3 ? C.Goal : C.Node);

            /* 锚点：贴在顶面的**四个角**。
             *
             * 【为什么在顶面边缘而不是顶面中央】
             * 玩家从下方荡上来，需要钩住一个「在上方偏侧」的点才能荡到平台上。
             * 顶面边缘的点正好提供这个方向；中央的点则要钩到正上方——
             * 而官方明确放弃过「垂直天空的蛛丝」。*/
            float half = nodeSize * 0.5f - 1.5f;   // 内缩 1.5 格，点在实体外
            for (int e = 0; e < 4; e++)
            {
                float dx = (e == 0 || e == 1) ? -1f : 1f;   // 0,1 → ±X
                float dz = (e == 0 || e == 2) ? -1f : 1f;   // 0,2 → ±Z
                AnchorAt("节点" + i + "_角" + e,
                    new Vector3(cx + dx * half, cy, top + dz * half));
            }
        }

        Debug.Log("[SwingUnit] 正方体节点摆荡关卡已创建\n" +
                  "  ── 设计意图 ──\n" +
                  "  最小可玩单元：节点 → 摆荡一次 → 下一个节点 → ……\n" +
                  "  不是复刻某个游戏关卡，只是验证「摆荡链」玩法成立。\n" +
                  "  ── 尺寸核算（非拍脑袋）──\n" +
                  "  重力 " + SilkPhysics.Gravity + " | 起跳 32 | 跑速 25\n" +
                  "  跳跃 apex = 32²/(2×98) = **5.2 格**\n" +
                  "  全速起跳跨度 = **16.3 格** | 理想绳长 = **22 格**\n" +
                  "  本关卡：间距 20 格、高差 12 格\n" +
                  "    · 高差 12 > apex 5.2  -> **跳不上去，必须摆荡**\n" +
                  "    · 高差 12 < 15         -> 摆荡爬得上去\n" +
                  "    · 间距 20 ≥ camDistance 20 -> 相机不撞节点\n" +
                  "  ── 节点布局 ──\n" +
                  "  节点0 (x=-10, 顶z=-40) ← 出生点\n" +
                  "  节点1 (x= 10, 顶z=-28)\n" +
                  "  节点2 (x= 30, 顶z=-16)\n" +
                  "  节点3 (x= 50, 顶z= -4) ← 终点（黄色）\n" +
                  "  ── 怎么玩 ──\n" +
                  "  1. 在节点0 上按空格跳起\n" +
                  "  2. 空中按**左键**发射丝线，钩住节点1 顶面角的黄色锚点\n" +
                  "  3. 按 **W** 摆荡过去，落在节点1 顶面\n" +
                  "  4. 重复三次，抵达节点3（黄色终点）\n" +
                  "  建议开 verboseFireLog 看选点与评分过程");
    }

    /// <summary>本关卡的球心起点（= 节点0 顶面 + 半径）。</summary>
    public static Vector3 StartPosition
    {
        get
        {
            const float startX = -10f;
            const float baseZ = -40f;
            const float nodeSize = 16f;
            return new Vector3(startX, 0f, baseZ + 4.5f);
        }
    }

    /// <summary>建一个正方体节点。</summary>
    static void Box(string name, Vector3 center, Vector3 size, Color color)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = "SwingUnit_" + name;
        go.transform.position = center;
        go.transform.localScale = size;
        go.transform.rotation = Quaternion.identity;

        // 标记组件：靠它识别归属（名字前缀已足够，但仍加上更保险）
        go.AddComponent<SwingUnitMarker>();

        var mr = go.GetComponent<MeshRenderer>();
        if (mr == null) return;
        Shader sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null) sh = Shader.Find("Standard");
        if (sh == null) sh = Shader.Find("Sprites/Default");
        if (sh == null) return;

        var mat = new Material(sh) { color = color };
        // 加自发光，远处也能看清节点轮廓
        if (mat.HasProperty("_EmissionColor"))
        {
            mat.EnableKeyword("_EMISSION");
            mat.SetColor("_EmissionColor", color * 0.35f);
        }
        mr.material = mat;
    }

    /// <summary>
    /// 放一个锚点（可钩挂点）。
    ///
    /// 【★ 极简：视觉全部由 AnchorPoint.Setup() 处理】
    ///
    /// 初版我在 Setup 之后又AddComponent 了 MeshFilter / MeshRenderer，
    /// 结果：
    ///   · 同一物体上有**两个** MeshFilter，两个渲染器 -> 视觉混乱
    ///   · 且 Setup() 内第 524 行会把物体名改成 "Anchor_" + type，
    ///我给的前缀被覆盖 -> Clear() 按前缀删不到它们
    ///
    /// Setup() 已经做了：MeshFilter(内置球网格) / MeshRenderer(共享材质)
    /// / localScale / SphereCollider / 自发光。这里只需调它。
    /// </summary>
    static void AnchorAt(string tag, Vector3 pos)
    {
        var go = new GameObject("SwingUnit_Anchor_" + tag);
        go.transform.position = pos;

        // 标记组件：Setup 会覆盖名字，靠它识别归属（见 Clear 的注释）
        go.AddComponent<SwingUnitMarker>();

        var anchor = go.AddComponent<AnchorPoint>();
        // Setup 会处理网格、材质、碰撞体、自发光，并重命名物体为Anchor_xxx。
        // 这里**不再**重复 AddComponent(MeshFilter/MeshRenderer)——
        //   否则同一物体上有两个 MeshFilter / 两个渲染器，视觉会乱。
        anchor.Setup(pos, Vector3Int.RoundToInt(pos), AnchorType.Wall);

        // 放大锚点球，让远距离也能看见（Setup 里用的是 VisualScale）
        go.transform.localScale = Vector3.one * 2.4f;
    }

    /// <summary>
    /// 移除本关卡物件。
    ///
    /// 【★ 为什么用组件标记而不是名字】
    /// `AnchorPoint.Setup()`（SilkBuilder.cs 第 524 行）会把物体重命名为
    /// "Anchor_" + type + "_" + voxel —— 前缀被吃掉。
    /// 而原版网格生成的墙锚点**也叫** Anchor_Wall_xxx
    ///（SilkBuilder.cs 第 1375 行附近建BreakNode 与网格锚点），
    /// 若按名字前缀删除会**误删原版网格的锚点**。
    ///
    /// 解决：给本关卡的锚点挂一个专属标记组件，Clear 只认它。
    /// </summary>
    public static void Clear()
    {
        int n = 0;
        foreach (var t in Object.FindObjectsOfType<Transform>())
        {
            if (t == null) continue;
            bool mine = t.GetComponent<SwingUnitMarker>() != null;
            if (t.name.StartsWith("SwingUnit_") || mine)
            {
                Object.Destroy(t.gameObject);
                n++;
            }
        }
        Debug.Log("[SwingUnit] 已清理 " + n + " 个物件");
    }

    /// <summary>本关卡物件的标记组件。
    /// 用它而不是名字来识别，因为 AnchorPoint.Setup() 会覆盖名字。</summary>
    public class SwingUnitMarker : MonoBehaviour
    {
    }

    static class C
    {
        public static readonly Color Ground = new Color(0.28f, 0.30f, 0.34f);
        public static readonly Color Node = new Color(0.42f, 0.48f, 0.58f);
        public static readonly Color Goal = new Color(0.95f, 0.78f, 0.25f);
    }
}