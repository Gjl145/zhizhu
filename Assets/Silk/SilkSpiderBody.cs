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

    [Tooltip("脚离当前锁定点多远就强制换落点（格）。0 = 用腿长的 70%")]
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
    }

    private LegState[] legState = new LegState[8];
    private Vector3[] footWorld = new Vector3[8];
    private Vector3[] rootWorld = new Vector3[8];

    private Vector3 previousWorldPos;
    private Vector3 velocity;
    private bool initialized;
    private Collider selfCollider;
    private SilkSphereCast footRay;
    private float gaitClock;
    private Vector3 bodyBasePos;

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
        if (footRay != null) footRay.SetIgnore(selfCollider);
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

        for (int i = 0; i < 8; i++)
        {
            Vector3 root = RootWorld(i);
            Vector3 guess = root - up * (BallRadius * 0.5f);

            Vector3 hit;
            if (TraceGround(guess, up, out hit))
                legState[i].LockedTarget = hit;
            else
                legState[i].LockedTarget = guess;

            legState[i].NextTarget = legState[i].LockedTarget;
            legState[i].Initialized = true;
            footWorld[i] = legState[i].LockedTarget;

            rootWorld[i] = root;
        }
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

        gaitClock += dt;
        if (gaitClock >= cycleTime) gaitClock -= cycleTime;

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
            float distFromLocked = Vector3.Distance(legState[i].LockedTarget, foot);
            bool outOfRange = distFromLocked > (forceStepDistance > 0f
                ? forceStepDistance
                : BallRadius * 1.8f);

            if (outOfRange)
            {
                //★ 顺序不可颠倒（视频踩过的最大的坑）：
                //  正确：脚当前点 + 速度向量 → 从投影点 trace → 命中点
                //  错误：脚当前点 → trace → 命中点 + 速度向量
                //  后者会让腿插进地面，因为「加完速度」的位置从没验证过有地面。
                Vector3 projected = foot + predictVel;
                Vector3 hit;
                if (TraceGround(projected, up, out hit))
                {
                    legState[i].LockedTarget = hit;
                    legState[i].NextTarget = projected;

                    //★ 随机负偏移：否则所有腿重新同步（视频 58:30）
                    if (desyncJitter > 0f)
                        legState[i].Phase = -desyncJitter * Random.Range(0.3f, 1f);
                }
                else
                {
                    // 打不到地面（悬空边缘）→ 把相位压回支撑相，下一帧再试
                    legState[i].Phase = dutyFactor * 0.5f;
                }

                foot = Vector3.Lerp(legState[i].LockedTarget, legState[i].NextTarget, 0f);
            }

            footWorld[i] = foot;
            feetAvg += foot;
        }

        // ---- 把足点交给解剖结构做 FABRIK 求解 ----
        if (anatomy != null)
        {
            anatomy.UpdateLimbs(rootWorld, footWorld, up);
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
        }
        anatomy.UpdateLimbs(rootWorld, footWorld, up);
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
        const float top = Screen.height - 20f;

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