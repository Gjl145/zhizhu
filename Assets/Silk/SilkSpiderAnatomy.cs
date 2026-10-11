using UnityEngine;

/// <summary>
/// 蜘蛛完整身体 —— 头胸部 + 腹部 + 螯肢 + 8 条多节腿。
///
/// 【★★ 为什么单独一个文件】
///   用户原话：「现在的蜘蛛像是小球加腿，我要的是蜘蛛，不是小球加腿。」
///   → 之前的实现只画了 1 个扁球（腹部）+ 8 条单段 LineRenderer，
///     形态上就是「球 + 装饰物」，不是生物。
///   → 真实蜘蛛的形态（来源见下）：
///     · 身体分**头胸部**（cephalothorax，前端，8 只眼）+ **腹部**（abdomen，后端）
///     · 两段之间有细腰（pedicel / 腹柄）
///     · 前端有**螯肢**（chelicerae，带毒牙）
///     · 4 对**步足**，每条7 节（基节/转节/腿节/膝节/胫节/后跗节/跗节），
///       外形上明显是「大腿—膝—小腿」的**折线**，不是一根直棍
///     · 腿从头胸部两侧**偏后**生出，向两侧张开，末端收细
///
/// 【形态来源】
///   · Locomotor 教程视频（用户给定）帧 f080 / f140 / f160：
///     真实蜘蛛 FBX 的骨骼树是
///     body → back → popa（腹部）
///          → zub_L_small / zub_L_big（螯肢）
///          → leg_L_1_1 → leg_L_1_2 → leg_L_1_3 → leg_L_1_4 → leg_L_1_5
///     即 **每条腿 5 节骨骼链**，左右各 4 对 → 8 条。
///     本文件的腿节数与之对齐（视觉上5 段折线）。
///   · 澳大利亚博物馆 / 科普中国「蜘蛛」形态条目：
///     头胸部有背甲（carapace），通常 8 个单眼排成 2~4 行；
///     腹部多为圆形或卵圆形；纺器 3 对在腹部后端。
///
/// 【坐标系】Z-up。腿的局部系由 SilkSpiderSurfaceMove 对齐到表面法线，
///   所以地面 / 墙面 / 天花板 / 转角共用同一套腿，不需要任何特判。
///
/// 【★ 不改 SilkBuilder.cs】
///   本文件完全独立，通过 SilkSpiderBody 自动挂载。
/// </summary>
[DisallowMultipleComponent]
public class SilkSpiderAnatomy : MonoBehaviour
{
    // ================================================================
    //  体形参数（全部由球半径 visualRadius 推导，不写字面量）
    // ================================================================

    // ================================================================
    //  FBX 模型（★ 唯一的可见体来源）
    // ================================================================

    /// <summary>
    /// ★ 球半径（体形基准）。默认取 SilkParkourController.visualRadius。
    ///   现在只用于在 HUD 上显示体型比例，不再驱动任何程序化建模
    ///   （程序化建模已于 2026-10-11 全部删除）。
    /// </summary>
    [Header("FBX 模型（唯一的可见体来源）")]
    public float scaleRadius = -1f;

    [Tooltip("FBX 实例。留空则从 Resources/spider_lowpoly 自动加载。\n"
        + "★ 这不是必需的 —— 运行时 AddComponent 的组件在编辑器里没有 Inspector，\n"
        + "  所以真正的用法是「留空，走 Resources 自动加载」。")]
    public GameObject fbxInstance;

    [Tooltip("FBX 朝向修正（绕 X 轴，度）。90 = FBX 的 Y-up 转成本项目 Z-up。")]
    public float fbxOrientationFixDeg = 90f;

    [Tooltip("FBX 加载失败时是否报错。★ 建议保持 true ——\n"
        + "  静默失败会让人以为「蜘蛛没渲染出来」而查错方向")]
    public bool failIfFbxMissing = true;

    // ================================================================
    //  运行时
    // ================================================================

    /// <summary>视觉根节点（头胸部/腹部/所有腿的父）。</summary>
    public Transform VisualRoot { get; private set; }
    /// <summary>头胸部节点（腿根与眼睛挂在这里）。</summary>
    public Transform Cephalothorax { get; private set; }
    /// <summary>腹部节点。</summary>
    public Transform Abdomen { get; private set; }
    /// <summary>各条腿的世界坐标根（腿根）。长度 = SilkSpiderFbxRig.SilkSpiderLegs.Count。</summary>
    public Vector3[] LegRoots { get; private set; }
    /// <summary>各条腿的当前足端世界坐标。长度 = SilkSpiderFbxRig.SilkSpiderLegs.Count。</summary>
    public Vector3[] FootPositions { get; private set; }

    private Transform[] cephaloSegments;      // 头胸部的分块（做轻微起伏）
    private SilkSpiderLimb[] limbs;           // SilkSpiderLegs.Count 条腿，每条多节
    private static Shader cachedShader;

    private MeshRenderer[] allRenderers;

    /// <summary>FBX 接管器（未启用时为 null）。</summary>
    private SilkSpiderFbxRig fbxRig;

    /// <summary>★ True = 可见体来自 FBX，程序化建模已关闭。</summary>
    public bool UsingFbx { get { return fbxRig != null && fbxRig.Attached; } }

    /// <summary>FBX 接管器（供外部查询 / 调试）。</summary>
    public SilkSpiderFbxRig FbxRig { get { return fbxRig; } }

    /// <summary>供外部（HUD/调试）查询腿节数量。</summary>
    public int LimbCount => limbs != null ? limbs.Length : 0;

    // ================================================================
    //  构建
    // ================================================================

    /// <summary>★ 由 SilkSpiderBody.Start 调用。幂等。</summary>
    public void Build(Transform parent)
    {
        if (VisualRoot != null) return;

        if (scaleRadius <= 0f) scaleRadius = ResolveRadius();
        if (legLength <= 0f) legLength = scaleRadius * 3.6f;

        var rootGo = new GameObject("SpiderAnatomy");
        rootGo.transform.SetParent(parent, false);
        VisualRoot = rootGo.transform;

        /*★★★ FBX 是**唯一**可见体来源（2026-10-11 删除全部程序化建模）
         *
         * 【为什么必须没有 fallback】
         *   之前是 `if (!fbxOk) { BuildCephalothorax(); ... }`，
         *   意思是「FBX 拿不到就用球+腿凑合」。但这恰恰是**问题本身**：
         *     · 失败时静默退回一个不能看的模型 → 用户看到的永远是错的
         *     · 因为「凑合还能动」，永远不会有人去查为什么 FBX 没加载
         *   → 曾经卡在这里很久：每次汇报都写「待办：用户去拖 FBX」，
         *     而 `SilkSpiderAnatomy` 是**运行时 AddComponent** 的，
         *     编辑器里根本没有 Inspector 可拖 → 这个 fallback 永远在生效。
         *   → 现在 FBX 走 Resources 自动加载；拿不到就**明确报错并中止**，
         *     不再伪装成「有东西在跑」。
         */
        if (!TryBuildFromFbx())
        {
            Debug.LogError("[SpiderAnatomy] ★ 没有可见体 —— 蜘蛛本体不显示。\n"
                + "  程序化建模已于 2026-10-11 全部删除（用户判定「完全不能看」），"
                + "不再作为 fallback。\n"
                + "  请修好 FBX 加载（看上面那条错误）。"
                + "碰撞体不受影响，仍可正常移动。", this);
            limbs = new SilkSpiderLimb[SilkSpiderLegs.Count];
            LegRoots = new Vector3[SilkSpiderLegs.Count];
            FootPositions = new Vector3[SilkSpiderLegs.Count];
            return;
        }

        BuildLimbs();

        LegRoots = new Vector3[SilkSpiderLegs.Count];
        FootPositions = new Vector3[SilkSpiderLegs.Count];
        for (int i = 0; i < SilkSpiderLegs.Count; i++)
        {
            LegRoots[i] = limbs[i].RootWorld;
            FootPositions[i] = limbs[i].FootWorld;
        }

        CollectRenderers();
    }

    /// <summary>
    /// FBX 资源在 Resources 下的路径。
    /// ★ 为什么放 Resources：SilkSpiderAnatomy 是**运行时 AddComponent** 加上去的
    ///   （SilkSpiderBody.cs:511），所以它在编辑器里根本不存在，
    ///   用户**没有任何机会**把 FBX 拖到 Inspector 字段上。
    ///   → 之前设计的「手动拖 Fbx Instance」是**做不到的**，这就是蜘蛛本体
    ///     一直是「球加腿」而没人发现的真正原因。
    ///   → 改成Resources 运行时加载，不再依赖任何手动接线。
    /// </summary>
    private const string FbxResourcePath = "spider_lowpoly";

    /// <summary>
    /// 尝试用 FBX 作为可见体。成功返回 true。
    /// </summary>
    private bool TryBuildFromFbx()
    {
        if (fbxInstance == null) fbxInstance = LoadFbxFromResources();

        if (fbxInstance == null)
        {
            if (failIfFbxMissing)
            {
                Debug.LogError("[SpiderAnatomy] ★ 找不到蜘蛛 FBX 模型。\n"
                    + "  期望路径：Assets/Resources/" + FbxResourcePath + ".fbx\n"
                    + "  当前状态：已退回程序化建模 = 「球加腿」。\n"
                    + "  如果刚把 FBX 放进 Resources 目录，Unity 还在导入中，"
                    + "请等导入完成（看Unity 右下角转圈结束）后再运行。\n"
                    + "  重新生成 FBX：\n"
                    + "    \"/c/Program Files/Blender Foundation/Blender 5.2/blender.exe\""
                    + " --background --python Tools/blender/make_spider.py", this);
            }
            return false;
        }

        fbxRig = GetComponent<SilkSpiderFbxRig>();
        if (fbxRig == null) fbxRig = gameObject.AddComponent<SilkSpiderFbxRig>();

        fbxRig.fbxInstance = fbxInstance;
        fbxRig.orientationFixDeg = fbxOrientationFixDeg;

        // ★ FBX 实例必须是**子物体**，不能是场景里的独立根。
        //   否则它不会跟着蜘蛛移动 → 蜘蛛跑起来、腿留在原地。
        if (fbxInstance.transform.parent != VisualRoot)
            fbxInstance.transform.SetParent(VisualRoot, false);

        if (!fbxRig.Attach()) return false;

        // FBX 模式下 Cephalothorax / Abdomen 指向 FBX 里的对应骨骼，
        // 这样外部若取这两个属性不会拿到 null。
        var fbxRootT = fbxRig.FbxRoot;
        if (fbxRootT != null)
        {
            Transform ceph = fbxRootT.Find("head");
            if (ceph != null) Cephalothorax = ceph;
            Transform abd = fbxRootT.Find("abdomen");
            if (abd != null) Abdomen = abd;
        }

        Debug.Log("[SpiderAnatomy] 可见体已切换为 FBX 模型"
            + "（程序化建模已跳过）。" + fbxRig.DescribeLegs());
        return true;
    }

    private float ResolveRadius()
    {
        if (scaleRadius > 0f) return scaleRadius;

        SilkParkourController c = GetComponent<SilkParkourController>();
        if (c == null) c = FindObjectOfType<SilkParkourController>();
        if (c != null && c.visualRadius > 0f) return c.visualRadius;
        return 1f;
    }

    /// <summary>
    /// 从 Resources 加载蜘蛛 FBX。
    ///
    /// 【★ 为什么必须 Instantiate 而不是直接用资源】
    ///   Resources.Load 拿到的是**共享的 prefab 式资产**，直接SetParent 会污染
    ///   资源本身 → 第二次调用拿到的是已经挂在别人身上的对象。
    ///   必须 Instantiate 出一个独立实例。
    ///
    /// 【★ 为什么优先读SkinnedMeshRenderer 而不是 MeshFilter】
    ///   FBX 里97 个网格带蒙皮，导入后是 SkinnedMeshRenderer（+MeshFilter 冗余）。
    ///   骨骼权重挂在 SkinnedMeshRenderer 上，所以 FindMeshFilter 找得到、
    ///   但真正出画面的是 SkinnedMeshRenderer —— 两个都要查，否则会误判「模型是空的」。
    /// </summary>
    private static GameObject LoadFbxFromResources()
    {
        GameObject asset = Resources.Load<GameObject>(FbxResourcePath);
        if (asset == null)
        {
            Debug.LogWarning("[SpiderAnatomy] Resources.Load(\"" + FbxResourcePath
                + "\") 返回 null。确认文件在 Assets/Resources/" + FbxResourcePath
                + ".fbx（不带 .fbx 后缀）。");
            return null;
        }

        var instance = Instantiate(asset);
        instance.name = "SpiderModel_FBX";

        var skinned = instance.GetComponentInChildren<SkinnedMeshRenderer>(true);
        var meshFilter = instance.GetComponentInChildren<MeshFilter>(true);
        var meshRenderer = instance.GetComponentInChildren<MeshRenderer>(true);

        if (skinned == null && meshRenderer == null)
        {
            Debug.LogError("[SpiderAnatomy] FBX 加载了但里面**没有任何渲染器**。\n"
                + "  导入设置检查：\n"
                + "  · Optimize Game Objects必须**关闭**（勾了会把骨骼节点删掉，"
                + "IK 就无从下手）\n"
                + "  · Material Import Mode 建议 Import via Material Description Later",
                instance);
            return null;
        }

        Debug.Log("[SpiderAnatomy] FBX 自动加载成功：" + FbxResourcePath
            + "（Skinned=" + (skinned != null) + " MeshFilter=" + (meshFilter != null) + "）");

        return instance;
    }

    // ---------------------------------------------------------------
    //  腿
    // ---------------------------------------------------------------

    /// <summary>
    /// ★ 腿的分节比例，**只用于步态落点解算**，不再用于生成 mesh。
    ///
    /// 【★ 为什么 FBX 模式下还要留这两个参数】
    ///   `SilkSpiderBody.ResolveLegLength()` 与步态的落点分布都读它们
    ///   （见 SilkSpiderBody.cs:716）。删掉会连累整个步态系统。
    ///   但它们**不再驱动任何程序化建模** —— 2026-10-11 起建模代码已全部删除。
    ///
    ///   比例来源（解剖学）：腿节(femur) &gt; 膝节+胫节 &gt; 跗节(tarsus)。
    /// </summary>
    public float[] legSegments = new float[] { 0.30f, 0.16f, 0.32f, 0.14f, 0.08f };

    /// <summary>
    /// ★ 解算用腿长（球半径的 3.6 倍），**只用于步态落点解算**。
    ///   真实蜘蛛的腿长远大于身体，这是最显眼的物种特征。
    /// </summary>
    public float legLength = -1f;

    /// <summary>
    /// SilkSpiderLegs.Count 条腿，每条 5 节骨骼（对齐 Locomotor 的 leg_X_N_M 骨骼链）。
    ///
    /// 【★ 腿根布局 —— 这里改了架构】
    ///   之前：4 对腿沿身体全长 -1~+1 均布 → 前腿长在头顶前面。
    ///   现在：4 对腿集中在头胸部**后半**（legRootRearBias=0.45），
    ///        因为真实蜘蛛的口器/螯肢/眼占据前端。
    /// </summary>
    private void BuildLimbs()
    {
        limbs = new SilkSpiderLimb[SilkSpiderLegs.Count];

        for (int i = 0; i < SilkSpiderLegs.Count; i++)
        {
            var limb = new SilkSpiderLimb();

            /*★★★ FBX 接管是**唯一**路径（2026-10-11删除了程序化建腿）
             *
             * 【原来的逻辑为什么必须删】
             *   原来是`if (UsingFbx) {...; continue;}  // 成功才跳过
             *   → 接管失败就往下走`limb.Build(...)` 程序化建腿。
             *   而 BuildLimbs 由 Build() 无条件调用，
             *   所以只要 FBX 有一根骨骼对不上，就会多出一套无蒙皮的腿——
             *   画面变成「8 条有肉的腿 + 8 根光骨头」，比球加腿更糟。
             *   → 现在接管失败就明确报错，**绝不偷偷建一条假腿**。
             */
            Transform[] chain = fbxRig.GetLegChain(i);
            limb.AdoptExternalChain(chain);
            if (!limb.UsesExternalBones)
            {
                Debug.LogError("[SpiderAnatomy] 第 " + (i + 1)
                    + " 条腿接管失败（骨骼缺失或父级断裂，见 SilkSpiderFbxRig 的日志）。"
                    + "该腿不会被程序化建模替代 —— 本项目的腿只有 FBX 一条来源。", this);
                limbs[i] = limb;
                continue;
            }

            limbs[i] = limb;
        }
    }

    // ---------------------------------------------------------------
    //  每帧更新
    // ---------------------------------------------------------------

    /// <summary>
    /// ★ 更新所有腿的足端位置。
    ///   由 SilkSpiderBody 解算好落点后调用，逐条写进 Limb 并求解关节角。
    /// </summary>
    /// <param name="rootWorld">8 个腿根世界坐标</param>
    /// <param name="footWorld">8 个足端世界坐标</param>
    /// <param name="up">表面法线（腿的「上」方向）</param>
    /// <param name="groundNormals">
    /// ★ 2026-10-09 新增：每条腿落点处的表面法线。
    ///   用于足端朝向跟随（spider_ik 的footAngleToNormal）。
    ///   null = 全部退回用 up（等价于关闭朝向跟随）。
    /// </param>
    public void UpdateLimbs(Vector3[] rootWorld, Vector3[] footWorld, Vector3 up,
    Vector3[] groundNormals = null)
    {
        if (limbs == null) return;

        for (int i = 0; i < limbs.Length; i++)
        {
            if (limbs[i] == null) continue;

            Vector3 gn = (groundNormals != null && i < groundNormals.Length)
                ? groundNormals[i]
                : up;

            limbs[i].Solve(
                i < rootWorld.Length ? rootWorld[i] : limbs[i].RootWorld,
                i < footWorld.Length ? footWorld[i] : limbs[i].FootWorld,
                up, gn, limbs[i].footTiltDeg);

            if (i < FootPositions.Length) FootPositions[i] = limbs[i].FootWorld;
        }
    }

    private void CollectRenderers()
    {
        MeshRenderer[] found = VisualRoot != null
            ? VisualRoot.GetComponentsInChildren<MeshRenderer>(true)
            : new MeshRenderer[0];
        allRenderers = found;
    }

    /// <summary>腿的折线段（调试用）。</summary>
    public int GetLimbSegmentCount(int limbIndex)
    {
        if (limbs == null || limbIndex < 0 || limbIndex >= limbs.Length) return 0;
        return limbs[limbIndex].BoneCount;
    }

}