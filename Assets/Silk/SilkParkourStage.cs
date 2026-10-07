/* ============================================================================
 *  基础跑酷关卡 —— 参照 Unity 官方 Roll-a-Ball + 经典 obstacle course
 * ============================================================================
 *
 *  【用户要求】
 *  「先做基础的滚动和跳跃关卡。钩爪的部分要有，但是不能是基础的技能」
 *  → **基础区完全不依赖钩爪**，只用「滚动 + 跳跃」即可通关。
 *    钩爪平台放在 y 方向的独立进阶区。
 *
 *  【设计原则】
 *  1. Unity 官方 Roll-a-Ball（入门教程）：
 *     球体 + 平面 + 空格跳 + 四面墙围住。先把「移动 / 跳跃 / 碰撞」做对。
 *
 *  2. 经典 obstacle course 设计原则（业界共识）：
 *     · 「一片平地 + 一个终点区 + 少量障碍」起步
 *     · 「一区一挑战」—— 一个区段只考验一件事
 *     · 障碍分工明确：高台逼你跳、窄桥考验控制
 *
 *  3. 渐进教学（来自参考视频「地标要适时出现」+ 幽灵行者
 *     「失败要快、重生要快，鼓励不断尝试」）：
 *     难度递增，每区只教一件事，且掉落会自动重生（见下方注释）。
 *
 *  【本关卡 = 4 个基础区段，各考验一件事】
 *     区 0 平地    宽 44 格 —— 熟悉滚动（加速/减速/转向）
 *     区 1 高台    高 3 格、间隙 6 格 —— 学跳跃
 *     区 2 宽桥    宽 14 格、带护栏 —— 学控制
 *     区 3 终点台  高 4 格、间隙 8 格 —— 收尾
 *     进阶区（y=60 隔离）  钩爪摆荡 —— **不是基础技能**
 *
 *  【★ 宽度以球直径为基准 —— 这是最容易出错的地方】
 *  球直径 = 9 格（visualRadius 4.5）。参考视频强调
 *  「前期未能建立正确的比例，后续修改成本极高」：
 *     宽度 ≥ 2.5 倍球径 = 宽松（能随便跑）
 *     宽度 ≈ 2.0 倍球径 = 中等（要瞄准）
 *     宽度 ≈ 1.3 倍球径 = 偏窄（需注意脚下）
 *     宽度 < 1.0 倍球径 = 球都塞不进（进阶强度，不该出现在基础区）
 *  原设计的「窄道」宽 8 格 = 0.89 倍球径，**比球还窄**，
 *  球心只能在 ±0.5 格内移动 —— 那是进阶强度。
 *  现基础区最窄处为 12 格 = 1.3 倍球径（仍有纠错空间）。
 *
 *  【为什么单排而不做 U 形折返】
 *  试过 U 形（去程 +X、回程 -X），但那要求玩家中途**掉头**——
 *  掉头属于「空间定向」技巧，不该出现在基础教学关。
 *  单排的代价是总长度受限（内墙 ±46），故各段宽度按比例压缩。
 *
 *  【坐标约定】X = 左右，Y = 前后，Z = 高度（重力沿 -Z）
 *  立方体内墙在 ±50（100³ 网格），故所有坐标留余量。
 *
 *  【调参公式】跳跃最高点 = jumpSpeed² / (2 × gravity)
 *     当前 jumpSpeed=26、gravity=50 -> 最高 6.8 格、滞空 1.04 秒
 *     故高差都 ≤ 4 格（留 2.8 格余量），间隙都 ≤ 8 格
 *     （全速起跳跨度 = moveSpeed × 滞空 = 50 × 1.04 ≈ 52 格，余量极大）。
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

        /* ============================================================
         *  基础关卡设计原则（据参考视频与用户要求）
         * ============================================================
         *  用户要求：「先做基础的滚动和跳跃关卡。
         *钩爪的部分要有，但是不能是基础的技能」
         *
         *  → 因此基础区**完全不依赖钩爪**，只用「滚动 + 跳跃」通关。
         *    钩爪相关的平台放在 y 方向的独立进阶区。
         *
         *  【渐进教学】参考视频2「地标要适时出现」+ 幽灵行者
         *  「失败要快、重生要快、鼓励不断尝试」：
         *每个区只教**一件事**，难度递增，且都有明确的视觉区分。
         *
         *  【比例必须与机制绑定】视频 2 强调「前期定好比例，
         *  后期修改成本极高」。本关卡的宽度以**球直径 9 格**为基准：
         *    宽度 ≥ 3.0 倍球径 = 宽敞（能随便跑）
         *    宽度 ≈ 2.0 倍球径 = 中等（要瞄准）
         *    宽度 < 1.2 倍球径 = 勉强（只能直线走）
         *  原设计里「窄道」宽 8 格 = 0.89 倍球径，
         *  比球还窄 -> 那是**进阶**强度，不该出现在基础区。
         * ============================================================ */

        /* ===== 区 0：起点大平地（学滚动）=====
         * 宽 26 格 = 2.9 倍球径，宽敞但不是无限大。
         * 完全平坦无障碍 —— 专门用来熟悉加速、减速、转向手感。
         *
         * 【为什么整条关卡沿 +X 单排，不做 U 形折返】
         * 试过 U 形（去程 +X、回程 -X），但那要求玩家中途**掉头**——
         * 掉头属于「空间定向」的技巧，不该出现在基础教学关。
         * 单排的代价是总长度受限，故各段宽度按比例压缩过
         * （见文件头「宽度以球直径为基准」）。*/
        Plat("0_平地", new Vector3(-32f, 0f, -38f) * k,
             new Vector3(26f, 30f, 8f) * k, C.Flat);

        /* ===== 区 1：缓坡高台（学跳跃）=====
         * 高 2 格（跳跃上限 6.8，余量充足）、间隙 4 格。
         * 宽 12 格 = 1.3 倍球径，比平地窄一些，需要瞄一下但不难。*/
        Plat("1_高台", new Vector3(-9f, 0f, -36f) * k,
             new Vector3(12f, 20f, 6f) * k, C.Jump);

        /* ===== 区 2：中等宽桥（学控制）=====
         * 宽 18 格 = 2.0 倍球径。比高台宽，走起来有余量，
         * 但因为横跨两个间隙，仍需一定准头 ——
         * 这是**基础区该有的强度**（原设计的 8 格 = 0.89 球径
         * 比球还窄，那是进阶强度）。
         * 两侧有护栏，掉落会自动重生。*/
        Plat("2_宽桥", new Vector3(14f, 0f, -38f) * k,
             new Vector3(18f, 14f, 6f) * k, C.Narrow);

        Barrier("2_宽桥_护栏L", new Vector3(14f, -7.8f, -33f) * k,
                new Vector3(18f, 0.8f, 5f) * k);
        Barrier("2_宽桥_护栏R", new Vector3(14f, 7.8f, -33f) * k,
                new Vector3(18f, 0.8f, 5f) * k);

        /* ===== 区 3：终点台（学落点控制）=====
         * 高 3 格、间隙 6 格。比前面难一点但不夸张。
         * 宽 12 格，落点有充足余量。*/
        Plat("3_终点", new Vector3(35f, 0f, -35f) * k,
             new Vector3(12f, 20f, 6f) * k, C.End);

        /* ===== 进阶区：钩爪摆荡（不是基础技能）=====
         * ⚠ 与基础区**物理隔离**（y 方向错开 16 格：基础区 y[-15,15]
         *   进阶区 y[31,45]），基础区完全不需要钩爪即可通关。
         *
         * 【y 用 38 而不是 60】内墙是 ±50，y=60 会越界
         *（check_stage 抓到了）。38 刚好在界内，且与基础区保持
         *  16 格间隔 —— 足够避免误踩，又不会因太远而看不清。*/
        Plat("4_钩爪高台", new Vector3(-10f, 38f, -8f) * k,
             new Vector3(16f, 14f, 8f) * k, C.Hook);
        Plat("5_钩爪落点", new Vector3(20f, 38f, -20f) * k,
             new Vector3(14f, 14f, 6f) * k, C.Hook);

        Debug.Log("[Stage] 基础跑酷关卡已创建\n" +
                  "  ── 基础区（只需滚动 + 跳跃，不需要钩爪）──\n" +
                  "  区0 平地x[-45,-19] 宽26(2.9球径) 顶面z=-34  学滚动\n" +
                  "  区1 高台  x[-15, -3] 宽12(1.3球径) 顶面z=-33  学跳跃（落差2/间隙4）\n" +
                  "  区2 宽桥  x[ 5, 23] 宽18(2.0球径) 顶面z=-35  学控制（带护栏）\n" +
                  "  区3 终点  x[29, 41] 宽12(1.3球径) 顶面z=-32  收尾（落差3/间隙6）\n" +
                  "  ── 进阶区（y=38 隔离，需要钩爪）──\n" +
                  "  区4/5 悬空钩爪平台\n" +
                  "  ── 参数 ──\n" +
                  "  球直径 9 格 | 跳跃上限 6.8 格 | 全速起跳跨度 52 格\n" +
                  "  掉落自动重生（z < -70）");
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