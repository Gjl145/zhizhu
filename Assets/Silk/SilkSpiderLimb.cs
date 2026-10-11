using UnityEngine;

/// <summary>
/// 单条多节蜘蛛腿 —— 真骨骼层级 + FABRIK IK 求解。
///
/// 【★ 为什么推翻重写（2026-10-09）】
///   用户原话：「现在的蜘蛛像是小球加腿，我要的是蜘蛛。」
///   第一版重构我试图用 5 个 Cylinder 拼折线，**那是错的**，两个原因：
///   1. Cylinder 的轴只能直上直下，转折处必然出现断口/穿插。
///      腿要"弯"，就必须有**能转的关节**，而不是几根棍子。
///   2. 逐段 `PlaceSegment` 自己算世界旋转，在父子层级下会与父级旋转叠加，
///      结果是段与段错位。★ 这是我第二次犯「父子旋转叠加」的错
///      （第一次是 8 关节 FABRIK 的 for 循环顺序），记进记忆了。
///
/// 【★★ 正确做法：真骨骼 + FABRIK】
///   · 建**父子链**：root → seg0 → seg1 → seg2 → seg3 → seg4（每段是上一段的子节点）
///     这样每段的 localRotation 天然是相对父骨的关节角，不会有叠加问题。
///   · 用**锥形 mesh**（Cylinder 网格但顶点按半径改过）表现关节，
///     关节处用球体盖住缝隙。
///   · 用 **FABRIK** 迭代求解：给定根与目标足端，反向/正向交替拉伸骨骼链。
///     FABRIK 对多节链天然成立，比手写两段 IK 更通用。
///
/// 【★ 骨骼命名对齐 Locomotor 教程的真实 FBX】
///   视频（用户给定）帧 f160 的 Rig 树显示：
///     bones → body → back → popa
///                → leg_L_1_1 → leg_L_1_2 → leg_L_1_3 → leg_L_1_4 → leg_L_1_5
///     即**每条腿 5 节，命名 leg_<侧>_<序号>_<节号>**。
///   本文件严格对齐这个结构，Unity 侧物体名与之一致。
///
/// 【★ 真实蜘蛛形态依据】
///   · 步足 4 对（8 条），**全部从头胸部生出**，关节多、明显外折。
///   · 大腿（腿节）向外上方张开，膝（膝节）折角明显，小腿（胫节）向下回折。
///     这是"∧"形轮廓 —— 少了这个折角就不像蜘蛛。
///   · 越往足端越细；末端有爪。
///   · 4 对腿的根部**集中在头胸部**，前后跨度不大，
///     外张主要靠"根部到足端的横向距离"，而不是"根部铺开"。
///
/// 【坐标系】Z-up。「上」由外部传入（表面法线），
///   所以同一套腿在地面 / 墙面 / 天花板 / 转角都成立，不需要特判。
/// </summary>
public class SilkSpiderLimb
{
    /// <summary>一段骨骼。</summary>
    private class Bone
    {
        public Transform Node;      // 空物体，负责 localRotation（关节角）
        public Transform Mesh;      // 锥形 mesh 网格（子节点）
        public Transform Joint;     // 关节球（子节点，盖住折角断口）
        public float Length;
        public Vector3 LocalOffset; // 相对父骨的静止偏移（球关节用）
    }

    private Bone[] bones;
    private Transform legRoot;      // 整条腿的根（挂在身体上）
    private float rootThickness;
    private float tipRatio;
    private float spreadDeg;
    private Color limbColor;

    /// <summary>腿根世界坐标（外部给定）。</summary>
    public Vector3 RootWorld { get; private set; }
    /// <summary>足端世界坐标（外部给定）。</summary>
    public Vector3 FootWorld { get; private set; }
    /// <summary>★ 落点处表面法线（最近一次 Solve 传入）。零向量 = 未提供。</summary>
    public Vector3 GroundNormal { get; private set; }

    /// <summary>
    /// ★ 足端与表面法线的夹角（度）。
    /// 来源：spider_ik 的 footAngleToNormal = 20。
    /// 0 = 关闭足端朝向跟随（脚会像「插进去」）。
    /// </summary>
    public float footTiltDeg = 20f;

    /// <summary>骨节数（默认 5，对齐 Locomotor 的leg_X_N_M）。</summary>
    public int BoneCount { get { return bones != null ? bones.Length : 0; } }

    /// <summary>
    /// ★ True = 本腿驱动的是**外部骨骼**（FBX 蒙皮骨骼），不是自己建的锥形 mesh。
    /// SilkSpiderAnatomy 靠它决定要不要显示程序化 mesh。
    /// </summary>
    public bool UsesExternalBones { get; private set; }

    private static Shader cachedShader;
    private static Mesh cachedJointMesh;        // 单位球 mesh（关节处盖缝）
    private static System.Collections.Generic.Dictionary<int, Mesh> taperedCache;

    // ================================================================
    //  构建
    // ================================================================

    /// <summary>
    /// ★★ 接管外部骨骼链（Blender 导出的 FBX 蒙皮骨骼）—— 不建任何 mesh。
    ///
    /// 【为什么加这个入口】
    ///   FBX 里已经有完整的 42 根骨骼 + 蒙皮权重。
    ///   如果再让Unity 用 Cylinder/锥形 mesh 重建一遍腿，就变成两套腿：
    ///   程序化的那套（无蒙皮、纯 GameObject）和 FBX 那套（有蒙皮）。
    ///   → 正确做法：**视觉用 FBX，IK 用现有的 FABRIK**，
    ///     FABRIK 解出的世界位置直接写进 FBX 骨骼的 Transform。
    ///   这样IK 算法一行都不用改，只是骨骼的来源不同。
    ///
    /// 【★ 为什么不能靠 transform 而不碰 mesh】
    ///   FABRIK 只需要两样东西：每节的 Transform（写位置）+ 每节长度。
    ///   两者都能从 FBX 骨骼直接读出来（rest姿态下父子骨的距离就是长度）。
    ///   → 本方法只填 bones[]，Solve() 的算法部分完全复用。
    ///
    /// 【坐标系】传入的节点必须已经处于**世界 Z-up** 的最终朝向。
    ///   FBX 是 Y-up，导入后必须先转正 —— 由调用方负责，见
    ///   SilkSpiderFbxRig 的 fixupOrientation。
    /// </summary>
    /// <param name="chainNodes">
    /// 每节骨骼的 Transform，索引 0 = 腿根骨（leg_X_N_1），末位 = 末节骨。
    /// 顺序即父→子，长度必须等于骨节数。
    /// </param>
    public void AdoptExternalChain(Transform[] chainNodes)
    {
        if (chainNodes == null || chainNodes.Length < 2)
        {
            Debug.LogError("[SilkSpiderLimb] AdoptExternalChain 需要至少 2 根骨骼，"
                + "收到 " + (chainNodes == null ? "null" : chainNodes.Length.ToString())
                + " → 本腿退回程序化 mesh。");
            return;
        }

        // ★ 任何一根缺失都不接管 —— 半条腿有骨骼、半条没有，
        //   会渲染成「一条腿有肉一条腿是骨头」，比全程序化更难看。
        for (int i = 0; i < chainNodes.Length; i++)
        {
            if (chainNodes[i] == null)
            {
                Debug.LogError("[SilkSpiderLimb] AdoptExternalChain 第 " + i
                    + " 根骨骼为 null → 放弃接管本腿。");
                return;
            }
        }

        bones = new Bone[chainNodes.Length];
        for (int i = 0; i < chainNodes.Length; i++)
        {
            var b = new Bone();
            b.Node = chainNodes[i];
            // 长度从 rest 姿态的父子距离量。
            // ★ 必须在父级当前姿态下量，而不是局部坐标：
            //   Unity 导入后骨骼的 localPosition 已经是父子世界偏移的逆变换结果，
            //   直接取 .localPosition 的模长在有旋转时会偏。
            Transform child = (i + 1 < chainNodes.Length) ? chainNodes[i + 1] : null;
            b.Length = child != null
                ? Vector3.Distance(chainNodes[i].position, child.position)
                : LastBoneLengthFallback(chainNodes[i]);
            b.Mesh = null;
            b.Joint = null;
            bones[i] = b;
        }

        legRoot = chainNodes[0];
        UsesExternalBones = true;
    }

    /// <summary>
    /// 末节骨没有子骨可量长度时的兜底。
    /// ★ 不能返回 0 —— FABRIK 里长度为 0 会走 dir = up 兜底，
    ///   整条腿笔直戳出去，正是「8 根棍子」那个老 bug。
    /// </summary>
    private static float LastBoneLengthFallback(Transform tip)
    {
        Transform p = tip.parent;
        if (p == null) return 0.001f;
        float d = Vector3.Distance(tip.position, p.position);
        if (d < 0.0001f) d = Vector3.Distance(tip.position, tip.parent.position);
        return d > 0.0001f ? d : 0.001f;
    }



    // ================================================================
    //  FABRIK 求解
    // ================================================================

    /// <summary>
    /// ★ FABRIK 求解：把足端拉向 targetWorld。
    ///
    /// 【算法】
    ///   重复若干次：
    ///     ① 反向：把足端骨设到 target，然后往根方向倒推各骨
   ///② 正向：把根骨设回 rootWorld，再往足端方向推出各骨
    ///   ★ 正反交替 = "Backward And Forward Reaching Inverse Kinematics"。
    ///     对多节链天然适用，且**保证每节长度不变**（不像"分配比例"会拉伸）。
    ///
    /// 【★ 为什么不用逐段 aim】
    ///   逐段 PlaceSegment 需要每段自己算世界旋转，在父子链下极易与父级叠加。
    ///   FABRIK 只解算**世界位置**，最后一次性把位置写进 Transform.localPosition，
    ///   完全绕开旋转叠加 —— 这是我踩过两次的坑。
    /// </summary>
    /// <summary>
    /// 求解一条腿的骨骼链，使足端抵达 targetWorld。
    ///
    /// 【★ 2026-10-09 新增参数 groundNormal / footTiltDeg】
    ///   来源：spider_ik 的 IKSolver 里「最后一节专门对齐命中点法线」：
    ///     angle = footAngleToNormal + 90 − SignedAngle(ProjectOnPlane(normal, axis), toEnd, axis)
    ///   作者用 footAngleToNormal = 20°，
    ///   与 Locomotor 视频的 Orient Foot to Ground Pitch 0.8 吻合
    ///   ——足端不是垂直插进地面，而是**倾斜着踩上去**。
    ///   我原来完全没有足端朝向控制 → 脚是「插进去」的，不是「踩上去」的。
    /// </summary>
    /// <param name="rootWorld">腿根世界坐标</param>
    /// <param name="targetWorld">足端目标世界坐标</param>
    /// <param name="up">身体朝向（球体表面的外法线）</param>
    /// <param name="groundNormal">落点处表面法线。零向量 → 退回用 up</param>
    /// <param name="footTiltDeg">足端与表面法线的夹角（度）。20 = 略微倾斜</param>
    public void Solve(Vector3 rootWorld, Vector3 targetWorld, Vector3 up,
                Vector3 groundNormal, float footTiltDeg)
    {
        RootWorld = rootWorld;
        FootWorld = targetWorld;
        GroundNormal = groundNormal;

        if (bones == null || bones.Length == 0 || legRoot == null) return;

        int n = bones.Length;
        if (up.sqrMagnitude < 0.0001f) up = Vector3.forward;

        // ★ 腿总长。**必须在下面的退化保护之前声明** ——
        //   保护里要用它算弓形幅度。原版把它声明在「目标不可达」那段，
        //   排在保护之后 → CS0103「totalLen 不存在于当前上下文」。
        //   ★ 这已经是本项目第 9 次「用了才声明 / 声明在使用之后」。
        float totalLen = TotalLength();

        // 缓存当前各骨的世界位置
        Vector3[] pts = new Vector3[n + 1];      // 0 = 根，n = 足端
        for (int i = 0; i <= n; i++)
        {
            pts[i] = i == 0
                ? rootWorld
                : bones[i - 1].Node.position;
        }

        /*★★★ 首帧退化保护（这是「看不到腿」的一个真实根因）
         *
         *   Build() 里每节localPosition 都是 zero，父子链上
         *   → 第一节的世界位置 = 腿根，之后每节也都等于腿根
         *   → **全部 n+1 个点完全重合**。
         *
         *   此时 FABRIK 的 dir = pts[i+1] - pts[i] 长度 = 0
         *   → 走 dir = up 兜底 → 整条腿朝表面法线笔直伸出去，
         *     看起来就是「8 根棍子戳在地上」，而不是蜘蛛。
         *
         *   ★ 与「足端探测成功但腿炸开」是老问题的同一形态，必须防。
         *   解法：检测重合 → 用 root→foot 的方向沿线均匀铺开作为初始姿态。
         */
        bool degenerate = false;
        for (int i = 1; i <= n; i++)
        {
            if ((pts[i] - pts[0]).sqrMagnitude < 0.0000001f) { degenerate = true; break; }
        }

        if (degenerate)
        {
            Vector3 dir0 = targetWorld - rootWorld;
            if (dir0.sqrMagnitude < 0.000001f) dir0 = -up;
            dir0.Normalize();
            // ★ 沿「根→足」方向按各节长度铺开，并叠加一个向上的弯曲
            //   —— 直线铺开看起来像棍子，弯曲才像腿。
            Vector3 bend = up - dir0 * Vector3.Dot(up, dir0);
            if (bend.sqrMagnitude > 0.0001f) bend.Normalize();
            else bend = Vector3.Cross(dir0, Vector3.right).normalized;

            float acc = 0f;
            for (int i = 1; i <= n; i++)
            {
                // 弯度：中间鼓，两端收 —— 类似真实腿的弓形
                float t = n > 1 ? (float)i / n : 0f;
                float bow = Mathf.Sin(t * Mathf.PI) * totalLen * 0.12f;
                pts[i] = rootWorld + dir0 * acc + bend * bow;
                acc += bones[i - 1].Length;
            }
        }

        // 目标不可达（比腿长还远）→ 先把足端拉到最远可达处
        Vector3 target = targetWorld;
        Vector3 toRoot = rootWorld - target;
        if (toRoot.magnitude > totalLen)
            target = rootWorld + (target - rootWorld).normalized * totalLen;

        const int ITER = 8;
        const float TOL = 0.0005f;

        /* ★★ 最小变化量提前退出（来源：spider_ik 的 minimumChangePerIteration）
         *   作者原文用途：误差不再下降就放弃，避免在无解状态空转。
         *   我原来只有 TOL（绝对误差），缺这条：
         *   目标在奇异位置时误差会「缓慢下降但永远达不到 TOL」，
         *   白白跑满 6 次迭代，且解出的姿态是抖的。
         */
        const float MIN_PROGRESS = 0.000001f;

        float prevErr = float.MaxValue;

        for (int iter = 0; iter < ITER; iter++)
        {
            // ---- 反向：足端 → 根 ----
            pts[n] = target;
            for (int i = n - 1; i >= 0; i--)
            {
                Vector3 dir = pts[i] - pts[i + 1];
                float d = dir.magnitude;
                if (d < 0.00001f)
                    dir = up;
                else
                    dir /= d;

                pts[i] = pts[i + 1] + dir * bones[i].Length;
            }

            // ---- 正向：根 → 足端 ----
            pts[0] = rootWorld;
            for (int i = 0; i < n; i++)
            {
                Vector3 dir = pts[i + 1] - pts[i];
                float d = dir.magnitude;
                if (d < 0.00001f)
                    dir = up;
                else
                    dir /= d;

                pts[i + 1] = pts[i] + dir * bones[i].Length;
            }

            float err = Vector3.Distance(pts[n], target);
            if (err < TOL) break;

            // ★ 误差不再改善 → 提前放弃（spider_ik 做法）
            if (prevErr != float.MaxValue
                && Mathf.Abs(prevErr - err) < MIN_PROGRESS) break;
            prevErr = err;
        }

        // ★ 把解出的世界位置转成「父级局部位置」。
        //   FABRIK 解的是世界坐标；骨骼链是父子层级，
        //   所以每节的 localPosition = inverse(parentWorldRot) * (worldPos - parentWorldPos)。
        //   这样**完全不需要算关节旋转**，父子关系自动处理朝向。
        for (int i = 0; i < n; i++)
        {
            Transform node = bones[i].Node;
            Transform parent = node.parent;

            Vector3 parentWorldPos = parent.position;
            Quaternion parentWorldRot = parent.rotation;
            Quaternion invParentRot = Quaternion.Inverse(parentWorldRot);

            node.localPosition = invParentRot * (pts[i] - parentWorldPos);

            // 朝向：让本骨的 +Z 指向自己的下一节（pts[i+1] - pts[i]）
            Vector3 seg = pts[i + 1] - pts[i];
            if (seg.sqrMagnitude > 0.0000001f)
            {
                Vector3 dirLocal = (invParentRot * seg).normalized;

                /*★★★ 用「最小旋转」而不是 LookRotation —— 这是必须踩的坑。
                 *
                 *   Quaternion.LookRotation(dir, Vector3.up) 在 dir 与 up 平行时
                 *   是**万向节死锁**（gimbal lock）：up 会退化，
                 *   look 的解不唯一，Unity 会返回一个任意的扭转。
                 *   → 表现为「腿有时会突然翻个面/拧一下」，随机且难复现。
                 *
                 *   ★ 蜘蛛的腿恰恰经常与表面法线平行（爬天花板、贴墙时必然发生）。
                 *   → 死锁不是边缘情况，是高频情况。
                 *
                 *   解法：FromToRotation(from, to) 给「唯一最小旋转」，
                 *   当 from 接近 ±to 时它退化成 identity（无害），
                 *   不会产生随机扭转。
                 */
                Quaternion aim = Quaternion.FromToRotation(Vector3.forward, dirLocal);

                /* 再补一帧「参考轴」，让骨的 roll（绕自身轴的旋转）也稳定。
                 * 只有 aim 会让绕自身轴的自由度完全由上一帧决定，
                 * 首帧容易出现难以预测的朝向。 */
                node.localRotation = StableRotation(dirLocal, aim);
            }
        }

        /* ★★ 足端朝向：让最后一节（跗节）与表面法线成footTiltDeg 夹角。
         *
         * 【为什么需要】
         *   FABRIK 只解位置，不管朝向 → 最后一节的朝向完全由上一帧决定，
         *   脚看起来是「插进去」的而不是「踩上去」的。
         *   参考实现（spider_ik）专门让最后一节对齐命中点的法线，
         *   留 20° 倾斜（与 Locomotor 的 Orient Foot to Ground Pitch 0.8 吻合）。
         *
         * 【做法】
         *   期望朝向 = 把末节的 +Z（指向足端延伸方向）转到「
         *   法线在末节所在平面内的投影」的方向，再偏 footTiltDeg。
         *   用 FromToRotation 而非 LookRotation —— 同样是为了避开万向节死锁
         *   （脚贴着墙/天花板时，末节方向与法线几乎平行，是高频情况）。
         */
        if (n >= 1 && footTiltDeg > 0.01f)
        {
            Vector3 gn = groundNormal;
            if (gn.sqrMagnitude < 0.000001f) gn = up;
            gn.Normalize();

            Transform lastNode = bones[n - 1].Node;
            Transform lastParent = lastNode.parent;
            if (lastParent != null)
            {
                Quaternion invLastParent = Quaternion.Inverse(lastParent.rotation);

                // 法线在「末节父级」的局部空间里
                Vector3 gnLocal = (invLastParent * gn).normalized;

                // 末节 +Z 在自己父级空间里的当前方向
                Vector3 curZParent = lastParent.InverseTransformDirection(lastNode.forward).normalized;

                // 目标：法线垂直于「足端延伸方向」的平面内分量
                //      → 让脚背朝法线倾斜，而不是正对法线插下去
                Vector3 intoSurface = Vector3.Cross(gnLocal, curZParent);
                if (intoSurface.sqrMagnitude > 0.000001f)
                {
                    // 绕 intoSurface 旋转，使 curZParent 朝法线「躺倒」
                    float cur = Vector3.Angle(curZParent, gnLocal);
                    float want = Mathf.Clamp(90f - footTiltDeg, 0f, 90f);
                    float delta = want - cur;
                    if (Mathf.Abs(delta) > 0.01f)
                    {
                        Vector3 axis = intoSurface.normalized;
                        Quaternion tilt = Quaternion.AngleAxis(delta, axis);
                        lastNode.localRotation = tilt * lastNode.localRotation;
                    }
                }
            }
        }
    }

    /// <summary>
    /// 在 aim（把 +Z 对到 dir）之上补一个稳定的参考轴。
    ///
    /// 【为什么需要】
    ///   FromToRotation 只保证 +Z 的朝向，绕自身轴的 roll 是「最短路径」解，
    ///   逐帧累积后会出现缓慢的自转（腿会像螺旋桨一样转）。
    ///   → 用「上一帧的局部 +Y」当参考轴，把 roll 锁住。
    ///
    /// 【为什么不用 LookRotation】
    ///   它在 dir 与 up 平行时万向节死锁 → 随机扭转。见调用处注释。
    /// </summary>
    private static Quaternion StableRotation(Vector3 dirLocal, Quaternion aim)
    {
        // 取 aim 变换后的 up（局部参考轴）
        Vector3 upRef = aim * Vector3.up;

        // 若 upRef 与 dirLocal 几乎平行 → 换个参考轴，否则重建 roll 时会死锁
        if (Mathf.Abs(Vector3.Dot(upRef.normalized, dirLocal)) > 0.999f)
            upRef = aim * Vector3.right;

        // 用 from-to 把 upRef 旋到与 dirLocal 正交的平面上
        Vector3 projected = Vector3.ProjectOnPlane(upRef, dirLocal);
        if (projected.sqrMagnitude < 0.000001f)
            return aim;

        return Quaternion.LookRotation(dirLocal, projected.normalized);
    }

    private float TotalLength()
    {
        float t = 0f;
        for (int i = 0; i < bones.Length; i++) t += bones[i].Length;
        return t;
    }
}
