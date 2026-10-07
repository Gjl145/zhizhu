/* ============================================================================
 *  跑酷物理学习档案 —— 来源清单与对照分析
 * ============================================================================
 *
 *  【本文件的用途】
 *  用户质疑「你一直在敷衍」。这份档案如实记录：
 *   1. 我至今用过的**全部**学习来源（可核查的 URL）
 *   2. 哪些是真资料、哪些只是评论文章
 *   3. 从真资料里查到的**具体参数**，与我们的差距
 *
 *  ★ 起因：用户反馈「摆荡手感是一坨狗屎，不管什么位置放置丝线都会
 *    摆到地上，长度固定且不实用」，并质问我的学习来源。
 *    核查后发现——我之前查的全是**设计评论文章**，零物理参数。
 *
 * ============================================================================
 *  一、之前的学习来源（2026-07-07 之前，共 6 篇）
 * ============================================================================
 *
 *  【A. 有价值但只有「按键/操作」的信息】
 *
 *  1. marvels-spider-man.fandom.com
 *     /wiki/Marvel's_Spider-Man_2_controls
 *     -> Swing=按住R2、WebZip=空中X、ZipToPoint=R1、PointLaunch=落地前+
 *     ★ 只有按键表，无任何物理参数
 *
 *  2. game.ali213.net（游侠网《消光2图文攻略》）
 *     -> 「在跳跃过程中按 L2/LT 释放抓钩，将其作为绳索摆动」
 *     -> VNC 大厦教学关：「这里需要二连抓」
 *     ★ 只有操作描述
 *
 *  【B. 只有「设计理念」，零参数】
 *
 *  3. gamesradar.com —— 幽灵行者「MOMENTUM FIRST」「二连抓」
 *  4. kotaku.com      —— 同上（评论文章）
 *  5. polygon.com     —— 「leaping from one wall to the next feel more
 *                         like flying than the traditional locomotion」
 *  6. pcgamesn.com    —— 「循环摆荡：滑铲→跳→再滑铲」
 *
 *  ★★ **我从未查过的东西**（这是敷衍的根源）：
 *    · 任何 GDC 演讲 / 开发者博客 / 技术分析
 *    · 任何游戏的**具体物理参数**
 *    · Verlet / 约束求解器的工程实现细节
 *
 * ============================================================================
 *  二、本次找到的真资料（★ 关键突破）
 * ============================================================================
 *
 *  【1】★ GDC 2019 · Classic Game Design Postmortem: Swinging with Spider-Man
 *       讲者：Jamie Fristrom —— Treyarch 技术总监兼设计师
 *       内容：2004 年《蜘蛛侠2》摆荡系统的设计与实现
 *       录像：GDC 官方 YouTube 频道免费
 *       出处：game developer.com / gamedeveloper.com 的 GDC Vault 介绍
 *
 *       ★★★ 他说的原话（Fristrom 访谈 + 演讲）：
 *
 *       【重力是地球的 10 倍】
 *       「Gravity in Spider-Man 2 is about 10 times Earth normal.
 *         That's the only way we could make the speed of the game
 *         have that exciting dynamic feel.」
 *         -> 地球 9.8 m/s²，所以游戏重力 ≈ **98**
 *         -> 我们 gravity = 50（1 格 = 1 米）= **5.1 倍地球重力**
 *         -> ★★ **我们的重力只有蜘蛛侠2 的一半**
 *
 *       【锚点用「射线探针」，不是规则网格】
 *       「We sent out rays like feelers, and where they intersect
 *         a surface, that's the point to swing from.」
 *       「I wanted them to be attached to points in the world,
 *         and I wanted to basically be a bob on a pendulum.」
 *
 *       ★ 他明确说早期原型把锚点放在「每栋楼的角上」，
 *         结果玩家越多锚点越困惑 -> 改成射线探测任意表面。
 *
 *       【不要追求「真实」，要「真实但仍是幻想」】
 *       「It wasn't that people wanted realism, it was that people
 *         wanted to be Spider-Man, and when you imagine being
 *         Spider-Man, webs attached to the sky isn't part of the
 *         fantasy. It violates the fantasy.」
 *       -> 他放弃了「垂直于天空的蛛丝」这种真实但不好玩的设计
 *
 *  【2】GDC 2024 · Insomniac 的 Spider-Man 2 移动系统演讲
 *       讲者：Doug Sheehan —— Insomniac 高级编程总监
 *       出处：gamasutra.com/programming/how-spider-man-2-s-traversal-
 *             physics-sling-a-faster-superhero-fantasy
 *             cheatcc.com/articles/how-insomniac-tuned-spider-man-s-swing
 *
 *       ★ 核心概念：「authentic but still fantastical」
 *         —— 用大量数学算速度与轨迹让弧线看起来可信，
 *            然后在任何「可信度会拖慢玩家或夺走控制权」的地方就改规则。
 *
 *       ★ 关键结论（直接命中我们的「摆到地上」问题）：
 *         「Real physics makes a poor guide to a web-swing.」
 *         「Fast is a feeling, not a speedometer.」
 *
 *       ★ 另一个重要洞察（关于辅助）：
 *         「Why Assistance Reads as Speed」——
 *         快速移动 = 连续移动。每一个卡顿（时机错的松手、
 *         没钩住的失败）都会被玩家读成「慢」。
 *         -> 这解释了为什么我们「摆到地上」会被读成「手感差」
 *
 *  【3】抓钩摆荡的工程实现（两个独立开发者博客）
 *       · gamedev.net/blogs/entry/2291868-krilling-dev-blog-11
 *       · coolasjake.github.io/projects/grappling-hooks.html
 *
 *       ★ 关键技术点（与我们实现不同的地方）：
 *         - 「a_t = g · sin(θ)」—— 只加**切向**加速度，
 *           摆荡期间**关闭常规重力**
 *         - 「gravity leaking（重力泄漏）」问题：
 *           离散步进里重力在移动前施加，导致球会缓慢下沉。
 *           解法是把球**瞬移回绳长上限**，再按相同量补速度。
 *         - 「velocity redirecting（速度重定向）」：
 *           把速度的非切向分量补到切向上，保证动量完美守恒。
 *
 *  【4】Unity Verlet 绳索实战（中文）
 *       · mtrt.cn/news/13776
 *       -> 给出不同场景的参数区间：
 *          摆动藤蔓：段数 15-25、迭代 4-5
 *          抓钩攀岩绳：段数 30-50、迭代 5-8、更硬朗
 *
 * ============================================================================
 *  ★★★ 对照我们的问题清单
 * ============================================================================
 *
 *  用户反馈：「摆荡手感是一坨狗屎，不管什么位置放置丝线都是会摆到地上，
 *            长度固定且不实用，不会根据当前位置构建固定长度的丝线」
 *
 *  ├─ 问题 1：重力只有蜘蛛侠2 的一半
 *  │    蜘蛛侠2：g≈98（10 倍地球重力）-> 周期快、力量感强
 *  │    我们：g=50（5.1 倍）      -> 摆荡慢 1.4 倍、最低点速度只有 60~70%
 *  │    ★ 这是「摆到地上」的直接原因之一
 *  │
 *  ├─ 问题 2：选点逻辑不考虑「当前速度能否荡到」
 *  │    Fristrom：用射线探针 + 钟摆模型
 *  │    我们：按「距离/前方/高度」打分 -> 可能选到荡不到的锚点
 *  │    -> 表现就是：钩住了，然后摆不动 -> 玩家看着它落到地上
 *  │
 *  ├─ 问题 3：重力泄漏（gravity leaking）我们没处理
 *  │    两个开发者博客都提到：离散步进会让球缓慢下沉
 *  │    我们用显式速度积分 + 8 次约束迭代，
 *  │    很可能每帧都在「往下漏一点」-> 摆荡能量流失
 *  │
 *  ├─ 问题 4：泵力方向错
 *  │    正确做法：只加**切向**加速度 a_t = g·sin(θ)，
 *  │              摆荡期间关闭常规重力
 *  │    我们：沿 tangential 加速度，但重力全程开着
 *  │    -> 两者叠加后摆荡轨迹是错的
 *  │
 *  └─ 问题 5：辅助强度不足
 *       Insomniac：「Fast is a feeling」——
 *       每一个失败点都会被读成「慢」
 *       我们：失败（钩不住/摆不动）没有补偿 -> 手感崩塌
 *
 * ============================================================================
 *  四、还没查的资料（下一步该补的）
 * ============================================================================
 *
 *  · GDC 2019 演讲的完整录像（GDC YouTube 频道）
 *    —— Fristrom 应该讲了具体的实现细节与数值
 *  · GDC 2024 Doug Sheehan 的完整演讲
 *    —— Insomniac 的具体做法
 *  · 「垂直于天空的蛛丝」为什么被放弃 —— 可能与「摆到地上」直接相关
 *
 * ============================================================================
 */

using UnityEngine;

/// <summary>跑酷物理的资料出处与参数对照表 —— 仅记录，不参与逻辑。</summary>
public static class SilkReferenceBook
{
    /// <summary>蜘蛛侠2（2004，Treyarch）的重力：约 10 倍地球重力。
    /// 来源：GDC 2019「Swinging with Spider-Man」，Jamie Fristrom。
    /// 原话：「Gravity in Spider-Man 2 is about 10 times Earth normal.」</summary>
    public const float Spiderman2Gravity = 98f;

    /// <summary>本项目的重力（SilkPhysics.Gravity）。
    /// ★ 只有蜘蛛侠2 的一半 —— 这是摆荡「没劲」的直接原因。</summary>
    public const float OurGravity = 50f;

    /// <summary>对比：摆荡周期 T = 2π√(L/g)。
    /// 同样的绳长，我们比蜘蛛侠2 慢 √(98/50) ≈ 1.4 倍。</summary>
    public static float SwingPeriod(float ropeLength)
    {
        float g = Mathf.Max(OurGravity, 0.01f);
        return 2f * Mathf.PI * Mathf.Sqrt(ropeLength / g);
    }

    /// <summary>蜘蛛侠2 的摆荡周期（对比用）。</summary>
    public static float SwingPeriodSpiderMan2(float ropeLength)
    {
        float g = Mathf.Max(Spiderman2Gravity, 0.01f);
        return 2f * Mathf.PI * Mathf.Sqrt(ropeLength / g);
    }

    /// <summary>摆到最低点时的速度 v = √(2gL) —— 「甩出去」的初始速度。</summary>
    public static float BottomSpeed(float ropeLength)
        => Mathf.Sqrt(2f * OurGravity * ropeLength);

    /// <summary>资料出处（供查阅与追溯）。</summary>
    public static readonly string[] Sources =
    {
        "★ GDC 2019 · Classic Game Design Postmortem: Swinging with Spider-Man",
        "   讲者 Jamie Fristrom（Treyarch 技术总监），GDC 官方 YouTube 有免费录像",
        "   关键：重力 10 倍地球 / 锚点用射线探针 / 放弃『垂直天空的蛛丝』",
        "",
        "★ GDC 2024 · Insomniac Spider-Man 2 移动系统",
        "   讲者 Doug Sheehan（高级编程总监）",
        "   gamasutra.com/programming/how-spider-man-2-s-traversal-physics-...",
        "   关键：authentic but still fantastical / Fast is a feeling",
        "        现实物理是糟糕的参照；每个卡顿都会被读成『慢』",
        "",
        "★ 抓钩摆荡的工程实现",
        "   gamedev.net/blogs/entry/2291868-krilling-dev-blog-11",
        "   coolasjake.github.io/projects/grappling-hooks.html",
        "   关键：a_t = g·sin(θ) 只加切向，摆荡时关常规重力",
        "        gravity leaking（重力泄漏）需处理",
        "        velocity redirecting（速度重定向）保动量",
        "",
        "★ Unity Verlet 绳索实战（中文）",
        "   mtrt.cn/news/13776",
        "   关键：摆动藤蔓 段数15-25/迭代4-5；抓钩绳 段数30-50/迭代5-8",
    };
}