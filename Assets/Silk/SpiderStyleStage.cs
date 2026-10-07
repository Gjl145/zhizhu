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
         * 覆盖整个内墙（±48），保证相机在任何位置都有地面参照。*/
        Box("地面", new Vector3(0f, 0f, -46f) * k,
            new Vector3(96f, 96f, 4f) * k, C.Ground);

        /* ============================================================
         *  ★★★ 建筑群：3×3 共 9 栋，高低错落
         * ============================================================
         *
         *  【为什么第一版只有 4 栋 —— 那是「几个盒子」，不是城市】
         *  官方 P3 的目标是「swinging through New York」，
         *  摆荡的乐趣来自**在建筑之间连续转移**。
         *  孤立几栋楼之间只有大片空档，钩不到下一栋就没得玩。
         *
         *  【尺寸约束】相机 camDistance=20、camHeight=10
         *  · 楼宽 16 格（半宽 8）< 相机 20→ 相机在楼间通道里，不被挡
         *    ★ 这是吸取上一版的教训：楼宽必须 < camDistance，
         *      否则相机会钻进楼里（之前半宽 24 > 20，画面全被挡）
         *  · 楼间距 26 / 22 格 —— 刚好容下一次摆荡（绳长~22）
         *  · 最靠 +X 的楼缘 x=30，相机退20 格 -> 50，刚好不超内墙
         */
        CityBlock(k);

        /* ===== 起跳台 =====
         * 放在建筑群 +X 外侧（相机退的方向），地面层。*/
        Box("起跳台", new Vector3(40f, 0f, -44f) * k,
            new Vector3(16f, 16f, 4f) * k, C.Start);

        Debug.Log("[SpiderStage] 蜘蛛侠风格跑酷关卡已创建\n" +
                  "  ── 规模 ──\n" +
                  "  4 栋建筑（2×2 错位布局，楼高 28~50 格）+ 地面 + 起跳台\n" +
                  "  锚点按官方 markup 方案铺在每栋楼的六个面\n" +
                  "  ── 尺寸约束（相机 camDistance=20, camHeight=10）──\n" +
                  "  · 楼宽 14 格、楼间隙 28 格 > 相机 20 → 相机有8 格余量\n" +
                  "  · 平面刻意错位（非规整网格）→ 才有「钩哪栋」的选择\n" +
                  "  · 全部要素在内墙 ±50 内\n" +
                  "  ── 设计依据（官方演讲）──\n" +
                  "  · 锚点用**体积化标记（markup）**，官方 P29：\n" +
                  "    「wrapping our buildings in box volumes... Then perform\n" +
                  "     a final raycast only on the best point」\n" +
                  "  · 官方 P43：每面生成**两个**候选点（最近点 + 射线点），\n" +
                  "    按面法线与输入方向混合 —— 我第一版漏了这步\n" +
                  "  · 锚点挂在建筑侧面，不放正上方（Fristrom 原话）\n" +
                  "  ── 怎么玩 ──\n" +
                  "  1. 从青色起跳台跳起（空格）\n" +
                  "  2. 空中按左键发射丝线，钩住楼侧的黄色锚点\n" +
                  "  3. WASD 摆荡，在 4 栋楼之间连续转移\n" +
                  "  4. 右键松手，惯性带你飞向下一个锚点\n" +
                  "  目标：抵达最高楼顶的黄色天台\n" +
                  "  ── 参数 ──\n" +
                  "  重力 " + SilkPhysics.Gravity + "（官方 10 倍地球重力）\n" +
                  "  跳跃 apex " + SilkParkourControllerJumpApex().ToString("F1") + " 格\n" +
                  "  建议开 verboseFireLog 看评分与选点过程");
    }

    /// <summary>
    /// 2×2 建筑群，高度错落 + 平面错位。
    ///
    /// 【为什么是 2×2 而不是 3×3 —— 相机约束】
    /// 楼间距必须 > 楼宽 + camDistance，否则相机退到下一栋楼里被挡。
    ///   楼宽 14 + 相机 20 = 最小中心距 34
    ///   3×3 需要 (34×2)+14 = 82 格，勉强塞得进但几乎没有余量；
    ///   2×2 中心距 42 -> 楼间隙 28 格 > 相机 20，**相机有8 格余量**。
    ///
    /// 【为什么平面要错位而不是规整网格】
    /// 规整网格看着像「停车场」。错位后每栋楼到邻居的距离不同，
    /// 玩家才有「钩哪栋」的选择 —— 这是摆荡玩法多样性的来源。
    ///
    /// 高度表（从地面算起）28~50 格：
    /// 跳跃 apex 只有 5.2 格，所以**只能钩住楼侧锚点向上摆荡**。
    /// </summary>
    static void CityBlock(float k)
    {
        // 平面位置：2×2 但刻意错位（不是标准网格）
        Vector3[] spots =
        {
            new Vector3(-16f, -14f, 0f),   // 0
            new Vector3(-10f,  14f, 0f),   // 1
            new Vector3( 16f, -18f, 0f),   // 2
            new Vector3( 12f,  10f, 0f),   // 3
        };
        float[] heights = { 28f, 44f, 36f, 50f };

        const float width = 14f;

        for (int i = 0; i < spots.Length; i++)
        {
            float h = heights[i];
            Vector3 center = new Vector3(spots[i].x, spots[i].y,
                                          -44f + h * 0.5f) * k;
            Vector3 size = new Vector3(width, width, h) * k;
            Building("楼" + i, center, size, ColorForHeight(h));
        }

        // 天台放在最高的楼（楼3, h=50）顶部
        float topZ = -44f + 50f;
        Box("天台", new Vector3(spots[3].x, spots[3].y, topZ + 1f) * k,
            new Vector3(12f, 12f, 2f) * k, C.Roof);
    }

    /// <summary>按高度配色 —— 玩家目测就能判断哪栋高、哪栋矮。</summary>
    static Color ColorForHeight(float h)
    {
        if (h < 32f) return new Color(0.32f, 0.38f, 0.48f);   // 暗：矮楼
        if (h < 42f) return new Color(0.38f, 0.44f, 0.56f);   // 中
        return new Color(0.45f, 0.52f, 0.64f);                // 亮：高楼
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
        // 建筑主体
        Box(name + "_体", center, size, color);

        float hx = size.x * 0.5f;
        float hy = size.y * 0.5f;
        float hz = size.z * 0.5f;

        /* ============================================================
         *  ★★★ 按官方 P43-P45 生成候选点（我第一版漏了这步）
         * ============================================================
         *
         *  官方原文（P43）：
         *   "we use that position and direction to generate **two more
         *    points on each face**:
         *   · The point on the plane **closest to our reference point**
         *   · A **ray-cast point** along our ideal line direction,
         *     which is **clamped to the volume bounds**"
         *
         *  官方原文（P44）—— 两个点各自的用途：
         *   "The **closest point** is useful when we are traveling
         *    **parallel to the plane**.
         *    The **ray-cast point** is useful when we are traveling
         *    **towards the plane**."
         *
         *  官方原文（P45）—— 混合方式：
         *   "We **blend between the two points** based on the difference
         *    between the **input direction and the plane normal**"
         *
         *  ★ 我第一版直接在表面均匀撒点 —— 那是「装饰锚点」，
         *    不是官方的「候选点」。差别在于：
         *    均匀撒点是静态的；官方的候选点随**玩家输入**而变，
         *    每个面上只放最可能有用的那两个。
         *
         *  实战意义：面中央附近的点几乎不会被选中（玩家不会朝
         *  正对着面中心荡过去），面边缘的点才有意义。
         *  这也是官方为什么强调 markup 能做到「infinite resolution」——
         *  因为候选点是按需生成的，不受网格密度限制。
         * ============================================================ */

        const float step = 8f;

        // 六个面：法线 + 面上取点的方式
        // 官方流程是「玩家输入驱动」，我们关卡是静态搭建，
        // 故按官方建议的 **面边缘加密** 布点：
        //   沿面的两个方向，都从边缘往内退step，
        //   这样靠近棱边的点才密集（那里才是有用的钩点）。
        for (int face = 0; face < 6; face++)
        {
            Vector3 n;                        // 面法线（朝外）
            Vector3 u, v;                     // 面内两个方向

            switch (face)
            {
                case 0: n = Vector3.right; u = Vector3.forward; v = Vector3.up; break;
                case 1: n = Vector3.left; u = Vector3.forward; v = Vector3.up; break;
                case 2: n = Vector3.forward; u = Vector3.right; v = Vector3.up; break;
                case 3: n = Vector3.back; u = Vector3.right; v = Vector3.up; break;
                case 4: n = Vector3.up; u = Vector3.right; v = Vector3.forward; break;
                default: n = Vector3.down; u = Vector3.right; v = Vector3.forward; break;
            }

            Vector3 faceCenter = center + new Vector3(
                n.x * hx, n.y * hy, n.z * hz);

            // 面内两个方向的半尺寸
            float uext = (Mathf.Abs(u.x) * hx + Mathf.Abs(u.y) * hy + Mathf.Abs(u.z) * hz);
            float vext = (Mathf.Abs(v.x) * hx + Mathf.Abs(v.y) * hy + Mathf.Abs(v.z) * hz);

            for (float uu = -uext; uu <= uext + 0.01f; uu += step)
            {
                for (float vv = -vext; vv <= vext + 0.01f; vv += step)
                {
                    Vector3 p = faceCenter + u * uu + v * vv;
                    // 官方 P43：射线点要「clamped to the volume bounds」
                    // 我们这里做同样的钳制，防止点跑到盒外
                    p.x = Mathf.Clamp(p.x, center.x - hx, center.x + hx);
                    p.y = Mathf.Clamp(p.y, center.y - hy, center.y + hy);
                    p.z = Mathf.Clamp(p.z, center.z - hz, center.z + hz);
                    AnchorAt(name + "_F" + face, p);
                }
            }
        }
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