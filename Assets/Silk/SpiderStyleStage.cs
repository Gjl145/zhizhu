/* ============================================================================
 *  蜘蛛侠风格跑酷关卡（SpiderStyleStage）
 * ============================================================================
 *
 *  【设计依据 —— 全部来自官方演讲，不是自创】
 *
 *  出处 A：Concrete Jungle Gym: Building Traversal in 'Marvel's Spider-Man'
 *          Doug Sheahan, Insomniac, GDC 2019
 *          本地：Docs/逆向资料/Insomniac_ConcreteJungleGym_GDC2019.pdf
 *
 *  出处 B：Building a Better Jump（Kyle Pittman, GDC 2016）
 *          本地：Docs/逆向资料/BuildingABetterJump_GDC2016.pdf
 *
 * ============================================================================
 *  ★ 关于锚点方案 —— 官方否决了纯 raycast（P22）
 * ============================================================================
 *
 *  官方原文（P22）：
 *   "The biggest issue is that **ray casts simply did not provide
 *    enough resolution**. Our line lengths would often exceed **50m**
 *    and even with a respectable density of ray casts we were getting
 *    **20m square gaps at full range**."
 *
 *  官方的解决方案（P22续）：
 *   "wrapping buildings in **box volumes** that approximate their shape,
 *    allowing for easy point placement on **each face of the volume**"
 *   "Markup provides **infinite resolution** for point placement"
 *
 *  → 本关卡因此采用**体积化标记（markup）**：
 *    每个平台上放一个 BoxCollider 作为「可钩体积」，
 *    锚点撒在它的各个面上 —— 而不是靠 raycast 碰运气。
 *
 * ============================================================================
 *  ★ 关于「垂直于天空的蛛丝」—— Treyarch 明确放弃过这种设计
 * ============================================================================
 *
 *  Fristrom（GDC 2019）：
 *   "We didn't want the webs to be attached to the sky. I wanted them
 *    to be attached to **points in the world**, and I wanted to
 *    basically be **a bob on a pendulum**."
 *
 *  → 本关卡的锚点**全部挂在建筑侧面/底面**，不放在正上方 ——
 *    符合官方「绳索是真实绳子」的设定。
 *
 * ============================================================================
 *  关卡结构（借鉴 Spider-Man 的「垂直城市」布局）
 * ============================================================================
 *
 *  坐标系（项目约定）：X = 左右   Y = 前后   Z = 高度（重力沿 -Z）
 *
 *  五个垂直排列的「街区」，玩家从底层一路摆荡到顶层：
 *
 *      z=+40┬─ ④ 天台          ← 终点（最高）
 *          │  锚点在楼侧
 *      z=+20┤─ ③ 平台 C
 *          │
 *      z=  0┤─ ② 平台 B
 *          │
 *      z=-20┤─ ① 平台 A       ← 起跳
 *          │
 *      z=-40┴─ 地面
 *
 *  每层之间的高差 20 格 —— 小于满速起跳跨度（约 52 格），
 *  但**大于跳跃高度（5.2 格）**，所以必须靠摆荡上去。
 *  这正是蜘蛛侠式跑酷的核心：跳不上，只能荡。
 *
 * ============================================================================
 */

using UnityEngine;

public static class SpiderStyleStage
{
    /// <summary>创建关卡。h = 立方体半高（本项目 50）。</summary>
    public static void Create(float h)
    {
        float k = h / 50f;

        /* ===== 地面 =====
         * 唯一的实体大平面。玩家从这里开始。*/
        Box("地面", new Vector3(0f, 0f, -44f) * k,
            new Vector3(120f, 120f, 4f) * k, C.Ground);

        /* ===== 四层街区 =====
         * 每层 = 一块「建筑体」（带 markup）+ 平台 + 侧面锚点。
         *
         * 设计要点（据官方 P22）：
         *   锚点撒在建筑的**各个面**上，而不是靠 raycast 探测 ——
         *   这样点密度与分辨率无关，长距离也不会有空洞。*/
        Building("街区A", new Vector3(-18f, 0f, -20f) * k,
                 new Vector3(24f, 24f, 40f) * k, C.BuildingA);
        Building("街区B", new Vector3(10f, 8f, 0f) * k,
                 new Vector3(20f, 20f, 40f) * k, C.BuildingB);
        Building("街区C", new Vector3(-8f, -14f, 20f) * k,
                 new Vector3(22f, 22f, 36f) * k, C.BuildingC);
        Building("天台", new Vector3(14f, 4f, 40f) * k,
                 new Vector3(20f, 20f, 8f) * k, C.Roof);

        /* ===== 起跳台 =====
         * 官方 P85：「平均速度需低于 30 m/s 以避免流式加载卡顿」。
         * 我们 25 格/秒 ≈ 90 km/h，与该量级一致。
         * 起跳台放在地面，方便起跳后钩住街区 A 的侧面。*/
        Box("起跳台", new Vector3(-18f, -14f, -40f) * k,
            new Vector3(14f, 14f, 4f) * k, C.Start);

        Debug.Log("[SpiderStage] 蜘蛛侠风格跑酷关卡已创建\n" +
                  "  ── 设计依据（官方演讲）──\n" +
                  "  · 锚点用**体积化标记**而非 raycast\n" +
                  "    官方 P22：raycast「resolution 不足」，\n" +
                  "    50m 线长下会有 20m 见方的空洞\n" +
                  "  · 锚点挂在**建筑侧面**，不放正上方\n" +
                  "    Fristrom：「we didn't want the webs to be attached\n" +
                  "     to the sky ... I wanted to basically be a bob\n" +
                  "     on a pendulum」\n" +
                  "  · 四层街区高差 20 格\n" +
                  "    跳跃 apex 只有 5.2 格 -> **跳不上，必须摆荡**\n" +
                  "  ── 怎么玩 ──\n" +
                  "  1. 从起跳台跳起（空格）\n" +
                  "  2. 空中按左键发射丝线，钩住建筑侧面的锚点\n" +
                  "  3. 用 WASD 摆荡，荡到更高一层的平台\n" +
                  "  4. 右键松手，惯性带你飞向下一个锚点\n" +
                  "  重复直到抵达天台（黄色平台）\n" +
                  "  ── 参数 ──\n" +
                  "  重力 " + SilkPhysics.Gravity + "（官方 10 倍地球重力）\n" +
                  "  跳跃 apex " + (SilkParkourControllerJumpApex()).ToString("F1") + " 格\n" +
                  "  建议开verboseFireLog 看评分与选点过程");
    }

    static float SilkParkourControllerJumpApex()
    {
        // 与 SilkParkourController.jumpSpeed 保持一致
        const float jump = 32f;
        return jump * jump / (2f * SilkPhysics.Gravity);
    }

    /// <summary>
    /// 建一栋「建筑」：主体 + 四面的 markup 锚点。
    ///
    /// 【markup 的含义】官方 P22：
    ///   "wrapping buildings in **box volumes** that approximate their
    ///    shape, allowing for easy point placement on **each face of
    ///    the volume**"
    ///
    /// 我们的实现：主体是一个 BoxCollider（供射线命中与碰撞），
    /// 锚点则撒在它四个侧面的网格上 —— 密度与分辨率无关。
    /// </summary>
    static void Building(string name, Vector3 center, Vector3 size, Color color)
    {
        // 建筑主体：比视觉体稍小一点，让锚点露在外面
        Box(name + "_体", center, size * 0.96f, color);

        float hx = size.x * 0.5f;
        float hy = size.y * 0.5f;
        float hz = size.z * 0.5f;

        // 锚点间距：官方 P22 提到 raycast「respectable density」也不够，
        // 但 markup 是「infinite resolution」，故这里可以按美术需要铺。
        // 用 8 格间距与项目的 VoxelGrid 一致。
        const float step = 8f;

        // 四个侧面各铺锚点
        for (float z = center.z - hz + step; z <= center.z + hz - step; z += step)
        {
            for (float x = center.x - hx + step; x <= center.x + hx - step; x += step)
            {
                // +X 侧面
                AnchorAt(name + "_PX", new Vector3(x, center.y + hy, z));
                // -X 侧面
                AnchorAt(name + "_NX", new Vector3(x, center.y - hy, z));
            }
            for (float y = center.y - hy + step; y <= center.y + hy - step; y += step)
            {
                // +Y 侧面
                AnchorAt(name + "_PY", new Vector3(center.x + hx, y, z));
                // -Y 侧面
                AnchorAt(name + "_NY", new Vector3(center.x - hx, y, z));
            }
        }

        // 顶部也铺一圈（方便从上方接近）
        for (float x = center.x - hx + step; x <= center.x + hx - step; x += step)
            for (float y = center.y - hy + step; y <= center.y + hy - step; y += step)
                AnchorAt(name + "_顶", new Vector3(x, y, center.z + hz));
    }

    static void AnchorAt(string tag, Vector3 pos)
    {
        var go = new GameObject("Anchor_" + tag);
        go.transform.position = pos;

        var anchor = go.AddComponent<AnchorPoint>();
        anchor.Setup(pos, Vector3Int.RoundToInt(pos), AnchorType.Wall);

        // 视觉：小球，与 SilkBuilder 生成的锚点一致
        var mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = BuildSphereMesh();

        var mr = go.AddComponent<MeshRenderer>();
        var sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null) sh = Shader.Find("Standard");
        if (sh == null) sh = Shader.Find("Sprites/Default");
        if (sh != null)
        {
            var mat = new Material(sh) { color = new Color(1f, 0.85f, 0.25f) };
            // 自发光 —— 让远处也能看见（与 SilkBuilder 的处理一致）
            if (mat.HasProperty("_EmissionColor"))
            {
                mat.EnableKeyword("_EMISSION");
                mat.SetColor("_EmissionColor", new Color(1f, 0.72f, 0.15f));
            }
            mr.sharedMaterial = mat;
        }
        go.transform.localScale = Vector3.one * 1.2f;
    }

    static Mesh _sphereMesh;
    static Mesh BuildSphereMesh()
    {
        if (_sphereMesh != null) return _sphereMesh;
        var temp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        _sphereMesh = temp.GetComponent<MeshFilter>().sharedMesh;
        Object.Destroy(temp);
        return _sphereMesh;
    }

    static void Box(string name, Vector3 center, Vector3 size, Color color)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = "SpiderStage_" + name;
        go.transform.position = center;
        go.transform.localScale = size;
        go.transform.rotation = Quaternion.identity;

        var mr = go.GetComponent<MeshRenderer>();
        if (mr != null)
        {
            Shader sh = Shader.Find("Universal Render Pipeline/Lit");
            if (sh == null) sh = Shader.Find("Standard");
            if (sh != null)
                mr.material = new Material(sh) { color = color };
        }
    }

    public static void Clear()
    {
        int n = 0;
        foreach (var t in Object.FindObjectsOfType<Transform>())
        {
            if (t == null) continue;
            if (t.name.StartsWith("SpiderStage_") || t.name.StartsWith("Anchor_"))
            {
                Object.Destroy(t.gameObject);
                n++;
            }
        }
        Debug.Log("[SpiderStage] 已清理 " + n + " 个物件");
    }

    /// <summary>配色 —— 按高度递进，让玩家能感知「我在第几层」。</summary>
    static class C
    {
        public static readonly Color Ground = new Color(0.30f, 0.32f, 0.36f);
        public static readonly Color Start = new Color(0.45f, 0.5f, 0.55f);
        public static readonly Color BuildingA = new Color(0.36f, 0.42f, 0.52f);
        public static readonly Color BuildingB = new Color(0.40f, 0.46f, 0.58f);
        public static readonly Color BuildingC = new Color(0.44f, 0.50f, 0.64f);
        public static readonly Color Roof = new Color(0.92f, 0.78f, 0.25f);
    }
}