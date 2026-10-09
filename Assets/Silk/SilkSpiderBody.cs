using UnityEngine;

/// <summary>
/// 蜘蛛步态调度 —— ★ 交替四足步态（alternating tetrapod gait）。
///
/// 【★★★ 本文件是第1 次「真重构」，推翻原单段直线版】
///
///   用户原话（2026-10-09）：
///   「现在的蜘蛛的腿好像不对。而且比例也不对。移动两步就需要重来。
///     现在的蜘蛛像是小球加腿啊。我要的是蜘蛛啊，不是小球加腿啊。」
///
/// 【★ 推翻的两处根本错误】
///
///   错误 1：相位 `(i / 8f) * cycleTime`
///     → 这是「8 条腿均匀错开 1/8 周期」，产生的是**波浪式乱步**。
///     → 真实蜘蛛不是这样走的。
///     ★ 生物学实测（Grammostola rosea，Wiley 2019，DOI 10.1155/2019/4617212）：
///         蜘蛛平地行走用 **交替四足步态**（alternating tetrapod gait），
///         8 条腿分成**两组各4 条**，交替支撑。
///         · 同侧：**L1 与 L3 同相**，**L2 与 L4 同相**（R 同理）
///         · 对角：**L1 与 R2 同相**，L2 与 R1 同相，L3 与 R4 同相，L4 与 R3 同相
///         · 相邻腿**反相**：L1 支撑时 L2 摆动
///         · 典型起步顺序 4-2-3-1（68.1% 出现率）或其轮换
///         · 占空比 duty factor = 0.6~0.75 → **大部分时间 5~6 条腿着地**
///         · 任何一组4 条腿忽略 → 退化成三角步态（很多节肢动物用）
///     → 论文原文：平地上「at least five legs on the ground at all times」
///       「Most of the time, six legs were on the ground」。
///
///   错误 2：腿只有 1 段（LineRenderer 直线）
///     → 没有膝关节折角 → 从任何角度都不是蜘蛛。
///     → 现在改为 SilkSpiderAnatomy + SilkSpiderLimb（5 节骨骼 + FABRIK IK）。
///     形态依据：Locomotor 教程视频的真实 FBX 骨架
///     `leg_L_1_1 → leg_L_1_2 → ... → leg_L_1_5`（每条腿 5 节）。
///
/// 【★ 保留原实现里已经踩过坑验证过的部分】
///   · 足端射线的**起点插值**（footRayStartBlend=0.5）——
///     来源视频 14:20–16:10，作者先试「只用骨盆上方」明确 did not help。
///   · 顺序：脚当前点 + 速度向量 → 从投影点 trace → 命中点。
///     反过来做腿会插进地面（视频最大的坑）。
///   · footRay 必须先于腿建立（TraceGround 第一件事就SetRadius）。
///   · 距离触发时给随机负偏移，否则所有腿重新同步（视频 58:30）。
///   · 速度平滑 0.1 低通滤波，防止帧率抖动传导到落点预测。
///   · 射线 ignore 自身 Collider，否则打到自己导致贴面抖动。
///
/// 【坐标系】Z-up。
/// 【零新增按键】本文件完全不读输入，跟随 SilkSpiderSurfaceMove。
/// </summary>
[RequireComponent(typeof(SilkSpiderSurfaceMove))]
[DisallowMultipleComponent]
public class SilkSpiderBody : MonoBehaviour
{
    // ================================================================
    //  步态参数
    // ================================================================

    [Header("步态 —— 交替四足步态")]
    [Tooltip("一个完整步态周期（秒）。★ 交替四足步态一个周期 = 每条腿走1 步。\n"
        + "生物实测平地行走频率约 1~2 Hz，这里默认 1.1 Hz。")]
    public float cycleTime = 0.9f;

    [Tooltip("★ 占空比duty factor —— 腿在「一个周期内着地」的时间占比。\n"
        + "来源：Grammostola rosea 实测 0.60~0.75（平硬地面偏大，斜软地面偏小）。\n"
        + "★ 含义：0.65 表示 65% 时间这条腿在地上支撑，35% 在空中摆动。\n"
        + "  取 0.65 → 平均同时着地腿数 = 8 × 0.65 ≈ 5.2 条，\n"
        + "  与论文「大部分时间 6 条腿着地、最少 5 条」吻合。\n"
        + "★ 必须 ≥0.5，否则着地腿数 < 4 → 站不住。")]
    [Range(0.5f, 0.9f)] public float dutyFactor = 0.65f;

    [Tooltip("★ 每条腿的相位偏移（归一化 0~1，一个周期为单位）。\n"
        + "**这个数组就是交替四足步态的全部秘密** ——\n"
        + "  8 个数按 L1,L2,L3,L4,R1,R2,R3,R4 排列。\n"
        + "  当前值来自生物实测：同侧隔条同相、对角同相、相邻反相。\n"
        + "  改这个数组就能换步态，不用改代码。")]
    public float[] phaseOffsets = new float[]
    {
        0.00f,   // L1
        0.50f,   // L2  ← 与 L1 反相
        0.00f,   // L3  ← 与 L1 同相（隔一条同相）
        0.50f,   // L4  ← 与 L3 反相
        0.50f,   // R1  ← 与 L2 同相（对角同相）
        0.00f,   // R2  ← 与 L1 同相（对角同相）
        0.50f,   // R3
        0.00f,// R4
    };

    [Tooltip("起步顺序偏移（秒）。★ 来源：论文实测起步顺序 4-2-3-1 占 68.1%。\n"
        + "给四条主相位的腿加微小时差，避免 8 条腿整齐同步启动。\n"
        + "0 = 关闭（会看到腿整齐同步，很机械）")]
    public float startStagger = 0.06f;

    [Tooltip("★ 抬腿插值窗口（占周期的比例）。\n"
        + "只在摆动相的前半段完成抬落，后半段已经在支撑。\n"
        + "交替四足步态的摆动相 = (1 - dutyFactor) = 0.35 → 取 0.22 合理。")]
    [Range(0.05f, 0.6f)] public float swingPortion = 0.22f;

    [Header("抬腿")]
    [Tooltip("抬腿峰值高度（格）。默认取球半径的 0.9 倍")]
    public float liftHeight = -1f;

    [Tooltip("★ 抬腿高度按速度缩放 —— 静止时不抬腿。\n"
        + "来源视频 47:00–49:00。0 = 关闭（会看到站着不动也在原地踏步）")]
    public bool scaleLiftBySpeed = true;

    [Tooltip("抬到满高度所需速度（格/秒）。默认取 runSpeed")]
    public float liftFullSpeed = -1f;

    [Header("落点探测")]
    [Tooltip("足端射线上探长度（格）。默认取球半径的 3 倍")]
    public float footRayUp = -1f;
    [Tooltip("足端射线下探长度（格）。默认取球半径的 3 倍")]
    public float footRayDown = -1f;

    [Tooltip("★ 射线起点在「骨盆上方」与「脚上方」之间的插值比例。\n"
        + "0 = 从脚正上方打；1 = 从骨盆正上方打；0.5 = 两者中间。\n"
        + "★ 为什么不能写死：射线垂直但腿是斜的，脚越过边缘时用脚正上方会打偏\n"
        + "  → 腿插进几何体。作者试过「只用骨盆上方」，明确说 did not help。")]
    [Range(0f, 1f)] public float footRayStartBlend = 0.5f;

    [Tooltip("足端射线半径（格）。默认取球半径的 0.12 倍")]
    public float footRayRadius = -1f;

    [Tooltip("射线的碰撞层。默认 Everything —— 与 SilkSpiderSurfaceMove 一致")]
    public LayerMask surfaceMask = ~0;

    [Tooltip("★ 脚离**站位锚点**多远就强制换落点（格）。\n"
        + "0 = 用腿长的 0.7 倍。\n"
        + "★ 2026-10-09 语义修正：原来测的是「离旧落点」，\n"
        + "  那是「移动两步就需要重来」的病根（旧落点永远在身体身后，\n"
        + "  越走越远 → 每帧都触发换点 → 无限连续迈步）。\n"
        + "  现在测的是「离站位」→ 腿永远朝站位方向回收。")]
    public float forceStepDistance = -1f;

    [Tooltip("★ 强制换点时给该腿的随机负偏移（秒）。\n"
        + "来源视频 58:30：「都重置到同一时刻，腿就同步了」。\n"
        + "0 = 关闭（会看到 8 条腿齐步走）")]
    public float desyncJitter = 0.18f;

    [Header("运动预测")]
    [Tooltip("速度预测投射比例。1 = 完全按当前速度投射落点")]
    [Range(0f, 2f)] public float velocityPrediction = 1f;
    [Tooltip("速度向量长度上限（格）。★ 必须有，否则跑起来腿会被甩到身后")]
    public float maxPredictLength = -1f;
    [Tooltip("速度平滑系数。★ 视频里作者用 0.1：「逐渐逼近真实值，不会一直累积误差」")]
    [Range(0.01f, 1f)] public float velocitySmoothing = 0.1f;

    // ================================================================
    //  站位锚点与换点判定
    //  来源：Docs/逆向资料/spider_ik参考实现分析.md（PhilS94 真实 Unity 工程）
    // ================================================================

    [Header("站位锚点（★ 「移动两步就需要重来」的根因修复）")]
    [Tooltip("★ 每条腿在**身体局部坐标**里的「待机站位」外扩比例（相对腿长）。\n"
        + "0.62 = 腿根往外 0.62 倍腿长的水平距离。\n"
        + "★ 为什么必须有这个：\n"
        + "  没有锚点 → 腿只会在「离旧落点太远」时才换点，\n"
        + "  而旧落点永远在身体身后 → 越走越远 → 每帧都触发换点\n"
        + "  → 无限连续迈步，就是你说的「移动两步就需要重来」。\n"
        + "  有锚点 → 腿永远「朝站位方向」迈，而不是「被甩开就追」。")]
    [Range(0.2f, 1.5f)] public float defaultFootSpread = 0.62f;

    [Tooltip("★ 向站位方向**过冲**的倍数（1 = 刚好走到站位）。\n"
        + "来源：spider_ik 的 defaultOvershootMultiplier = 1.5。\n"
        + "★ 为什么 >1：身体在摆动相期间一直在前进，\n"
        + "  若只走到站位中心，落点会落在身体**后方** → 腿永远追不上。\n"
        + "  过冲 1.5 让落点超前于站位，摆动结束时正好在身体下方。\n"
        + "★ 但过冲量会被 overshootMaxRatio 封顶 —— 见该参数说明。")]
    [Range(1f, 2f)] public float overshootMultiplier = 1.5f;

    [Tooltip("★ 迈步后「落点到站位」的偏差上限（占触发距离 farLimit 的比例）。\n"
        + "★ 这是本项目「移动两步就重来」的**第二个独立机制**，来自 metapika\n"
        + "  的 unity-procedural-animation 作者原话：\n"
        + "    \"Overhead Amount — DO NOT set it higher than or equal to\n"
        + "     the Step Distance. If you do, the leg will move forward\n"
        + "     and backwards endlessly.\"\n"
        + "  作者建议：StepDistance − 0.15（快腿）/ − 0.5（慢腿）。\n"
        + "★ 在我们公式里的正确含义：过冲+速度预测合起来，\n"
        + "  会把落点推到站位另一侧 → 下一帧偏差反向 → 前后反复。\n"
        + "  夹住最终偏差 < farLimit，这条振荡在数学上不可能发生。\n"
        + "★ 我第一版错在「只夹过冲量」—— 那样做反而更慢：\n"
        + "  偏差本来是几何衰减 dev→0.5×dev（9 步收敛），\n"
        + "  夹成固定步长后变成线性递减（要 200+ 步）。\n"
        + "  ★ 教训：先证性质再写代码，别凭直觉。")]
    [Range(0.3f, 0.95f)] public float maxDeviationRatio = 0.7f;

    [Tooltip("★ 腿收得太近时强制换点（占腿长的比例）。\n"
        + "来源：spider_ik 的 minDistance = 0.2 × chainLength。\n"
        + "★ 为什么需要：爬窄缝/贴墙时足端会怼到腿根，\n"
        +"  此时 FABRIK 会把腿拉直或抖动 → 步态看起来崩掉。")]
    [Range(0.05f, 0.5f)] public float minFootDistance = 0.2f;

    [Tooltip("★ 步时随速度缩放 —— **这是「步幅」能恒定的唯一办法**。\n"
        + "来源：spider_ik 的 calculateStepTime()：stepTime = k /速度。\n"
        + "★ 我原来的固定 cycleTime 是错的：\n"
        +"  慢走 → 每步跨太远（够不到，劈叉）\n"
        + "  快跑 → 每步跨太近（在地上拖，滑步）\n"
        + "  无论怎么调 cycleTime 都不对，因为它必须是速度的函数。\n"
        + "  开启后：周期 = clamp(cycleTime × (参考速度/实际速度), min, max)。\n"
        + "  含义：慢走时周期拉长（少迈大步子），快跑时周期缩短（多迈小步子）。")]
    public bool scaleCycleBySpeed = true;

    [Tooltip("步时缩放的参考速度（格/秒）。默认取 runSpeed ——\n"
        + "★ 即「在runSpeed 速度下周期 = cycleTime」")]
    public float cycleRefSpeed = -1f;

    [Tooltip("步时缩放后的周期下限（秒）。★ 防止速度趋0时周期爆炸 → 原地疯狂踏步")]
    public float minCycleTime = 0.28f;

    [Tooltip("★ 站立不动多少秒后停止迈步。\n"
        + "来源：spider_ik 的 stopSteppingAfterSecondsStill，\n"
        + "  作者原注释：\"This fixes the indefinite stepping going on.\"\n"
        + "★ 我原来只有「静止不抬腿」，但相位仍在推进 → 原地踏步。")]
    [Range(0f, 2f)] public float stopSteppingAfterStill = 0.35f;

    [Header("分级射线（防「腿卡死」）")]
    [Tooltip("★ 换点时依次尝试的射线方向数。\n"
        + "来源：spider_ik 的 updateCasts() —— 建 12 条射线（6 个方向 × 2 组），\n"
        + "  字典顺序即优先级，作者注释\"order is of very high importance\"。\n"
        + "★ 我原来只有 1 条射线，打不到就把相位压回支撑相\n"
        + "  → 这条腿**卡死不动**。多方向兜底后不会卡死。")]
    [Range(1, 6)] public int castDirectionCount = 6;

    [Tooltip("★ 换落点时允许的最大坡度（度）。0 = 不限制。\n"
        + "来源：spider_ik 对 Frontal 射线用 ±65° 过滤 ——\n"
        + "  太陡的坡不算落脚点，否则腿会插进悬崖。\n"
        + "★ 用 78°：比参考的 65° 宽松些，兼顾贴墙/屋顶场景。")]
    [Range(30f, 90f)] public float maxWalkableSlopeDeg = 78f;

    [Header("身体")]
    [Tooltip("★ 身体随步伐起伏的幅度（格）。\n"
        + "真实蜘蛛行走时头胸部有轻微的上下起伏与侧摆（步态副产物）。\n"
        + "0 = 完全不动（身体像一块滑板，很假）")]
    public float bodyBobAmount = -1f;

    [Tooltip("身体起伏的频率倍数（相对步态周期）")]
    public float bodyBobFreqMul = 2f;

    [Tooltip("★ 身体跟随平均足位（0~1）。腿动时身体朝那侧偏，像真的有重量。")]
    [Range(0f, 1f)] public float bodyFollow = 0.22f;

    [Header("调试")]
    [Tooltip("画出每条腿的锁定点（绿）、目标点（黄）、足端（青）")]
    public bool showDebug = false;

    [Tooltip("★ 默认勾上 —— 不勾就看不到腿，等于交付物不可见。")]
    public bool enableLegs = true;

    [Tooltip("显示实时步态图（8 条腿的时序条带）—— ★ 判断步态对不对最直观的工具")]
    public bool showGaitDiagram = false;

    // ================================================================
    //  运行时
    // ================================================================

    private SilkParkourController ctrl;
    private SilkSpiderSurfaceMove mover;
    private SilkSpiderAnatomy anatomy;

    /// <summary>每条腿的落点状态。</summary>
    private struct LegState
    {
        public Vector3 LockedTarget;     // 当前着地点
        public Vector3 NextTarget;       // 下一个落点
        public float Phase;              // 0~1 的步态相位
        public bool Initialized;

        // ===== ★ 2026-10-09新增：站位锚点（spider_ik 参考实现）=====

        /// <summary>
        /// 这条腿的「待机站位」，存在**身体局部坐标**里。
        /// ★ 有了它，腿才会「朝站位方向迈步」；
        ///   没有它，腿只会被身体越拖越远 → 无限连续迈步
        ///   （用户反馈的「移动两步就需要重来」）。
        /// </summary>
        public Vector3 DefaultLocal;
    }

    private LegState[] legState = new LegState[8];
    private Vector3[] footWorld = new Vector3[8];
    private Vector3[] rootWorld = new Vector3[8];

    /// <summary>★ 每条腿落点处的表面法线（供足端朝向跟随用）。</summary>
    private Vector3[] footNormals = new Vector3[8];

    private Vector3 previousWorldPos;
    private Vector3 velocity;
    private bool initialized;
    private Collider selfCollider;

    /// <summary>
    /// ★ 蜘蛛自己的碰撞代理（isTrigger 球，**无渲染器**）。
    /// 用于让足端射线能命中脚下地面、但不命中自己。
    /// 与旧的 Body 球并存 —— Body 已退成不可见代理。
    /// </summary>
    private SphereCollider spiderProxyCollider;
    private SilkSphereCast footRay;
    private float gaitClock;
    private Vector3 bodyBasePos;

    /// <summary>★ 站立计时（秒）—— 超过 stopSteppingAfterStill 就停止迈步。</summary>
    private float timeStandingStill;

    /// <summary>★ 腿长（格）—— 由解剖结构反查，用于所有以腿长为单位的阈值。</summary>
    private float legLengthCached = -1f;

    /// <summary>解剖结构（HUD / 调试查询用）。</summary>
    public SilkSpiderAnatomy Anatomy => anatomy;

    /// <summary>腿是否已建（HUD 查询）。</summary>
    public bool LegsBuilt => anatomy != null;

    /// <summary>球半径 —— 全项目唯一读取入口。</summary>
    private float BallRadius
    {
        get
        {
            ResolveReferences();
            if (ctrl != null && ctrl.visualRadius > 0f) return ctrl.visualRadius;
            return 1f;
        }
    }

    // ================================================================
    //  生命周期
    // ================================================================

    public static SilkSpiderBody AttachTo(SilkParkourController target)
    {
        SilkSpiderBody exist = target.GetComponent<SilkSpiderBody>();
        if (exist != null) return exist;
        return target.gameObject.AddComponent<SilkSpiderBody>();
    }

    private void Awake()
    {
        ResolveReferences();
    }

    private void ResolveReferences()
    {
        if (ctrl == null) ctrl = GetComponent<SilkParkourController>();
        if (ctrl == null) ctrl = FindObjectOfType<SilkParkourController>();
        if (mover == null) mover = GetComponent<SilkSpiderSurfaceMove>();
    }

    private void Start()
    {
        ResolveReferences();

        if (mover == null)
        {
            mover = gameObject.AddComponent<SilkSpiderSurfaceMove>();
            Debug.LogWarning("[SpiderBody] 缺少 SilkSpiderSurfaceMove，已自动补挂。");
        }

        float r = BallRadius;
        if (liftHeight <= 0f) liftHeight = r * 0.9f;
        if (footRayUp <= 0f) footRayUp = r * 3f;
        if (footRayDown <= 0f) footRayDown = r * 3f;
        if (footRayRadius <= 0f) footRayRadius = r * 0.12f;
        if (maxPredictLength <= 0f) maxPredictLength = r * 3f;
        if (bodyBobAmount <= 0f) bodyBobAmount = r * 0.16f;

        //★ 步态时序：footRay 必须先于 anatomy 建立，
        //  因为 InitializeFootTargets() 会调 TraceGround()，
        //  而 TraceGround 第一件事就是 footRay.SetRadius(...)
        //  → 顺序错了抛 NullReferenceException（踩过一次）。
        BuildFootRay();
        BuildAnatomy();
        InitializeFootTargets();

        previousWorldPos = transform.position;
        bodyBasePos = anatomy.VisualRoot.localPosition;
        gaitClock = 0f;
        initialized = true;

        Debug.Log("[SpiderBody] 蜘蛛就绪：头胸部 + 腹部 + 腹柄 + 螯肢 + "
                + "8 条五节腿（FABRIK IK）。步态 = 交替四足步态，"
                + "dutyFactor=" + dutyFactor.ToString("F2")
                + " → 平均着地腿数≈ " + (8 * dutyFactor).ToString("F1") + " 条。");
    }

    private System.Collections.IEnumerator AttachSelfColliderNextFrame()
    {
        yield return null;
        selfCollider = FindSelfCollider();
        if (selfCollider == null)
            Debug.LogWarning("[SpiderBody] 找不到自身 Collider，足端射线会打到自己。");

        if (footRay == null) yield break;

        footRay.SetIgnore(selfCollider);

        /*★★ 额外把「整个本体根」也加入忽略。
         *
         * 【为什么必须】独立化后本体上有**两个**会挡住射线的碰撞体：
         *   ① Body 的 groundProbe（旧球遗留）
         *   ② SpiderProxy（我新建的）
         * 而 SilkSphereCast.SetIgnore() 只能接收**一个** Collider，
         * 逐个SetIgnore 会互相覆盖，漏掉的那个照样挡住射线。
         * → 用忽略根 Transform 的方式一次排除全部子碰撞体。
         *
         * 【为什么这样安全】项目里所有几何体都在 Default 层（见 MEMORY 第二节），
         * 不能靠摘 Layer 排除。 SilkSphereCast.CastAll 里的判据是
         *     all[i].collider.transform.root == ignoreRoot
         * 而场景几何体不是本体的子级，root 不会等于本体根 → 不会被误排除。
         */
        footRay.SetIgnoreRoot(transform);
    }

    private Collider FindSelfCollider()
    {
        Collider c = GetComponent<Collider>();
        if (c != null) return c;
        Collider[] kids = GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < kids.Length; i++)
            if (kids[i] != null) return kids[i];
        return null;
    }

    // ================================================================
    //  构建
    // ================================================================

    private void BuildAnatomy()
    {
        anatomy = GetComponent<SilkSpiderAnatomy>();
        if (anatomy == null)
            anatomy = gameObject.AddComponent<SilkSpiderAnatomy>();

        anatomy.Build(transform);

        /*★★★ 独立化：蜘蛛有自己的可见体与碰撞体，不再依附球的造型。
         *
         * 【用户原话】「这个不还是球加腿吗？我现在要的是真的蜘蛛模型」
         *
         * 【两个独立根因，都必须修，缺一个就还是「球加腿」】
         *
         *   根因 1（已修，在 SilkSpiderAnatomy）：头胸部用 new GameObject() 创建，
         *     既无 MeshFilter 也无 MeshRenderer → Paint() 首行就 return →
         *     **头胸部完全不可见**。画面上只剩蓝球 + 8 条腿。
         *
         *   根因 2（本处）：玩家的 Body 是半径 1.25 的**蓝球**，
         *     蜘蛛解剖体是它的子物体。即使头胸部建出来了，
         *     也会被这个球**整个包住** → 视觉上还是「一个球」。
         *
         * 【正确架构 —— 视觉/物理彻底解耦】
         *   · SpiderRig（本组件 transform 下的独立根）
         *       ├─ 可视：头胸部 + 腹部 + 腹柄 + 螯肢 + 8 眼 + 8 条五节腿
         *       └─ 碰撞：SpiderProxy（SphereCollider 触发器，**无渲染器**）
         *   · Body / Nose：旧球形外观，渲染器关闭，仅保留原物理行为
         *
         *   ★ 为什么碰撞要自己建一个：
         *     球是**旧架构遗留**，它的半径(1.25) 与地面吸附逻辑深度耦合
         *     （ResolveGround / groundProbe / 相机距离 / 门洞净宽全按它算）。
         *     贸然删掉会连带打断一堆已验证的物理与关卡参数。
         *     → 正确做法：让球退成「不可见的碰撞代理」，
         *       可见的部分全部交给蜘蛛。以后换 FBX 模型时，
         *       只需替换可视部分，物理与关卡参数一个都不用动。
         */
        BuildSpiderProxyCollider();
        HideBallVisuals();

        Debug.Log("[SpiderBody] 蜘蛛独立化完成："
            + "可见体 = 头胸部 + 腹部 + 腹柄 + 螯肢 + 8 眼 + 8 条五节腿；"
            + "碰撞体 = SpiderProxy（球退为不可见代理）。"
            + "不再有「球加腿」。");
    }

    /// <summary>
    /// 给蜘蛛建一个独立的球形触发器碰撞体（**不带渲染器**）。
    ///
    /// 【为什么必须有】
    ///   足端射线（SilkSphereCast）要能命中自己脚下的地面，
    ///   但不能命中自己。旧的 Body 球提供了这件事，
    ///   现在 Body 不可见了，但**碰撞体不能跟着消失**——
    ///   否则足端射线会直接穿过自己的身体，或者身体失去地面吸附。
    ///
    /// 【为什么用 isTrigger】
    ///   与原Body 的 groundProbe 一致：不产生碰撞响应、不与物理体打架，
    ///   只作为地面探测与射线命中的目标。
    /// </summary>
    private void BuildSpiderProxyCollider()
    {
        var proxyGo = new GameObject("SpiderProxy");
        proxyGo.transform.SetParent(transform, false);
        proxyGo.transform.localPosition = Vector3.zero;

        var sc = proxyGo.AddComponent<SphereCollider>();
        sc.isTrigger = true;

        // ★ 半径换算：SphereCollider 的 radius 是**本地单位下的半径**，
        //   缩放会乘上去。所以要除掉 lossyScale，
        //   让最终世界半径 = visualRadius（与旧 Body 的视觉/物理半径一致）。
        //   不除 → 实际碰撞半径 = radius × lossyScale，会随父级缩放漂移。
        float scale = transform.lossyScale.x;
        if (scale < 0.0001f) scale = 1f;
        sc.radius = BallRadius / scale;

        spiderProxyCollider = sc;
    }

    /// <summary>
    /// 关掉玩家本体球（Body）与朝向球（Nose）的渲染器，只保留 Collider。
    ///
    /// ★ 这是「球加腿」的最后一道闸门：
    ///   只要球还可见，无论解剖体建得多好，画面中心永远是个球。
    /// </summary>
    private void HideBallVisuals()
    {
        int hidden = 0;

        HideRenderersUnder(transform, "Body", ref hidden);
        HideRenderersUnder(transform, "Nose", ref hidden);

        // ---- 兜底：直接挂在本体上的渲染器（没挂在 Body / Nose 下的话）----
        Renderer[] own = GetComponents<Renderer>();
        for (int i = 0; i < own.Length; i++)
        {
            if (own[i] == null || !own[i].enabled) continue;
            own[i].enabled = false;
            hidden++;
        }

        if (hidden == 0)
            Debug.LogWarning("[SpiderBody] 一个球体渲染器都没找到。"
                + "若画面上仍有蓝球，说明球的命名不是 Body/Nose，"
                + "请把这里的名字补上。");

        Debug.Log("[SpiderBody] 已隐藏旧球体渲染器 " + hidden + " 个（碰撞体保留）。");
    }

    /// <summary>递归关闭指定名字子物体下的所有渲染器。</summary>
    private static void HideRenderersUnder(Transform root, string name, ref int count)
    {
        Transform t = FindDeepChild(root, name);
        if (t == null)
        {
            // 找不到不算错：Nose 未必存在，Body 名字可能被改过
            return;
        }
        Renderer[] rs = t.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < rs.Length; i++)
        {
            if (rs[i] == null || !rs[i].enabled) continue;
            rs[i].enabled = false;
            count++;
        }
    }

    /// <summary>
    /// 递归找子物体（Transform.Find 只找直接子级）。
    /// Body 有可能挂在 visual 节点下而不是本体根下。
    /// </summary>
    private static Transform FindDeepChild(Transform root, string name)
    {
        if (root == null) return null;
        int n = root.childCount;
        for (int i = 0; i < n; i++)
        {
            Transform c = root.GetChild(i);
            if (c == null) continue;
            if (c.name == name) return c;
            Transform deep = FindDeepChild(c, name);
            if (deep != null) return deep;
        }
        return null;
    }

    private void BuildFootRay()
    {
        /* ★ 用不绑父的构造函数。
         *   绑定父版本会把端点存成父的局部坐标，
         *   但足端射线每帧都传显式起止（CastBetween），绑父反而误导。*/
        footRay = new SilkSphereCast(
            transform.position,
            transform.position - transform.up * footRayDown,
            footRayRadius);

        StartCoroutine(AttachSelfColliderNextFrame());
    }

    /// <summary>
    /// 初始化 8 条腿的落点。
    /// ★ 必须给全部 8 条都赋初值 —— 否则默认 Vector3.zero，
    ///   腿会朝世界原点伸过去（表现为瞬间炸开成一团）。
    ///   与视频作者踩的 "writes to blank array → out of bounds" 是同一个坑。
    /// </summary>
    private void InitializeFootTargets()
    {
        Vector3 up = SafeUp();

        legLengthCached = ResolveLegLength();
        timeStandingStill = 0f;

        for (int i = 0; i < 8; i++)
        {
            Vector3 root = RootWorld(i);

            // ★ 站位锚点（身体局部坐标）。**必须在算落点之前**——
            //   初始落点就应该落在站位上，而不是「腿根下方随便一点」。
            legState[i].DefaultLocal = ComputeDefaultLocal(i, up);

            Vector3 defaultWorld = transform.TransformPoint(legState[i].DefaultLocal);

            Vector3 guess = defaultWorld - up * 0.02f;

            Vector3 hit;
            if (TraceGround(guess, up, out hit))
                legState[i].LockedTarget = hit;
            else
                legState[i].LockedTarget = guess;

            legState[i].NextTarget = legState[i].LockedTarget;
            legState[i].Initialized = true;
            footWorld[i] = legState[i].LockedTarget;
            footNormals[i] = up;

            rootWorld[i] = root;
        }
    }

    /// <summary>
    /// ★ 反查腿长（格）。用于所有以「腿长」为单位的阈值
    ///   （最小强制换点距离、站位外扩）。
    /// 来源：spider_ik 的 chainLength = ikChain.calculateChainLength()。
    ///
    /// ★ 2026-10-09 修正：初版这里写成「测腿根到站位的距离」，
    ///   而站位又依赖腿长 → **循环依赖**。
    ///   腿长是解剖结构的固有属性，直接读它，不要绕。
    /// </summary>
    private float ResolveLegLength()
    {
        if (anatomy != null && anatomy.legLength > 0f)
            return anatomy.legLength;
        // 兜底：解剖默认腿长 = 球半径 × 3.6（见 SilkSpiderAnatomy.ResolveDerived）
        return BallRadius * 3.6f;
    }

    /// <summary>
    /// ★★ 计算第 i 条腿的站位锚点（身体局部坐标）。
    ///
    /// 【为什么必须有这个 ——「移动两步就需要重来」的病根】
    ///   我原来没有锚点，只有「离旧落点太远就换点」：
    ///     旧落点被身体越拖越远 → 每帧都满足换点条件
    ///     → 无限连续迈步 → 用户看到的「移动两步就需要重来」。
    ///   有锚点后：腿永远「朝站位方向」迈，不管当前落在哪。
    ///
    /// 【算法】（来源：spider_ik 的 calculateDefault）
    ///   站位 = 腿根 + 水平外扩 × 展开方向 + 沿法线下压到表面高度
    ///   外扩 = 腿长 × defaultFootSpread
    ///   前���分布 = 与 legRootRearBias 一致（腿根在哪，站位就在哪下方）
    /// </summary>
    private Vector3 ComputeDefaultLocal(int i, Vector3 up)
    {
        Vector3 rootWorld = RootWorld(i);

        // 腿根在身体局部坐标里的位置
        Vector3 rootLocal = transform.InverseTransformPoint(rootWorld);

        // 水平外扩方向：从身体中心指向腿根（去掉法线分量）
        Vector3 outward = Vector3.ProjectOnPlane(rootLocal, up);
        if (outward.sqrMagnitude < 0.000001f)
            outward = Vector3.ProjectOnPlane(Vector3.right, up);
        if (outward.sqrMagnitude < 0.000001f) outward = Vector3.right;
        outward.Normalize();

        float len = legLengthCached > 0f ? legLengthCached : BallRadius * 3.6f;

        // 站位 = 腿根沿「外扩」方向再往外一段
        Vector3 def = rootLocal + outward * (len * defaultFootSpread);

        // 沿法线方向：从腿根高度往下压，让站位大致在身体下方。
        // 爬墙/天花时「往下」= 沿 -up，身体 localPosition 不变，
        // 这样贴墙时锚点会自动跟着法线转。
        def -= up * (len * 0.9f);

        return def;
    }

    // ================================================================
    //  主循环
    // ================================================================

    private void FixedUpdate()
    {
        if (!initialized) return;

        float dt = Time.fixedDeltaTime;
        if (dt <= 0f) return;

        UpdateVelocity(dt);

        if (enableLegs) UpdateLegs(dt);
        else PushLimbsToRest();
    }

    private void UpdateVelocity(float dt)
    {
        Vector3 current = transform.position;
        Vector3 raw = (current - previousWorldPos) / dt;
        previousWorldPos = current;
        velocity = Vector3.Lerp(velocity, raw, velocitySmoothing);
    }

    /// <summary>
    /// ★ 步态主更新 —— 交替四足步态。
    /// </summary>
    private void UpdateLegs(float dt)
    {
        Vector3 up = SafeUp();

        // ★★ 站立计时（spider_ik 的 stopSteppingAfterSecondsStill）
        //   作者原注释："This fixes the indefinite stepping going on."
        //   我原来只有「静止不抬腿」，但相位仍在推进 → 原地踏步。
        float speedNow = velocity.magnitude;
        if (speedNow < 0.35f) timeStandingStill += dt;
        else timeStandingStill = 0f;
        bool freezeSteps = timeStandingStill > stopSteppingAfterStill;

        // ★★ 步时随速度反比缩放（spider_ik 的 calculateStepTime）
        //   stepTime = k / 速度  → 周期 = cycleTime × (参考速度 / 实际速度)
        //   为什么必须这样：
        //     固定周期 → 慢走时每步跨太远（够不到，劈叉）
        //               快跑时每步跨太近（在地上拖，滑步）
        //     无论怎么调 cycleTime 都不对，因为它必须是速度的函数。
        float cyc = cycleTime;
        if (scaleCycleBySpeed)
        {
            float refSpd = cycleRefSpeed > 0f
                ? cycleRefSpeed
                : Mathf.Max(1f, mover != null ? mover.runSpeed : 5f);
            // 速度越低 → 周期越长（少迈大步子）；速度为 0 → 用上限，避免爆炸。
            cyc = speedNow < 0.05f
                ? cycleTime * 2.2f
                : cycleTime * (refSpd / Mathf.Max(0.05f, speedNow));
            cyc = Mathf.Clamp(cyc, minCycleTime, cycleTime * 2.5f);
        }

        // ★ 冻结时不推进步态相位 → 腿站在站位上不动（而不是原地踏步）
        if (!freezeSteps)
        {
            gaitClock += dt;
            if (gaitClock >= cyc) gaitClock -= cyc;
        }

        // ---- 抬腿高度按速度缩放（静止不抬腿）----
        float lift = liftHeight;
        if (scaleLiftBySpeed)
        {
            float refSpeed = liftFullSpeed > 0f
                ? liftFullSpeed
                : Mathf.Max(1f, mover != null ? mover.runSpeed : 5f);
            lift *= Mathf.Clamp01(velocity.magnitude / refSpeed);
        }

        // ---- 速度预测（clamp 长度，否则腿被甩到身后）----
        Vector3 predictVel = velocity * velocityPrediction;
        float pvLen = predictVel.magnitude;
        if (pvLen > maxPredictLength)
            predictVel = predictVel.normalized * maxPredictLength;

        // ---- 起步顺序错开：给主相位加微小延迟 ----
        //   论文实测起步顺序 4-2-3-1 最多 → 用一个固定的错开序列近似。
        //   数组下标 = 腿序号 0..7（L1..L4,R1..R4）
        float[] stagger = StaggerOffsets();

        Vector3 feetAvg = Vector3.zero;

        for (int i = 0; i < 8; i++)
        {
            rootWorld[i] = RootWorld(i);

            // ---- 计算本腿当前相位（0~1，循环）----
            float phase = ComputePhase(i, gaitClock, stagger);
            legState[i].Phase = phase;

            // ---- 相位 → 支撑/摆动 + 插值进度 ----
            //★ 交替四足步态的时序：
            //   phase ∈[0, dutyFactor)                → 支撑相（脚不动）
            //   phase ∈ [dutyFactor, dutyFactor+swing) → 摆动相（脚划弧线）
            //   phase ∈ [dutyFactor+swing, 1)          → 支撑（已经落地）
            float stepAlpha;
            bool inSwing;

            if (phase < dutyFactor)
            {
                inSwing = false;
                stepAlpha = 1f;
            }
            else
            {
                float swingStart = dutyFactor;
                float swingEnd = dutyFactor + swingPortion;
                if (phase < swingEnd)
                {
                    inSwing = true;
                    stepAlpha = (phase - swingStart) / swingPortion;
                }
                else
                {
                    inSwing = false;
                    stepAlpha = 1f;
                }
            }

            // ---- 当前足端世界坐标 ----
            Vector3 foot = Vector3.Lerp(
                legState[i].LockedTarget, legState[i].NextTarget,
                Mathf.Clamp01(stepAlpha));

            if (inSwing)
            {
                // ★ 抬腿弧线：sin 曲线，中段最高。
                //   抬的是足端目标 → FABRIK 会让整条腿跟着拱起。
                float h = Mathf.Sin(Mathf.Clamp01(stepAlpha) * Mathf.PI);
                foot += up * (lift * h);
            }

            // ---- 换落点判定 ----
            //★★spider_ik 的 stepCheck()有三个触发条件，我原来只有一个：
            //   ① 目标悬空（没落地）→ 迈
            //   ② 离站位太远（被甩开）→ 迈
            //   ③ ★ 离腿根太近（收拢）→ 迈   ←我完全漏了这条
            //   漏掉 ③ 的后果：爬窄缝/ 贴墙时足端怼到腿根，
            //   FABRIK 把腿拉直或抖动 → 步态看起来崩掉。
            float len = legLengthCached > 0f ? legLengthCached : BallRadius * 3.6f;
            Vector3 defaultWorld = transform.TransformPoint(legState[i].DefaultLocal);

            float distFromDefault = Vector3.Distance(defaultWorld, foot);
            float farLimit = forceStepDistance > 0f ? forceStepDistance : len * 0.7f;
            bool outOfRange = distFromDefault > farLimit;

            //★ spider_ik 原文条件③：
            //   Vector3.Distance(rootJoint.getRotationPoint(), target.position) < minDistance
            //   —— 测的是「**腿根 → 落点**」的距离，不是落点到站位的距离。
            //   我第一版写成后者，等于「落点偏离合站位」，不是「腿收拢」，
            //   语义错了。爬窄缝时腿收拢，落点离腿根近 → 这条才该触发。
            float distFromRoot = Vector3.Distance(rootWorld[i], legState[i].LockedTarget);
            bool tooClose = distFromRoot < len * minFootDistance;

            if (!freezeSteps && (outOfRange || tooClose))
            {
                /* ★★★ 三段式换点（严格照 spider_ik 的 Step() 顺序）
                 *
                 * 【原来为什么「移动两步就需要重来」】—— 有**两个**独立机制：
                 *   机制 A（早已修）：projected = foot + predictVel，只加速度预测，
                 *     没有「这条腿应该待在哪」的概念。旧落点被身体越拖越远
                 *     → 每帧都 outOfRange → 无限连续迈步。
                 *     解法：引入身体局部站位锚点 DefaultLocal。
                 *
                 *   机制 B（本次修）：最终落点偏差没有被夹住。
                 *     过冲（overshootMultiplier−1）与速度预测（predictVel，上限 3×半径）
                 *     会叠加，把落点推到站位**另一侧** → 下一帧偏差反向
                 *     → 前后反复。metapika 作者原话：
                 *       "if the Overhead Amount >= the Step Distance,
                 *        the leg will move forward and backwards endlessly."
                 *     解法：对**最终**落点偏差夹紧到 maxDeviationRatio × farLimit。
                 *     ★ 注意必须夹「最终偏差」而不是「过冲量」：
                 *       偏差本身是几何衰减 dev → 0.5×dev，只夹过冲量
                 *       会把几何衰减退化成线性递减，收敛反而慢 20倍。
                 */

                // ① 过冲：朝站位方向迈 overshootMultiplier 倍
                //   必须 >1：身体在摆动相期间一直在前进，
                //   只走到站位中心 → 落点在身体后方 → 永远追不上。
                Vector3 start = Vector3.ProjectOnPlane(foot, up);
                Vector3 toDefault = defaultWorld - start;
                float far = toDefault.magnitude;
                Vector3 overshoot = (far > 0.0001f)
                    ? start + (toDefault / far) * ((overshootMultiplier - 1f) * far)
                    : defaultWorld;

                // ② 速度预测：补偿摆动期间的身体位移
                Vector3 projected = overshoot + predictVel;

                // ③ ★ 夹紧最终偏差 —— 防前后反复振荡（见上方机制 B）
                Vector3 devVec = defaultWorld - projected;
                float devLen = devVec.magnitude;
                float devCap = farLimit * maxDeviationRatio;
                if (devLen > devCap)
                    projected = defaultWorld - (devVec / devLen) * devCap;

                Vector3 hit;
                bool grounded;
                Vector3 hitNormal;
                if (TraceGroundMulti(projected, defaultWorld, up,
                                     out hit, out hitNormal, out grounded))
                {
                    legState[i].LockedTarget = hit;
                    legState[i].NextTarget = hit;
                    footNormals[i] = hitNormal;

                    // ★ 随机负偏移：否则所有腿重新同步（视频 58:30）
                    if (desyncJitter > 0f)
                        legState[i].Phase = -desyncJitter * Random.Range(0.3f, 1f);
                }
                else
                {
                    // 附近完全没有可踩面 → 优先退回首选项「站位本身」，
                    // 而不是把相位压回支撑相让腿卡死（那正是原来「腿卡死」的成因）。
                    legState[i].LockedTarget = defaultWorld;
                    legState[i].NextTarget = defaultWorld;
                    legState[i].Phase = dutyFactor * 0.5f;
                }

                foot = legState[i].LockedTarget;
            }

            footWorld[i] = foot;
            feetAvg += foot;

            // ★ 法线兜底：还没踩到过地面（footNormals 为零）时退回 up。
            //   零法线会让足端朝向求解拿到无意义的输入。
            if (footNormals[i].sqrMagnitude < 0.000001f) footNormals[i] = up;
        }

        // ---- 把足点交给解剖结构做 FABRIK 求解 ----
        if (anatomy != null)
        {
            anatomy.UpdateLimbs(rootWorld, footWorld, up, footNormals);
        }

        // ---- 身体起伏与跟随 ----
        UpdateBodyPosture(feetAvg, dt, up);
    }

    /// <summary>关掉腿时保持静止姿态。</summary>
    private void PushLimbsToRest()
    {
        Vector3 up = SafeUp();
        if (anatomy == null) return;
        for (int i = 0; i < 8; i++)
        {
            if (rootWorld[i] == Vector3.zero) rootWorld[i] = RootWorld(i);
            if (footWorld[i] == Vector3.zero) footWorld[i] = legState[i].LockedTarget;
            if (footNormals[i].sqrMagnitude < 0.000001f) footNormals[i] = up;
        }
        anatomy.UpdateLimbs(rootWorld, footWorld, up, footNormals);
    }

    /// <summary>
    /// ★ 身体姿态：随步伐上下起伏 + 追平均足位。
    ///
    /// 【来源】
    ///   · 起伏：真实蜘蛛行走时头胸部有轻微上下运动（步态的副产物），
    ///     且**一个周期内起伏两次**（8 条腿各抬一次 → 但按四足步态是4 组交替，
    ///     视觉上身体上下摆 2 次最自然）→ bodyBobFreqMul = 2。
    ///   · 追平均脚位：视频 58:30 最后一节，作者明确做了这个。
    ///     放在所有腿之后算 —— 「不想让骨盆在腿动完之后才动」。
    /// </summary>
    private void UpdateBodyPosture(Vector3 feetAvg, float dt, Vector3 up)
    {
        if (anatomy == null || anatomy.VisualRoot == null) return;

        // 起伏：sin(2π × phase × freqMul)
        float bobPhase = gaitClock / Mathf.Max(0.01f, cycleTime);
        float bob = Mathf.Sin(bobPhase * Mathf.PI * 2f * bodyBobFreqMul) * bodyBobAmount;

        Vector3 target = bodyBasePos + up * bob;

        // 追平均足位（横向偏移）
        if (feetAvg != Vector3.zero && bodyFollow > 0f)
        {
            Vector3 lateral = feetAvg - transform.position;
            // 只取「平行于表面」的分量 —— 法线方向由 up 决定，
            // 若把高度差也算进去，身体会被按到地里。
            lateral -= up * Vector3.Dot(lateral, up);
            target += lateral * bodyFollow * 0.35f;
        }

        anatomy.VisualRoot.localPosition = Vector3.Lerp(
            anatomy.VisualRoot.localPosition, target,
            Mathf.Clamp01(6f * dt));
    }

    /// <summary>
    /// ★ 计算某条腿当前相位。
    ///
    /// 【交替四足步态的相位表】
    ///   phaseOffsets = { L1:0, L2:0.5, L3:0, L4:0.5, R1:0.5, R2:0, R3:0.5, R4:0 }
    ///   校验这 8 个数是否满足论文里的三条规律：
    ///     ① 同侧隔条同相：L1(0)=L3(0) ✓  L2(0.5)=L4(0.5) ✓  R1(0.5)=R3(0.5) ✓
    ///        R2(0)=R4(0) ✓
    ///     ② 对角同相：L1(0)=R2(0) ✓  L2(0.5)=R1(0.5) ✓  L3(0)=R4(0) ✓
    ///        L4(0.5)=R3(0.5) ✓
    ///     ③ 相邻反相：L1(0) vs L2(0.5) ✓  L2(0.5) vs L3(0) ✓  L3(0) vs L4(0.5) ✓
    ///     → 全部成立。这就是交替四足步态。
    /// </summary>
    private float ComputePhase(int legIndex, float clock, float[] stagger)
    {
        float basePhase = 0f;
        if (phaseOffsets != null && legIndex < phaseOffsets.Length)
            basePhase = phaseOffsets[legIndex];

        float offset = stagger != null && legIndex < stagger.Length ? stagger[legIndex] : 0f;

        float p = (clock / Mathf.Max(0.01f, cycleTime)) + basePhase - offset;
        p -= Mathf.Floor(p);      // 归一到 [0,1)
        return p;
    }

    /// <summary>
    /// 起步顺序错开量。
    /// 论文实测：**4-2-3-1** 是最常见的同侧起步顺序（68.1%），
    /// 其次 4-1-3-2（14.8%）、4-3-1-2（12.8%）。
    /// → 给第4 条腿（索引 3）最小延迟，第 1 条（索引 0）最大延迟。
    /// 这样「4 → 2 → 3 → 1」依次启动，而不是 8 条一起动。
    /// </summary>
    private float[] staggerCache;
    private float[] StaggerOffsets()
    {
        if (staggerCache == null) staggerCache = new float[8];

        // 4-2-3-1 → 延迟量：腿4=0, 腿2=1步, 腿3=2步, 腿1=3步
        // 步长 = startStagger（秒）
        float s = Mathf.Max(0f, startStagger);
        // 左半侧 0..3 = L1..L4，右半侧 4..7 = R1..R4（对角同延迟）
        staggerCache[0] = s * 3f / 3f;   // L1  最后启动
        staggerCache[1] = s * 1f;       // L2  第一个启动（4-2-3-1）
        staggerCache[2] = s * 2f;       // L3
        staggerCache[3] = s * 0f;       // L4  最先启动
        staggerCache[4] = s * 3f;       // R1（与 L2 对角）
        staggerCache[5] = s * 1f;       // R2（与 L3 对角）
        staggerCache[6] = s * 2f;       // R3（与 L4 对角）
        staggerCache[7] = s * 0f;       // R4（与 L1 对角）
        return staggerCache;
    }

    // ================================================================
    //  地面探测
    // ================================================================

    /// <summary>安全取表面法线。</summary>
    private Vector3 SafeUp()
    {
        if (mover != null)
        {
            Vector3 n = mover.SurfaceNormal;
            if (n.sqrMagnitude > 0.0001f) return n;
        }
        Vector3 tu = transform.up;
        return tu.sqrMagnitude > 0.0001f ? tu : Vector3.forward;
    }

    /// <summary>
    /// 足端地面探测。
    ///
    /// 【★ 射线起点必须在「骨盆上方」与「脚上方」之间插值】
    ///   来源视频 14:20–16:10。作者先试「只用骨盆上方」→ 明确 did not help；
    ///   最后用两者之间的插值才解决。
    /// </summary>
    private bool TraceGround(Vector3 target, Vector3 up, out Vector3 hitPoint)
    {
        /* ★ 安全降级：射线没建好就不能探测。
         *   假腿只是可视化，坏了不该让本体抛异常。*/
        if (footRay == null)
        {
            hitPoint = target;
            return false;
        }

        Vector3 pelvisAbove = transform.position + up * footRayUp;
        Vector3 footAbove = target + up * footRayUp;
        Vector3 start = Vector3.Lerp(footAbove, pelvisAbove, footRayStartBlend);
        Vector3 end = start - up * (footRayUp + footRayDown);

        footRay.SetRadius(footRayRadius);

        RaycastHit rh;
        if (!footRay.CastBetween(start, end, surfaceMask, out rh))
        {
            hitPoint = target;
            return false;
        }

        hitPoint = rh.point;
        return true;
    }

    /// <summary>
    /// ★★ 分级多方向射线落地（spider_ik 的 findTargetOnSurface + updateCasts）
    ///
    /// 【为什么要多方向】
    ///   我原来只有一条「从上往下」的射线。打不到时只能
    ///   把相位压回支撑相让腿等下一帧 → 这条腿**卡死不动**，
    ///   表现为「有两条腿不迈步」。
    ///   spider_ik 建 12 条（6 方向 × 预测点/站位两组），
    ///   逐条尝试直到命中 → 不会卡死。
    ///
    /// 【方向优先级】严格照作者的设计思路（他注释写明顺序极其重要）：
    ///   1. 从「预测点」朝站位方向打 —— 首选，落点最符合步态意图
    ///   2. 从「预测点」垂直向下打 —— 主路径
    ///   3. 从「站位」垂直向下打 —— 身体已经走过去了的兜底
    ///   4~n. 从站位向各个水平方向扇形打 —— 边缘/悬空时找最近可踩面
    ///
    /// 【坡度过滤】来源：spider_ik 对 Frontal 射线用 ±65°。
    ///   角度太大（悬崖/墙）不算落脚点，否则腿会插进去。
    /// </summary>
    /// <param name="predicted">过冲 + 速度预测后的落点候选</param>
    /// <param name="fallback">站位锚点（第二组射线的原点）</param>
    /// <param name="up">表面法线</param>
    /// <param name="hitPoint">命中点</param>
    /// <param name="hitNormal">命中法线（可用于足端朝向）</param>
    /// <param name="grounded">是否真的踩到地面</param>
    private bool TraceGroundMulti(Vector3 predicted, Vector3 fallback, Vector3 up,
                                 out Vector3 hitPoint, out Vector3 hitNormal,
                                 out bool grounded)
    {
        hitPoint = predicted;
        hitNormal = up;
        grounded = false;

        /* ★ 安全降级：射线没建好就不能探测。假腿只是可视化。*/
        if (footRay == null) return false;

        footRay.SetRadius(footRayRadius);

        float maxSlope = maxWalkableSlopeDeg;

        // ---- 尝试 1：从预测点朝站位方向（水平方向） ----
        Vector3 towardDefault = Vector3.ProjectOnPlane(fallback - predicted, up);
        if (towardDefault.sqrMagnitude > 0.000001f)
        {
            if (TryCastAndValidate(predicted, towardDefault.normalized * (footRayUp + footRayDown),
                                   up, maxSlope, out hitPoint, out hitNormal))
            {
                grounded = true;
                return true;
            }
        }

        // ---- 尝试 2：从预测点垂直向下（主路径） ----
        if (TryCastAndValidate(predicted, -up * (footRayUp + footRayDown),
                               up, maxSlope, out hitPoint, out hitNormal))
        {
            grounded = true;
            return true;
        }

        // ---- 尝试 3：从站位垂直向下（身体已走过去的兜底） ----
        if (TryCastAndValidate(fallback, -up * (footRayUp + footRayDown),
                               up, maxSlope, out hitPoint, out hitNormal))
        {
            grounded = true;
            return true;
        }

        // ---- 尝试 4~n：从站位向水平方向扇形打 ----
        //   castDirectionCount 个方向，均分 360°。
        int n = Mathf.Clamp(castDirectionCount, 1, 6);
        if (n > 1)
        {
            // 以「指向预测点」的方向为 0°，左右各铺开
            Vector3 baseDir = towardDefault.sqrMagnitude > 0.000001f
                ? towardDefault.normalized
                : Vector3.ProjectOnPlane(Vector3.right, up).normalized;

            float reach = footRayUp + footRayDown;
            float startDeg = 180f / n;

            for (int k = 1; k <= n / 2 + (n % 2); k++)
            {
                float ang = startDeg * k;
                for (int s = -1; s <= 1; s += 2)
                {
                    Vector3 probe = (s < 0)
                        ? Quaternion.AngleAxis(ang, up) * baseDir
                        : Quaternion.AngleAxis(-ang, up) * baseDir;
                    if (probe.sqrMagnitude < 0.000001f) continue;

                    if (TryCastAndValidate(fallback, probe.normalized * reach,
                                           up, maxSlope, out hitPoint, out hitNormal))
                    {
                        grounded = true;
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 单条射线 + 坡度校验（spider_ik 的 slope filter）。
    /// ★ 坡度必须在这里过滤，不能等到摆动相里再判——
    ///   太陡的坡一旦被当成落脚点，腿会直接插进去。
    /// </summary>
    private bool TryCastAndValidate(Vector3 from, Vector3 offset, Vector3 up,
                                    float maxSlopeDeg,
                                    out Vector3 hitPoint, out Vector3 hitNormal)
    {
        hitPoint = from;
        hitNormal = up;

        Vector3 pelvisAbove = transform.position + up * footRayUp;
        Vector3 footAbove = from + up * footRayUp;
        Vector3 start = Vector3.Lerp(footAbove, pelvisAbove, footRayStartBlend);
        Vector3 end = start + offset;

        RaycastHit rh;
        if (!footRay.CastBetween(start, end, surfaceMask, out rh))
            return false;

        // ★ 坡度过滤：法线与 up 的夹角太大 → 不算可踩面
        float slope = Vector3.Angle(rh.normal, up);
        if (slope > maxSlopeDeg) return false;

        hitPoint = rh.point;
        hitNormal = rh.normal;
        return true;
    }

    /// <summary>第 i 条腿的腿根世界坐标（来自解剖结构）。</summary>
    private Vector3 RootWorld(int i)
    {
        if (anatomy != null && anatomy.LegRoots != null
            && i < anatomy.LegRoots.Length && anatomy.LegRoots[i] != Vector3.zero)
            return anatomy.LegRoots[i];

        // 兜底：解剖还没建好 → 用身体位置 + 一个粗略的横向偏移。
        float side = (i < 4) ? -1f : 1f;
        float k = (i % 4) / 3f - 0.5f;
        return transform.position
             + transform.right * (side * BallRadius * 1.2f)
             + transform.forward * (k * BallRadius * 1.4f);
    }

    // ================================================================
    //  调试
    // ================================================================

    private void LateUpdate()
    {
        if (showDebug && initialized) DrawLegDebug();
    }

    /// <summary>
    /// ★ 步态图必须在 OnGUI 里画 —— GUI.DrawTexture / GUI.Label 只在
    ///   IMGUI 事件（OnGUI / OnGUILayout）里有效。
    ///   放在 LateUpdate 里**能编译通过但什么都不会显示**，
    ///   而且不会报任何错 → 极难排查。
    /// </summary>
    private void OnGUI()
    {
        if (showGaitDiagram && initialized) DrawGaitDiagram();
    }

    private void DrawLegDebug()
    {
        Vector3 up = SafeUp();
        for (int i = 0; i < 8; i++)
        {
            DrawCross(legState[i].LockedTarget, BallRadius * 0.35f, Color.green);
            DrawCross(legState[i].NextTarget, BallRadius * 0.3f, Color.yellow);
            DrawCross(footWorld[i], BallRadius * 0.4f, Color.cyan);
            Debug.DrawLine(rootWorld[i], footWorld[i],
                new Color(1f, 1f, 1f, 0.35f));

            float h = liftHeight * Mathf.Sin(
                Mathf.Clamp01(SwingAlpha(i)) * Mathf.PI);
            Debug.DrawLine(footWorld[i], footWorld[i] + up * h,
                new Color(1f, 0.5f, 0f, 0.7f));
        }
    }

    private float SwingAlpha(int i)
    {
        float phase = legState[i].Phase;
        if (phase < dutyFactor) return 0f;
        float swingEnd = dutyFactor + swingPortion;
        if (phase >= swingEnd) return 1f;
        return (phase - dutyFactor) / swingPortion;
    }

    /// <summary>
    /// ★ 步态图 —— 8 条腿的时序条带。
    ///   黑条 = 支撑相，白底 = 摆动相。
    ///   对照论文的 gait diagram，一眼能看出是不是交替四足步态。
    /// </summary>
    private void DrawGaitDiagram()
    {
        const float x0 = 12f;
        const float w = 240f;
        const float rowH = 12f;

        // ★ 不能写成 const：Screen.height 是**运行时**属性，不是编译期常量。
        //   `const float top = Screen.height - 20f` → CS0133
        //     「The expression being assigned to 'top' must be constant」。
        float top = Screen.height - 20f;

        for (int i = 0; i < 8; i++)
        {
            float y = top - rowH * (i + 1);
            float ph = legState[i].Phase;

            // 底：摆动相（浅色）
            GUI.color = new Color(0.25f, 0.25f, 0.25f, 0.9f);
            GUI.DrawTexture(new Rect(x0, y, w, rowH - 1f), Texture2D.whiteTexture);

            // 支撑相：填满 [0, duty) 以及 [duty+swing, 1)
            GUI.color = new Color(0.85f, 0.75f, 0.2f, 1f);
            float stanceEnd = dutyFactor * w;
            GUI.DrawTexture(new Rect(x0, y, stanceEnd, rowH - 1f), Texture2D.whiteTexture);

            // 当前相位游标
            GUI.color = Color.white;
            GUI.DrawTexture(
                new Rect(x0 + ph * w - 1f, y - 2f, 2f, rowH + 3f),
                Texture2D.whiteTexture);

            GUI.color = Color.white;
            GUI.Label(new Rect(x0 - 60f, y - 3f, 58f, rowH),
                LegName(i), GUI.skin.label);
        }

        // 摆动段高亮
        GUI.color = new Color(0.4f, 0.8f, 1f, 0.5f);
        GUI.DrawTexture(new Rect(x0 + dutyFactor * w, top - rowH * 8,
            swingPortion * w, rowH * 8f), Texture2D.whiteTexture);
        GUI.color = Color.white;
    }

    private static string LegName(int i)
    {
        string side = i < 4 ? "L" : "R";
        return side + (i % 4 + 1);
    }

    /// <summary>
    /// 画三维十字标记。
    /// 【为什么不用 Debug.DrawSphere / DrawWireSphere】
    ///   这两个 API 在不同 Unity 版本里重载签名有差异
    ///   （带 duration 的重载在旧版本不存在），而 DrawLine 是最稳定的。
    ///   调试辅助代码不值得为它冒编译风险。★ 已栽过两次。
    /// </summary>
    private static void DrawCross(Vector3 center, float size, Color col)
    {
        Debug.DrawLine(center - Vector3.right * size, center + Vector3.right * size, col);
        Debug.DrawLine(center - Vector3.forward * size, center + Vector3.forward * size, col);
        Debug.DrawLine(center - Vector3.up * size, center + Vector3.up * size, col);
    }
}