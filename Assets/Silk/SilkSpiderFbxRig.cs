using UnityEngine;

/// <summary>
/// ★★ Blender 低模蜘蛛 FBX 的接管器 —— 让 FBX 成为唯一的**可见体**。
///
/// 【★ 为什么必须有这个组件】
///   在它之前有两套蜘蛛：
///     ① SilkSpiderAnatomy 用 C# 在运行时拼球 + 锥形腿
///     ② Blender 导出的 FBX（42 骨骼 + 97 网格 + 蒙皮）
///   两套并存 = 屏幕上看到「程序化的球体」压着「FBX 的蜘蛛」，
///   而用户要的正是「**不是球加腿**」。
///   → 本组件把 ① 关掉、② 接上，让 FBX 成为唯一视觉来源。
///
/// 【职责边界 —— 刻意不做的事】
///   · **不**做任何 IK / 步态 / 落点计算 → 那是 SilkSpiderLimb 的 FABRIK
///   · **不**改物理、不碰球体代理 → SpiderProxy 与 silk 物理完全不动
///   · 只做三件事：实例化 FBX → 摆正朝向 → 按名字把骨骼交给 Limb
///
/// 【★ 朝向：本项目是 Z-up，FBX 是 Y-up】
///   Blender 侧高度写在 Z 上（zs 曲线），导出时axis_up='Y'。
///   Unity 是 Y-up，所以导入后蜘蛛**侧躺**，高度在 Unity 的 Y 轴。
///   要转到本项目的 Z-up：绕 X 轴 **+90°**（Y→Z, Z→−Y, X 不变）。
///   这个旋转是纯旋转，行列式 +1，不改变手性、不翻转法线，
///   所以蒙皮与法线都保持正确。
///   ★ 旋转加在**FBX 实例根**上，不改骨骼本身——
///     骨骼一旦被旋转过，Unity 的蒙皮会二次变换。
///
/// 【骨骼命名契约（与 Tools/blender/make_spider.py 一致）】
///   head · abdomen · leg_L_1 .. leg_L_4 · leg_R_1 .. leg_R_4
///   leg_&lt;侧&gt;_&lt;序号&gt;_&lt;节号&gt;  —— 节号 1..5
/// </summary>
[DisallowMultipleComponent]
public class SilkSpiderFbxRig : MonoBehaviour
{
    /// <summary>FBX 里每条腿的骨节数（leg_X_N_1 .. leg_X_N_5）。</summary>
    public const int SegmentsPerLeg = 5;

    /// <summary>
    /// FBX 实例（GameObject）。运行时用 Instantiate 生成。
    /// 不填则自动从 Resources / 同目录 prefab 里找。
    /// </summary>
    public GameObject fbxInstance;

    /// <summary>
    /// ★ 朝向修正绕X 轴的角度（度）。
    /// 90 = FBX 的Y-up 转成本项目 Z-up。
    /// 写成字段而不是硬编码，是为了**实测后能改**：不实测就锁死 90
    /// 是猜谜，猜错的表现是蜘蛛躺着且不报错。
    /// </summary>
    public float orientationFixDeg = 90f;

    /// <summary>是否已成功接管（FBX 存在且 8 条腿的骨骼都齐）。</summary>
    public bool Attached { get; private set; }

    /// <summary>接管失败的原因（成功时为 null）。</summary>
    public string AttachError { get; private set; }

    /// <summary>FBX 根节点（已摆正朝向）。</summary>
    public Transform FbxRoot { get; private set; }

    /// <summary>8 条腿的骨骼链，索引与 SilkSpiderAnatomy.LegRoots 一致。</summary>
    private Transform[][] legChains;

    // ================================================================
    //  骨骼名约定
    // ================================================================

    /// <summary>第 i 条腿（0..7，0..3 = 左侧前→后）的骨骼名数组。</summary>
    public static string[] LegBoneNames(int legIndex)
    {
        bool left = legIndex < 4;
        int k = (legIndex % 4) + 1;
        string tag = left ? "L" : "R";

        var names = new string[SegmentsPerLeg];
        for (int s = 1; s <= SegmentsPerLeg; s++)
            names[s - 1] = "leg_" + tag + "_" + k + "_" + s;
        return names;
    }

    /// <summary>
    /// 8 条腿的全部骨骼名（调试 / 自检用），长度 = 8 × SegmentsPerLeg。
    ///
    /// ★ 返回类型必须是 string[]：
    ///   我第一版写成 `public static string AllLegBoneNames()`，返回 `all`
    ///   （string[]）→ CS0029「无法把 string[] 隐式转换为 string」。
    ///   真编译抓到，precheck 抓不到（precheck 不做类型推导）。
    ///   → 这条印证【precheck 是文本启发式，不是编译器】。
    /// </summary>
    public static string[] AllLegBoneNames()
    {
        var all = new string[8 * SegmentsPerLeg];
        for (int i = 0; i < 8; i++)
        {
            string[] names = LegBoneNames(i);
            for (int s = 0; s < SegmentsPerLeg; s++)
                all[i * SegmentsPerLeg + s] = names[s];
        }
        return all;
    }

    // ================================================================
    //  接管流程
    // ================================================================

    /// <summary>
    /// ★ 由 SilkSpiderAnatomy.Build 调用。幂等。
    /// </summary>
    /// <returns>true = 接管成功，调用方应关掉程序化建模</returns>
    public bool Attach()
    {
        if (Attached) return true;
        if (!string.IsNullOrEmpty(AttachError)) return false;

        if (fbxInstance == null)
        {
            AttachError = "未指定 fbxInstance（FBX 实例）。"
                + "→ 在检查器（Inspector）里拖入 spider_lowpoly 生成的 FBX 实例，"
                + "或让 SilkSpiderAnatomy 用 Resources.Load 自动加载。";
            Debug.LogError("[SpiderFbxRig] " + AttachError, this);
            return false;
        }

        // ---- 1. 摆正朝向 ----
        FbxRoot = fbxInstance.transform;
        ApplyOrientation();

        // ---- 2. 建骨骼索引 ----
        var index = BuildBoneIndex(FbxRoot);

        // ---- 3. 取 8 条腿的链 ----
        legChains = new Transform[8][];
        var missing = new System.Text.StringBuilder();

        for (int i = 0; i < 8; i++)
        {
            string[] names = LegBoneNames(i);
            var chain = new Transform[SegmentsPerLeg];

            for (int s = 0; s < SegmentsPerLeg; s++)
            {
                Transform t;
                if (!index.TryGetValue(names[s], out t) || t == null)
                {
                    if (missing.Length > 0) missing.Append(", ");
                    missing.Append(names[s]);
                    chain = null;
                    break;
                }
                chain[s] = t;
            }
            legChains[i] = chain;
        }

        if (missing.Length > 0)
        {
            AttachError = "FBX 里缺少这些骨骼：" + missing
                + "（共 " + (8 * SegmentsPerLeg) + " 根应有骨骼）。"
                + "→ 检查 Blender 侧 build_rig 的命名，或确认导出时勾了 ARMATURE。";
            Debug.LogError("[SpiderFbxRig] " + AttachError, this);
            legChains = null;
            return false;
        }

        // ---- 4. 校验链是连续的父子关系 ----
        // ★ 不校验的后果：若 FBX 骨骼被重命名或层级变了，
        //   FABRIK 算出的 pts[i] 写进不相邻的骨里 → 腿会拧成麻花，
        //   而且**不报任何错**。
        for (int i = 0; i < 8; i++)
        {
            Transform[] chain = legChains[i];
            for (int s = 0; s < SegmentsPerLeg - 1; s++)
            {
                if (chain[s + 1].parent != chain[s])
                {
                    AttachError = "第 " + (i + 1) + " 条腿的第 " + (s + 2)
                        + " 根骨骼（" + chain[s + 1].name + "）的父级不是 "
                        + chain[s].name + "，而是 "
                        + (chain[s + 1].parent != null
                            ? chain[s + 1].parent.name : "null")
                        + " → 骨骼链断裂，FABRIK 会解出拧成麻花的腿。";
                    Debug.LogError("[SpiderFbxRig] " + AttachError, this);
                    legChains = null;
                    return false;
                }
            }
        }

        Attached = true;
        AttachError = null;

        int totalBones = index.Count;
        Debug.Log("[SpiderFbxRig] 接管成功：FBX 骨骼 " + totalBones
            + " 根，8 条腿 × " + SegmentsPerLeg + " 节全部就位，"
            + "朝向修正 " + orientationFixDeg + "°（绕 X 轴）。"
            + "视觉体已切换到 FBX，程序化建模将关闭。");
        return true;
    }

    /// <summary>
    /// 施加朝向修正。
    ///
    /// ★ 关键：旋转加在 FBX 实例根上，**逐骨骼设置 localRotation** 是错的——
    ///   Unity 的 SkinnedMeshRenderer 会在骨骼的**世界矩阵**上做蒙皮，
    ///   而骨骼的世界矩阵 = 父级链上所有旋转的乘积。
    ///   在骨骼上直接转，等于把旋转算两遍（父级一次+ 自己一次）。
    ///   → 只转根，让它沿层级自然传下去。
    /// </summary>
    private void ApplyOrientation()
    {
        // ★ 用 localRotation 而不是 rotation：
        //   FbxRoot 刚实例化、父级是本组件（Z-up已就位），
        //   localRotation 表达的是「相对父级的修正」，语义更明确。
        FbxRoot.localRotation = Quaternion.Euler(orientationFixDeg, 0f, 0f);
        FbxRoot.localPosition = Vector3.zero;
        FbxRoot.localScale = Vector3.one;   // ★ 缩放必须归一，否则蒙皮权重会偏
    }

    /// <summary>递归收集所有 Transform，按名字建索引。</summary>
    private static System.Collections.Generic.Dictionary<string, Transform>
        BuildBoneIndex(Transform root)
    {
        var map = new System.Collections.Generic.Dictionary<string, Transform>();
        if (root == null) return map;

        var all = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
        {
            //★ 后写覆盖先写：同名时取更深的那个
            //  （Unity 导入有时会在根下再套一层同名容器）
            map[all[i].name] = all[i];
        }
        return map;
    }

    /// <summary>
    /// 取第 i 条腿的骨骼链（供 SilkSpiderLimb 接管）。
    /// </summary>
    public Transform[] GetLegChain(int legIndex)
    {
        if (legChains == null) return null;
        if (legIndex < 0 || legIndex >= legChains.Length) return null;
        return legChains[legIndex];
    }

    /// <summary>实测：把腿根/ 足端的世界坐标打出来，供核对 FBX 朝向。</summary>
    public string DescribeLegs()
    {
        if (legChains == null) return "（未接管）";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[SpiderFbxRig] 8 条腿实测世界坐标（验证朝向用）：");
        for (int i = 0; i < 8; i++)
        {
            Transform[] c = legChains[i];
            if (c == null) { sb.AppendLine("  腿 " + i + "：缺骨骼"); continue; }
            sb.AppendLine(string.Format(
                "  leg{0}_{1}  根({2:F2},{3:F2},{4:F2})  膝({5:F2},{6:F2},{7:F2})  足({8:F2},{9:F2},{10:F2})",
                i < 4 ? "L" : "R", (i % 4) + 1,
                c[0].position.x, c[0].position.y, c[0].position.z,
                c[1].position.x, c[1].position.y, c[1].position.z,
                c[SegmentsPerLeg - 1].position.x,
                c[SegmentsPerLeg - 1].position.y,
                c[SegmentsPerLeg - 1].position.z));
        }

        // ---- 关键判据：足端必须比腿根低（重力沿 -Z） ----
        //若 foot.z > root.z，说明蜘蛛是倒挂的 → orientationFixDeg 需要调整。
        int inverted = 0;
        for (int i = 0; i < 8; i++)
        {
            Transform[] c = legChains[i];
            if (c == null) continue;
            Vector3 root = c[0].position;
            Vector3 foot = c[SegmentsPerLeg - 1].position;
            if (foot.z > root.z) inverted++;
        }
        sb.AppendLine();
        sb.AppendLine("  足端高于腿根的腿： " + inverted + " / 8");
        sb.AppendLine(inverted == 0
            ? "  → 朝向正确：足端全部低于腿根（符合 Z-up 重力方向）"
            : "  → ★ 朝向有问题：蜘蛛倒挂。orientationFixDeg 需调整");
        return sb.ToString();
    }
}
