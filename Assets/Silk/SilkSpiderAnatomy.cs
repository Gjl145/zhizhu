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

    [Header("整体")]
    [Tooltip("球半径（体形基准）。默认取 SilkParkourController.visualRadius")]
    public float scaleRadius = -1f;

    [Tooltip("头胸部长度（沿前后方向，Z-up 项目里是 Y 轴）。球半径的 1.15 倍")]
    public float cephaloLength = -1f;

    [Tooltip("头胸部宽度（左右方向）。球半径的 1.30 倍\n"
        + "★ 比长度宽 —— 真实蜘蛛头胸部正面看接近圆形/方形")]
    public float cephaloWidth = -1f;

    [Tooltip("头胸部高度（法线方向）。球半径的 0.62 倍\n"
        + "★ 比宽度矮 —— 蜘蛛是「扁」的，这是最关键的轮廓特征")]
    public float cephaloHeight = -1f;

    [Tooltip("腹部半径（卵圆形的长半轴）。球半径的 1.15 倍\n"
        + "★ 真实蜘蛛的腹部往往比头胸部**大**，这是「蜘蛛」而非「甲虫」的关键")]
    public float abdomenRadius = -1f;

    [Tooltip("头胸部中心到腹部中心的距离。球半径的 1.95 倍\n"
        + "★ 拉开两者才有「两个身体段」的观感；靠太近会退回「一个球」")]
    public float bodyGap = -1f;

    [Header("步足")]
    [Tooltip("每条腿的**总长**（腿根到足端）。球半径的 3.6 倍\n"
        + "★ 真实蜘蛛的腿长远大于身体，这是最显眼的物种特征。\n"
        + "  之前用 2.4 倍 → 腿短到像装饰品，必须加大")]
    public float legLength = -1f;

    [Tooltip("腿的**分节比例**。从腿根到足端，各段占腿长的比例。\n"
        + "★ 依据解剖学：腿节(femur) > 膝节+胫节(tibia) > 跗节(tarsus)。\n"
        + "  之前的实现是单段直线 → 从任何角度看都没有「关节」")]
    public float[] legSegments = new float[] { 0.30f, 0.16f, 0.32f, 0.14f, 0.08f };

    [Tooltip("每条腿的**粗细**（腿根处半径）。球半径的 0.155倍")]
    public float legRootThickness = -1f;

    [Tooltip("足端相对腿根的粗细比例（腿尖只剩 35%）")]
    public float legTipRatio = 0.35f;

    [Tooltip("腿根相对头胸部的外扩（左右方向）。球半径的 0.62 倍\n"
        + "★ 腿根要**在头胸部之外**，否则腿看起来从肚子底下长出来")]
    public float legRootSpread = -1f;

    [Tooltip("腿根沿前后方向的位置分布（-1 最前 → +1 最后）。\n"
        + "★ 真实蜘蛛的 4 对腿**都长在头胸部后半**——\n"
        + "  因为口器、螯肢、眼睛占据着前端。\n"
        + "  之前的实现是 -1~+1 均布在全长上 → 前腿长到头顶前面，不对")]
    public float legRootRearBias = 0.45f;

    [Tooltip("腿的静态张开角（度）。0 = 完全竖直，90 = 完全水平")]
    public float legSpreadDeg = 62f;

    [Header("螯肢与眼")]
    [Tooltip("是否长螯肢（嘴前的一对小钳）。★ 强烈建议开 ——\n"
        + "  有螯肢才像蜘蛛，只剩8 条腿像螃蟹")]
    public bool buildChelicerae = true;

    [Tooltip("螯肢长度。球半径的 0.85 倍")]
    public float cheliceraLength = -1f;

    [Tooltip("眼睛数量（真实蜘蛛通常 8 只）")]
    public int eyeCount = 8;

    [Tooltip("单眼半径。球半径的 0.075 倍")]
    public float eyeRadius = -1f;

    [Header("配色")]
    public Color bodyColor = new Color(0.20f, 0.15f, 0.13f, 1f);
    public Color abdomenColor = new Color(0.26f, 0.19f, 0.15f, 1f);
    public Color legColor = new Color(0.16f, 0.13f, 0.12f, 1f);
    public Color eyeColor = new Color(0.95f, 0.85f, 0.55f, 1f);

    // ================================================================
    //  运行时
    // ================================================================

    /// <summary>视觉根节点（头胸部/腹部/所有腿的父）。</summary>
    public Transform VisualRoot { get; private set; }
    /// <summary>头胸部节点（腿根与眼睛挂在这里）。</summary>
    public Transform Cephalothorax { get; private set; }
    /// <summary>腹部节点。</summary>
    public Transform Abdomen { get; private set; }
    /// <summary>8 条腿的世界坐标根（腿根）。</summary>
    public Vector3[] LegRoots { get; private set; }
    /// <summary>8 条腿的当前足端世界坐标。</summary>
    public Vector3[] FootPositions { get; private set; }

    private Transform[] cephaloSegments;      // 头胸部的分块（做轻微起伏）
    private SilkSpiderLimb[] limbs;           // 8 条腿，每条多节
    private static Shader cachedShader;

    private MeshRenderer[] allRenderers;

    /// <summary>供外部（HUD/调试）查询腿节数量。</summary>
    public int LimbCount => limbs != null ? limbs.Length : 0;

    // ================================================================
    //  构建
    // ================================================================

    /// <summary>★ 由 SilkSpiderBody.Start 调用。幂等。</summary>
    public void Build(Transform parent)
    {
        if (VisualRoot != null) return;

        float r = ResolveRadius();

        // ---- 参数推导：全部由球半径导出 ----
        if (scaleRadius <= 0f) scaleRadius = r;
        if (cephalLength <= 0f) cephaloLength = r * 1.15f;
        if (cephaloWidth <= 0f) cephaloWidth = r * 1.30f;
        if (cephaloHeight <= 0f) cephaloHeight = r * 0.62f;
        if (abdomenRadius <= 0f) abdomenRadius = r * 1.15f;
        if (bodyGap <= 0f) bodyGap = r * 1.95f;
        if (legLength <= 0f) legLength = r * 3.6f;
        if (legRootThickness <= 0f) legRootThickness = r * 0.155f;
        if (legRootSpread <= 0f) legRootSpread = r * 0.62f;
        if (cheliceraLength <= 0f) cheliceraLength = r * 0.85f;
        if (eyeRadius <= 0f) eyeRadius = r * 0.075f;

        if (legSegments == null || legSegments.Length != 5)
        {
            legSegments = new float[] { 0.30f, 0.16f, 0.32f, 0.14f, 0.08f };
        }
        // 归一化，保证 5 段之和 = 1（否则腿的总长会随参数漂移）
        NormalizeSegments();

        var rootGo = new GameObject("SpiderAnatomy");
        rootGo.transform.SetParent(parent, false);
        VisualRoot = rootGo.transform;

        BuildCephalothorax();
        BuildAbdomen();
        if (buildChelicerae) BuildChelicerae();
        BuildEyes();
        BuildLimbs();

        LegRoots = new Vector3[8];
        FootPositions = new Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            LegRoots[i] = limbs[i].RootWorld;
            FootPositions[i] = limbs[i].FootWorld;
        }

        CollectRenderers();
    }

    private float ResolveRadius()
    {
        if (scaleRadius > 0f) return scaleRadius;

        SilkParkourController c = GetComponent<SilkParkourController>();
        if (c == null) c = FindObjectOfType<SilkParkourController>();
        if (c != null && c.visualRadius > 0f) return c.visualRadius;
        return 1f;
    }

    private void NormalizeSegments()
    {
        float sum = 0f;
        for (int i = 0; i < legSegments.Length; i++) sum += Mathf.Max(0.01f, legSegments[i]);
        if (sum <= 0.0001f) return;
        for (int i = 0; i < legSegments.Length; i++)
            legSegments[i] = Mathf.Max(0.01f, legSegments[i]) / sum;
    }

    // ---------------------------------------------------------------
    //  头胸部
    // ---------------------------------------------------------------

    /// <summary>
    /// 头胸部 = 扁椭球 + 略微隆起的中脊。
    /// ★ 为什么要「扁」：蜘蛛不是球，是被压扁的。高度只有宽度的 0.48 左右。
    /// </summary>
    private void BuildCephalothorax()
    {
        var go = new GameObject("Cephalothorax");
        go.transform.SetParent(VisualRoot, false);

        // 本项目 Z-up：Z = 法线方向（厚度），X = 左右（宽）， Y = 前后（长）
        go.transform.localScale = new Vector3(
            cephaloWidth * 2f, cephaloLength * 2f, cephaloHeight * 2f);

        Paint(go, bodyColor);
        Cephalothorax = go.transform;

        // ★ 前端略窄：真实蜘蛛头胸部前端（口器区）比后端细。
        //   做法：在前端加一个小的锥形收窄块，形成「盾形」轮廓。
        var snout = GameObject.CreatePrimitive(PrimitiveType.Cube);
        snout.name = "CephaloSnout";
        snout.transform.SetParent(go.transform, false);
        // Cube 原点是中心，前端在 +Y → localPosition 要往后偏移半个身位
        snout.transform.localPosition = new Vector3(0f, cephaloLength * 0.72f, 0f);
        snout.transform.localScale = new Vector3(
            cephaloWidth * 1.10f, cephaloLength * 0.55f, cephaloHeight * 1.45f);
        KillCollider(snout);
        Paint(snout, bodyColor);

        cephaloSegments = new Transform[] { go.transform };
    }

    private void BuildAbdomen()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = "Abdomen";
        go.transform.SetParent(VisualRoot, false);

        // ★ 腹部在**后方**（-Y），且比头胸部大
        go.transform.localPosition = new Vector3(0f, -bodyGap, -cephaloHeight * 0.15f);
        // 卵圆形：前后(Y) 略长、左右(X) 略窄、上下(Z) 最扁
        go.transform.localScale = new Vector3(
            abdomenRadius * 1.80f, abdomenRadius * 2.20f, abdomenRadius * 1.55f);

        KillCollider(go);
        Paint(go, abdomenColor);
        Abdomen = go.transform;

        // ★ 腹柄（pedicel）—— 头胸部与腹部之间的细腰。
        //   来源：科普中国「蜘蛛」：二者之间有纤细的腹柄相连。
        //   ★ 视觉作用极大：没有它，两个球直接相连 = 「一个球」；
        //     有了它，才读成「两个身体段」。
        var stalk = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        stalk.name = "Pedicel";
        stalk.transform.SetParent(VisualRoot, false);
        stalk.transform.localPosition = new Vector3(0f, -bodyGap * 0.52f, 0f);
        // Cylinder 原型高 2、轴向 Y→ 缩放时长度 = scale.y
        stalk.transform.localScale = new Vector3(
            cephaloWidth * 0.30f, bodyGap * 0.42f, cephaloWidth * 0.30f);
        KillCollider(stalk);
        Paint(stalk, bodyColor);

        //★ 纺器（spinnerets）：腹部后端的3 对小突起。
        //  来源：科普中国「蜘蛛」：腹部末端有纺器，一般 3 对。
        for (int i = 0; i < 3; i++)
        {
            float sx = (i - 1) * abdomenRadius * 0.34f;
            var sp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            sp.name = "Spinneret_" + i;
            sp.transform.SetParent(go.transform, false);
            sp.transform.localPosition = new Vector3(
                sx, -go.transform.localScale.y * 0.5f * 0.86f, 0f);
            sp.transform.localScale = new Vector3(
                abdomenRadius * 0.16f, abdomenRadius * 0.34f, abdomenRadius * 0.14f);
            KillCollider(sp);
            Paint(sp, abdomenColor * 0.7f);
        }
    }

    /// <summary>
    /// 螯肢 —— 头胸部前端的一对小钳。
    /// 来源：科普中国「蜘蛛」：第一对附肢为螯肢，基部为螯节，端部为螯牙。
    /// ★ 作用：只有 8 条腿 + 一个球 = 螃蟹；加上前端两把小钳才是蜘蛛。
    /// </summary>
    private void BuildChelicerae()
    {
        if (Cephalothorax == null) return;

        for (int s = -1; s <= 1; s += 2)
        {
            // 螯节（粗）
            var baseSeg = GameObject.CreatePrimitive(PrimitiveType.Cube);
            baseSeg.name = "Chelicera_" + (s < 0 ? "L" : "R");
            baseSeg.transform.SetParent(Cephalothorax, false);
            baseSeg.transform.localPosition = new Vector3(
                s * cephaloWidth * 0.34f,
                cephaloLength * 0.92f,
                cephaloHeight * 0.10f);
            baseSeg.transform.localScale = new Vector3(
                cephaloWidth * 0.26f, cheliceraLength * 0.60f, cephaloHeight * 0.55f);
            KillCollider(baseSeg);
            Paint(baseSeg, bodyColor * 1.15f);

            // 螯牙（细长，指向内下方 —— 蜘蛛的螯牙朝内）
            var fang = GameObject.CreatePrimitive(PrimitiveType.Cube);
            fang.name = "Fang_" + (s < 0 ? "L" : "R");
            fang.transform.SetParent(baseSeg.transform, false);
            fang.transform.localPosition = new Vector3(
                -s * cephaloWidth * 0.10f, cheliceraLength * 0.62f, -cephaloHeight * 0.20f);
            fang.transform.localScale = new Vector3(
                cephaloWidth * 0.11f, cheliceraLength * 0.55f, cephaloHeight * 0.22f);
            KillCollider(fang);
            Paint(fang, eyeColor * 0.55f);
        }
    }

    /// <summary>
    /// 单眼 —— 头胸部前端 2~4 行小圆点。
    /// 来源：澳洲博物馆「What is a spider」：通常 8 只眼，
    ///   排成两列于背甲前端。
    /// </summary>
    private void BuildEyes()
    {
        if (Cephalothorax == null || eyeCount <= 0) return;

        for (int i = 0; i < eyeCount; i++)
        {
            // 两行排列：4 + 4
            int row = i / 4;                 // 0 = 前排, 1 = 后排
            int col = i % 4;                 // 0..3
            float sideSign = (col < 2) ? -1f : 1f;
            float along = cephaloLength * (0.80f - row * 0.20f - (col % 2) * 0.06f);
            float across = sideSign * cephaloWidth * (0.46f - (col / 2) * 0.20f);

            var eye = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            eye.name = "Eye_" + i;
            eye.transform.SetParent(Cephalothorax, false);
            eye.transform.localPosition = new Vector3(
                across, along, cephaloHeight * 0.86f);
            eye.transform.localScale = Vector3.one * (eyeRadius * 2f);
            KillCollider(eye);
            Paint(eye, eyeColor);
        }
    }

    // ---------------------------------------------------------------
    //  腿
    // ---------------------------------------------------------------

    /// <summary>
    /// 8 条腿，每条 5 节折线（对齐 Locomotor 的 leg_X_N_M 骨骼链）。
    ///
    /// 【★ 腿根布局 —— 这里改了架构】
    ///   之前：4 对腿沿身体全长 -1~+1 均布 → 前腿长在头顶前面。
    ///   现在：4 对腿集中在头胸部**后半**（legRootRearBias=0.45），
    ///        因为真实蜘蛛的口器/螯肢/眼占据前端。
    /// </summary>
    private void BuildLimbs()
    {
        limbs = new SilkSpiderLimb[8];

        for (int i = 0; i < 8; i++)
        {
            bool left = i < 4;
            float sideSign = left ? -1f : 1f;

            // k = 该侧的第几条腿（0 = 最前，3 = 最后）
            float k = i % 4;

            // ★ 沿前后方向：整体往后偏（rearBias），再在很小的范围内铺开。
            //   真实蜘蛛的 4 对步足根部间距不大，跨度靠「膝关节外张」而不是根部铺开。
            float rearOffset = legRootRearBias;
            float spread = 0.26f;
            float along = (rearOffset + (k / 3f - 0.5f) * spread)
                           * cephaloLength * 0.5f * 2f;

            Vector3 rootLocal = new Vector3(
                sideSign * (cephaloWidth * 0.42f + legRootSpread),
                along,
                -cephaloHeight * 0.15f);      // 略偏下（贴地那侧）

            var limb = new SilkSpiderLimb();
            limb.Build(
                VisualRoot,
                "leg_" + (left ? "L" : "R") + "_" + (k + 1),
                rootLocal,
                legSegments,
                legLength,
                legRootThickness,
                legTipRatio,
                legSpreadDeg,
                legColor);

            limbs[i] = limb;
        }
    }

    // ---------------------------------------------------------------
    //  每帧更新
    // ---------------------------------------------------------------

    /// <summary>
    /// ★ 更新 8 条腿的足端位置。
    ///   由 SilkSpiderBody 解算好落点后调用，逐条写进 Limb 并求解关节角。
    /// </summary>
    /// <param name="rootWorld">8 个腿根世界坐标</param>
    /// <param name="footWorld">8 个足端世界坐标</param>
    /// <param name="up">表面法线（腿的「上」方向）</param>
    public void UpdateLimbs(Vector3[] rootWorld, Vector3[] footWorld, Vector3 up)
    {
        if (limbs == null) return;

        for (int i = 0; i < limbs.Length; i++)
        {
            if (limbs[i] == null) continue;

            limbs[i].Solve(
                i < rootWorld.Length ? rootWorld[i] : limbs[i].RootWorld,
                i < footWorld.Length ? footWorld[i] : limbs[i].FootWorld,
                up);

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
        return limbs[limbIndex].SegmentCount;
    }

    // ---------------------------------------------------------------
    //  绘制辅助
    // ---------------------------------------------------------------

    private static Shader GetShader()
    {
        if (cachedShader != null) return cachedShader;
        cachedShader = Shader.Find("Standard");
        if (cachedShader == null) cachedShader = Shader.Find("Legacy Shaders/Diffuse");
        if (cachedShader == null) cachedShader = Shader.Find("Unlit/Color");
        return cachedShader;
    }

    /// <summary>统一上色。</summary>
    private static void Paint(GameObject go, Color c)
    {
        MeshRenderer mr = go.GetComponent<MeshRenderer>();
        if (mr == null) return;

        Shader sh = GetShader();
        if (sh == null)
        {
            Debug.LogWarning("[SpiderAnatomy] 找不到可用着色器，蜘蛛会是粉红色。");
            return;
        }

        mr.material = new Material(sh);
        if (mr.material.HasProperty("_Color")) mr.material.color = c;
        if (mr.material.HasProperty("_BaseColor")) mr.material.SetColor("_BaseColor", c);
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
    }

    /// <summary>★ 必须删掉 CreatePrimitive 自带的 Collider。</summary>
    private static void KillCollider(GameObject go)
    {
        Collider c = go.GetComponent<Collider>();
        if (c != null) Destroy(c);
    }
}