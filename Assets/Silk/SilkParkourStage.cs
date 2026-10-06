/* ============================================================================
 *  基础跑酷关卡 —— 参照 Unity 官方 Roll-a-Ball + 经典 obstacle course
 * ============================================================================
 *
 *  【参照来源与设计原则】
 *  1. Unity 官方 Roll-a-Ball（入门教程）：
 *     球体 + 平面 + 空格跳 + 四面墙围住（撞墙反弹）。
 *     结构极简，先把「移动 / 跳跃 / 碰撞」三件事做对。
 *
 *  2. 经典 obstacle course 设计原则（业界共识）：
 *     · 「一片平地 + 一个终点区 + 少量障碍」起步
 *     · 「一区一挑战」—— 一个区段只考验一件事，不要一次给五个
 *     · 「玩家一眼看不懂的关卡，对新手关卡来说太复杂了」
 *     · 障碍分工明确：高台逼你跳、窄道考验平衡、移动块考验时机
 *
 *  【本关卡 = 4 个区段，各考验一件事】
 *     区 0 平地    -> 熟悉移动与跳跃（无障碍，随便跑）
 *     区 1 高台    -> 跳跃（跳上高台）
 *     区 2 窄道    -> 平衡（两侧无护栏，走过去）
 *     区 3 终点台  -> 收尾
 *  摆荡相关的钩子平台放在区 3 之后，属于「进阶区」，
 *  与基础区**物理隔离**，不干扰基础移动的测试。
 *
 *  【坐标约定】X = 左右，Y = 前后，Z = 高度（重力沿 -Z）
 *  立方体内墙在 ±50（100³ 网格），故所有坐标留余量。
 *
 *  【调参公式】跳跃最高点 = jumpSpeed² / (2 × gravity)
 *     当前 jumpSpeed=15、gravity=15 -> 最高 7.5 格
 *     故所有「需要跳上去」的高差都≤ 5 格（留 2.5 格余量），
 *     「跳不过去」的水平间隙 ≥ 9 格。
 *
 *  【删除方法】删掉本文件 + SilkWorldBootstrap 里的一行调用。
 */

using UnityEngine;

public static class SilkParkourStage
{
    /// <summary>创建基础跑酷关卡。h = 立方体半高（本项目为 50）。</summary>
    public static void Create(float h)
    {
        float k = h / 50f;   // 自适应缩放

        /* ===== 区 0：起点大平地 =====
         * 完全平坦、没有障碍 —— 专门用来熟悉移动与跳跃。
         * 尺寸刻意做大（30×44），让玩家有足够空间试手感。*/
        Plat("Plat_0_平地", new Vector3(-30f, 0f, -38f) * k,
             new Vector3(30f, 44f, 8f) * k, C.Flat);

        /* ===== 区 1：高台（考验跳跃）=====
         * 高 5 格（跳跃上限 7.5，留 2.5 格余量），
         * 跨度 16 格 —— 轻松跳过去，不会因距离失败。*/
        Plat("Plat_1_高台", new Vector3(-2f, 0f, -33f) * k,
             new Vector3(16f, 16f, 6f) * k, C.Jump);

        /* ===== 区 2：窄道（考验平衡）=====
         * 宽 8 格、长 20 格。球直径 9 格 -> 窄道比球还窄，
         * 天然有压迫感（对应视频说的「消极空间：玩家感到受限」）。
         *
         * 【关键】两侧加了「不可通过」的视觉封堵 —— 见 Barrier()的注释。
         * 没有它，玩家看到直角方块边缘不知道能不能跳出去，
         * 试了掉下去会以为是 bug 而不是设计（视频：功能非可供性缺失）。*/
        Plat("Plat_2_窄道", new Vector3(26f, 0f, -36f) * k,
             new Vector3(20f, 8f, 6f) * k, C.Narrow);

        /* 窄道两侧的护栏：明确标示「此处不可通过」。
         *
         * 高度 5 格 —— 按球半径（4.5 格）取约1.1 倍，
         * 视觉上与球等高才够明确；太矮（试过 1.5 格）在
         * 9 格直径的球旁边几乎看不见，等于没加。
         * 位置在窄道外侧（内缘 ±4.2 vs 边缘 ±4），不挡通行。
         * 材质用 Sprites/Default 以支持真正的半透明。*/
        Barrier("2_窄道_护栏L", new Vector3(26f, -4.8f, -31f) * k,
                new Vector3(20f, 0.8f, 6f) * k);
        Barrier("2_窄道_护栏R", new Vector3(26f, 4.8f, -31f) * k,
                new Vector3(20f, 0.8f, 6f) * k);

        /* ===== 区 3：终点台（收尾）=====
         * 比窄道高 5 格，需再跳一次。颜色醒目提示「到头了」。*/
        Plat("Plat_3_终点", new Vector3(44f, 0f, -33f) * k,
             new Vector3(12f, 16f, 6f) * k, C.End);

        /* ===== 进阶区：摆荡钩子平台 =====
         * ⚠ 与基础区**物理隔离**（在 y 方向隔开 40 格），
         *   不干扰基础移动测试。摆荡相关功能明天再调。*/
        Plat("Plat_4_摆荡钩子", new Vector3(-10f, 40f, -8f) * k,
             new Vector3(16f, 14f, 8f) * k, C.Hook);
        Plat("Plat_5_摆荡落点", new Vector3(20f, 40f, -20f) * k,
             new Vector3(14f, 14f, 6f) * k, C.Hook);

        Debug.Log("[Stage] 基础跑酷关卡已创建（参照 Roll-a-Ball + obstacle course）\n" +
                  "  区0 平地  z顶=-34  x[-45,-15]  —— 熟悉移动/跳跃，无障碍\n" +
                  "  区1 高台  z顶=-30  高 5 格        —— 考验跳跃（上限 7.5 格）\n" +
                  "  区2 窄道  宽 8 格                —— 考验平衡，两侧无护栏\n" +
                  "  区3 终点  z顶=-30高 5 格        —— 收尾\n" +
                  "  进阶区    y=40（与基础区隔离）    —— 摆荡用，明天再调\n" +
                  "  跳跃最高点 = 15²/(2×15) = 7.5 格；需要跳的高差都 ≤ 5 格");
    }

    /// <summary>移除关卡物件（运行时兜底，便于切回 FreeFly 时清理）。</summary>
    public static void Clear()
    {
        int n = 0;
        foreach (var t in Object.FindObjectsOfType<Transform>())
        {
            if (t == null) continue;
            if (t.name.StartsWith("Plat_"))
            {
                Object.Destroy(t.gameObject);
                n++;
            }
        }
        if (n > 0) Debug.Log("[Stage] 清理 " + n + " 个平台");
    }

    /// <summary>配色 —— 玩家靠颜色分辨区段用途。</summary>
    static class C
    {
        public static readonly Color Flat = new Color(0.45f, 0.5f, 0.55f);   // 灰蓝：平地
        public static readonly Color Jump = new Color(0.3f, 0.55f, 0.85f);  // 蓝：跳跃
        public static readonly Color Narrow = new Color(0.4f, 0.7f, 0.5f);  // 绿：窄道
        public static readonly Color End = new Color(0.95f, 0.85f, 0.3f);    // 黄：终点
        public static readonly Color Hook = new Color(0.9f, 0.6f, 0.25f);    // 橙：摆荡（进阶）
    }

    /// <summary>建一块平台（自带 BoxCollider，能站上去）。</summary>
    static void Plat(string name, Vector3 center, Vector3 size, Color color)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = "Plat_" + name;
        go.transform.position = center;
        go.transform.localScale = size;
        go.transform.rotation = Quaternion.identity;

        var mr = go.GetComponent<MeshRenderer>();
        if (mr == null) return;
        Shader sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null) sh = Shader.Find("Standard");
        if (sh == null) sh = Shader.Find("Sprites/Default");
        if (sh != null) mr.material = new Material(sh) { color = color };
    }

    /// <summary>
    /// 建一道「不可通过」的边界标记。
    ///
    /// 【为什么必须有它 —— 来自参考视频「功能非可供性」原则】
    /// 视频原话：「在做边界封堵时，一定要明确表示此处无法通过」。
    /// 反面案例是《战神》：某些地方看上去能跳上去，实际却是空气墙 ——
    /// 玩家会以为那是 bug，不会以为是设计。
    ///
    /// 正面案例是《消逝之光2》：不能进入的建筑外墙覆盖大量藤蔓且
    /// **不留缺口**；能进入的虽然也有植被，但**留了缺口**做提示。
    /// 两相对照形成「一致性」，玩家一看就懂。
    ///
    /// 【我们的窄道之前的问题】
    /// 宽 8 格（比球直径 9 格还窄），但两侧什么都没有。
    /// 玩家看到直角方块边缘，分不清「这是边缘」还是「我能跳出去」，
    /// 试了掉下去只会以为程序有问题。
    ///
    /// 【做法】半透明格栅：既封堵边界，又不完全挡住视线，
    /// 让玩家能看见下面是什么（知情后才不会误判）。
    /// </summary>
    static void Barrier(string name, Vector3 center, Vector3 size)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = "Plat_" + name;
        go.transform.position = center;
        go.transform.localScale = size;
        go.transform.rotation = Quaternion.identity;

        // 碰撞体**保留**（要真的挡住）—— 玩家撞上去就该停住，
        // 而不是穿过护栏掉下去，那才是视频批评的「空气墙」糟糕体验。
        var mr = go.GetComponent<MeshRenderer>();
        if (mr == null) return;

        // 用 Sprites/Default 而非 Lit —— 前者天然支持透明且不受光照影响，
        // 后者即使设了 alpha 也会因为不透明渲染模式而看不见通透效果。
        Shader sh = Shader.Find("Sprites/Default");
        if (sh == null) sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) sh = Shader.Find("Standard");
        if (sh != null)
            mr.material = new Material(sh)
            {
                // 半透明橙红 —— 与平台色明显区分，一眼看出「这是边界」，
                // 且能透过去看见下方（知情后才不会误判能不能跳）。
                color = new Color(0.95f, 0.35f, 0.2f, 0.4f)
            };
    }
}