/* ============================================================================
 *  摆荡测试关卡（SwingTestStage）—— 隔离变量用
 * ============================================================================
 *
 *  【为什么单独做一个关卡】
 *  用户反馈「摆荡还是太慢」，需要隔离问题。
 *  原基础区里摆荡区域在 y 方向的隔离角落，绳长由玩家与锚点的
 *  距离决定 —— 距离多远完全随机，无法系统性地对比不同绳长的手感。
 *
 *  本关卡提供**受控的绳长梯度**，让玩家能明确回答：
 *  「绳长 X 格时，摆荡周期是多少秒？荡到最低点要几秒？」
 *
 * ============================================================================
 *  ★★ 坐标系（本项目约定，务必注意）
 * ============================================================================
 *  **X = 左右   Y = 前后   Z = 高度（重力沿-Z）**
 *
 *  所以「锚点」必须在球的**正上方**（z 更大），
 *  绳长 = 锚点 z − 球心 z。
 *  写这个关卡时曾把z 当成水平轴，导致锚点位置全错 ——
 *  设计前务必先确认坐标系。
 *
 * ============================================================================
 *  ★ 关键的物理诊断结论（Python 实算）
 * ============================================================================
 *
 *  【摆荡周期是单摆的固有周期，与初始速度无关】
 *      T = 2π√(L/g)        荡到最低点 = π√(L/g) = T/2
 *
 *  当前 g = 50 时：
 *
 *      绳长 L      周期 T     荡到最低点
 *      14 格       3.32 秒    1.66 秒
 *      19 格       3.87 秒    1.94 秒
 *      24 格       4.35 秒    2.18 秒
 *      29 格       4.79 秒    2.39 秒
 *      34 格       5.18 秒    2.59 秒
 *
 *  ★ **不是重力的问题** —— g=50 在游戏里已经**偏大**
 *    （现实 9.8，游戏常用 20~30）。
 *    加大重力反而让跳跃高度下降：
 *      g=100 时 apex = 26²/200 = 3.4 格，跳不上 4 格台阶。
 *
 *  ★ **唯一能加快摆荡的旋钮是绳长**（周期 ∝ √L）：
 *    绳长砍半 -> 周期缩短到 0.71 倍。
 *
 *  ★ **绳长有下限** —— 太短球会撞上锚点：
 *    球直径 9 格，14 格是「快」与「不穿模」的折中点。
 *
 *  ============================================================================
 *  本关卡的绳长梯度设计
 * ============================================================================
 *
 *  布局：**从高往下摆**（起跳台在最上方，锚点依次降低）。
 *  这样最容易控制 —— 玩家只需往下跳，不必担心够不到锚点。
 *
 *      点位   绳长    锚点 z最低点球心 z  理论周期  荡到最低点
 *      S0     14 格   17.5     3.5       3.32 秒    1.66 秒   ★最快
 *      S1     19 格   15.0    -4.0       3.87 秒    1.94 秒
 *      S2     24 格   12.5   -11.5       4.35 秒    2.18 秒
 *      S3     29 格   10.0   -19.0       4.79 秒    2.39 秒
 *      S4     34 格7.5   -26.5       5.18 秒    2.59 秒   ★最慢
 *
 *  玩家在 S0~S4 之间连续摆荡，直接对比周期差异。
 *
 * ============================================================================
 *  ★ 怎么判断问题出在哪
 * ============================================================================
 *
 *  情况 A「S0（14 格，最短）明显比 S4 快」
 *    -> 确认是**绳长太长**导致的慢。
 *       解决办法：把关卡的所有摆荡空间压到 15~20 格。
 *
 *  情况 B「S0 也一样慢」
 *    -> 问题不在绳长，而是**缺少「最高点放绳」**。
 *       标杆靠「最低点收绳 + 最高点放绳」的呼吸节奏
 *       才能越荡越高；我们只做了收绳。
 *
 *  情况 C「周期对了，但荡不高」
 *    -> 收绳（Reel-In）没生效或收得不够。
 *       检查 reelInRatio 是否为 0，以及 verboseReelInLog 的输出。
 *
 * ============================================================================
 *  ★ 与标杆的差距来源
 * ============================================================================
 *
 *  我们现在是「钟摆」：受重力 + 固定绳长约束，周期固定。
 *  真实蜘蛛侠是「轨道」：
 *     荡到最低点 -> 收绳（半径变小，速度变大）
 *     荡到最高点 -> 放绳（半径变大，省力上摆）
 *     -> 半径持续变化，于是**每次都荡得比上次更高**
 *
 *  已实现：最低点收绳（ApplyReelIn，默认 15%）。
 *  待实现：最高点放绳。
 *
 * ============================================================================
 */

using UnityEngine;

public static class SilkSwingTestStage
{
    /// <summary>创建摆荡测试关卡。</summary>
    public static void Create(float h)
    {
        float k = h / 50f;

        /* ===== 起跳台（最上方）=====
         * 顶面 z = 20，球心起始 z = 24.5。
         *
         * 【★ 起点高度由调用方处理】
         * startPosition 是全局的（对应基础区平地的顶面 -34）。
         * 切到本关卡时必须同步改起点，否则球会出生在空中 ——
         * ResolveGround 只在**下落时**吸附，出生时 vertVel=0 不吸附，
         * 球会「悬空掉一截」才落地。
         * 故 StartPosition 属性供 BallController 切换关卡时读取。*/
        Plat("T_0_起跳台", new Vector3(-26f, 0f, 16f) * k,
             new Vector3(20f, 20f, 8f) * k, C.Start);

        /* ===== 5 个摆荡点 =====
         * 锚点 z 由绳长反推：让球心荡到最低点时略低于起跳台高度，
         * 玩家能自然地从上一个点荡到下一个点。
         *
         * 每点由「顶部平台（锚点）+ 悬挂柱」组成 ——
         * 悬挂柱让玩家提前看见「这里可以钩」（参考视频 2 的地标原则）。*/
        SwingPoint("S0_绳长14", -6f, 17.5f, 14f, C.Swift);
        SwingPoint("S1_绳长19", 8f, 15.0f, 19f, C.Fast);
        SwingPoint("S2_绳长24", 22f, 12.5f, 24f, C.Mid);
        SwingPoint("S3_绳长29", 36f, 10.0f, 29f, C.Slow);
        SwingPoint("S4_绳长34", 50f, 7.5f, 34f, C.Slower);

        /* ===== 底部收尾台 =====
         * z = -32。摆荡到 S4 后可以落在这里。
         * 掉下去会自动重生（z < fallRespawnZ = -70）。*/
        Plat("T_1_收尾台", new Vector3(50f, 0f, -36f) * k,
             new Vector3(18f, 18f, 8f) * k, C.End);

        Debug.Log("[SwingTest] 摆荡测试关卡已创建（★隔离变量用）\n" +
                  "  ── 设计目的 ──\n" +
                  "  5 个摆荡点构成绳长梯度（14→34 格），\n" +
                  "  用来定位「摆荡太慢」究竟是绳长问题还是机制问题。\n" +
                  "  ── 物理公式 ──\n" +
                  "  摆荡周期 T = 2π√(L/g)，与初始速度无关，只由绳长决定。\n" +
                  "  g = " + SilkPhysics.Gravity + "\n" +
                  "  ── 绳长梯度（理论值）──\n" +
                  Row("S0", 14f) + Row("S1", 19f) + Row("S2", 24f) +
                  Row("S3", 29f) + Row("S4", 34f) +
                  "  ── 怎么判断问题 ──\n" +
                  "  A. S0 明显比 S4 快 -> 绳长太长，需压缩关卡的摆荡空间\n" +
                  "  B. S0 也一样慢       -> 缺「最高点放绳」（待实现）\n" +
                  "  C. 周期对但荡不高     -> 收绳没生效，查 verboseReelInLog\n" +
                  "  ── 收绳（Reel-In）──\n" +
                  "  已启用，最低点收绳 15%（理论速度×1.18）。\n" +
                  "  在 SilkParkourController 上调 reelInRatio（设 0 关闭）、\n" +
                  "  勾 verboseReelInLog 可看收缩日志。");
    }

    /// <summary>生成一行绳长报告。</summary>
    static string Row(string name, float L)
    {
        float T = Period(L);
        return "  " + name + ": 绳长 " + L.ToString("F0").PadLeft(2) +
               " 格  周期 " + T.ToString("F2") + " 秒  荡到最低点 " +
               (T / 2f).ToString("F2") + " 秒\n";
    }

    /// <summary>
    /// 本关卡的球心起点（= 起跳台顶面 + 球半径）。
    ///
    /// 【为什么需要它】`BallController.startPosition` 是全局唯一的，
    /// 对应基础区平地的顶面。两个关卡的起跳台高度不同，
    /// 切关卡时必须同步改起点 —— 否则球出生在空中，
    /// 而 ResolveGround 只在**下落时**吸附（vertVel > 0 或静止都不贴），
    /// 球会「悬空掉一截」才落地。
    /// </summary>
    public static Vector3 StartPosition
        => new Vector3(-26f, 0f, 20f + 4.5f);   // 顶面 20 + 半径 4.5

    /// <summary>单摆周期 T = 2π√(L/g)。</summary>
    static float Period(float ropeLength)
    {
        float g = Mathf.Max(SilkPhysics.Gravity, 0.01f);
        return 2f * Mathf.PI * Mathf.Sqrt(ropeLength / g);
    }

    /// <summary>
    /// 建一个摆荡点：顶部平台（锚点）+ 悬挂柱。
    ///
    /// 【为什么加悬挂柱】参考视频 2 的「地标」原则 ——
    /// 玩家需要**提前看见**摆荡点，否则会以为是随机方块。
    /// 柱子让「这里可以钩」一目了然。
    /// </summary>
    static void SwingPoint(string name, float x, float anchorZ, float ropeLen, Color color)
    {
        // 顶部平台：锚点挂在它的底面。
        // center.z = anchorZ + 3使平台底面正好在 anchorZ。
        Plat(name + "_顶", new Vector3(x, 0f, anchorZ + 3f),
             new Vector3(9f, 9f, 6f), color);

        // 悬挂柱：从平台底面向下延伸 3 格，标示可钩位置。
        // Cylinder 的 scale.y 是**半高**，故 3f = 向下 3 格。
        var pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        pole.name = "SwingTest_" + name + "_柱";
        pole.transform.localScale = new Vector3(1.0f, 3f, 1.0f);
        pole.transform.position = new Vector3(x, 0f, anchorZ - 3f);
        var mr = pole.GetComponent<MeshRenderer>();
        if (mr != null)
        {
            Shader sh = Shader.Find("Sprites/Default");
            if (sh != null)
                mr.material = new Material(sh)
                { color = new Color(1f, 0.92f, 0.45f, 0.45f) };
        }

        // 地面投影线：标示「球摆到最低点时会在哪」——
        // 让玩家目测绳长，是理解「绳长决定周期」最直观的方式。
        //用一条细长的半透明方块画在最低点的高度上。
        float lowZ = anchorZ - ropeLen;
        Plat(name + "_最低点标记", new Vector3(x, 0f, lowZ),
             new Vector3(2f, 2f, 0.4f), C.Mark);
    }

    /// <summary>建一块平台。</summary>
    static void Plat(string name, Vector3 center, Vector3 size, Color color)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = "SwingTest_" + name;
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

    /// <summary>移除关卡物件。</summary>
    public static void Clear()
    {
        int n = 0;
        foreach (var t in Object.FindObjectsOfType<Transform>())
        {
            if (t == null) continue;
            if (t.name.StartsWith("SwingTest_"))
            {
                Object.Destroy(t.gameObject);
                n++;
            }
        }
        Debug.Log("[SwingTest] 已清理 " + n + " 个物件");
    }

    /// <summary>配色 —— 从「快」到「慢」用色相递变，方便视觉定位。</summary>
    static class C
    {
        public static readonly Color Start = new Color(0.45f, 0.5f, 0.55f);   // 灰蓝：起跳台
        public static readonly Color Swift = new Color(0.25f, 0.85f, 0.5f);   // 绿：最快
        public static readonly Color Fast = new Color(0.5f, 0.8f, 0.35f);     // 黄绿
        public static readonly Color Mid = new Color(0.9f, 0.8f, 0.3f);      // 黄
        public static readonly Color Slow = new Color(0.9f, 0.55f, 0.28f);    // 橙
        public static readonly Color Slower = new Color(0.9f, 0.32f, 0.28f);  // 红：最慢
        public static readonly Color End = new Color(0.55f, 0.55f, 0.65f);   // 灰：收尾台
        public static readonly Color Mark = new Color(1f, 1f, 1f, 0.5f);      // 白：最低点标记
    }
}