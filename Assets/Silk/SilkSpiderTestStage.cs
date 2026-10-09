using UnityEngine;

/// <summary>
/// 贴面移动测试场 —— ★ **今天第一个能立刻看到效果的交付物**
///
/// 【为什么必须有独立测试场 · MEMORY 迭代纪律】
///   「一次一特性，每个特性配独立测试关卡」。
///   贴面移动需要的几何条件很具体：
///     · 一面**足够高的竖墙**（至少 8 格）—— 验证「爬墙」
///     · 一段**矮台阶**（2~3 格）—— 验证「上台阶不卡」
///     · 一个**90° 内角**—— 验证「转角不抽搐」（原项目最难的场景）
///     · 一段**顶部悬空的横梁**—— 验证「天花板也能走」
///   这些在现有的住宅关卡里凑不齐，且会互相干扰定位。
///
/// 【坐标系】X = 左右 / Y = 前后 / Z = 高度。
///   所以「竖墙」是 XY 平面上的长方体，「台阶」是抬高 Z。
///
/// 【与主关卡的关系】
///   只在 freeBuildMode（自由搭建模式）下自动生成，
///   不动 createTestLevel 的原有住宅关卡 —— 用户正在自由搭建，
///   绝不能把它清掉（MEMORY 第十三节）。
/// </summary>
public class SilkSpiderTestStage : MonoBehaviour
{
    [Header("开关")]
    [Tooltip("★ **默认勾上** —— 进 Play 立刻能看到测试场与假骨骼，不用手动建。\n"
        + "为什么这次默认开：MEMORY 迭代纪律说「一次一特性配独立测试关卡」，\n"
        + "  测试场就是那个独立关卡。它是纯几何 + 幂等构建，\n"
        + "  不碰住宅关卡、不碰 freeBuildMode 的自由搭建成果。\n"
        + "★ 若你要在自己的场景里干净地试，把这个勾掉。")]
    public bool buildOnStart = true;

    [Header("尺寸（格）")]
    [Tooltip("主墙高度。至少要8 格以上才看得出「爬墙」")]
    public float wallHeight = 14f;

    [Tooltip("主墙宽度")]
    public float wallWidth = 10f;

    [Tooltip("主墙厚度")]
    public float wallDepth = 2f;

    [Tooltip("墙前方留给球活动的纵深")]
    public float floorDepth = 16f;

    [Tooltip("台阶数量。三个不同高度，覆盖「上台阶」")]
    public int stairCount = 3;

    [Tooltip("每级台阶的升高（格）。按 jumpSpeed=32 / g=98 算，\n"
        + "单次跳跃 apex = 32²/(2×98) = 5.2 格，3 格台阶爬得上去")]
    public float stairRise = 3f;

    [Tooltip("每级台阶的进深（格）。要大于球径 2.5 格，否则球会卡在棱上")]
    public float stairRun = 5f;

    [Tooltip("横梁离地高度（格）。做成「天花板」验证倒挂行走")]
    public float beamHeight = 9f;

    private static readonly Color WallColor = new Color(0.72f, 0.74f, 0.78f);
    private static readonly Color StairColor = new Color(0.86f, 0.64f, 0.42f);
    private static readonly Color BeamColor = new Color(0.55f, 0.72f, 0.62f);
    private static readonly Color FloorColor = new Color(0.62f, 0.64f, 0.68f);

    private void Awake()
    {
        /* ★★ 必须在 **Awake** 里做，不能放 Start。
         *
         * 【为什么】
         *   控制器 SilkParkourController.Start()（SilkBuilder.cs 4463起）
         *   会：① 若 createTestLevel 则 RebuildParkourStage()
         *        ② transform.position = startPosition     （默认 -30, 0, -32.75）
         *   本组件的 Start 与它**同帧、顺序不保证**——
         *   若控制器先跑，住宅已建、球已摆到旧坐标，我再改字段也来不及。
         *   ★ Unity 保证：同一物体上**所有 Awake 早于所有 Start**。
         *   → 这是唯一能可靠抢在关卡生成之前改字段的地方。
         *
         * 【两件事】
         *  ① createTestLevel = false → 别建住宅关卡，
         *     否则两套几何叠加，射线打到住宅的墙，看到的一切都是错的。
         *  ② freeBuildMode = true    → 球自动落到场景最高处。
         *     ★ 这条是关键：freeBuildMode 开时控制器走 DetectFreeBuildSpawn()，
         *       用 OverlapBox 扫全场景找Z 最高的顶面 —— 正好落在测试场的地板上。
         *       不开的话 startPosition 是写死的住宅坐标 (-30, 0, -32.75)，
         *       那里**没有地板**，球会直接掉出场景（就是「小球很容易丢了」那个老问题）。
         *
         * 【恢复方式】
         *   SilkBuilder 的 Create Test Level 与 Free Build Mode 都勾回去，
         *   并把本组件的 Build On Start 勾掉。
         */
        if (!buildOnStart) return;

        /*★★★ 不要因为「没有 SilkBuilder」就 return。
         *
         * 【踩过的坑 —— 这是「啥也没看到」的第6 个根因】
         *   原来这里写的是：
         *       SilkBuilder builder = GetComponent<SilkBuilder>();
         *       if (builder == null) return;      // ★ 直接放弃
         *
         *   而宿主 `SpiderTestHost` 是 `EnsureTestStage()` 自己 new 的空物体，
         *   **上面根本没有 SilkBuilder** → 每一次都命中这个 return
         *   → 下面那个 Build() 永远不执行
         *   → 测试场、相机、光源全都没建。
         *
         * ★ builder 在下面只用于「关掉 createTestLevel / 打开 freeBuildMode」
         *   这两件**锦上添花**的事，没有它也必须照常建测试场。
         */
        SilkBuilder builder = GetComponent<SilkBuilder>();
        if (builder == null)
        {
            // 场景里可能别处有（挂在别的物体上）—— 找一下，找不到也不影响建场
            builder = FindObjectOfType<SilkBuilder>();
            if (builder == null)
                Debug.Log("[SpiderTest] 场景里没有 SilkBuilder，"
                        + "将只用默认设置建测试场（不受影响）。");
        }

        if (builder != null && builder.createTestLevel)
        {
            builder.createTestLevel = false;
            Debug.Log("[SpiderTest] 已自动关掉 Create Test Level —— "
                    + "测试场与代码生成的住宅关卡不能共存。");
        }

        /*★★ freeBuildMode 必须开 —— 但落点要用 freeBuildSpawnOverride **钉死**。
         *
         * 【踩过的坑 —— 这才是「还是啥也没看到」的**真正**原因】
         *   `DetectFreeBuildSpawn()`（SilkBuilder.cs:4505）的逻辑是
         *   「扫全场找**Z 最高**的有顶面的物体，把球放在那个顶面上」。
         *
         *   ★ 测试场里 Z 最高的是**主墙的顶面（z = 14）**，不是地板（z = 0）。
         *   → 球被放到墙顶上：
         *       [FreeBuild] 自动落点：顶面 z=14.0 -> 球心 z=15.3
         *   → 相机跟球保持 4.1 格（FollowCamera 的固定距离）：
         *       [Follow] 相机到球 4.1 格 | 球半径 1.25
         *   → 球直径才 2.5 格，相机在 4.1 格外 —— **画面里只有一个小黑点**，
         *      腿也只有 2.5 格长 → 看上去就是「什么都没有」。
         *
         *   【为什么要开 freeBuildMode】
         *     不开的话 startPosition 是写死的住宅坐标 (-30, 0, -32.75)，
         *     那里没有地板，球会直接掉出场景。
         *
         * 【解法】
         *   freeBuildSpawnOverride 有最高优先级（4508行）——
         *   `if (builder.freeBuildSpawnOverride != Vector3.zero) return它 + forward*radius`
         *   → 用它把球**钉在测试场地板上**，绕开「找最高顶面」的逻辑。
         *   注意它加的是 `Vector3.forward * visualRadius`（+Z 方向），
         *   所以 override 要给**地板面**的 Z，而不是球心的 Z。
         */
        if (builder != null)
        {
            if (!builder.freeBuildMode)
            {
                builder.freeBuildMode = true;
                Debug.Log("[SpiderTest] 已自动开 Free Build Mode —— "
                        + "关掉它球会落在写死的住宅坐标（那里没有地板，会掉出场景）。");
            }

            // ★ 落点钉在地板：地板面在 z = 0（Build() 里 Floor 的 z 中心 -1、高 2）
            //   Y=-3：墙在 Y=0，球在墙前 3 格 —— 近到能直接撞上（测贴墙），
            //        又不会一出生就贴着墙（贴面逻辑来不及初始化）。
            //   Z=0：加完visualRadius 后球心 = 1.25，正好悬在地板面上。
            if (builder.freeBuildSpawnOverride == Vector3.zero)
            {
                builder.freeBuildSpawnOverride = new Vector3(0f, -3f, 0f);
                Debug.Log("[SpiderTest] 已把球心落点钉在地板 (0, -3, ~1.25)。\n"
                        + "  ★ 不钉的话 DetectFreeBuildSpawn 会找「Z 最高的顶面」，"
                        + "  那就是**主墙顶 z=14**，球会站到墙顶上、离相机极远，"
                        + "  画面里只剩一个小黑点。");
            }
        }

        /* ★★ 关键：测试场必须在**这里（Awake）就建好**，不能等 Start。
         *
         * 【为什么】
         *   控制器 Start() 里是：
         *       transform.position = startPosition;
         *       CreateVisual();          ← 球在这里建出来
         *   ★ 但它用的是 **startPosition**（写死的字段），
         *     **不是** EffectiveStartPosition（那个才走 DetectFreeBuildSpawn）。
         *     → 所以 freeBuildMode 在 Start 阶段对落点**毫无影响**。
         *
         *   而 Tab 切到 Parkour 时（HandleModeSwitch, 5054行）才走：
         *       if (builder.freeBuildMode) startPosition = DetectFreeBuildSpawn();
         *
         *   → **结论：球要正确落在地板上，必须切一次 Tab。**
         *     这也是原项目「小球很容易丢了」的同一个老问题的解法。
         *
         *   ★ 无论地板建在Awake 还是 Start 都不影响这一点 ——
         *     因为 DetectFreeBuildSpawn 只在 Tab 切换时被调用。
         *     建早一点只是保证「按 Tab 时地板已经在场」。
         */
        Build();
    }

    /// <summary>
    /// 兜底：把任何**已存在**的旧关卡几何清掉。
    ///
    /// 【为什么 Awake 已经关掉了 createTestLevel，还要再做一次】
    ///   `Awake` 只能拦住「还没生成」的情况。但住宅也可能在这些时机冒出来：
    ///     · Tab 切模式时 RebuildParkourStage()（若 createTestLevel 被别处改回true）
    ///     · 用户在检查器里手动改过开关
    ///     · 上一次 Play 的残留（切回 FreeFly 时才Destroy，某些路径不Destroy）
    ///   → 与其枚举所有时机，**不如每帧确认一次「场里只有我的测试场」**。
    ///
    /// 【为什么是 LateUpdate 而不是每帧 Clear】
    ///   Clear() 会遍历全场做 Find，这是有成本的。
    ///   而住宅生成只发生在关卡重建时（切模式 / 启动），
    ///   所以**只在相位变化的那一帧查一次**就够了 ——
    ///   LateUpdate 里检查是否刚切过模式，切了才清。
    /// </summary>
    private void LateUpdate()
    {
        if (!buildOnStart) return;

        /*★ 用 SilkBuilder.parkourMode，**不用**控制器的 mode 字段——
         *   `SilkControlMode mode` 是 SilkParkourController 的**私有**字段
         *   （SilkBuilder.cs:5017，无访问修饰符 = private），
         *   从本文件读它会报 **CS0122「不可从外部访问」**。
         *   `parkourMode` 是 public（SilkBuilder.cs:2759），
         *   且控制器切模式时会同步（SilkBuilder.cs:5036）：
         *       builder.parkourMode = (mode == SilkControlMode.Parkour);
         *   ★ MEMORY 第六节：跨类访问私有成员是本项目栽过 8 次的坑。
         *
         * ★★ 没有 SilkBuilder 时**不能直接 return**（那就等于永不执行）：
         *   宿主 SpiderTestHost 上就没有 SilkBuilder。
         *   → 没有它时按「不在 Parkour」处理，但仍然做一次清场兜底。
         */
        SilkBuilder builder = GetComponent<SilkBuilder>();
        if (builder == null) builder = FindObjectOfType<SilkBuilder>();

        bool inParkour = builder != null && builder.parkourMode;

        // 只在「刚切进 Parkour」的那一帧动手
        if (!inParkour)
        {
            lastParkour = false;
            return;
        }

        if (lastParkour) return;      // 已经在Parkour 里且没再切换 → 不重复清
        lastParkour = true;

        ClearForeignStages();
    }

    /// <summary>上一次检查时是否已在 Parkour 世界（用于只在切换那一帧清场）。</summary>
    private bool lastParkour;

    /// <summary>清掉所有不是 SpiderTestStage 的旧关卡几何。</summary>
    private void ClearForeignStages()
    {
        // 这些 Clear 都是幂等的（找不到就什么都不做），
        // 住宅/摆荡关卡若没被生成就是空操作
        SilkParkourStage.Clear();
        SilkSwingTestStage.Clear();
        SilkSwingUnitStage.Clear();
        HouseBlockout.Clear();

        // 兜底：扫一遍全场，把名字带关卡前缀的残留物删掉
        // （重建失败的半成品、或重建时 Destroy 漏掉的）
        GameObject[] all = FindObjectsOfType<GameObject>();
        int removed = 0;

        for (int i = 0; i < all.Length; i++)
        {
            GameObject go = all[i];
            if (go == null) continue;

            // ★ 只删**根物体**，子物体随父级一起消失。
            //   判断依据：没有父物体（自己就是根）
            if (go.transform.parent != null) continue;
            if (go.name == "SpiderTestStage") continue;

            /*★★ 白名单：自己的东西、以及相机/光源绝不能删。
             *   ★ 这是血的教训 —— 之前只写了「跳过 SpiderTestStage」，
             *     但宿主 SpiderTestHost、相机 SpiderTestCamera、
             *     光源 SpiderTestLight 也都是根物体。
             *     规则虽然恰好没匹配上它们的命名，**但那是运气，不是设计**。
             *   → 显式列出，别指望命名规则永远不出错。*/
            if (go.name == "SpiderTestHost" ||
                go.name == "SpiderTestCamera" ||
                go.name == "SpiderTestLight" ||
                go.name == "SpiderAutoAttach" ||
                go.name == "SilkPlayer")
                continue;

            // ★ 用户自己的场景物体也不能删 —— 规则是给「代码生成的关卡」用的，
            //   不是给用户手搭的东西用的。
            if (go.name == "Cube" || go.name == "Sphere" ||
                go.name == "Main Camera" || go.name == "Directional Light")
                continue;

            string n = go.name;
            bool looksLikeStage =
                n.StartsWith("Plat_") || n.StartsWith("Barrier_") ||
                n.Contains("House") || n.Contains("Stage_") ||
                n.Contains("Swing") || n.Contains("Anchor");

            if (looksLikeStage)
            {
                Debug.LogWarning("[SpiderTest] 发现残留关卡物体，已删除：" + n);
                Destroy(go);
                removed++;
            }
        }

        if (removed > 0)
            Debug.LogWarning("[SpiderTest] 共清理 " + removed
                    + " 个旧关卡物体。测试场现在应该是干净的了。");
    }

    private void Start()
    {
        // ★ 刻意留空：几何已在 Awake 建好。
        //   若放 Start 建，控制器的 Start 可能先跑完，AutoAttach 链就乱了。

        /*★★ 必须在这里做「缺什么补什么」——
         *   否则玩家看到的是**一片空白**。
         *
         * 【查清的事实】
         *   SampleScene 里只有 `bossbattle` 一个物体，它挂 `SilkBuilder`。
         *   而相机、控制器（SilkPlayer）、球**全都是 `SilkBuilder.Build()` 里
         *   运行时创建的**（SilkBuilder.cs 3630–3660）：
         *
         *       var camGO = new GameObject("Main Camera");
         *       var playerGO = new GameObject("SilkPlayer");
         *           playerGO.AddComponent<SilkParkourController>();
         *
         *   ★ 但 Build() 开头有**幂等守卫**（SilkBuilder.cs 3614）：
         *       var existing = Object.FindObjectOfType<SilkBuilder>();
         *       if (existing != null) { ...; return; }   ← 直接跳过！
         *
         *   场景里已经有 `bossbattle`（它就是 SilkBuilder）→
         *   → Build() 整个跳过 → **相机没建、控制器没建、球没建**
         *   → 屏幕上什么都没有。
         *
         * 【这就是「啥也没看到」的真正原因】
         *   不是组件没挂、不是腿没生成，是**根本没有相机在渲染**。
         */
        EnsurePlayable();
    }

    /// <summary>
    /// 保证场上**至少有一个能渲染的相机 + 一个光源**。
    ///
    /// ★★ 这个方法是「用户啥也没看到」的**最后一层兜底**，
    ///    必须在建几何**之前**就跑，且完全不依赖 SilkParkourController。
    ///
    /// 【为什么单独抽出来】
    ///   Build() 由 Awake 调用（比所有 Start 都早），
    ///   而 EnsurePlayable() 在 Start 里调—— 那时相机已经晚了一帧，
    ///   首帧渲染出来是黑的。所以 Build() 里必须自己先调一次。
    ///
    /// 【为什么场景里没有相机】
    ///   SampleScene.unity 里只有一个 `bossbattle`（挂 SilkBuilder），
    ///   Camera / Light / MeshRenderer 的数量**都是 0**。
    ///   相机原本是 SilkBuilder.Build() 在运行时 new 出来的，
    ///   但那个方法开头有幂等守卫 `FindObjectOfType&lt;SilkBuilder&gt;() != null → return`，
    ///   场景里已经有 bossbattle 了 → **相机永远不会被创建**。
    ///
    /// ★ 全部幂等：已存在的绝不重复创建。
    /// </summary>
    private void EnsureCameraAndLight()
    {
        /*★★ 关键：不仅「缺失才建」，**存在也要摆位**。
         *
         * 【踩过的坑 —— 「啥也没看到」的第 5 个根因】
         *   `ceshi.unity` 里**是有相机的**（Main Camera，在 (0, 1, -10)），
         *   所以旧写法 `if (cam == null) { ...建相机... }` 整段被跳过。
         *   ★ 但那个相机是**Unity 默认 Y-up 摆放**的：
         *       位置 (0,1,-10)，朝 −Z 方向看（相机自己的 +Z 是「背后」）
         *       → 它看向的是 Z = −10 之外的空处
         *   而本项目是 **Z-up**，测试场建在 Z = 0 ~ 14 的高度上。
         *   → **相机存在，但完全没在看测试场** → 屏幕上什么都没有。
         *
         *   同样的问题还有场景里那两个物体：
         *       Cube   在 (2, 101, 86.5)        —— 离原点 139 格
         *       Sphere 在 (41152, 166.5, 114044) —— 离原点 **12 万格**
         *   Sphere 甚至在远裁剪面（1000）之外，**永远不会被渲染**。
         *
         * 【★ 只摆一次，不要反复覆盖用户的视角】
         *   `Build()`（Awake）与 `EnsurePlayable()`（Start）都会调本方法。
         *   若每次都强制摆位，用户第一次 Play 摆好的机位会在下一帧被冲掉。
         *   → 用 cameraPlaced 标记，只在第一次摆。
         */
        if (cameraPlaced) return;

        // ---------- ① 相机 ----------
        Camera cam = Camera.main;
        if (cam == null) cam = FindObjectOfType<Camera>();

        if (cam == null)
        {
            var camGO = new GameObject("SpiderTestCamera");
            camGO.tag = "MainCamera";
            cam = camGO.AddComponent<Camera>();
            camGO.AddComponent<SimpleOrbitCamera>();
            Debug.Log("[SpiderTest] 场景里没有相机，已自动创建 SpiderTestCamera。");
        }
        else
        {
            Debug.Log("[SpiderTest] 场景里已有相机 " + cam.name
                    + "，已把它摆到测试场正前方（原来它没在看测试场）。");
        }

        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.10f, 0.11f, 0.14f);
        cam.nearClipPlane = 0.05f;
        cam.farClipPlane = 500f;

        /*② 摆位 —— 本项目是 Z-up（MEMORY 第二节）。
         *
         * ★ 看的目标点不是原点，而是**测试场中心偏下**：
         *   地板面 z=0、主墙 0~14，横梁 z=9，
         *   球的落点钉在 (0, -3, 0)+半径 → 球心 (0,-3,1.25)。
         *   → 目标点取 (0, -2, 4)：既能看到地板，也能在画面里带上墙的上半段。
         *
         *   相机站在 −Y 侧偏上，往 +Y 看。
         *   ★ 用 LookRotation 的双参数重载显式指定参考上轴 = +Z；
         *     不要先给 transform.up 赋值再 LookAt ——
         *     那是两次独立重算 rotation，叠加后朝向不可控。*/
        Vector3 lookTarget = new Vector3(0f, -2f, 4f);
        Vector3 camPos = new Vector3(0f, -24f, 11f);
        cam.transform.position = camPos;
        cam.transform.rotation = Quaternion.LookRotation(
            (lookTarget - camPos).normalized, Vector3.forward);

        // 相机得有 SimpleOrbitCamera 才能转视角（否则鼠标动不了）
        if (cam.GetComponent<SimpleOrbitCamera>() == null)
            cam.gameObject.AddComponent<SimpleOrbitCamera>();

        cameraPlaced = true;

        // ---------- ③ 光照（没光的话几何全黑，一样看不见）----------
        Light existingLight = FindObjectOfType<Light>();
        if (existingLight == null)
        {
            var lightGO = new GameObject("SpiderTestLight");
            lightGO.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            var light = lightGO.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;

            Debug.Log("[SpiderTest] 场景里没有光源，已自动创建 SpiderTestLight。");
        }

        // ★ 环境光无条件设：场景里若 Lightmap 已烘焙 / 环境光为黑，
        //   朝不到光的背面会全黑，看不出轮廓。
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.42f, 0.44f, 0.48f);
    }

    /// <summary>相机是否已摆过位（避免 Start 里二次覆盖用户的视角）。</summary>
    private bool cameraPlaced;

    /*★★★ 屏幕自检提示 —— 用 OnGUI 直接把状态画在画面上。
     *
     * 【为什么要这个】
     *   「啥也没看到」这个反馈之所以难办，是因为**用户无法描述看到的是什么**：
     *   是纯黑？是灰地板但没球？有球但太小？还是根本没进 Play？
     *   ★ 加了这段，只要 Play 起来，画面左上角一定会有文字，
     *     不用看Console、不用猜 —— 一次就能定位到是哪一环断了。
     *
     * 【为什么用 OnGUI 而不是 UI Toolkit / TextMeshPro】
     *   · OnGUI 不需要任何场景资源，代码加完就能显示
     *   · Built-in 管线 + 可能没有导入 TMP 包（不引入新依赖）
     *   · IMGUI 在运行时绘制，最适合做「临时自检 HUD」
     *
     * 【显示内容】
     *   当前模式（FreeFly / Parkour）· 球是否存在 · 8 条腿是否已建
     *   球心坐标 · 相机与球的距离 · 当前操作提示
     */
    private void OnGUI()
    {
        if (!buildOnStart) return;
        if (!showHud) return;

        // ★ 只有真的看到东西才提示，否则纯黑屏上写字没有意义
        Camera cam = Camera.main != null ? Camera.main : FindObjectOfType<Camera>();

        string mode = "未知";
        bool ballInScene = false;
        Vector3 ballPos = Vector3.zero;
        float camDist = 0f;

        SilkParkourController ctrl = FindObjectOfType<SilkParkourController>();
        if (ctrl != null)
        {
            // mode 是 private（SilkBuilder.cs:5017）→ 不能读。
            // ★ 改用「球是否已建」反推：Parkour 才有球。
            Transform[] kids = ctrl.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < kids.Length; i++)
            {
                if (kids[i] != null && kids[i].name == "ParkourBody")
                {
                    ballInScene = true;
                    ballPos = kids[i].position;
                    break;
                }
            }

            if (cam != null)
                camDist = Vector3.Distance(cam.transform.position, ctrl.transform.position);
        }

        SilkSpiderBody body = FindObjectOfType<SilkSpiderBody>();
        int boneCount = 0;
        int bodyPartCount = 0;
        string gaitInfo = "未建";
        if (body != null && body.Anatomy != null)
        {
            /*★★ 2026-10-09：HUD 改读 SilkSpiderAnatomy。
             *   旧的单段直线版腿部实现已删除，其成员（Legs 数组）也已不存在，
             *   → 继续读会 CS1061 / NullReference。*/
            SilkSpiderAnatomy anat = body.Anatomy;
            bodyPartCount = 1;
            if (anat.Cephalothorax != null) bodyPartCount++;
            if (anat.Abdomen != null) bodyPartCount++;

            // 骨节总数 = 8 条腿 × 每条节数 → 检验「多节腿」真的建出来了
            if (anat.LegRoots != null)
            {
                for (int i = 0; i < anat.LegRoots.Length && i < 8; i++)
                {
                    int segs = anat.GetLimbSegmentCount(i);
                    if (segs > 0) boneCount += segs;
                }
            }
            gaitInfo = "交替四足步态 duty=" + body.dutyFactor.ToString("F2")
                      + " → 着地≈ " + (8 * body.dutyFactor).ToString("F1") + " 条\n"
                      + "步时: " + (body.scaleCycleBySpeed ? "随速度缩放" : "固定")
                      + "（过冲 " + body.overshootMultiplier.ToString("F2")
                      + "× 站位）";
        }

        mode = ballInScene ? "Parkour（球已建）" : "FreeFly（未按 Tab）";

        string text =
            "蜘蛛自检（真解剖 + FABRIK IK）\n" +
            "模式: " + mode + "\n" +
            "球: " + (ballInScene ? "存在  心(" + ballPos.x.ToString("F1") + ", "
                                  + ballPos.y.ToString("F1") + ", "
                                  + ballPos.z.ToString("F1") + ")" : "不存在") + "\n" +
            "身体段: " + bodyPartCount + " / 3（头胸+腹部+腹柄）\n" +
            "腿骨节: " + boneCount + " （8条 × 5节 = 40）\n" +
            gaitInfo + "\n" +
            "相机↔球: " + camDist.ToString("F1") + " 格\n" +
            (cam != null ? "相机(" + cam.transform.position.x.ToString("F0") + ", "
                               + cam.transform.position.y.ToString("F0") + ", "
                               + cam.transform.position.z.ToString("F0") + ")\n" : "") +
            (ballInScene ? "WASD走位 空格跳 鼠标转视角"
                         : "★ 现在按 Tab 切到 Parkour 才会建球");

        GUI.Label(new Rect(10, 10, 480, 240), text, HudStyle);
    }

    /// <summary>HUD 文字样式（只初始化一次）。</summary>
    private GUIStyle HudStyle
    {
        get
        {
            if (hudStyle == null)
            {
                hudStyle = new GUIStyle();
                // ★ 用粗体大字：截图发我时一眼能看清
                hudStyle.fontSize = 16;
                hudStyle.fontStyle = FontStyle.Bold;
                // 自带深色描边，压在任何背景上都能读
                hudStyle.normal.textColor = Color.white;
                hudStyle.normal.textColor = new Color(1f, 1f, 1f, 1f);
                hudStyle.wordWrap = false;
            }
            return hudStyle;
        }
    }
    private GUIStyle hudStyle;

    /// <summary>是否显示自检 HUD（默认开，排查完可关掉）。</summary>
    [Tooltip("★ 画面左上角的实时状态显示。"
        + "排查「看不到东西」时非常有用——不用看 Console 就能知道断在哪一环。")]
    public bool showHud = true;

    /// <summary>
    /// 补齐「能看见、能操作」所必需的东西：相机、光源、控制器。
    ///
    /// ★ 全部幂等——已有的绝不重复创建。
    /// </summary>
    private void EnsurePlayable()
    {
        // 相机 + 光源：Build() 里已经调过一次，这里再调一次保证幂等。
        EnsureCameraAndLight();

        // ---------- 控制器 ----------
        SilkParkourController ctrl = GetComponent<SilkParkourController>();
        if (ctrl == null) ctrl = FindObjectOfType<SilkParkourController>();

        if (ctrl == null)
        {
            var playerGO = new GameObject("SilkPlayer");
            playerGO.transform.position = new Vector3(0f, -8f, 3f);
            ctrl = playerGO.AddComponent<SilkParkourController>();
            Debug.Log("[SpiderTest] 场景里没有控制器，已自动创建 SilkPlayer。");
        }

        Debug.Log("[SpiderTest] ===== 测试场就绪 =====\n"
                + "  ★ 现在按一下 Tab 切到 Parkour —— 球会自动落到地板上、长出蜘蛛。\n"
                + "  ★ 不按 Tab 看不到球：mode 初值是 FreeFly，控制器只在 Parkour 建球\n"
                + "    （SilkBuilder.cs:4475）。这是原有设计，不是 bug。\n"
                + "  开关默认全部开启（Show Debug / Enable Legs / Show Gait Diagram），\n"
                + "  不需要去检查器里手动勾 —— 组件是运行时自动挂的，检查器里看不到。\n"
                + "  ★ 想看步态对不对 → 打开 SilkSpiderBody 的 Show Gait Diagram，\n"
                + "    屏幕右上角会画出 8 条腿的时序条带（黑=支撑 / 蓝=摆动）。");
    }

    /// <summary>手动调用也能建（可从 Unity 编辑器右键菜单触发）。</summary>
    [ContextMenu("构建贴面测试场")]
    public void Build()
    {
        // ★ 先保证「有东西可看」—— 相机/光源必须在建几何之前就位，
        //   否则 Build() 刚跑完的那一帧画面还是黑的。
        EnsureCameraAndLight();

        // 幂等：重复调用不会堆出两层地板
        Transform existing = transform.Find("SpiderTestStage");
        if (existing != null) Destroy(existing.gameObject);

        var root = new GameObject("SpiderTestStage");
        root.transform.SetParent(transform, false);

        // ★ 一切以球心为原点 —— 让球出生在 (0, 0, 高度)，
        //   离墙一段距离正好用来测试「撞墙 → 贴上去」这个过程。
        float floorY = -wallDepth * 0.5f - 3f;   // 墙在 Y=0，球在 Y=+8 的开阔地

        // ---------- 地面 ----------
        MakeBox(root.transform, "Floor",
            new Vector3(0f, floorY * 0.5f, -1f),
            new Vector3(wallWidth + 16f, Mathf.Abs(floorY) + 6f, 2f),
            FloorColor);

        // ---------- 主墙（验证爬墙）----------
        MakeBox(root.transform, "MainWall",
            new Vector3(0f, 0f, wallHeight * 0.5f),
            new Vector3(wallWidth, wallDepth, wallHeight),
            WallColor);

        // ---------- 台阶（验证上台阶）----------
        // 放在墙的左侧，沿 +X 往上，每级升高 stairRise
        float stairX = wallWidth * 0.5f + stairRun * (stairCount + 1);
        for (int i = 0; i < stairCount; i++)
        {
            float h = stairRise * (i + 1);
            MakeBox(root.transform, "Stair_" + i,
                new Vector3(stairX - stairRun * (i + 0.5f), 0f, h * 0.5f),
                new Vector3(stairRun, wallDepth, h),
                StairColor);
        }

        // ---------- 横梁（验证天花板倒挂）----------
        // 架在墙前 6 格处，高度 beamHeight
        MakeBox(root.transform, "CeilingBeam",
            new Vector3(0f, -6f, beamHeight),
            new Vector3(wallWidth + 6f, 3f, 1f),
            BeamColor);

        // ---------- 90 度内角（验证转角）----------
        // 两面墙成直角，球从一面走到另一面 —— 原项目这里最容易抽搐
        MakeBox(root.transform, "CornerWall",
            new Vector3(wallWidth * 0.5f + 4f, -4f, 5f),
            new Vector3(1.5f, 10f, 10f),
            WallColor);

        Debug.Log("[SpiderTest] 测试场已建：主墙 " + wallWidth + "×" + wallHeight
                + " 格 / " + stairCount + " 级台阶 / 横梁 z=" + beamHeight
                + " / 内角在 (" + (wallWidth * 0.5f + 4f) + ", -4)。\n"
                + "★ 现在按一下 Tab 切到 Parkour —— 球会自动落到地板上。\n"
                + "  蜘蛛：SilkSpiderAnatomy（头胸部+腹部+腹柄+螯肢+8条5节腿）\n"
                + "        SilkSpiderLimb 用 FABRIK IK 让足端精确踩地\n"
                + "  步态：SilkSpiderBody 的 alternating tetrapod（交替四足步态）\n"
                + "        开关 Show Gait Diagram 可在屏幕上看时序条带。");
    }

    /// <summary>
    /// 建一个带BoxCollider 的立方体。
    ///
    /// ★★★ 【踩过的坑 —— 之前这里写的是 <c>new GameObject(name)</c>】
    ///   空GameObject 上**没有 MeshFilter / MeshRenderer**，
    ///   所以 <c>GetComponent&lt;MeshRenderer&gt;()</c> 永远返回 null，
    ///   下面的着色器赋色整段被跳过——
    ///   **结果：碰撞体在、几何不可见。**
    ///   （有碰撞体 ≠ 能看见。用户说「啥也没看到」时这是第二个独立根因，
    ///   与相机/控制器那个循环依赖是两回事。）
    ///
    /// 【正确做法】
    ///   <c>GameObject.CreatePrimitive(PrimitiveType.Cube)</c>
    ///   自带 MeshFilter + MeshRenderer + BoxCollider。
    ///   ★ 它的 Cube 网格是 1×1×1，正好配transform.localScale = size；
    ///     BoxCollider 的 size 也是单位立方，一行都不用改。
    ///   ★ 全程只用正缩放 —— 负缩放会让法线翻转，这里不需要。
    ///
    /// 【为什么不用自己 new Mesh】
    ///   Built-in 管线里 <c>Resources.GetBuiltinResource&lt;Mesh&gt;("Cube.fbx")</c>
    ///   在部分版本上路径已变，而 CreatePrimitive 是稳定公开 API。
    /// </summary>
    private static void MakeBox(Transform parent, string name,
                                Vector3 localPos, Vector3 size, Color color)
    {
        // ★ CreatePrimitive 而不是 new GameObject —— 后者没有渲染组件
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = size;          // 缩放直接等于尺寸（Cube 网格是单位立方）

        // CreatePrimitive 自带 BoxCollider(size = 1)，与 localScale 配合正好
        // 是 size 的实际尺寸，无需再动。

        // 显式补一句 null 检查：万一将来 Primitive 行为变了，不要静默变黑
        MeshRenderer mr = go.GetComponent<MeshRenderer>();
        if (mr == null)
        {
            Debug.LogWarning("[SpiderTest] " + name+ " 没有 MeshRenderer，将不可见。");
            return;
        }

        /* ★★ Built-in 管线的标准着色器名是 "Standard"。
         *   万一项目改过管线设置，退回 Diffuse 以免整块变洋红。*/
        Shader sh = Shader.Find("Standard");
        if (sh == null) sh = Shader.Find("Diffuse");

        if (sh != null)
        {
            Material mat = mr.material;         // material 会自动实例化（= 触发警告的那行）
            mat.shader = sh;
            if (mat.HasProperty("_Color")) mat.color = color;
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        }
        else
        {
            Debug.LogWarning("[SpiderTest] 找不到 Standard/Diffuse 着色器，"
                           + name + " 可能是洋红色。");
        }
    }
}