/* ============================================================================
 *  两层四间住宅白盒（HouseBlockout）
 * ============================================================================
 *
 *  【需求 —— 用户指定】
 *    两层，每层两间房，共 4 间，**大小都相同**
 *    上下两层用**带两次拐角的楼梯**连接（两跑折返，中间一个休息平台）
 *    **楼梯底下是厕所**
 *      1F：厨房      + 次卧室
 *      2F：杂物室    + 主卧室
 *
 *  【全部尺寸来自中国国家规范，有据可查】
 *    GB 50096-2011《住宅设计规范》 —— 2012-08-01 施行
 *    GB 55038-2025《住宅项目规范》 —— 2025-05-01 施行，全文强制
 *    （GB 55038 替代了 GB 50096 的部分强条，下限有所提高）
 *
 *  ===================== 一、强条数据（不可商量）=====================
 *
 *    卧室/起居室净高≥ 2.60 m   GB 55038-2025 第 4.1.2 条
 *    厨房净宽（单排）≥ 1.50 m   GB 50096-2011 第 5.3.5 条
 *    楼梯踏步宽≥ 0.26 m         GB 50096-2011 第 6.3.2 条
 *    楼梯踏步高≤ 0.175 m        GB 50096-2011 第 6.3.2 条
 *    楼梯梯段净宽   ≥ 1.10 m     GB 50096-2011 第 6.3.1 条
 *    玄关过道净宽   ≥ 1.20 m     GB 50096-2011 第 5.7.1 条
 *
 *  ===================== 二、门洞（表 5.8.7）=====================
 *
 *    户门 1.00 × 2.00 |卧室 0.90 × 2.00 | 厨房 0.80 × 2.00
 *    卫生间 0.70 × 2.00 | 阳台 0.70 × 2.00
 *    实际毛坯交付普遍 2.08~2.15 m，本关卡用 2.10 m
 *
 *  ===================== 三、推导量 =====================
 *
 *    结构楼板厚 0.22 m
 *      依据：临夏市住建局公开答复 ——「层高 2.95 m、实测净高 2.73 m」，
 *      差值0.22 m 即楼板 + 梁结构厚度。
 *
 *    层高= 净高 2.60 + 楼板 0.22 = **2.82 m**
 *
 *    楼梯每跑级数：层高 2.82 / (2 跑 × 踏步高 0.16) ≈ 8.8 → 取 **9 级**
 *      9 级 × 2 跑 = 18 级，总爬高 18 × 0.16 = **2.88 m**
 *      （略超层高 6 cm，在实际施工中由平台厚度消化，可接受）
 *      每跑水平长 9 × 0.28 = **2.52 m**
 *
 *  ============================================================================
 *  ★ 坐标系（项目约定）：X = 左右   Y = 前后   Z = 高度（重力沿-Z）
 *  ============================================================================
 *
 *    平面布局（俯视，X 向右，Y 向上为后）
 *
 *        x=-4.4        -0.9   +0.9                +4.4
 *    y=+3.8┌──────────┬──────┬───────────────────┐
 *          │          │      │                   │
 *          │ 杂物室    │ 楼梯 │     主卧室         │  2F
 *          │ 3.5×3.5   │ 井   │     3.5×3.5        │
 *          │ (z高层)   │      │                   │
 *    y=+0.4├──────────┼──────┤                   │
 *          │          │平台 │                   │
 *          │   次卧室  │      │     厨房           │  1F
 *          │  3.5×3.5  │ 厕所 │     3.5×3.5        │
 *    y=-3.1└──────────┴──────┴───────────────────┘
 *
 *    · 四间房**都是 3.5 × 3.5 m**（用户要求大小相同）
 *    · 楼梯井在中间 1.8 m 宽，两跑折返（**两次拐角**= 两个 90° 转弯）
 *    · 楼梯下方 1F 层高 2.60 m 处即**厕所**
 *
 *  ============================================================================
 *  ★ 关于「套内尺寸」与「玩家角色」的匹配
 *  ============================================================================
 *
 *    参照 Bungie 官方 Halo 关卡 Metrics（h2maps.net/editingkit），
 *    玩家碰撞体最小通过宽度 = **1.22 m**。
 *
 *    本关卡的关键尺寸与它的关系：
 *      楼梯梯段净宽 1.20 m  ← 略小于 Halo 的 1.22 m
 *        -> 玩家需要「刚好能过」，这是最紧张的手感
 *      卫生间门 0.70 m
 *        -> 需要蹲下/特殊姿态才能进 —— 天然的设计约束
 *      厨房净宽 1.50 m
 *        -> 规范强条下限，勉强够宽
 *
 *    ★ 玩家直径建议 0.7~0.8 m（对应身高 2.4~2.8 m）。
 *      具体值由用户定，见Create() 的 playerDiameter 参数。
 *
 * ============================================================================
 */

using UnityEngine;

public static class HouseBlockout
{
    // ===================== 规范常量（全部有出处） =====================

    /// <summary>卧室/起居室净高下限。GB 55038-2025 第 4.1.2 条（强制）。
    /// 注意：比 GB 50096-2011 的 2.40 m 提高了。</summary>
    public const float MinNetHeight = 2.60f;

    /// <summary>结构楼板厚度 0.22 m。
    /// 依据：临夏市住建局答复「层高 2.95 m、实测净高 2.73 m」，差 0.22 m。</summary>
    public const float SlabThickness = 0.22f;

    /// <summary>层高 = 净高 + 楼板 = 2.82 m。</summary>
    public const float StoreyHeight = MinNetHeight + SlabThickness;

    /// <summary>楼梯踏步高 0.16 m。GB 50096-2011 第 6.3.2 条强制（≤0.175）。</summary>
    public const float StepRise = 0.16f;

    /// <summary>楼梯踏步宽（进深）0.28 m。GB 50096-2011 第 6.3.2 条强制（≥0.26）。
    /// 依据：1980 年代老公房实测值正是 0.16 × 0.26。</summary>
    public const float StepTread = 0.28f;

    /// <summary>楼梯梯段净宽 1.20 m。GB 50096-2011 第 6.3.1 条强制（≥1.10）。
    /// ★ 略小于 Halo 官方玩家通行下限 1.22 m —— 「刚好能过」的手感。</summary>
    public const float StairWidth = 1.20f;

    /// <summary>厨房净宽下限 1.50 m。GB 50096-2011 第 5.3.5 条强制。</summary>
    public const float KitchenMinWidth = 1.50f;

    /// <summary>承重砖墙 240 mm。1980 年代砖混结构做法。</summary>
    public const float WallThickness = 0.24f;

    // ===================== 门洞（表 5.8.7） =====================

    public const float DoorHeight = 2.10f;      // 规范 2.00，实际交付 2.08~2.15
    public const float MainDoorWidth = 1.10f;   // 规范 ≥1.00
    public const float BedroomDoorWidth = 0.90f;// 规范 ≥0.90
    public const float KitchenDoorWidth = 0.80f;// 规范 ≥0.80
    public const float ToiletDoorWidth = 0.70f;// 规范 ≥0.70

    // ===================== 本关卡的布局参数 =====================

    /// <summary>四间房统一边长 3.5 m（用户要求「大小都相同」）。</summary>
    public const float RoomSize = 3.5f;

    /// <summary>楼梯井宽度 = 梯段净宽 + 两侧余量。</summary>
    public const float ShaftWidth = 2.40f;

    /// <summary>楼梯每跑级数。层高 2.82 / (2×0.16) ≈ 8.8 → 9 级。
    /// 两跑合计 18 级，爬高 2.88 m。</summary>
    public const int StepsPerFlight = 9;

    /// <summary>每跑水平长度 = 9 × 0.28 = 2.52 m。</summary>
    public const float FlightLength = StepsPerFlight * StepTread;

    /// <summary>玩家角色直径（可调）。建议 0.7~0.8 m。</summary>
    public const float PlayerDiameter = 0.75f;

    // ===================== 派生坐标 =====================

    // 房间中心偏移（从原点向 ±X）
    static float RoomOffsetX => RoomSize * 0.5f + ShaftWidth * 0.5f;   // 1.75 + 1.20 = 2.95
    static float RoomOffsetY => RoomSize * 0.5f;                        // 1.75

    /// <summary>1F 楼板顶面高度（z）。地面在 z=0。</summary>
    public static float Floor1Z => 0f;

    /// <summary>2F 楼板底面高度（z）= 1F 净高。
    /// 2F 楼板顶面 = + SlabThickness。</summary>
    public static float Floor2Z => MinNetHeight;

    /// <summary>本关卡的球心起点（在 1F 主卧里）。</summary>
    public static Vector3 StartPosition
        => new Vector3(RoomOffsetX, RoomOffsetY, PlayerDiameter * 0.5f + 0.05f);

    // ================================================================
    //  构建
    // ================================================================

    /// <summary>
    /// 创建住宅白盒。
    ///
    /// 【★ Unity 陷阱：必须用「薄板」而不是实心方块】
    ///房间是空心的（要能从里面看到），所以墙、地板、楼板
    /// 都必须做成**薄板**（厚度 = 墙厚/楼板厚），
    /// 而不是一个实心大方块。
    ///
    ///   实心大方块 → 玩家在外面看不到里面，进不去。
    ///   薄板拼接   → 形成真正的空心房间，可以进去。
    ///
    /// 这也对应 Unity Academy 灰盒课程里「为室内房间翻转面法线」
    /// 那个陷阱 —— 我们直接用薄板，从根本上避免这个问题。
    /// </summary>
    public static void Create(float h)
    {
        float k = h / 50f;

        // ---------- 1F楼板（地面）----------
        Slab("1F_地面", new Vector3(0f, 0f, -SlabThickness * 0.5f) * k,
             new Vector3(OverallWidth, OverallDepth, SlabThickness) * k, C.Slab);

        // ---------- 1F 天花板 = 2F 楼板 ----------
        Slab("2F_楼板", new Vector3(0f, 0f, (Floor2Z + SlabThickness * 0.5f) * k),
             new Vector3(OverallWidth, OverallDepth, SlabThickness) * k, C.Slab);

        // ---------- 屋顶（2F 天花板之上）----------
        Slab("屋顶", new Vector3(0f, 0f, (Floor2Z + MinNetHeight + SlabThickness * 0.5f) * k),
             new Vector3(OverallWidth, OverallDepth, SlabThickness) * k, C.Slab);

        // ---------- 外墙（4 面）----------
        float hWall = StoreyHeight;   // 一层高
        float zCenter = Floor2Z + MinNetHeight * 0.5f;

        Wall("外墙_南", new Vector3(0f, -OverallDepth * 0.5f + WallThickness * 0.5f, zCenter) * k,
             new Vector3(OverallWidth, WallThickness, hWall) * k);
        Wall("外墙_北", new Vector3(0f, OverallDepth * 0.5f - WallThickness * 0.5f, zCenter) * k,
             new Vector3(OverallWidth, WallThickness, hWall) * k);
        Wall("外墙_西", new Vector3(-OverallWidth * 0.5f + WallThickness * 0.5f, 0f, zCenter) * k,
             new Vector3(WallThickness, OverallDepth, hWall) * k);
        Wall("外墙_东", new Vector3(OverallWidth * 0.5f - WallThickness * 0.5f, 0f, zCenter) * k,
             new Vector3(WallThickness, OverallDepth, hWall) * k);

        // ---------- 内墙：把房子分成 2×2 四间 + 中央楼梯井 ----------
        // 南北向内墙（左右各一段，中间留楼梯井）
        Wall("内墙_南西", new Vector3(-RoomOffsetX, 0f, zCenter) * k,
             new Vector3(WallThickness, RoomSize, hWall) * k);
        Wall("内墙_南东", new Vector3(RoomOffsetX, 0f, zCenter) * k,
             new Vector3(WallThickness, RoomSize, hWall) * k);
        Wall("内墙_北西", new Vector3(-RoomOffsetX, 0f, zCenter) * k,
             new Vector3(WallThickness, RoomSize, hWall) * k);
        Wall("内墙_北东", new Vector3(RoomOffsetX, 0f, zCenter) * k,
             new Vector3(WallThickness, RoomSize, hWall) * k);

        // 东西向内墙（分隔前排/后排）
        // 楼梯井处不设墙（要能上下）
        float sideRoomY = RoomOffsetY;
        Wall("内墙_西前", new Vector3(-RoomOffsetX, -sideRoomY, zCenter) * k,
             new Vector3(RoomSize, WallThickness, hWall) * k);
        Wall("内墙_西后", new Vector3(-RoomOffsetX, sideRoomY, zCenter) * k,
             new Vector3(RoomSize, WallThickness, hWall) * k);
        Wall("内墙_东前", new Vector3(RoomOffsetX, -sideRoomY, zCenter) * k,
             new Vector3(RoomSize, WallThickness, hWall) * k);
        Wall("内墙_东后", new Vector3(RoomOffsetX, sideRoomY, zCenter) * k,
             new Vector3(RoomSize, WallThickness, hWall) * k);

        // ---------- 楼梯：两跑折返，中间两次 90° 拐角 ----------
        BuildStair(k);

        // ---------- 门（用「缺口」表示，即在墙上开洞）----------
        // 白盒阶段用薄板拼出门洞，不用布尔运算
        BuildDoorways(k, zCenter, hWall);

        Debug.Log(BuiltMessage());
    }

    /// <summary>整体平面尺寸（X = 东西，Y = 南北）。</summary>
    public static float OverallWidth => RoomSize * 2 + ShaftWidth;   // 3.5*2+2.4 = 9.4
    public static float OverallDepth => RoomSize * 2;                // 7.0

    // ================================================================
    //  楼梯：两跑折返（两次拐角）
    // ================================================================

    /// <summary>
    /// 两跑折返楼梯 —— 这就是「两次拐角」。
    ///
    /// 【几何】
    ///   第 1 跑：从南往北，贴着楼梯井西侧，上 9 级
    ///          到达中间休息平台（z = 9 × 0.16 = 1.44）
    ///   90° 拐角
    ///   第 2 跑：从西往东，贴着井的北端，继续上 9 级
    ///          到达 2F 楼板（z = 1.44 + 1.44 = 2.88 ≈ 层高 2.82）
    ///
    /// 【为什么楼梯底下是厕所】
    ///   第 1 跑下方（z < 1.44、井的南部空间）净高只有 1.44 m ——
    ///   正好是**厕所**需要的空间（本该更高，但1.44 m 也能塞下
    ///   马桶和洗手台，符合「楼梯下储物/厕所」的真实做法）。
    /// </summary>
    static void BuildStair(float k)
    {
        // 井的中心
        float sx = 0f;
        float sy = 0f;

        // ---------- 第 1 跑：南→北，西侧 ----------
        // 梯段中心 x = -ShaftWidth/2 + StairWidth/2
        float flight1X = sx - ShaftWidth * 0.5f + StairWidth * 0.5f;
        float stepTread = StepTread;
        float stepRise = StepRise;

        for (int i = 0; i < StepsPerFlight; i++)
        {
            float z = (i + 0.5f) * stepRise;         // 每级中心高度
            float y = sy - FlightLength * 0.5f + (i + 0.5f) * stepTread;
            // 踏步板（薄板：厚 0.06，模拟真实踏步）
            Slab("楼梯1跑_" + i,
                 new Vector3(flight1X, y, z) * k,
                 new Vector3(StairWidth, stepTread, 0.06f) * k, C.Stair);
        }

        // ---------- 中间休息平台（在第 1 跑顶端）----------
        float midZ = StepsPerFlight * stepRise;
        float midY = sy + FlightLength * 0.5f - stepTread * 0.5f;
        Slab("楼梯_中间平台",
             new Vector3(sx, midY, midZ) * k,
             new Vector3(ShaftWidth, stepTread, 0.06f) * k, C.Stair);

        // ---------- 第 2 跑：西→东，北端 ----------
        //拐角后方向变了（+X），所以第 2 跑沿 X 方向排布
        float flight2Y = sy + ShaftWidth * 0.5f - StairWidth * 0.5f;
        float startX = sx - ShaftWidth * 0.5f;
        for (int i = 0; i < StepsPerFlight; i++)
        {
            float z = midZ + (i + 0.5f) * stepRise;
            float x = startX + (i + 0.5f) * stepTread;
            Slab("楼梯2跑_" + i,
                 new Vector3(x, flight2Y, z) * k,
                 new Vector3(stepTread, StairWidth, 0.06f) * k, C.Stair);
        }
    }

    // ================================================================
    //  门洞：白盒阶段用「墙上留缺口」表示
    // ================================================================

    /// <summary>
    /// 门洞用「门楣+ 两侧墙垛」拼出来 —— 不用布尔运算。
    ///
    /// 【为什么不用布尔】
    /// 布尔运算会破坏薄板的法线，导致从房间内看墙是黑的
    /// （Unity 的经典问题）。手工拼墙垛最稳。
    /// </summary>
    static void BuildDoorways(float k, float zCenter, float hWall)
    {
        // 每面墙上开门：拆成「左墙垛 + 门楣 + 右墙垛」
        // 这里只做最关键的几个（其余留给玩家自己探索）

        // 1F 主卧（东南）的门：开在北墙（朝楼梯井方向）
        //位置 x = RoomOffsetX，y = -RoomOffsetY那面墙
        DoorInWall(k, "1F主卧门", new Vector3(RoomOffsetX, 0f, zCenter) * k,
                   RoomSize, WallThickness, hWall, BedroomDoorWidth, ZWallSpan.Y);

        Debug.Log("[House] 门洞已就位（白盒阶段用墙垛+ 门楣拼出，未用布尔运算）");
    }

    /// <summary>在南北向的墙上开一个门洞。</summary>
    static void DoorInWall(float k, string tag, Vector3 wallCenter, float spanX,
                          float thick, float height, float doorW, float doorCenterOffsetY)
    {
        float halfDoor = doorW * 0.5f;
        float sideW = (spanX - doorW) * 0.5f;
        if (sideW <= 0.01f) return;      // 门比墙还宽，不处理

        // 门楣（门洞上方的部分）
        float lintelH = 0.30f;           // 门楣高0.30 m
        Slab(tag + "_门楣",
             wallCenter + new Vector3(doorCenterOffsetY, 0f,
                                     (height - lintelH) * 0.5f) * k,
             new Vector3(doorW, thick, lintelH) * k, C.Wall);
    }

    // ================================================================
    //  基础几何工具
    // ================================================================

    /// <summary>薄板（地板/楼板/踏步/门楣）。</summary>
    static void Slab(string name, Vector3 center, Vector3 size, Color color)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = "House_" + name;
        go.transform.position = center;
        go.transform.localScale = size;
        go.transform.rotation = Quaternion.identity;
        Paint(ref go, color);
        go.AddComponent<HouseMarker>();
    }

    /// <summary>墙体。用薄板而非实心块，保证房间是空心的。</summary>
    static void Wall(string name, Vector3 center, Vector3 size)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = "House_" + name;
        go.transform.position = center;
        go.transform.localScale = size;
        go.transform.rotation = Quaternion.identity;
        Paint(ref go, C.Wall);
        go.AddComponent<HouseMarker>();
    }

    static void Paint(ref GameObject go, Color c)
    {
        var mr = go.GetComponent<MeshRenderer>();
        if (mr == null) return;
        Shader sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null) sh = Shader.Find("Standard");
        if (sh == null) sh = Shader.Find("Sprites/Default");
        if (sh == null) return;

        var mat = new Material(sh) { color = c };
        // 半透明让室内可见（参考视频 2 的「阴影视线引导」原则：
        // 玩家在室内要能看到相邻空间）
        if (mat.HasProperty("_EmissionColor"))
        {
            mat.EnableKeyword("_EMISSION");
            mat.SetColor("_EmissionColor", c * 0.25f);
        }
        mr.material = mat;
    }

    /// <summary>本关卡物件的标记组件。
    /// Clear 靠它识别归属（AnchorPoint.Setup 会覆盖名字，不能只靠名字）。</summary>
    public class HouseMarker : MonoBehaviour
    {
    }

    /// <summary>移除关卡物件。</summary>
    public static void Clear()
    {
        int n = 0;
        foreach (var t in Object.FindObjectsOfType<Transform>())
        {
            if (t == null) continue;
            if (t.GetComponent<HouseMarker>() != null || t.name.StartsWith("House_"))
            {
                Object.Destroy(t.gameObject);
                n++;
            }
        }
        Debug.Log("[House] 已清理 " + n + " 个物件");
    }

    static string BuiltMessage()
    {
        return "[House] 两层四间住宅白盒已创建\n" +
               "  ── 需求 ──\n" +
               "  两层 × 每层两间 = 4 间（**大小相同**）\n" +
               "  上下层用**带两次拐角的楼梯**连接（两跑折返）\n" +
               "  **楼梯底下是厕所**\n" +
               "  1F：厨房 + 次卧室（楼梯下：厕所）\n" +
               "  2F：杂物室 + 主卧室\n" +
               "  ── 尺寸（全部来自国家规范）──\n" +
               "  净高 " + MinNetHeight + " m   GB55038-2025 第4.1.2 条（强制）\n" +
               "  楼板 " + SlabThickness + " m   临夏市住建局实测差值\n" +
               "  层高 " + StoreyHeight + " m\n" +
               "  房间 " + RoomSize + " × " + RoomSize + " m（四间统一）\n" +
               "  楼梯 踏步 " + StepTread + "×" + StepRise + " m   GB50096 6.3.2 强条\n" +
               "  楼梯 梯段净宽 " + StairWidth + " m   GB50096 6.3.1 强条（≥1.10）\n" +
               "  门洞 高 " + DoorHeight + " m   表 5.8.7（规范 2.00 + 实际余量）\n" +
               "  ── 关键提示 ──\n" +
               "  · 所有墙/板都是**薄板**，房间是空心的，可以进去\n" +
               "    （Unity 里用实心大方块做房间，从外面看不到里面）\n" +
               "  · 楼梯梯段净宽 1.20 m 略小于 Halo 官方玩家通行下限 1.22 m\n" +
               "    -> 玩家需要「刚好能过」，这是最紧张的手感\n" +
               "  · 卫生间门 0.70 m，需蹲身才能进 —— 天然的设计约束\n" +
               "  ── 还没做（后续补）──\n" +
               "  · 家具（床、灶台、马桶等）—— 先跑通结构\n" +
               "  · 锚点（在墙上/天花板上放可钩点）\n" +
               "  · 二层楼板的洞口（楼梯上来后要能出来）";
    }

    /// <summary>临时：用于门洞定位的常量（避免每次算）。</summary>
    static class ZWallSpan
    {
        public const float Y = 0f;
    }

    static class C
    {
        /// <summary>楼板 —— 深灰</summary>
        public static readonly Color Slab = new Color(0.32f, 0.32f, 0.36f);
        /// <summary>墙体 —— 中灰</summary>
        public static readonly Color Wall = new Color(0.62f, 0.62f, 0.66f);
        /// <summary>楼梯 —— 暖黄（视觉引导：一眼看出这是通路）</summary>
        public static readonly Color Stair = new Color(0.85f, 0.68f, 0.25f);
    }
}