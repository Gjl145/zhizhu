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

        // ★★ 换装后步态腿长自动反推（2026-10-11）
        //   之前 legLength = scaleRadius × 3.6（按球半径推导），
        //   换任意模型后落点分布就和腿的实际可达半径脱节 → 腿永远伸直够落点。
        //   现在：取 8 条腿里最长的链总长 × 0.95（留一点余量给身体起伏），
        //   步态落点永远在 FABRIK 可达范围内，**换任何模型都不用调参**。
        if (UsingFbx && limbs != null)
        {
            float maxChain = 0f;
            for (int i = 0; i < limbs.Length; i++)
                if (limbs[i] != null && limbs[i].UsesExternalBones)
                    maxChain = Mathf.Max(maxChain, limbs[i].ChainLength);
            if (maxChain > 0.01f)
            {
                legLength = maxChain * 0.95f * legLengthScale;
                Debug.Log("[SpiderAnatomy] 步态腿长自动反推 = " + legLength.ToString("F2")
                    + "（最长腿链 " + maxChain.ToString("F2")
                    + " × 0.95 × 腿长系数 " + legLengthScale + "）");
            }
        }

        LegRoots = new Vector3[SilkSpiderLegs.Count];
        FootPositions = new Vector3[SilkSpiderLegs.Count];
        for (int i = 0; i < SilkSpiderLegs.Count; i++)
        {
            LegRoots[i] = limbs[i].RootWorld;
            FootPositions[i] = limbs[i].FootWorld;
        }

        CollectRenderers();

        // ★★ 自动打一次可见体自检 —— 不用用户做任何操作就能看到卡在哪一环。
        //   起因：用户连续两次说「和之前没区别」，而我两次都猜错了原因
        //   （先猜「Unity 没编译」，再猜「HideBallVisuals 关掉了 FBX」）。
        //   → 猜的成本是「让用户再跑一次 + 再描述一次现象」，太高。
        //   → 不猜了，直接把依据打出来。
        DumpVisibleStateForDebug();
    }

    /// <summary>
    /// FBX 资源在 Resources 下的路径。
    /// ★ 2026-10-11 换装 Quaternius Spider（CC0，8 腿 × 3 节，
    ///   见 Docs/参考/蜘蛛模型选型_QuaterniusSpider_2026-10-11.md）。
    ///   旧模型 spider_lowpoly 保留在 Resources 里做对照，不再被加载。
    ///
    /// 【为什么放 Resources】SilkSpiderAnatomy 是**运行时 AddComponent** 加上去的
    ///   （SilkSpiderBody.cs），所以它在编辑器里根本不存在，
    ///   用户**没有任何机会**把 FBX 拖到 Inspector 字段上。
    ///   → 之前设计的「手动拖 Fbx Instance」是**做不到的**，这就是蜘蛛本体
    ///     一直是「球加腿」而没人发现的真正原因。
    ///   → 改成 Resources 运行时加载，不再依赖任何手动接线。
    /// </summary>
    private const string FbxResourcePath = "spider_quaternius";

    /// <summary>★ 当前用的蜘蛛资产名（mixall 包里的蓝色捕鸟蛛）。</summary>
    private const string FbxAssetName = "blue tarantula";

    /// <summary>
    /// 按资产名在全部已导入资产里找蜘蛛 FBX。
    /// 【为什么需要】Package Manager 导入的包资产不在 Resources 目录，
    ///   Resources.Load 拿不到；又因为是运行时 AddComponent、没有 Inspector
    ///   可拖引用 → 只能运行时扫资产。
    /// 【判据】!gameObject.scene.IsValid() = 是资产而不是场景里的实例。
    /// </summary>
    private static GameObject LoadFbxByAssetName()
    {
        GameObject[] all = Resources.FindObjectsOfTypeAll<GameObject>();
        GameObject found = null;
        for (int i = 0; i < all.Length; i++)
        {
            var g = all[i];
            if (g == null || g.name != FbxAssetName) continue;
            if (g.scene.IsValid()) continue;    // 场景实例跳过，只要资产
            var smr = g.GetComponentInChildren<SkinnedMeshRenderer>(true);
            if (smr != null) { found = g; break; }
        }
        if (found == null)
        {
            Debug.LogWarning("[SpiderAnatomy] 按资产名找不到 \"" + FbxAssetName
                + "\"。确认 mixall 包（Spiders - characters with animations）"
                + "已导入且包含该模型。");
            return null;
        }
        var instance = Instantiate(found);
        instance.name = "SpiderModel_FBX";
        FixMaterials(instance);
        Debug.Log("[SpiderAnatomy] FBX 资产扫描加载成功：" + FbxAssetName);
        return instance;
    }

    /// <summary>
    /// ★★ 材质可见性修复（两个加载路径共用）。
    ///
    /// 【修复 1：粉材质】mixall 包描述原话：「默认状态下，所有设置均针对URP配置。
    ///   如果您的整个场景显示为"粉色"，请更改渲染设置」—— 本项目是 Built-in，
    ///   URP 专用 shader 不被支持 → 渲染成洋红。→ 检测到不支持的 shader 换 Standard。
    /// 【修复 2：过暗】Quaternius 的教训：底色近黑的蜘蛛在暗光下融进背景
    ///   （22:24 截图证据）。底色最大分量 &lt; 0.3 时抬到中灰。
    /// 【为什么用 r.materials 而不是 sharedMaterial】
    ///   sharedMaterial 会改到 Assets 里的源资产（污染资源），
    ///   r.materials 拿的是运行时实例副本。
    /// </summary>
    private static void FixMaterials(GameObject instance)
    {
        var allRenderers = instance.GetComponentsInChildren<Renderer>(true);
        int fixedShaders = 0, tintedMats = 0;
        for (int i = 0; i < allRenderers.Length; i++)
        {
            Material[] mats = allRenderers[i].materials;
            bool changed = false;
            for (int m = 0; m < mats.Length; m++)
            {
                if (mats[m] == null) continue;

                // 粉材质：shader 不被当前管线支持（URP 专用 → Built-in 项目）
                if (mats[m].shader == null || !mats[m].shader.isSupported)
                {
                    Shader std = Shader.Find("Standard");
                    if (std != null) { mats[m].shader = std; fixedShaders++; changed = true; }
                }

                if (mats[m].HasProperty("_Color"))
                {
                    Color c = mats[m].color;
                    if (Mathf.Max(c.r, Mathf.Max(c.g, c.b)) < 0.3f)
                    {
                        mats[m].color = new Color(0.38f, 0.36f, 0.34f, c.a);
                        tintedMats++;
                        changed = true;
                    }
                }
            }
            if (changed) allRenderers[i].materials = mats;
        }
        if (fixedShaders > 0 || tintedMats > 0)
            Debug.Log("[SpiderAnatomy] 材质修复：粉材质换 Standard × " + fixedShaders
                + "，过暗抬亮 × " + tintedMats);
    }

    /// <summary>
    /// 深度按名查找（Transform.Find 只找直接子级，FBX 骨骼树都是深嵌套）。
    /// </summary>
    private static Transform FindDeepByName(Transform root, string name)
    {
        if (root == null) return null;
        int n = root.childCount;
        for (int i = 0; i < n; i++)
        {
            Transform c = root.GetChild(i);
            if (c == null) continue;
            if (c.name == name) return c;
            Transform deep = FindDeepByName(c, name);
            if (deep != null) return deep;
        }
        return null;
    }

    /// <summary>
    /// 尝试用 FBX 作为可见体。成功返回 true。
    /// </summary>
    private bool TryBuildFromFbx()
    {
        if (fbxInstance == null) fbxInstance = LoadFbxFromResources();

        // ★ 2026-10-12 换装 mixall 包：Package Manager 导入的资产**不在 Resources**，
        //   Resources.Load 拿不到 → 按资产名在全部已导入资产里扫描。
        if (fbxInstance == null) fbxInstance = LoadFbxByAssetName();

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
        // ★ 按当前模型的名字查（换模型时对照 Blender 骨骼树改这个列表）：
        //   Quaternius: Thorax(→Head) / Abdomen
        //   mixall 捕鸟蛛: body（头胸部）/ fanny（腹部）
        var fbxRootT = fbxRig.FbxRoot;
        if (fbxRootT != null)
        {
            Transform ceph = FindDeepByName(fbxRootT, "Thorax");
            if (ceph == null) ceph = FindDeepByName(fbxRootT, "body");
            if (ceph == null) ceph = FindDeepByName(fbxRootT, "Head");
            if (ceph != null) Cephalothorax = ceph;
            Transform abd = FindDeepByName(fbxRootT, "fanny");
            if (abd == null) abd = FindDeepByName(fbxRootT, "Abdomen");
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
        FixMaterials(instance);   // ★ 粉材质/过暗修复（共用方法，见其注释）

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

    /// <summary>
    /// ★★ 一次性把「画面上该有什么」全部打出来 —— 验收自检。
    ///
    /// 【★ 为什么要这个】
    ///   2026-10-11 用户连续两次反馈「和之前没区别」，而我只能靠猜：
    ///     第一次猜「Unity 没编译」→ 猜错了（DLL 确实比源码新）
    ///     第二次猜「HideBallVisuals 把 FBX 关了」→ 也猜错了（它只按名字关 Body/Nose）
    ///   **猜的成本太高，而且每次猜错都要用户再跑一次。**
    ///   → 不猜了：把所有判断依据一次打印出来，看日志就知道卡在哪一环。
    ///
    /// 【打印内容】
    ///   ① 层级：FBX 实例有几个渲染器、几个骨骼、有没有挂在场景里
    ///   ② 可见性：每个渲染器的 enabled 状态 + 实际是否在视锥内
    ///   ③ 尺寸：FBX 的包围盒 —— 模型太小或缩到 0 也会「看起来没区别」
    ///   ④ 位置：FBX 实例的世界坐标是否跟身体重合
    /// </summary>
    public void DumpVisibleStateForDebug()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== [蜘蛛可见体自检] ===");

        if (VisualRoot == null)
        {
            sb.AppendLine("★ VisualRoot 为 null → Build() 从未成功执行");
            Debug.Log(sb.ToString());
            return;
        }

        sb.AppendLine("VisualRoot = " + VisualRoot.name
            + "  世界坐标 " + VisualRoot.position.ToString("F3")
            + "  activeInHierarchy=" + VisualRoot.gameObject.activeInHierarchy
            + "  localScale=" + VisualRoot.localScale.ToString("F3"));

        Transform model = VisualRoot.Find("SpiderModel_FBX");
        if (model == null)
        {
            sb.AppendLine("★★ VisualRoot 下没有 SpiderModel_FBX —— FBX 接管失败");
            sb.AppendLine("    VisualRoot 的子物体：");
            for (int i = 0; i < VisualRoot.childCount; i++)
                sb.AppendLine("      - " + VisualRoot.GetChild(i).name);
            Debug.Log(sb.ToString());
            return;
        }

        sb.AppendLine("FBX 实例 = " + model.name
            + "  世界坐标 " + model.position.ToString("F3")
            + "  localScale=" + model.localScale.ToString("F3")
            + "  active=" + model.gameObject.activeInHierarchy
            + "  layer=" + model.gameObject.layer);

        Renderer[] rs = model.GetComponentsInChildren<Renderer>(true);
        int enabledCount = 0;
        var bbox = new Bounds(model.position, Vector3.zero);
        bool first = true;
        for (int i = 0; i < rs.Length; i++)
        {
            if (rs[i] == null) continue;
            if (rs[i].enabled) enabledCount++;
            if (!first) bbox.Encapsulate(rs[i].bounds.center);
            else { bbox = rs[i].bounds; first = false; }
        }

        sb.AppendLine("渲染器总数 = " + rs.Length + "，enabled = " + enabledCount);
        sb.AppendLine("★ 包围盒尺寸 = " + bbox.size.ToString("F4")
            + "  中心 " + bbox.center.ToString("F3"));
        if (bbox.size.sqrMagnitude < 1e-6f)
            sb.AppendLine("  ★★ 包围盒退化（全零）→ 模型没有有效顶点，或 scale = 0");

        // 材质与着色器 —— 粉色/白色通常意味着 shader 丢失
        var smr = model.GetComponentInChildren<SkinnedMeshRenderer>(true);
        if (smr != null)
        {
            sb.AppendLine("SkinnedMeshRenderer: " + (smr.enabled ? "enabled" : "★ disabled")
                + "  bones=" + (smr.bones != null ? smr.bones.Length : 0)
                + "  visible=" + smr.isVisible);
            var mats = smr.sharedMaterials;
            if (mats != null)
            {
                sb.AppendLine("  材质 " + mats.Length + " 个：");
                for (int i = 0; i < Mathf.Min(3, mats.Length); i++)
                {
                    if (mats[i] == null) { sb.AppendLine("    [" + i + "] null"); continue; }
                    sb.AppendLine("    [" + i + "] " + mats[i].name
                        + "  shader=" + (mats[i].shader != null
                            ? mats[i].shader.name : "★ null")
                        + (mats[i].shader != null && !mats[i].shader.isSupported
                            ? "  ★ shader 不被当前渲染管线支持" : ""));
                }
            }
            sb.AppendLine("  bounds(本地) = " + smr.localBounds.ToString("F4"));
        }

        // ---- ★★ 逐渲染器明细（2026-10-11）----
        // 【为什么加】97 个渲染器里只有 3 个 isVisible，其余 94 个去哪了
        //   靠汇总数字定不了性：是缩成像素点？包围盒退化被剔除？
        //   还是跑到画面外？→ 每个都列出可见性 + 世界包围盒 + 顶点数 + 骨骼数，
        //   一份日志就能分类（ Speck=包围盒极小 / OffScreen=包围盒在远处 /
        //   Degenerate=包围盒为零）。只加诊断，不改任何渲染逻辑。
        sb.AppendLine("---- 逐渲染器明细（" + rs.Length + " 个）----");
        int visCount = 0, invisSmall = 0, invisBig = 0;
        for (int i = 0; i < rs.Length; i++)
        {
            var r = rs[i];
            if (r == null) { sb.AppendLine("  [" + i.ToString("D3") + "] null"); continue; }

            bool vis = r.isVisible;
            if (vis) visCount++;
            Vector3 size = r.bounds.size;
            float maxDim = Mathf.Max(size.x, size.y, size.z);
            if (!vis && maxDim < 0.05f) invisSmall++;   // 疑似缩成点
            else if (!vis) invisBig++;                  // 有体积却不可见 → 位置/剔除问题

            string type = r is SkinnedMeshRenderer ? "SMR" : "MR";
            int bones = 0, verts = 0;
            var smr2 = r as SkinnedMeshRenderer;
            if (smr2 != null)
            {
                bones = smr2.bones != null ? smr2.bones.Length : 0;
                if (smr2.sharedMesh != null) verts = smr2.sharedMesh.vertexCount;
            }
            else
            {
                var mf = r.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null) verts = mf.sharedMesh.vertexCount;
            }

            sb.AppendLine("  [" + i.ToString("D3") + "] " + r.gameObject.name
                + " (" + type + ")"
                + " enabled=" + (r.enabled ? 1 : 0)
                + " visible=" + (vis ? 1 : 0)
                + " 骨骼=" + bones
                + " 顶点=" + verts
                + " 世界包围盒=" + size.ToString("F3")
                + " 中心=" + r.bounds.center.ToString("F1"));
        }
        sb.AppendLine("★ 明细汇总: visible=" + visCount
            + " / invisible且包围盒<0.05(缩成点)=" + invisSmall
            + " / invisible但有体积(位置或剔除)=" + invisBig);

        // 骨骼链是否真的有 5 节
        for (int i = 0; i < SilkSpiderLegs.Count; i++)
        {
            Transform[] chain = FbxRig != null ? FbxRig.GetLegChain(i) : null;
            sb.AppendLine("  腿" + i + " 链节数 = "
                + (chain != null ? chain.Length.ToString() : "null（接管失败）"));
        }

        Debug.Log(sb.ToString());
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

    [Tooltip("★ 步态腿长缩短系数（2026-10-11，用户原话「减低腿的长度，变成稍微短一点的腿」）。\n"
        + "1 = 完全按骨骼链实测长度（站得高、腿舒展）。\n"
        + "0.75 = 落点收拢到可达半径的 75%，FABRIK 会把腿**蜷起来**——\n"
        + "  视觉上就是短腿、蹲姿，身体高度也会被 Smart Body 自动降低。\n"
        + "★ 只影响落点分布，不用改模型、不用改步态参数。")]
    [Range(0.4f, 1f)] public float legLengthScale = 0.75f;

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