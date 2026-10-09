using UnityEngine;

/// <summary>蜘蛛贴面移动 —— 假重力 + 双球形射线。
///
/// ★★★ 本文件是独立文件，**故意不并入 SilkBuilder.cs**。
///   SilkBuilder.cs 已有 7124 行 / 16 个类，是CS0103 的头号灾区
///   （历史上栽了 8 次，见 MEMORY 第六节）。新特性单独成文件可避开。
///
/// 【核心机制：为什么不需要「攀爬状态机」】
/// 人形攀爬需要 Idle / ClimbUp / Shimmy / Mantle / Corner... 七个状态，
/// 因为「人」只能沿着**边缘**走，必须先找到能挂住的边。
/// 但蜘蛛能走**整面墙** —— 所以不需要找边缘，也就不需要状态。
///
/// ★ 关键洞察（整个方案的支点）：
///   **只要把 transform.up 对齐到命中面的法线，「垂直」就被重新定义了。**
///   地面、墙面、天花板、90 度转角，全部退化成同一个坐标系下的「地板」。
///   → 同一套代码，零特判。
///   这与 MEMORY 第十节记的 GDC 2019「放弃垂直天空蛛丝」是同一思路：
///   **不要为特殊情况写分支，要让坐标系吸收特殊情况。**
///
/// 【移植自开源项目】Unity-Procedural-IK-Wall-Walking-Spider (PhilS94)
///   原项目 Spider.cs 的贴面逻辑，改写为 Z-up + 位置直写（无 Rigidbody）。
///   ——我们项目的球是位置控制挂点，没有 Rigidbody（见 SilkBuilder 第4284 行注释），
///   所以「AddForce 假重力」必须改成「直接沿法线修正位置」。
///
/// 【坐标系】X = 左右 / Y = 前后 / **Z = 高度（重力沿 −Z）**
///   原项目是 Unity 默认 Y-up，代码里到处写 Vector3.up / -transform.up。
///   本文件一律用 **Vector3.forward（+Z）** 作为「上」，
///   并且**每个局部坐标系都由 surfaceNormal 定义**，不写死任何世界轴。
/// </summary>
[RequireComponent(typeof(SilkParkourController))]
[DisallowMultipleComponent]
public class SilkSpiderSurfaceMove : MonoBehaviour
{
    /// <summary>
    /// ★ 自动挂载入口 —— **不改动 SilkBuilder.cs 的 7124 行**。
    ///
    /// 【为什么单独写静态方法而不是改控制器】
    ///   MEMORY 第六节：SilkBuilder.cs 有 16 个类，历史上栽过 8 次 CS0103。
    ///   新特性一律单独成文件。这里用 [RuntimeInitializeOnLoadMethod]
    ///   在场景加载后自动补挂组件，主文件一行都不用碰。
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoAttach()
    {
        /*★★★ 这里踩了**两次**坑，两个都独立：
         *
         * 【坑1 · 循环依赖】
         *   原来的写法是「遍历所有 SilkParkourController，给它挂组件」。
         *   ★ 但本项目里控制器是**运行时**由 SilkSpiderTestStage 创建的，
         *     而 SilkSpiderTestStage 又是本方法挂上去的：
         *       场景里没有控制器 → 遍历循环体一次都不执行
         *       → SilkSpiderTestStage 永远挂不上 → 相机/控制器/球全都没建
         *
         * 【坑 2 ★★ 「宿主可能根本不存在」—— 更致命】
         *   修完坑 1 后我改成「遍历 SilkBuilder[]，在它身上挂测试场」。
         *   ★★ 但 `ceshi.unity`（用户实际在跑的场景）**里根本没有 SilkBuilder**：
         *      它只有 Cube / Sphere / Main Camera / Directional Light，
         *      连一个 MonoBehaviour 都没有。
         *   → FindObjectsOfType<SilkBuilder>() 返回空数组
         *   → 循环体又一次不执行 → 测试场还是建不出来
         *
         * 【正确做法 · 不能依赖任何特定组件存在】
         *   拿 SilkBuilder 当宿主是**假设**，不是保证。
         *   → 优先级：① 已有的 SilkBuilder → ② 场景里任意 GameObject
         *             → ③ 自己 new 一个专用宿主
         *   第③ 步是唯一能覆盖「空场景」的做法，也是最终兜底。
         */
        EnsureTestStage();
        EnsureForAllControllers();
        EnsureForAllBuilders();
    }

    /// <summary>
    /// ★ 保证场上有一个挂了 <see cref="SilkSpiderTestStage"/> 的物体。
    ///
    /// 【三种宿主，按优先级】
    ///   ① SilkBuilder —— 用户自己的入口，能复用它的 builder 引用
    ///   ② 场景里任意 GameObject —— 场景没 SilkBuilder 但有别的东西
    ///   ③ **自己 new 一个** —— ★ 场景是空的（`ceshi.unity` 就是这种）
    ///
    /// 【为什么必须有第 ③ 步】
    ///   `ceshi.unity` 里连一个 MonoBehaviour 都没有，
    ///   只有 Cube / Sphere / Main Camera / Directional Light。
    ///   前两种策略全落空 → 测试场永远建不出来 → 屏幕上一个像素都没有。
    /// </summary>
    private static void EnsureTestStage()
    {
        // ① 已有的 SilkSpiderTestStage 直接复用
        SilkSpiderTestStage exist = FindObjectOfType<SilkSpiderTestStage>();
        if (exist != null) return;

        // ② 优先挂在 SilkBuilder 上（能复用它的 builder 引用）
        SilkBuilder[] builders = FindObjectsOfType<SilkBuilder>();
        if (builders.Length > 0 && builders[0] != null)
        {
            builders[0].gameObject.AddComponent<SilkSpiderTestStage>();
            Debug.Log("[SpiderMove] 已挂上 SilkSpiderTestStage（测试场 + 相机 + 控制器）。");
            return;
        }

        // ③ ★ 场景里没有 SilkBuilder —— 自己建一个专用宿主
        var host = new GameObject("SpiderTestHost");
        Object.DontDestroyOnLoad(host);

        /*★ DontSave（不是 HideAndDontSave）——
         *   这样它**不会被存进场景文件**。
         *   宿主是运行时产物，若被保存，用户下次打开场景会看到一个
         *   带 SilkBuilder 的神秘物体，还可能触发 Awake 的实例唯一性守卫。
         *   理由同SpiderAutoAttach：DontSave 只影响保存，不影响运行时生命周期。*/
        host.hideFlags = HideFlags.DontSave;

        /*★★★ 宿主上必须补一个 SilkBuilder，否则一按 Tab 就崩。
         *
         * 【踩过的坑 —— 「啥也没看到」的第 7 个根因，也是最容易崩的】
         *   控制器的 `builder` 字段是这么来的（SilkBuilder.cs:4465）：
         *       builder = FindObjectOfType<SilkBuilder>();
         *
         *   场景里没有 SilkBuilder → `builder` 全程是 **null**。
         *   而下面这些地方**完全不做 null 检查**：
         *       4508/4509  if (builder.freeBuildSpawnOverride != ...)
         *       4522       new Vector3(500, 500, builder.freeBuildSearchMaxZ * .5f)
         *       4538       if (top <= builder.freeBuildSearchMaxZ)
         *       4561       => builder.freeBuildMode ? ... : ...      ← EffectiveStartPosition
         *       4566       => builder.freeBuildMode ? ... : ...      ← EffectiveFallRespawnZ
         *   ★ 其中 4561/4566 是 **EffectiveStartPosition / EffectiveFallRespawnZ**，
         *     而切Tab 时控制器正是通过它们决定球往哪放。
         *   → 一按 Tab 立刻 NullReferenceException，什么都看不到。
         *
         *   ★ 为什么补一个 SilkBuilder 是安全的：
         *     · Awake 只有「实例唯一性守卫」（发现重复就自杀后来者）——
         *       这里场上本来就没有别的，守卫不会触发；
         *     · Start 只设两个 LayerMask，无副作用；
         *     · 它是 MonoBehaviour，不 new 不代表要手动构造。
         */
        host.AddComponent<SilkBuilder>();
        host.AddComponent<SilkSpiderTestStage>();

        Debug.LogWarning("[SpiderMove] 场景里没有 SilkBuilder，"
                       + "已自建宿主 SpiderTestHost（内含 SilkBuilder + SilkSpiderTestStage）。\n"
                       + "★ 这说明当前场景是空的（只有默认 Cube/Sphere/Camera/Light）。\n"
                       + "  测试场、相机、控制器、球、8 条腿都已自动创建。\n"
                       + "  别忘了按 Tab 切到 Parkour —— 球只在 Parkour 模式才建。");
    }

    /// <summary>给所有控制器挂上贴面移动与假骨骼。</summary>
    private static void EnsureForAllControllers()
    {
        SilkParkourController[] all = FindObjectsOfType<SilkParkourController>();
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] == null) continue;

            if (all[i].GetComponent<SilkSpiderSurfaceMove>() == null)
                all[i].gameObject.AddComponent<SilkSpiderSurfaceMove>();

            /* ★ 假骨骼依赖本组件（它提供 SurfaceNormal / runSpeed），
             *   所以必须**在挂完本组件之后**才挂它。
             *   顺序反了的话 SilkSpiderBody.Start 里 GetComponent 会拿到 null，
             *   而 [RequireComponent] 只保证「同一物体上必须有」，
             *   **不保证 Awake/Start 的执行顺序** —— 这是 Unity 的已知坑。
             *   → 挂载顺序必须显式控制，不能靠 RequireComponent。
             *
             * ★ 这里**不能 continue**：已经挂过 SurfaceMove 的物体
             *   （例如用户手动加的、或上次运行残留的）仍然需要挂 Body。*/
            SilkSpiderBody.AttachTo(all[i]);
        }
    }

    /// <summary>
    /// Tab 切模式时会**新建** SilkPlayer（因为 FreeFly 下不建球），
    /// 那时才需要补挂。用 Update 盯着，代价极低。
    /// </summary>
    private static bool controllerWatchStarted;

    private static void EnsureForAllBuilders()
    {
        if (controllerWatchStarted) return;
        controllerWatchStarted = true;

        var watcher = new GameObject("SpiderAutoAttach");
        Object.DontDestroyOnLoad(watcher);
        /*★★★ hideFlags 必须是 DontSave，**不能是 HideAndDontSave**。
         *
         * 【区别（Unity 语义）】
         *   HideFlags.HideAndDontSave = HideInHierarchy | HideInInspector
         *       | DontSaveInEditor | DontSaveInBuild | DontUnloadUnusedAsset
         *   ★ 带 DontSaveInEditor / DontSaveInBuild 的物体**不参与构建**，
         *     Unity 不把它当普通对象管理；与 DontDestroyOnLoad 混用时行为不可靠
         *     —— 常见症状是切场景后 watcher 静默消失，补挂逻辑不再执行。
         *
         *   HideFlags.DontSave = DontSaveInEditor | DontSaveInBuild
         *     只影响「保存」，**不影响运行时生命周期** —— 配合 DontDestroyOnLoad 才正确。
         *
         * 【为什么这个细节重要】
         *   watcher 是「按 Tab 之后还能补挂腿」的唯一保障。
         *   它一失效，玩家就再也看不到腿，而且**没有任何报错**。
         */
        watcher.hideFlags = HideFlags.DontSave;

        /* ★ 不要写 comp.Awake()。
         *   AddComponent 在运行时**已经**会自动调一次 Awake，
         *   再手动调既多余，又会撞 **CS0122**——
         *   SpiderAutoAttachWatcher 是本类的**嵌套类**，
         *   它的 private Awake() 外层类无权访问。
         *   （MEMORY 第六节：跨类私有成员是本项目栽过 8 次的坑）*/
        watcher.AddComponent<SpiderAutoAttachWatcher>();
    }

    /// <summary>
    /// 只做一件事：确保每个控制器身上都挂了贴面移动 + 假骨骼。
    ///
    /// ★ 为什么需要它：`SilkSpiderTestStage.Start()` 里
    ///   `EnsurePlayable()` 会在**第一次按 Tab**时把 `SilkPlayer` 建出来。
    ///   那个控制器是**新建的**，不经过 AutoAttach 的首轮遍历 → 会漏挂。
    /// </summary>
    public class SpiderAutoAttachWatcher : MonoBehaviour
    {
        private float nextCheck;

        private void Update()
        {
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 0.5f;   // 每 0.5 秒查一次，足够便宜

            SilkParkourController[] all = FindObjectsOfType<SilkParkourController>();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null) continue;

                if (all[i].GetComponent<SilkSpiderSurfaceMove>() == null)
                    all[i].gameObject.AddComponent<SilkSpiderSurfaceMove>();

                if (all[i].GetComponent<SilkSpiderBody>() == null)
                    SilkSpiderBody.AttachTo(all[i]);
            }
        }
    }
    [Header("接地探测")]
    [Tooltip("脚下的球形射线长度（格）。它是「悬浮高度」，越大越容易贴住远处的面")]
    public float downRayLength = 2.5f;

    [Tooltip("脚下的球形射线半径（格）。比 colliderRadius 略小，避免每帧都判定为正好接触")]
    public float downRayRadius = 1f;

    [Tooltip("向前的球形射线长度（格）。撞墙时用它把球顶到墙面上")]
    public float forwardRayLength = 2f;

    [Tooltip("向前的球形射线半径（格）")]
    public float forwardRayRadius = 1f;

    [Tooltip("射线的碰撞层。默认 Everything —— 白盒场景没设自定义层")]
    public LayerMask surfaceMask = ~0;

    [Header("贴面")]
    [Tooltip("假重力加速度（格/秒²）。沿命中面法线把球压在面上。\n"
        + "★ 与 SilkPhysics.Gravity 的关系：那个是**世界向下**的重力（用于摆荡），\n"
        + "  这个是**贴面**用的，需要独立调 —— 太大会让球在墙上卡死动不了。")]
    public float gravityMultiplier = 60f;

    [Tooltip("地面朝向的插值速度。落地要稳，所以比墙面慢")]
    public float groundNormalAdjustSpeed = 4f;

    [Tooltip("墙面朝向的插值速度。撞墙要跟手，所以比地面快")]
    public float forwardNormalAdjustSpeed = 8f;

    [Tooltip("离面距离小于这个比例的 colliderRadius 时停用假重力。\n"
        + "贴太紧时若仍施力，球会被按在面上抖个不停")]
    [Range(0f, 1f)] public float gravityOffDistance = 0.3f;

    [Header("移动")]
    [Tooltip("贴面行走速度（格/秒）")]
    public float walkSpeed = 12f;

    [Tooltip("贴面奔跑速度（格/秒）")]
    public float runSpeed = 22f;

    [Tooltip("转向速度（度/秒）")]
    public float turnSpeed = 540f;

    [Tooltip("单帧位移上限 = 本值 × downRayRadius。\n"
        + "★ 这是防「跑得太快导致射线打空、丢失接地」的关键闸门，与原项目一致")]
    [Range(0.1f, 0.99f)] public float maxStepRatio = 0.9f;

    [Tooltip("速度插值（1 = 无阻尼）。越小越滑，越大越跟手")]
    [Range(0f, 1f)] public float velocityDamping = 0.15f;

    /// <summary>当前命中的表面法线（已归一化）。这就是「蜘蛛所认为的向上」。</summary>
    public Vector3 SurfaceNormal { get; private set; } = Vector3.forward;

    /// <summary>是否正贴在某个面上。</summary>
    public bool IsGrounded { get; private set; }

    private SilkParkourController ctrl;
    private SilkSphereCast downRay;
    private SilkSphereCast forwardRay;
    private RaycastHit hitInfo;

    /// <summary>★ 自身碰撞体 —— 射线必须排除它，否则永远打到自己。
    /// 见 <see cref="SilkSphereCast.Cast"/> 的说明。</summary>
    private Collider selfCollider;

    /// <summary>当前速度（格/秒）。</summary>
    public Vector3 CurrentVelocity { get; private set; }

    private void Awake()
    {
        ctrl = GetComponent<SilkParkourController>();
        selfCollider = FindSelfCollider();
    }

    /// <summary>
    /// 找出自身碰撞体。
    ///
    /// ★ 注意：碰撞体**不在控制器所在的 GameObject 上**。
    ///   看 SilkParkourController.CreateVisual() ——
    ///   球是运行时新建的 `ParkourBody/Body` 子物体，
    ///   其 SphereCollider 被设成 isTrigger（供地面探测用），
    ///   挂在子物体上而非本物体。
    ///   → 所以必须向下找子物体，`GetComponent<Collider>()` 会返回 null。
    /// </summary>
    private Collider FindSelfCollider()
    {
        Collider c = GetComponent<Collider>();
        if (c != null) return c;

        // 含Inactive，覆盖「先建控制器、后建球」的顺序
        Collider[] kids = GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < kids.Length; i++)
        {
            if (kids[i] != null) return kids[i];
        }
        return null;
    }

    private void Start()
    {
        // ★ 半径全部由 visualRadius 推导，不写字面量 ——球径改了这里自动跟随
        float r = ctrl.visualRadius;

        downRayRadius = downRayRadius <= 0f ? r * 0.8f : downRayRadius;
        downRayLength = downRayLength <= 0f ? r * 2f : downRayLength;
        forwardRayRadius = forwardRayRadius <= 0f ? r * 0.8f : forwardRayRadius;
        forwardRayLength = forwardRayLength <= 0f ? r * 1.6f : forwardRayLength;

        downRay = new SilkSphereCast(
            transform.position,
            transform.position - SurfaceNormal * downRayLength,
            downRayRadius, transform);

        forwardRay = new SilkSphereCast(
            transform.position,
            transform.position + transform.forward * forwardRayLength,
            forwardRayRadius, transform);

        /* ★ 两条射线都必须排除球自己 —— 射线起点就在球心，必然会打到自己。
         *
         * 【执行顺序陷阱 ★ 本移植踩到的一处】
         *   控制器 SilkParkourController.Start() 里是
         *       transform.position = startPosition;
         *       CreateVisual();      <-- 球在这里才被建出来
         *   而本组件是 AddComponent 上去的，**它的 Start 可能先跑**，
         *   此时 GetComponentsInChildren 找不到任何 Collider。
         *   → 所以必须延迟一帧重查，否则射线会打到自己 → 法线错 → 原地抖动。
         */
        StartCoroutine(AttachSelfColliderNextFrame());
    }

    private System.Collections.IEnumerator AttachSelfColliderNextFrame()
    {
        yield return null;   // 等控制器的 Start 跑完、球建出来

        if (selfCollider == null)
        {
            selfCollider = FindSelfCollider();
            if (selfCollider == null)
                Debug.LogWarning("[SpiderMove] 找不到自身 Collider，"
                    + "射线会打到自己导致贴面抖动。"
                    + "检查球是否挂了 SphereCollider。");
        }

        if (downRay != null) downRay.SetIgnore(selfCollider);
        if (forwardRay != null) forwardRay.SetIgnore(selfCollider);
    }

    /// <summary>
    /// 表面探测 —— **先探前、再探下**，顺序不可颠倒。
    ///
    /// ★ 为什么前优先（移植时最容易搞错的一点）：
    ///   球贴在一面竖直墙旁时，「下」方向已经变成垂直于墙面了。
    ///   若先探下方，会打中墙面 → 法线是水平的 → 球被横着贴在墙上，
    ///   但玩家想的是「站在墙上继续往前」。所以必须先问「前面有没有面」。
    /// </summary>
    private void ProbeSurface()
    {
        RaycastHit forwardHit, downHit;
        bool hasForward = forwardRay.Cast(out forwardHit, surfaceMask);
        bool hasDown = downRay.Cast(out downHit, surfaceMask);

        if (hasForward) hitInfo = forwardHit;
        else if (hasDown) hitInfo = downHit;

        if (hasForward || hasDown)
        {
            SurfaceNormal = hitInfo.normal.normalized;
            IsGrounded = true;
        }
        else
        {
            // 什么都没打到 → 悬空，此时沿「上一帧法线」继续拉
            IsGrounded = false;
        }
    }

    private void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        if (dt <= 0f) return;

        // ★ 探测必须在用旧法线之前完成 —— 因为「下」的方向依赖当前法线
        ProbeSurface();
        AlignToSurface(dt);
        ApplyFakeGravity();
    }

    /// <summary>
    /// 朝表面法线插值旋转。
    ///
    /// ★ 为什么必须 Slerp 而不是直接赋值：
    ///   直接赋值会在转角处瞬间翻 90 度，玩家看到的是「抽搐」。
    ///   原项目用的公式是 0.02 × adjustSpeed（每FixedUpdate），
    ///   这里换算成「每帧比例」，并把地面/墙面速度分开 —— 落地要稳，撞墙要跟手。
    /// </summary>
    private void AlignToSurface(float dt)
    {
        if (!IsGrounded) return;

        float adjust = Vector3.Dot(SurfaceNormal, Vector3.forward) > 0.7f
            ? groundNormalAdjustSpeed      // 像地面 → 慢慢转
            : forwardNormalAdjustSpeed;      // 像墙面 → 快速跟手

        Vector3 slerpNormal = Vector3.Slerp(
            transform.up, SurfaceNormal, Mathf.Clamp01(adjust * dt));

        Vector3 right = Vector3.ProjectOnPlane(transform.right, slerpNormal);
        if (right.sqrMagnitude > 0.0001f && slerpNormal.sqrMagnitude > 0.0001f)
        {
            Vector3 forward = Vector3.Cross(right, slerpNormal);
            transform.rotation = Quaternion.LookRotation(forward, slerpNormal);
        }
    }

    /// <summary>
    /// 假重力 —— 把球沿法线压向表面。
    ///
    /// 【与原项目的差异 ★ 移植要点】
    ///   原项目用 `rb.AddForce(-normal * g)`，因为它有 Rigidbody。
    ///   ★ 我们的球**没有 Rigidbody**（位置直写，transform 不触发物理解算），
    ///   所以必须改成「直接沿法线修正位置」。
    ///
    /// 【为什么这仍然成立】
    ///   假重力的物理意义是「持续贴住表面」，
    ///   用「每帧施加一个位置修正」完全可以替代「每帧施加一个力」——
    ///   区别只在于前者不产生动量累积（不会越贴越快）。
    ///   对蜘蛛贴面来说，**不要动量累积反而是优点**（贴墙不会弹开）。
    /// </summary>
    private void ApplyFakeGravity()
    {
        float dt = Time.fixedDeltaTime;

        float distToSurface = Vector3.Distance(transform.position, hitInfo.point);
        // ★ 减去射线半径 —— hitInfo.point 是**球面**接触点不是球心，
        //   球心到「面」的真正距离 = 球心到接触点的距离 − 射线半径。
        //   少减这一步，球会被当成「还差 1 格才贴上」而一直被往下按，
        //   结果贴在地上反而压进地里。
        float gap = distToSurface - downRayRadius;
        float offDistance = gravityOffDistance * ctrl.visualRadius;

        // 贴太紧就停用 —— 否则每帧都被往下按，抖动
        if (gap <= offDistance) return;

        // ★ 修正量 = g · dt²。
        //   加速度积分两次（∫∫a dt²）得到位移，所以这里必须是 dt² 而不是 dt。
        //   写成 dt 会让贴面速度与 gravityMultiplier 成线性关系，
        //   数值含义完全变了（这是移植时最容易搞错的一处）。
        Vector3 pos = transform.position
                    - SurfaceNormal * (gravityMultiplier * dt * dt);
        transform.position = pos;
    }

    /// <summary>
    /// 贴面前进 —— 由控制器调用。
    ///
    /// 【单帧位移闸门的来历 ★ 移植要点】
    /// 原项目有这段：
    ///   distance = Mathf.Clamp(distance, 0, 0.99f * downRayRadius);
    ///   注释原文："Makes sure the ground is not lost due to moving too fast"
    ///   —— 跑太快时，下一帧的位置已经越过射线长度，射线打空 → 误判为悬空 → 掉下来。
    ///   这是最容易被当成「偶发 bug」的一类问题，必须保留这个闸门。
    /// </summary>
    public void walk(Vector3 direction) => Move(direction, walkSpeed);
    public void run(Vector3 direction) => Move(direction, runSpeed);

    private void Move(Vector3 direction, float speed)
    {
        if (direction.sqrMagnitude < 0.000001f) return;
        if (ctrl.visualRadius <= 0f) return;

        Vector3 target = direction.sqrMagnitude > 1f ? direction.normalized : direction;

        // ★ 闸门：单帧位移不得超过射线长度的 maxStepRatio 倍
        float maxDist = maxStepRatio * downRayRadius;
        float distance = speed * Time.fixedDeltaTime;
        distance = Mathf.Min(distance, maxDist);
        if (distance <= 0f) return;

        target = target.normalized * distance;
        CurrentVelocity = Vector3.Lerp(CurrentVelocity, target, velocityDamping);

        transform.position += CurrentVelocity;
    }

    /// <summary>转向。目标方向会被投影到当前表面上（不能朝墙里转）。</summary>
    public void turn(Vector3 goalForward)
    {
        Vector3 onSurface = Vector3.ProjectOnPlane(goalForward, SurfaceNormal);
        if (onSurface.sqrMagnitude < 0.0001f) return;

        Quaternion target = Quaternion.LookRotation(onSurface.normalized, SurfaceNormal);
        transform.rotation = Quaternion.RotateTowards(
            transform.rotation, target, turnSpeed * Time.fixedDeltaTime);
    }

    // ================================================================
    //  输入驱动 —— 让这个组件能被直接试玩
    // ================================================================

    [Header("输入驱动")]
    [Tooltip("★ **默认勾上** —— 不勾按W/A/S/D 没有任何反应，等于没法验证。\n"
        + "打开后由本组件接管移动（贴面走），关闭则完全走原有控制器逻辑。")]
    public bool enableInput = true;

    [Tooltip("开启输入驱动后，是否用 Shift 冲刺")]
    public bool runWithShift = true;

    [Tooltip("开启输入驱动后，相机（用于取「前」方向）。没找到时退化为球自身朝向")]
    public Camera inputCamera;

    [Tooltip("开启输入驱动后，左 Shift / 右 Shift 都算冲刺（用户键位是左 Shift）")]
    public bool acceptRightShiftToo = true;

    /// <summary>
    /// 读输入并驱动贴面移动。
    ///
    /// 【★ 零新增按键 —— 沿用用户的 8键硬规则】
    ///   W / S / A / D  前后左右移动（方向投影到当前表面）
    ///   Q / E          在表面上「上爬 / 下爬」——
    ///                    ★ 这里正好对应「爬墙」：球贴着竖直墙时，
    ///                      世界 +Z 已被旋转到「垂直于墙面」，
    ///                      按 E 就是沿墙面往上爬，**不需要任何爬墙代码**。
    ///   左Shift        冲刺
    ///   Tab            切模式（由控制器处理，这里不读）
    ///
    /// ★ 不新增任何按键。
    /// </summary>
    private void Update()
    {
        if (!enableInput) return;

        // 取「前」与「右」：优先用相机的水平朝向，
        // 否则用球自身朝向 —— 这样脱离相机也能独立测试。
        Vector3 fwd, right;
        if (inputCamera != null)
        {
            Vector3 cf = inputCamera.transform.forward;
            Vector3 cr = inputCamera.transform.right;
            fwd = Vector3.ProjectOnPlane(cf, SurfaceNormal);
            right = Vector3.ProjectOnPlane(cr, SurfaceNormal);
        }
        else
        {
            fwd = Vector3.ProjectOnPlane(transform.forward, SurfaceNormal);
            right = Vector3.ProjectOnPlane(transform.right, SurfaceNormal);
        }

        if (fwd.sqrMagnitude < 0.0001f) fwd = Vector3.forward;
        if (right.sqrMagnitude < 0.0001f) right = Vector3.right;

        Vector3 wish = Vector3.zero;
        if (Input.GetKey(KeyCode.W)) wish += fwd;
        if (Input.GetKey(KeyCode.S)) wish -= fwd;
        if (Input.GetKey(KeyCode.D)) wish += right;
        if (Input.GetKey(KeyCode.A)) wish -= right;

        // ★ 沿表面法线的「上 / 下」—— 贴墙时这就是沿墙爬
        if (Input.GetKey(KeyCode.E)) wish += SurfaceNormal;
        if (Input.GetKey(KeyCode.Q)) wish -= SurfaceNormal;

        bool shift = runWithShift
            && (Input.GetKey(KeyCode.LeftShift)
                || (acceptRightShiftToo && Input.GetKey(KeyCode.RightShift)));

        if (wish.sqrMagnitude < 0.0001f)
        {
            CurrentVelocity = Vector3.Lerp(CurrentVelocity, Vector3.zero, 0.2f);
            return;
        }

        if (shift) run(wish); else walk(wish);
        turn(wish);
    }

    [Header("调试")]
    [Tooltip("画出两条射线与法线，便于肉眼确认贴面是否正确。\n"
        + "★ 默认勾上 —— 不开调试线看不到「球到底贴没贴上」，无法定位问题。")]
    public bool showDebug = true;

    private void LateUpdate()
    {
        if (!showDebug || downRay == null) return;
        downRay.Draw(IsGrounded ? Color.green : Color.red);
        forwardRay.Draw(Color.cyan);
        Debug.DrawLine(transform.position,
                       transform.position + SurfaceNormal * ctrl.visualRadius * 3f,
                       Color.yellow);
    }
}