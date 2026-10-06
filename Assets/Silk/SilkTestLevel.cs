/* ============================================================================
 *  跑酷测试场 —— 今天的核心目标：平台 + 锚点 + 基础跑酷手感验证
 * ============================================================================
 *
 *  【今天要验证什么】
 *   1. 基础移动   —— 地面跑动、跳跃、落地不穿台
 *   2. 锚点构建   —— 平台上空构建锚点、固化、连线
 *   3. 自动瞄准   —— 发射时能否自动选中合适的高处锚点
 *   4. 摆荡       —— 勾住→摆荡→松手→动量保留
 *
 *  【设计原则：平台是「可读的」】
 *   每个平台都有明确的用途与颜色区分，玩家一眼就知道该往哪走：
 *     灰=起点 /蓝 = 常规平台 /橙 = 高处锚点平台 /黄 = 终点
 *   平台之间的高度与距离**故意做成递增**，形成一条难度曲线。
 *
 *  【坐标约定】X = 左右，Y = 前后，Z = 高度（重力沿 -Z）
 *   立方体内墙在 ±50（100³ 网格），故所有坐标都要留余量。
 *
 *  【锚点说明】
 *   平台上方的锚点由 SilkBuilder 的墙面锚点系统提供；
 *   另可在平台上按C 自行固化节点（PlayerNode）。
 *
 *  【删除方法】删掉本文件 + SilkWorldBootstrap 里的一行调用即可。
 */

using UnityEngine;

public static class SilkParkourStage
{
    /// <summary>创建跑酷测试场。h = 立方体半高（本项目为 50）。</summary>
    public static void Create(float h)
    {
        float k = h / 50f;   // 自适应缩放

        /* ===== 1. 起点大平台 =====
         * 足够宽敞，让玩家能先熟悉移动+跳跃，再往外走。*/
        Plat("Plat_1_起点", new Vector3(-38f, 0f, -34f) * k,
             new Vector3(24f, 44f, 8f) * k, C.Start);

        /* ===== 2. 阶梯：两级台阶，练习跳跃 =====
         * 台阶高 6 格、跨度 14 格 —— 跳跃最高 7.5 格，刚好能跳上去，
         * 逼玩家用跳跃而不是走过去。*/
        Plat("Plat_2_台阶低", new Vector3(-14f, -12f, -30f) * k,
             new Vector3(14f, 16f, 6f) * k, C.Normal);
        Plat("Plat_3_台阶高", new Vector3(0f, -12f, -24f) * k,
             new Vector3(14f, 16f, 6f) * k, C.Normal);

        /* ===== 3. 长桥：测试高速移动与惯性 =====
         * 宽 7 格的窄桥，两侧无护栏 —— 考验平衡与动量控制。
         * x 范围 -3..21（缩短，为后面的沟壑留出空间）。*/
        Plat("Plat_4_窄桥", new Vector3(9f, 6f, -24f) * k,
             new Vector3(24f, 7f, 6f) * k, C.Narrow);

        /* ===== 4. 高处平台：摆荡测试的主要目标 =====
         * 顶面 z=-2，比窄桥（顶面 -21）高 19 格 —— 自动瞄准会优先选这附近。
         * 在窄桥的斜后上方，玩家在桥上抬头就能看到。*/
        Plat("Plat_5_高处锚点", new Vector3(16f, 24f, -6f) * k,
             new Vector3(20f, 18f, 8f) * k, C.High);

        /* ===== 5. 沟壑对岸：测试摆荡跨越 =====
         * 与窄桥**同一条 y 带**（y 2.5..9.5），中间留 24 格空隙
         *（窄桥 x 上限 21 -> 对岸 x 下限 45）。
         * 纯靠地面跑动绝对过不去：
         *   · 水平跨度 24 格，跳跃极限够不到
         *   · 两块平台顶面同高，跳跃也上不去
         * 必须发射蛛丝摆荡过去 —— 这是摆荡的核心测试点。
         * x 上限 52 略超内墙 50，故中心收在 42、半宽 7（x 35..49）。*/
        Plat("Plat_6_沟壑对岸", new Vector3(42f, 6f, -24f) * k,
             new Vector3(14f, 7f, 6f) * k, C.Normal);

        /* ===== 6. 终点台 =====
         * 最高的一座，用于确认玩家能连续摆荡抵达。*/
        Plat("Plat_7_终点", new Vector3(2f, 30f, 6f) * k,
             new Vector3(18f, 18f, 8f) * k, C.End);

        Debug.Log("[Stage] 跑酷测试场已创建：7 块平台（起点/台阶×2/窄桥/高处/沟壑后/终点）\n" +
                  "  台阶高 6 格（跳跃上限约 7.5 格）\n" +
                  "  窄桥宽 7 格，两侧无护栏\n" +
                  "  沟壑宽 22 格，需摆荡跨越\n" +
                  "  高处平台 z=-6，比主路线高 18 格");
    }

    /// <summary>移除跑酷测试场（运行时兜底，便于切回FreeFly 时清理）。</summary>
    public static void Clear()
    {
        int n = 0;
        foreach (var t in Object.FindObjectsOfType<Transform>())
        {
            if (t == null) continue;
            if (t.name.StartsWith("Plat_") || t.name.StartsWith("Tip_"))
            {
                Object.Destroy(t.gameObject);
                n++;
            }
        }
        if (n > 0) Debug.Log("[Stage] 清理 " + n + " 个平台物件");
    }

    /// <summary>平台配色 —— 玩家靠颜色分辨用途。</summary>
    static class C
    {
        public static readonly Color Start = new Color(0.55f, 0.55f, 0.6f);   // 灰
        public static readonly Color Normal = new Color(0.3f, 0.55f, 0.85f);  // 蓝
        public static readonly Color High = new Color(0.9f, 0.6f, 0.25f);     // 橙：挂点
        public static readonly Color End = new Color(0.95f, 0.85f, 0.3f);     // 黄：终点
        public static readonly Color Narrow = new Color(0.4f, 0.7f, 0.5f);    // 绿：窄桥
    }

    /// <summary>建一块平台（自带 BoxCollider，能站上去）。</summary>
    static void Plat(string name, Vector3 center, Vector3 size, Color color)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = "Plat_" + name;
        go.transform.position = center;
        go.transform.localScale = size;
        go.transform.rotation = Quaternion.identity;

        var mr = go.GetComponent<MeshRenderer>();
        if (mr == null) return;
        Shader sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null) sh = Shader.Find("Standard");
        if (sh == null) sh = Shader.Find("Sprites/Default");
        if (sh != null) mr.material = new Material(sh) { color = color };
    }
}