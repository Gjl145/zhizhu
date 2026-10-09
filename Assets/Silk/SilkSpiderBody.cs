using UnityEngine;

/// <summary>
/// 蜘蛛假骨骼 —— 8 条腿的步态调度。
///
/// 【★ 本文件的12 条机制全部来自 58 分钟 UE5 程序动画视频 + MIT 开源仓库】
///   详细来源分析见 Docs/参考视频转写/三个资源完整分析_2026-10-09.md
///   每条机制在下面都有「来源」标注。
///
/// 【★ 零新增按键 —— 本文件完全不读输入】
///   它只跟着 SilkSpiderSurfaceMove 的身体走，没有自己的操作。
///   用户的 8 键硬规则不受影响。
///
/// 【坐标系】Z-up。腿的局部系由 SilkSpiderSurfaceMove 对齐到表面法线，
///   所以地面 / 墙面 / 天花板 / 转角用的是同一套腿，不需要任何特判。
/// </summary>
[RequireComponent(typeof(SilkSpiderSurfaceMove))]
[DisallowMultipleComponent]
public class SilkSpiderBody : MonoBehaviour
{
    // ================================================================
    //  体形参数（全部由 visualRadius 推导，不写字面量）
    // ================================================================

    [Header("体形")]
    [Tooltip("身体半长（前后方向半轴）。默认取球半径的 1.0倍")]
    public float bodyHalfLength = -1f;

    [Tooltip("身体半宽（左右方向半轴）。默认取球半径的 1.1 倍——\n"
        + "★ 比半长宽，因为 90 秒视频 u90 帧显示蜘蛛是「腿朝两侧张开」的")]
    public float bodyHalfWidth = -1f;

    [Tooltip("身体离表面的高度。默认取球半径的 0.9 倍")]
    public float bodyHeight = -1f;

    [Tooltip("每条腿的最大长度（从腿根到足端）。默认取球半径的 2.4 倍")]
    public float legLength = -1f;

    [Tooltip("线段粗细。默认取球半径的 0.16 倍")]
    public float legThickness = -1f;

    [Tooltip("腿的颜色")]
    public Color legColor = new Color(0.22f, 0.18f, 0.16f, 1f);

    [Tooltip("腿根相对身体中心的额外外扩（沿左右方向）。负值 = 收到身体内侧")]
    public float legRootSpread = -1f;

    [Tooltip("腿的静态初始角度（沿前后方向的张开程度）。\n"
        + "★ 越大则前腿越靠前、后腿越靠后，看起来像张开的蜘蛛")]
    public float legSplay = 1.35f;

    [Header("落点探测")]
    [Tooltip("足端射线的上探长度（格）。默认取球半径的 3 倍")]
    public float footRayUp = -1f;

    [Tooltip("足端射线的下探长度（格）。默认取球半径的 3 倍")]
    public float footRayDown = -1f;

    [Tooltip("★ 射线起点在「骨盆上方」与「脚上方」之间的插值比例（来源：视频 14:20–16:10）。\n"
        + "0 = 从脚正上方打；1 = 从骨盆正上方打；0.5 = 两者中间。\n"
        + "★ 为什么不能写死：射线是垂直的但腿是斜的，脚越过边缘时\n"
        + "  用脚正上方会打偏→ 腿插进几何体。作者试过「只用骨盆上方」，\n"
        + "  明确说 did not help，只有插值才解决。")]
    [Range(0f, 1f)] public float footRayStartBlend = 0.5f;

    [Tooltip("射线的碰撞层。默认 Everything —— 与 SilkSpiderSurfaceMove 一致")]
    public LayerMask surfaceMask = ~0;

    // ================================================================
    //  步态参数
    // ================================================================

    [Header("步态")]
    [Tooltip("一次完整迈步的周期（秒）。视频作者用的2 秒")]
    public float cycleTime = 2f;

    [Tooltip("★ 插值进度用 Remap(0 → 0.2) 而非 Clamp(0 → 1)（来源：视频 45:00）。\n"
        + "含义：计时到 0.2 时插值就走完，剩下 1.8 秒脚都不动。\n"
        + "★ 为什么不用 clamp：clamp 会让脚在原地反复抖 ——\n"
        + "  目标不变但计时一直涨，alpha 卡在 1 又被重置，来回抖。")]
    [Range(0.01f, 1f)] public float moveAlphaWindow = 0.2f;

    [Tooltip("脚离当前落点多远就强制迈步（格）。\n"
        + "★ 用「离锁定点」而不是「离身体」，这是视频踩过的坑 ——\n"
        + "  用身体算距离时，脚实际想去的地方和身体不是一回事，判据失真。\n"
        + "0 = 用 legLength 的 80%")]
    public float forceStepDistance = -1f;

    [Tooltip("★ 强制迈步后给该腿的随机负偏移（秒）。\n"
        + "来源：视频 58:30——「距离触发时设0 会让所有腿重新同步」。\n"
        + "0 = 关闭随机（会看到 8 条腿齐步走）")]
    public float desyncJitter = 0.35f;

    [Tooltip("速度预测的投射比例。1 = 完全按当前速度投射落点")]
    [Range(0f, 2f)] public float velocityPrediction = 1f;

    [Tooltip("速度向量长度的上限（格）。★ 必须有，否则跑起来腿会被甩到身后")]
    public float maxPredictLength = -1f;

    [Tooltip("速度平滑系数。★ 视频里作者用 0.1，理由：\n"
        + "「用 0.1 会逐渐逼近真实值，不会一直累积误差」")]
    [Range(0.01f, 1f)] public float velocitySmoothing = 0.1f;

    [Header("抬腿")]
    [Tooltip("抬腿峰值高度（格）。默认取球半径的 1.2 倍")]
    public float liftHeight = -1f;

    [Tooltip("★ 抬腿高度按速度缩放：静止时不抬腿（来源：视频 47:00–49:00）。\n"
        + "0 = 关闭这个行为（会看到站着不动也在原地踏步）")]
    public bool scaleLiftBySpeed = true;

    [Tooltip("抬到满高度所需的速度（格/秒）。默认取 runSpeed")]
    public float liftFullSpeed = -1f;

    [Header("骨盆起伏")]
    [Tooltip("★ 骨盆追平均脚位置（来源：视频 58:30 最后一节）。\n"
        + "8 条脚的落点求平均，骨盆朝那个点移动 —— 身体会随步伐轻微摇摆。\n"
        + "0 = 关闭（身体完全不晃，很机械）")]
    [Range(0f, 1f)] public float bodyFollow = 0.35f;

    [Tooltip("骨盆追平均的插值速度")]
    public float bodyFollowSpeed = 6f;

    [Header("调试")]
    [Tooltip("画出每条腿的锁定点（绿）、目标点（黄）、足端（青）")]
    public bool showDebug = false;

    [Tooltip("★ **默认勾上** —— 不勾就看不到腿，等于交付物不可见。\n"
        + "每帧要跑 8 次球形射线，比普通贴面移动贵。\n"
        + "★ 步态确认没问题后可以取消，省下这部分开销。")]
    public bool enableLegs = true;

    // ================================================================
    //  运行时状态
    // ================================================================

    private SilkParkourController ctrl;
    private SilkSpiderSurfaceMove mover;
    private SilkSpiderLeg[] legs = new SilkSpiderLeg[8];
    private Transform visualRoot;      //腿的父节点（跟着身体起伏偏移）
    private Transform bodyPivot;       // 骨盆（做起伏的节点）

    private Vector3 previousWorldPos;
    private Vector3 velocity;
    private bool initialized;
    private Collider selfCollider;

    private SilkSphereCast footRay;

    /// <summary>8 条腿，供外部查询（如调试 UI）。</summary>
    public SilkSpiderLeg[] Legs => legs;

    /// <summary>腿当前的移动速度（格/秒，已平滑）。</summary>
    public Vector3 SmoothedVelocity => velocity;

    // ================================================================
    //  自动挂载
    // ================================================================

    /// <summary>
    /// ★ 自动挂载 —— 与 SilkSpiderSurfaceMove 用同一套办法，
    ///   **不改动 SilkBuilder.cs**。
    ///   但注意：挂载有顺序问题 —— 必须在 SurfaceMove 之后。
    ///   所以这里不自动 AddComponent，而是由 SilkSpiderSurfaceMove 负责挂。
    /// </summary>
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

    /// <summary>
    /// ★ 解析所有外部引用。**幂等**，可在 Awake 与 Start 各调一次。
    ///
    /// 【为什么要两次】
    ///   本组件由 `SilkSpiderSurfaceMove.AutoAttach` 在
    ///   `RuntimeInitializeLoadType.AfterSceneLoad` 阶段挂上。
    ///   那个阶段里，场景原有物体的 Awake 已跑完，Start 还没跑。
    ///   而本组件的 Awake 是在 `AddComponent` 时**立刻**执行的 ——
    ///   也就是说：**本组件的 Awake 会先于场景原有物体的 Start**。
    ///   ★ 这其实是好事（能抢到前面），但也意味着依赖链上的
    ///   `SilkParkourController` 此刻状态未知。
    ///   → 所以 Start 里必须再解析一次。
    ///
    /// 【为什么用 provider 字段而不是到处 GetComponent】
    ///   球是**运行时**创建的（`SilkParkourController.CreateVisual()`），
    ///   `visualRadius` 在 Awake 阶段可能还是默认值。
    ///   → 所有读取球半径的地方都必须走这个方法，
    ///   绝不能缓存成局部变量或字段。
    /// </summary>
    private void ResolveReferences()
    {
        if (ctrl == null) ctrl = GetComponent<SilkParkourController>();
        if (mover == null) mover = GetComponent<SilkSpiderSurfaceMove>();
    }

    /// <summary>
    /// ★ 球半径 —— 全项目唯一的读取入口。
    ///
    /// 【为什么不能用 ctrl.visualRadius 直接读】
    ///   球是运行时创建的，`visualRadius` 可能在 Awake 时还是默认值 0。
    ///   直接用会得到 0 → 射线半径 0 → SphereCast 打空 → 永远探测不到地面。
    ///   → 这里做兜底，绝不让它返回 0。
    /// </summary>
    private float BallRadius
    {
        get
        {
            ResolveReferences();

            if (ctrl != null && ctrl.visualRadius > 0f) return ctrl.visualRadius;

            // 实在拿不到（控制器缺失/半径未初始化）→ 用球半径的常见值当兜底。
            // ★ 宁可数值不对，也不要 NullReferenceException。
            return 1f;
        }
    }

    private void Start()
    {
        /* ★ mover 必须在 Start 里再取一次，不能只靠 Awake。
         *   见 ResolveReferences 的说明 —— Awake 时依赖链可能还没就绪。*/
        ResolveReferences();

        if (mover == null)
        {
            mover = gameObject.AddComponent<SilkSpiderSurfaceMove>();
            Debug.LogWarning("[SpiderBody] 缺少 SilkSpiderSurfaceMove，已自动补挂。");
        }

        // ★ 所有体形参数由 BallRadius 推导，不写字面量 —— 球径改了自动跟随
        float r = BallRadius;
        if (r <= 0f) r = 1f;

        if (bodyHalfLength <= 0f) bodyHalfLength = r * 1.0f;
        if (bodyHalfWidth <= 0f) bodyHalfWidth = r * 1.1f;
        if (bodyHeight <= 0f) bodyHeight = r * 0.9f;
        if (legLength <= 0f) legLength = r * 2.4f;
        if (legThickness <= 0f) legThickness = r * 0.16f;
        if (legRootSpread <= 0f) legRootSpread = r * 0.75f;

        if (footRayUp <= 0f) footRayUp = r * 3f;
        if (footRayDown <= 0f) footRayDown = r * 3f;
        if (liftHeight <= 0f) liftHeight = r * 1.2f;
        if (maxPredictLength <= 0f) maxPredictLength = r * 3f;
        if (forceStepDistance <= 0f) forceStepDistance = legLength * 0.8f;

        /*★★ 顺序：先建射线，再建腿。
         *
         * 【踩过的坑】
         *   原来顺序是 BuildBody → BuildLegs → BuildFootRay，
         *   而 BuildLegs 里会调 InitializeFootTargets() → TraceGround()，
         *   **TraceGround 第一件事就是 footRay.SetRadius(...)**。
         *   → 抛 NullReferenceException。
         *
         *   ★ 为什么静态检查没抓到：
         *     `footRay` 是合法声明的字段，`BuildFootRay()` 也确实存在，
         *     只是一次性初始化的**顺序**错了。
         *     文本正则判断不了「哪个方法先跑」。
         *
         * 【为什么 InitializeFootTargets 要在 BuildLegs 里】
         *   它需要 legs[] 已经填好才能算8 条腿的初始落点。
         *   所以正确的约束是「射线 < 腿」，不是把 InitializeFootTargets 挪走。
         */
        BuildBody();
        BuildFootRay();   // ★ 必须在 BuildLegs 之前
        BuildLegs();

        previousWorldPos = transform.position;
        initialized = true;
    }

    private System.Collections.IEnumerator AttachSelfColliderNextFrame()
    {
        yield return null;

        selfCollider = FindSelfCollider();
        if (selfCollider == null)
            Debug.LogWarning("[SpiderBody] 找不到自身 Collider，足端射线会打到自己。");

        if (footRay != null) footRay.SetIgnore(selfCollider);
    }

    /// <summary>找自身碰撞体（在子物体上，见 SilkSpiderSurfaceMove 的说明）。</summary>
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
    //  搭建
    // ================================================================

    private void BuildBody()
    {
        var rootGo = new GameObject("SpiderVisual");
        rootGo.transform.SetParent(transform, false);
        visualRoot = rootGo.transform;

        var pivotGo = new GameObject("SpiderPelvis");
        pivotGo.transform.SetParent(visualRoot, false);
        bodyPivot = pivotGo.transform;

        /*★★★ 腹部网格 —— 没有它屏幕上只有 8 条线飘着，看不出「蜘蛛」，
         *   用户会以为腿是凭空出现的（这正是「啥也没看到」的一部分）。
         *
         * 【为什么用 CreatePrimitive(Sphere) 而不是 new GameObject】
         *   空 GameObject 没有 MeshFilter/MeshRenderer → 完全不可见。
         *   CreatePrimitive 自带网格 + 渲染器。
         *
         * 【★ 必须销毁 Collider】
         *   Sphere 自带 SphereCollider，而本项目的球**已经**有一个 Collider，
         *   多一个会干扰 SilkSpiderSurfaceMove 的射线判定
         *   （射线会打到蜘蛛自己的肚子上）。腿的 Collider 同样已删。
         *
         * 【尺寸】扁椭球：X/Z 略宽、Y 略扁 —— 蜘蛛腹部的经典轮廓。*/
        var bodyGo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        bodyGo.name = "SpiderAbdomen";
        bodyGo.transform.SetParent(bodyPivot, false);
        // 单位球半径 0.5 → 缩放即直径。用体形参数推导，不写字面量。
        bodyGo.transform.localScale = new Vector3(
            bodyHalfWidth * 2f, bodyHalfLength * 2f, bodyHeight * 2f);

        Collider bodyCol = bodyGo.GetComponent<Collider>();
        if (bodyCol != null) Destroy(bodyCol);

        MeshRenderer bodyMr = bodyGo.GetComponent<MeshRenderer>();
        if (bodyMr != null)
        {
            // 用不受光照影响的纯色，保证任何角度都看得见轮廓
            Shader sh = Shader.Find("Unlit/Color");
            if (sh == null) sh = Shader.Find("Standard");
            if (sh == null) sh = Shader.Find("Diffuse");

            if (sh != null)
            {
                bodyMr.material = new Material(sh);
                if (bodyMr.material.HasProperty("_Color"))
                    bodyMr.material.color = legColor;
            }
        }
    }

    /// <summary>
    /// 8 条腿的布局：左右各 4 条，沿前后方向展开。
    ///
    /// ★ 为什么左右各 4 而不是前后各 4：
    ///   90 秒视频 u90 帧显示蜘蛛是「蹲姿、身体压低、腿朝两侧张开」。
    ///   → 左右是主要的展开方向，前后只用来错开前后腿的位置。
    /// </summary>
    private void BuildLegs()
    {
        for (int i = 0; i < 8; i++)
        {
            var leg = new SilkSpiderLeg();
            leg.Index = i;
            leg.SideSign = (i < 4) ? -1f : 1f;

            // 同侧 4 条腿沿前后均布：-1, -1/3, +1/3, +1
            int k = i % 4;
            leg.ForwardT = (k / 3f) * 2f - 1f;

            /*★ 相位错开 —— 8 条腿不同步的**唯一**来源。
             *   视频里作者写的是 footTimings[i] = i / 8，注释原文：
             *   「这是八分之一秒，所以每条腿会错开 0.125」——
             *   「这是唯一解决所有脚同时动的方法」。
             *
             *   ★ 必须写进 Timing 而不是只存在 Phase：
             *     视频是把i/8 写进计时器数组本身。
             *     只存Phase 不写进 Timing 的话，8 条腿第一帧会同时从 0 开始迈步。
             */
            leg.Timing = (i / 8f) * cycleTime;

            leg.BuildVisual(bodyPivot, "Leg_" + i, legThickness, legColor);
            legs[i] = leg;
        }

        /* ★ 初始落点必须落在身体下方 —— 不初始化的话默认是 Vector3.zero，
         *   8 条腿第一帧会全部朝世界原点伸过去（表现为瞬间炸开成一团）。
         *   这与「往数组里写值之前要先给数组 8 个初始项」是同一个坑
         *   （视频里作者也踩了：writes to blank array → out of bounds）。
         *   在这里等价于：给每个数组元素一个初值。*/
        InitializeFootTargets();
    }

    /// <summary>把每条腿的初始锁定点/目标点落到身体下方的地面上。</summary>
    private void InitializeFootTargets()
    {
        /* ★ mover 可能还是 null —— 它由 SilkSpiderSurfaceMove.AutoAttach 挂上，
         *   而 AutoAttach 在 RuntimeInitializeLoadType.AfterSceneLoad 阶段跑，
         *   那时本组件可能已经进了 Start。
         *   Start 里做了兜底重取，但万一GetComponent 真的拿不到（异常场景），
         *   这里的 mover.SurfaceNormal 就会 NullReferenceException。
         *   → 这里再兜一层，绝不让初始化抛异常打断整个 Start。*/
        Vector3 up = transform.up;
        if (mover != null)
        {
            Vector3 n = mover.SurfaceNormal;
            if (n.sqrMagnitude > 0.0001f) up = n;
        }

        for (int i = 0; i < 8; i++)
        {
            Vector3 root = LegRootWorld(i);
            Vector3 guess = root - up * bodyHeight * 0.5f;

            Vector3 hit;
            if (TraceGround(i, guess, up, out hit))
                legs[i].LockedTarget = hit;
            else
                legs[i].LockedTarget = guess;

            legs[i].NextTarget = legs[i].LockedTarget;
        }
    }

    private void BuildFootRay()
    {
        /* ★ 用**不绑父**的构造函数。
         *   绑定父版本会把端点存成父的局部坐标，
         *   适合 SurfaceMove 那种「方向恒定」的射线。
         *   但足端射线每帧都传显式起点终点（CastBetween），
         *   绑父只会让 GetOrigin() 拿到无意义的值 → 反而误导。
         *
         *   ★ 注意：ignoreRoot 会因此是 null —— 但没关系，
         *     CastBetween 里对 ignoreRoot 做了 null 判断，
         *     而真正生效的排除靠的是 ignoreCollider（球自己的 Collider）。
         *     腿本身没有 Collider（BuildVisual 里已确保），
         *     所以只需排除球即可。
         */
        footRay = new SilkSphereCast(
            transform.position,
            transform.position - transform.up * footRayDown,
            BallRadius * 0.15f);

        StartCoroutine(AttachSelfColliderNextFrame());
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
    }

    /// <summary>
    /// 速度计算 —— 视频里的 `Calculate Velocity` 函数。
    ///
    /// 【★ 必须平滑，理由来自视频】
    ///   作者原话：「如果一直用 0.1，它会不断累积、越来越接近真实值」
    ///   → 意思是 Lerp(0.1) 是低通滤波，防止帧率抖动传导到落点预测。
    /// </summary>
    private void UpdateVelocity(float dt)
    {
        Vector3 current = transform.position;
        Vector3 raw = (current - previousWorldPos) / dt;
        previousWorldPos = current;

        velocity = Vector3.Lerp(velocity, raw, velocitySmoothing);
    }

    private void UpdateLegs(float dt)
    {
        Vector3 up = mover.SurfaceNormal;
        if (up.sqrMagnitude < 0.0001f) up = transform.up;

        // 速度限制长度 —— ★ 没有这个腿会被甩到身后（视频 52:00）
        Vector3 predictVel = velocity * velocityPrediction;
        if (predictVel.magnitude > maxPredictLength)
            predictVel = predictVel.normalized * maxPredictLength;

        // 抬腿高度按速度缩放—— 静止时不抬（视频 47:00）
        float lift = liftHeight;
        if (scaleLiftBySpeed)
        {
            float refSpeed = liftFullSpeed > 0f
                ? liftFullSpeed
                : Mathf.Max(1f, mover.runSpeed);
            lift *= Mathf.Clamp01(velocity.magnitude / refSpeed);
        }

        Vector3 pelvisAvg = Vector3.zero;
        int counted = 0;

        for (int i = 0; i < 8; i++)
        {
            legs[i].Timing += dt;
            SolveLeg(i, up, predictVel, lift);

            pelvisAvg += legs[i].FootWorld;
            counted++;
        }

        // ★ 骨盆追平均脚位置 —— 放在所有腿之后算（视频 58:30）
        //   原文理由：「不想让骨盆在腿动完之后才动，
        //   那样腿也会跟着脚一起被带走」
        if (counted > 0 && bodyFollow > 0f)
        {
            pelvisAvg /= counted;
            Vector3 goal = Vector3.Lerp(bodyPivot.position, pelvisAvg, bodyFollow);
            bodyPivot.position = Vector3.Lerp(
                bodyPivot.position, goal,
                Mathf.Clamp01(bodyFollowSpeed * dt));
        }
    }

    /// <summary>
    /// 解算单条腿 —— 对应视频里的 `Calculate New Foot Targets` +抬腿。
    ///
    /// 【顺序不可颠倒★ 视频踩过的最大的坑】
    ///   错误顺序：脚当前点 → trace 打点 →命中点 + 速度向量
    ///     → 移动时腿会插进地面，因为「加完速度」的位置从没验证过有地面
    ///   正确顺序：脚当前点 + 速度向量 → 从投影点 trace → 命中点
    ///     作者原话：「现在你能看到脚移动时总是踩到地面了」
    /// </summary>
    private void SolveLeg(int i, Vector3 up, Vector3 predictVel, float lift)
    {
        SilkSpiderLeg leg = legs[i];
        Vector3 rootWorld = LegRootWorld(i);

        // ---- 1. 插值进度：Remap(0 → moveAlphaWindow)，不是 Clamp(0 → 1) ----
        float alpha = Mathf.Clamp01(leg.Timing / moveAlphaWindow);

        // ---- 2. 当前足端应该在的位置 ----
        Vector3 footWorld = Vector3.Lerp(leg.LockedTarget, leg.NextTarget, alpha);

        // ---- 3. 抬腿弧线（抬的是足端目标，腿自然跟着拱起）----
        float stepLift = lift * SilkSpiderLeg.LiftCurveAt(alpha);
        footWorld += up * stepLift;

        // ---- 4. 是否需要换落点 ----
        bool outOfRange = Vector3.Distance(leg.LockedTarget, footWorld) > forceStepDistance;
        bool timeUp = leg.Timing > cycleTime;

        if (outOfRange || timeUp)
        {
            Vector3 projected = footWorld + predictVel;
            Vector3 hit;
            if (TraceGround(i, projected, up, out hit))
            {
                // 旧目标变成锁定点，立刻生成新目标
                leg.LockedTarget = hit;
                leg.NextTarget = projected;

                // ★ 距离触发时给一个随机负偏移，否则 8 条腿会重新同步
                //   （视频 58:30 的原话：「都重置到同一时刻，腿就同步了」）
                if (outOfRange && desyncJitter > 0f)
                    leg.Timing = -desyncJitter * Random.Range(0.3f, 1f);
                else
                    leg.Timing = 0f;

                footWorld = leg.LockedTarget;
                stepLift = lift * SilkSpiderLeg.LiftCurveAt(
                    Mathf.Clamp01(leg.Timing / moveAlphaWindow));
                footWorld += up * stepLift;
            }
            else
            {
                //打不到地面（悬空边缘）→ 只是把计时压回去，下一帧再试
                leg.Timing = cycleTime * 0.5f;
            }
        }

        leg.Solve(rootWorld, footWorld, up, 0f, bodyPivot);
    }

    /// <summary>
    /// 足端地面探测。
    ///
    /// 【★★ 射线起点必须在「骨盆上方」与「脚上方」之间插值】
    ///   来源：视频 14:20–16:10。
    ///   作者原话：「射线是从脚的正上方向正下方打的，
    ///   但腿并不是从正上方或正下方来的…… 越过边缘时会突然下坠，
    ///   腿会插进几何体。」
    ///   他先试「把起点改成骨盆正上方」→ 明确 did not help；
    ///   最后用「两者之间的插值」才解决。
    /// </summary>
    private bool TraceGround(int legIndex, Vector3 target, Vector3 up, out Vector3 hitPoint)
    {
        /* ★ 安全降级：射线没建好就不能探测。
         *   调用链上有三处会走到这里：
         *     InitializeFootTargets()（Start 内，BuildFootRay 之后）
         *     SolveLeg()             （FixedUpdate 内）
         *   任一处环境异常都不该让整段代码抛 NullReferenceException ——
         *   **假腿只是调试可视化，坏了不该影响本体运行。**
         */
        if (footRay == null)
        {
            hitPoint = target;
            return false;
        }

        // 骨盆上方 = 身体中心 + up * 足部探测高度
        Vector3 pelvisAbove = transform.position + up * footRayUp;

        // 脚上方 = 目标点 + up * 足部探测高度
        Vector3 footAbove = target + up * footRayUp;

        // ★ 起点插值
        Vector3 start = Vector3.Lerp(footAbove, pelvisAbove, footRayStartBlend);
        Vector3 end = start - up * (footRayUp + footRayDown);

        footRay.SetRadius(BallRadius * 0.15f);

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
    /// 腿根的世界坐标。
    ///
    /// ★ 用身体局部系算，所以贴到墙上时腿会自动「贴」着墙的表面排布。
    ///   local X = 左右 · local Y = 表面法线方向 · local Z = 前后
    ///   bodyHeight 取负 → 腿根在身体「下方」（贴着表面那一侧）。
    /// </summary>
    private Vector3 LegRootWorld(int i)
    {
        SilkSpiderLeg leg = legs[i];

        float side = leg.SideSign * (bodyHalfWidth + legRootSpread);
        float along = leg.ForwardT * bodyHalfLength * legSplay;

        Vector3 local = new Vector3(side, -bodyHeight, along);
        return transform.TransformPoint(local);
    }

    // ================================================================
    //  调试
    // ================================================================

    private void LateUpdate()
    {
        if (!showDebug || !initialized) return;

        for (int i = 0; i < 8; i++)
        {
            SilkSpiderLeg leg = legs[i];

            // 腿根 →锁定点
            Debug.DrawLine(LegRootWorld(i), leg.LockedTarget, Color.green);
            // 锁定点 → 目标点（这是本帧即将迈向的位置）
            Debug.DrawLine(leg.LockedTarget, leg.NextTarget, Color.yellow);

            // 足端实际位置 —— 用十字标记，不依赖 Debug.DrawSphere
            DrawCross(leg.FootWorld, BallRadius * 0.4f, Color.cyan);

            // 抬腿弧线的高度指示
            if (enableLegs)
            {
                float a = Mathf.Clamp01(leg.Timing / moveAlphaWindow);
                Vector3 up = mover.SurfaceNormal;
                Debug.DrawLine(leg.LockedTarget,
                               leg.LockedTarget + up * liftHeight
                                   * SilkSpiderLeg.LiftCurveAt(a),
                               new Color(1f, 0.5f, 0f, 0.6f));
            }
        }
    }

    /// <summary>
    /// 画一个三维十字标记。
    ///
    /// 【为什么不用 Debug.DrawSphere / Debug.DrawWireSphere】
    ///   这两个 API 在不同 Unity 版本里签名有差异
    ///   （带 duration 的重载在旧版本不存在），
    ///   而 Debug.DrawLine 是最古老、最稳定的那个。
    ///   → 调试辅助代码不值得为它冒编译风险。
    /// </summary>
    private static void DrawCross(Vector3 center, float size, Color col)
    {
        Debug.DrawLine(center - Vector3.right * size, center + Vector3.right * size, col);
        Debug.DrawLine(center - Vector3.forward * size, center + Vector3.forward * size, col);
        Debug.DrawLine(center - Vector3.up * size, center + Vector3.up * size, col);
    }
}