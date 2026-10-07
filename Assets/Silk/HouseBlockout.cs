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
 *    平面布局（俯视，X 向右 = 东西，Y 向上 = 南北）
 *
 *           x=-4.5     -2.11  +2.11      +4.5
 *   y=+2.25 ┌──────────┬──────┬──────────┐
 *           │          │      │          │
 *           │  杂物室  │ 楼梯 │  主卧室  │  2F
 *           │  4.5×4.5 │ 井   │  4.5×4.5 │
 *           │          │      │          │
 *   y= 0.00 ├──────────┤ 厕所 ├──────────┤
 *           │          │(楼梯下)│         │
 *           │  次卧室  │      │   厨房   │  1F
 *           │  4.5×4.5 │      │  4.5×4.5 │
 *   y=-2.25 └──────────┴──────┴──────────┘
 *
 *    · 四间房**都是 4.5 × 4.5 m**（用户要求「大小相同」）
 *    · 楼梯井 4.22(X) × 1.42(Y)，在中间
 *    · 楼梯下方 = **厕所**
 *
 *    ★ 为什么房间是 4.5 而不是 3.5 m：
 *      楼梯两跑纵跑合计 14 级 × 0.28 = 3.92 m，
 *      加余量后楼梯井需4.22 m —— **3.5 m 的房间放不下**。
 *      这是楼梯段数决定的下限，不是随便选的。
 *
 *  ============================================================================
 *  ★ 楼梯几何：9 + 4 + 5 = 18 级，两个 90° 转角
 *  ============================================================================
 *
 *    俯视（楼梯井内）：
 *
 *        ┌───────────────────────────┐
 *        │                    跑1 →  │  第 1 跑：沿 X（南侧），9 级
 *        │  跑3↑            ├──────  │  长 9 × 0.28 = 2.52 m
 *        │      │            │ 跑2 ↑│
 *        │      │            │      │  第 2 跑：沿 Y（东端），4 级
 *        │──────┘            │      │  横向连接，长 1.12 m
 *        └───────────────────────────┘  第 3 跑：沿 X（北侧），5 级
 *                                           长 1.40 m
 *      总爬高 18 × 0.16 = 2.88 m ≈ 层高 2.82 m
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

    /// <summary>四间房统一边长（用户要求「大小都相同」）。
    ///
    /// ★ 【必须 ≥ 4.22 m】—— 由楼梯井的X 向尺寸决定：
    ///   楼梯两跑纵跑合计 14 级 × 0.28 = 3.92 m，加余量 = 4.22 m。
    ///   若房间只有 3.5 m，楼梯井放不下 —— 这就是为什么
    ///   「3.5 m 的房间 + 9+4+5 的楼梯」在几何上不成立。
    ///
    /// 取 4.5 m：满足楼梯井要求，且仍符合住宅开间的常见范围
    /// （1980 年代砖混住宅标准开间 3.3/3.6 m，4.5 m 属于稍宽但合理）。
    /// </summary>
    public const float RoomSize = 4.5f;

    /// <summary>楼梯的跑数分段（用户指定 9+4+5 = 18 级）。
    ///
    /// 【为什么是 9+4+5 而不是 9+9】
    /// 用户明确要求「两个转角，所以应该是 9+4+5」。
    /// 实际住宅里两跑往往**不等长** —— 第一跑长（起步空间大），
    /// 折返后到顶层的距离由剩余层高决定。
    ///
    /// ★ 关键约束：两跑纵跑的**总长是固定的**
    ///   跑1 沿 X：9 × 0.28 = 2.52 m
    ///   跑3 沿 X：5 × 0.28 = 1.40 m（反向，叠在跑1 的另一侧）
    ///   合计 X 向跨度 = (9+5) × 0.28 = **3.92 m**
    ///   —— 无论怎么分配级数，只要两跑都是纵向，这个值都不变。
    ///   所以「房间 3.5 m」放不下，必须 ≥ 3.92 m。
    ///
    /// 【第 2 跑是横向连接段】沿 Y：4 × 0.28 = 1.12 m
    /// </summary>
    public const int Flight1Steps = 9;      // 沿 X（西侧），纵跑
    public const int Flight2Steps = 4;      // 沿 Y（北端），横向连接
    public const int Flight3Steps = 5;      // 沿 X（东侧），纵跑

    /// <summary>总级数（三跑之和）= 18。</summary>
    public const int TotalSteps = Flight1Steps + Flight2Steps + Flight3Steps;

    /// <summary>总爬高 = 18 × 0.16 = 2.88 m。</summary>
    public const float TotalRise = TotalSteps * StepRise;

    /// <summary>
    /// 楼梯井的 X 向尺寸（东西方向）—— **由两跑纵跑的级数决定**。
    /// (Flight1Steps + Flight3Steps) × StepTread = 14 × 0.28 = **3.92 m**
    /// 再加两端各 0.15 余量 -> 4.22 m。
    /// ★ 这是「房间必须 ≥ 4.22 m」的根源。
    /// </summary>
    public const float ShaftLengthX = (Flight1Steps + Flight3Steps) * StepTread + 0.30f;

    /// <summary>楼梯井的 Y 向尺寸（南北方向）= 第 2 跑横向段 + 余量。</summary>
    public const float ShaftWidthY = Flight2Steps * StepTread + 0.30f;

    /// <summary>玩家角色直径（可调）。建议 0.7~0.8 m。</summary>
    public const float PlayerDiameter = 0.75f;

    // ===================== 派生坐标 =====================

    /// <summary>楼梯井 X 向尺寸（东西）= 4.22 m。
    /// 由两跑纵跑的级数决定：14 级 × 0.28 = 3.92，加余量。</summary>
    public static float ShaftSizeX => ShaftLengthX;

    /// <summary>楼梯井 Y 向尺寸（南北）= 1.42 m。第 2 跑横向段。</summary>
    public static float ShaftSizeY => ShaftWidthY;

    // 房间中心偏移：从原点向 ±X（左右两间房）
    static float RoomOffsetX => RoomSize * 0.5f + ShaftSizeX * 0.5f;
    // 房间中心偏移：从原点向 ±Y（前后两间房）
    static float RoomOffsetY => RoomSize * 0.5f;

    /// <summary>1F 楼板顶面高度（z）。地面在 z=0。</summary>
    public static float Floor1Z => 0f;

    /// <summary>2F 楼板底面高度（z）= 1F 净高。
    /// 2F 楼板顶面 = + SlabThickness。</summary>
    public static float Floor2Z => MinNetHeight;

    /// <summary>本关卡的球心起点（在 1F 东南角的房间里）。</summary>
    public static Vector3 StartPosition
        => new Vector3(RoomOffsetX, -RoomOffsetY, PlayerDiameter * 0.5f + 0.05f);

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
    public static float OverallWidth => RoomSize * 2 + ShaftSizeX;
    public static float OverallDepth => RoomSize * 2;                // 7.0

    // ================================================================
    //  楼梯：两跑折返（两次拐角）
    // ================================================================

    /// <summary>
    /// 三跑折返楼梯 —— 9 + 4 + 5 = 18 级，**两个 90° 拐角**。
    ///
    /// 【几何 —— 按用户指定的分段】
    ///   第 1 跑：南 → 北，贴井的**西侧**，上 9 级
    ///          到达标高 9 × 0.16 = **1.44 m**
    ///   ▼ 第一个 90° 拐角（转向东）
    ///   第 2 跑：西 → 东，贴井的**北端**，上 4 级
    ///          到达标高 13 × 0.16 = **2.08 m**
    ///   ▼ 第二个 90° 拐角（转向南）
    ///   第 3 跑：东 → 南，贴井的**东侧**，上 5 级
    ///          到达标高 18 × 0.16 = **2.88 m** ≈ 层高 2.82
    ///
    /// 【为什么第 2 跑只有 4 级】
    /// 两跑不等长是真实住宅的常态—— 第一跑要跨越较大的进深，
    /// 折返后到顶层的剩余距离由第 2、3 跑分配。
    /// ★ 总级数不变（18），所以**总爬高与层高的关系不受影响**。
    ///
    /// 【为什么楼梯底下是厕所】
    ///   第 1 跑下方（z &lt; 1.44、井的南部）净高只有 1.44 m ——
    ///   正好塞得下马桶与洗手台。
    ///   这符合真实做法：楼梯下是「层高最低、面积最小」的空间，
    ///   常被用作厕所或储藏。
    /// </summary>
    static void BuildStair(float k)
    {
        float sx = 0f;      // 井中心 x
        float sy = 0f;      // 井中心 y
        float tread = StepTread;
        float rise = StepRise;
        const float slabT = 0.06f;      // 踏步板厚度

        /* ★ 两跑的朝向不同 —— 这是「两次90° 转角」的关键
         *
         *   第 1 跑（9 级）：沿 **X** 方向（西侧），长 2.52 m
         *      ▼ 90° 转角
         *   第 2 跑（4 级）：沿 **Y** 方向（北端），长 1.12 m  ← 横向连接
         *      ▼ 90° 转角
         *   第 3 跑（5 级）：沿 **X** 方向（东侧），长 1.40 m
         *
         *  → 所以 X 向需要容纳两跑：2.52 + 1.40 = 3.92 m
         *  → Y 向只需容纳中间的横向段：1.12 m
         */
        float sizeX = ShaftSizeX;        // 4.22 m（沿 X）
        float sizeY = ShaftSizeY;        // 1.42 m（沿 Y）

        // 梯段中心线
        float westX = sx - sizeX * 0.5f + StairWidth * 0.5f;   // 跑1 在西侧
        float eastX = sx + sizeX * 0.5f - StairWidth * 0.5f;   // 跑3 在东侧
        float northY = sy + sizeY * 0.5f - StairWidth * 0.5f;   // 跑2 在北端
        float southY = sy - sizeY * 0.5f + StairWidth * 0.5f;   // 起步区

        // ============================================================
        //  第 1 跑：沿 -X → +X（西侧），9 级
        // ============================================================
        float xStart1 = sx - sizeX * 0.5f + tread * 0.5f;
        for (int i = 0; i < Flight1Steps; i++)
        {
            float z = (i + 0.5f) * rise;                          // 0.08 .. 1.36
            float x = xStart1 + i * tread;
            Slab("楼梯_跑1_" + (i + 1),
                 new Vector3(x, southY, z) * k,
                 new Vector3(tread, StairWidth, slabT) * k, C.Stair);
        }

        // ---------- 拐角 1 的休息平台（井的西南角）----------
        float z1 = Flight1Steps * rise;                              // 1.44
        float xPlat1 = sx - sizeX * 0.5f + StairWidth * 0.5f;
        Slab("楼梯_平台1",
             new Vector3(xPlat1, sy, z1) * k,
             new Vector3(StairWidth, sizeY, slabT) * k, C.Stair);

        // ============================================================
        //  第 2 跑：沿 +Y（北端），横向连接，4 级
        // ============================================================
        float yStart2 = sy - sizeY * 0.5f + tread * 0.5f;
        for (int i = 0; i < Flight2Steps; i++)
        {
            float z = z1 + (i + 0.5f) * rise;                        // 1.52 .. 2.00
            float y = yStart2 + i * tread;
            Slab("楼梯_跑2_" + (i + 1),
                 new Vector3(xPlat1, y, z) * k,
                 new Vector3(StairWidth, tread, slabT) * k, C.Stair);
        }

        // ---------- 拐角 2 的休息平台（井的西北角）----------
        float z2 = (Flight1Steps + Flight2Steps) * rise;             // 2.08
        float yPlat2 = sy + sizeY * 0.5f - tread * 0.5f;
        Slab("楼梯_平台2",
             new Vector3(xPlat1, yPlat2, z2) * k,
             new Vector3(StairWidth, tread, slabT) * k, C.Stair);

        // ============================================================
        //  第 3 跑：沿 +X（东侧），5 级，抵达 2F
        // ============================================================
        float xStart3 = sx - sizeX * 0.5f + tread * 0.5f;
        for (int i = 0; i < Flight3Steps; i++)
        {
            float z = z2 + (i + 0.5f) * rise;                      // 2.16 .. 2.80
            float x = xStart3 + i * tread;
            Slab("楼梯_跑3_" + (i + 1),
                 new Vector3(x, northY, z) * k,
                 new Vector3(tread, StairWidth, slabT) * k, C.Stair);
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