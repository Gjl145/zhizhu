using System.Collections.Generic;
using UnityEngine;

/* ============================================================
 *   SilkBuilder.cs ——  正方体蛛网（单文件，含两个世界）
 *   X=左右  Y=前后  Z=高度（重力方向）
 *
 *   【本文件含两个世界，键位故意不共享】
 *
 *   FreeFly 自由视角（编辑器世界，用于搭建关卡、关注精确操作）
 *     左键       点选锚点（两次点击连成一条丝线）
 *     右键拖拽   环绕视角
 *     右键单击   断最近的线
 *     WASD/QE    飞行 / 升降      Shift 加速      滚轮微移
 *     R 取消选中   C 清空   G 织网   X 老化
 *
 *   Parkour 第三人称（游戏世界，用于测试跑酷、手感优先）
 *     WASD/QE    移动 / 升降      左Shift 加速
 *     空格       地面=跳跃，空中=抓丝线（自动瞄准）
 *     左键       自动瞄准并抓住最优锚点
 *     左Shift    抓着丝线时松手
 *     G          断视线中心的线
 *     C          固化当前位置为节点（PlayerNode）
 *     R          回起点
 *
 *   跑酷关卡见 SilkParkourStage.cs（4 个基础区段 + 摆荡进阶区）。
 * ============================================================ */

public enum SilkState { Intact, Broken, Fading }

/// <summary>
/// 全局重力常量（格/秒²）。
///
/// 【为什么要抽成全局】
/// 重力会同时影响三处：玩家跳跃（SilkParkourController.gravity）、
/// 丝线约束链的末端摆动（SilkChain.gravity，建链时由 SilkLine 赋值）、
/// 以及断裂后丝线下坠。
/// 这三处必须一致，否则会出现「人跳得很轻快、但丝线荡得很慢」
/// 这种割裂感（用户反馈「上升下降都太慢」时排查发现）。
///
/// SilkLine 是普通 C# 类、拿不到 MonoBehaviour 的字段，
/// 所以这里用全局常量作为唯一来源，避免三处各自写死数值而漂移。
///
/// 【取值】50 —— 用户反馈原值 15 时「上升和下降都太慢、
/// 跳跃有延迟」。原值下上升 1 秒 + 下降 1 秒（来回 2 秒），
/// 视觉上像没跳起来。50 时上升/下降各 0.44 秒，干脆利落。
/// </summary>
public static class SilkPhysics
{
    public const float Gravity = 50f;
}

/* ===================== 丝线状态机 =====================
 *
 * 明天跑酷会有三种线并存：静止的、断裂后自然下坠的、带初速度且末端挂物体的。
 * 之前用 state / isFreeEnd / noSag 三个独立布尔标记管这件事，
 * 它们可以互相矛盾（noSag+isFreeEnd 同时 true → 双重积分；
 * state=Intact 但 chain!=null → 双重渲染），必然混乱。
 *
 * 改为**单一状态字段**：一个丝线在任何时刻只处于一种状态，
 * 行为完全由状态决定，不可能出现矛盾组合。
 *
 *   Static    静止 —— 渲染悬链线，不跑物理，不响应施力
 *   Anchored  末端挂着物体（玩家抓住了）—— 建约束链但根部仍锚在墙上
 *   Swinging  摆动中 —— 约束链受玩家施力驱动
 *   FreeFall  脱手 —— 末端无挂载，纯重力 + 阻尼
 *   Fading    淡出中（终态，不可再转换）
 *
 * 允许的转换（其余一律拒绝并警告）：
 *   Static --划断--> FreeFall
 *   Static --抓住--> Anchored
 *   Anchored --施力--> Swinging
 *   Anchored --松手--> FreeFall
 *   Swinging --松手--> FreeFall
 *   任意 --开始淡出--> Fading
 */
public enum SilkLifeState
{
    Static,    // 静止
    Anchored,  // 末端挂载物体
    Swinging,  // 摆动中（玩家施力）
    FreeFall,  // 脱手下坠
    Fading,    // 淡出（终态）
}

/// <summary>
/// 可挂到丝线末端的物体。玩家、敌人、箱子、道具都实现它即可。
/// 明天扩展 SpiderShot（发射后粘住某物拉过来）时，
/// 只需让目标体实现本接口，不用改 SilkLine / SilkChain 任何内部逻辑。
/// </summary>
public interface SilkAttachable
{
    /// <summary>丝线末端应挂在这个点上（通常是手、钩子、头顶等）。</summary>
    Transform AttachPoint { get; }

    /// <summary>挂载时的显示名，用于日志。</summary>
    string name { get; }
}


public enum SilkColor { White, Yellow, Red }
/// <summary>
/// 锚点类型。
///   Wall       Bootstrap 生成的墙面锚点（只读，装饰用）
///   Internal   玩家点击创建的锚点
///   SilkNode   断裂产生的断点
///   PlayerNode **玩家自己构建的可粘附节点** —— 蜘蛛侠玩法核心：
///               既能作为静态结网的连接点，也能被动态丝线粘住当墙面挂点用
/// </summary>
public enum AnchorType { Wall, Internal, SilkNode, PlayerNode }
public enum BreakMode { Middle, Quarter, ThreeQuarter, SpiderShot }

/* 丝线的结构角色。真实蛛网不是所有丝线都一样：
 *   Radii  主丝/辐射丝 —— 从中心辐射向外，绷紧、承重、粗
 *   Spiral 辅丝/螺旋丝 —— 横向连接，松弛、装饰、细
 * 静止观感的关键就在这个区分：全是同一种，网就没有层次。 */
public enum SilkRole { Radii, Spiral }

/* ===================== 断裂信号（信号驱动） =====================
 *
 * 原实现是「右键 → 找最近丝线 → 用预设 BreakMode 断」，所有触发逻辑
 * 耦合在 SilkBuilder.TryCutNearest 里。跑酷需要多种触发源（玩家末端物体、
 * 敌人切断、定时老化、半空中发射即断），故拆成「谁触发」与「怎么断」两层：
 *
 *   SilkBreakSignal  —— 一次断裂请求的完整描述（谁、在哪、什么模式、附带参数）
 *   SilkEventBus —— 全局事件总线，任何对象都能 Post，SilkBuilder 只负责执行
 *
 * 这样明天做跑酷时，「玩家抓住丝线后松手」只需发一个 SilkBreakSignal，
 * 不需要碰 SilkBuilder 的内部逻辑。
 */

/* 谁触发的断裂 */
public enum SilkBreakCause
{
    ManualCut,     // 玩家右键划断
    PlayerRelease, // 玩家松手（跑酷主场景）
    AttachHit,     // 末端物体碰到障碍
    EnemyCut,      // 敌人切断
    Aging,         // 老化
    ShotRelease,   // 发射后在空中断开
}

/* 断点如何确定 */
public enum SilkBreakPointMode
{
    Preset,        // 用 SilkLine 自带的 BreakMode（1/4~1/2 随机）
    WorldPoint,    // 指定世界坐标（玩家的手 / 碰撞点）
    Normalized,    // 按跨度比例 0~1（从低处往高处算）
}

/// <summary>一次断裂请求的完整描述。既是数据，也是事件（实现 ISilkEvent）。</summary>
public class SilkBreakSignal : ISilkEvent
{
    public SilkBreakCause cause;              // 谁触发的
    public SilkBreakPointMode pointMode;      // 断点怎么定
    public Vector3 worldPoint;                // pointMode=WorldPoint 时用
    public float normalizedT = 0.5f;          // pointMode=Normalized 时用
    public Vector3 initialVelocity;           // 断裂后注入的初速度（明天跑酷用）
    public float tipBoost = 1.6f;             // 初速度沿 root→末端的增幅
    public bool injectVelocity = false;       // 是否注入初速度
    public SilkLine target;                   // 指定断裂的线；null = 广播

    public SilkBreakSignal(SilkBreakCause c)
    {
        cause = c;
        pointMode = SilkBreakPointMode.Preset;
    }

    public SilkBreakSignal At(Vector3 worldPos)
    {
        pointMode = SilkBreakPointMode.WorldPoint;
        worldPoint = worldPos;
        return this;
    }

    public SilkBreakSignal AtNormalized(float t)
    {
        pointMode = SilkBreakPointMode.Normalized;
        normalizedT = Mathf.Clamp01(t);
        return this;
    }

    public SilkBreakSignal WithVelocity(Vector3 v, float boost = 1.6f)
    {
        injectVelocity = true;
        initialVelocity = v;
        tipBoost = boost;
        return this;
    }

    public SilkBreakSignal On(SilkLine line)
    {
        target = line;
        return this;
    }

    /// <summary>事件入口：总线调用。断裂的具体执行交给 SilkBuilder。</summary>
    public void Handle(SilkBuilder handler)
    {
        if (handler == null) return;
        handler.ExecuteBreak(this);
    }
}

/* ============ 其他事件类型（示范「加事件不改分发」） ============ */

/// <summary>抓住丝线：末端挂上某物。</summary>
public class SilkGrabSignal : ISilkEvent
{
    public Transform attachPoint;
    public string label = "物体";
    public bool startSwing = false;      // 抓住后是否立刻开始摆动
    public SilkLine target = null;       // 指定要抓的线；null = 由处理器自行搜索

    public SilkGrabSignal(Transform point, string label = "物体", bool startSwing = false)
    {
        attachPoint = point; this.label = label; this.startSwing = startSwing;
    }

    /// <summary>指定目标线。发布方已找到线时用它，
    /// 可避免处理器用自己的搜索半径重新找一遍导致抓到不同的线。</summary>
    public SilkGrabSignal On(SilkLine line)
    {
        target = line;
        return this;
    }

    public void Handle(SilkBuilder handler)
    {
        if (handler == null) return;
        handler.ExecuteGrab(this);
    }
}

/// <summary>松手：末端脱钩，转自然下坠。</summary>
public class SilkReleaseSignal : ISilkEvent
{
    public void Handle(SilkBuilder handler)
    {
        if (handler == null) return;
        handler.ExecuteRelease();
    }
}

/// <summary>对丝线施加一次力（泵力/横推），明天跑酷的核心输入。</summary>
public class SilkForceSignal : ISilkEvent
{
    public Vector3 force;        // 世界坐标下的力
    public float tipBoost = 1.6f;

    public SilkForceSignal(Vector3 f, float boost = 1.6f)
    {
        force = f; tipBoost = boost;
    }

    public void Handle(SilkBuilder handler)
    {
        if (handler == null) return;
        handler.ExecuteForce(this);
    }
}

/// <summary>
/// 发射丝线：从施力点向命中点生成一条新的静态丝线。
/// 这是 SpiderShot 的基础 —— 后续「结网」功能也走这条路径。
/// </summary>
public class SilkFireSignal : ISilkEvent
{
    public Vector3 from;         // 发射点（玩家手部）
    public Vector3 to;           // 命中点（墙面）
    public bool autoAttach;      // 生成后是否立刻挂上去

    public SilkFireSignal(Vector3 from, Vector3 to, bool autoAttach = true)
    {
        this.from = from; this.to = to; this.autoAttach = autoAttach;
    }

    public void Handle(SilkBuilder handler)
    {
        if (handler == null) return;
        handler.ExecuteFire(this);
    }
}

/* ===================== 两个世界（重要约定，勿混淆） =====================
 *
 * 同一套架构（VoxelGrid / AnchorPoint / SilkLine / SilkChain / 事件总线），
 * 但**运行的是两个完全不同的世界**，通过 Tab 切换：
 *
 *   FreeFly 自由视角 —— 编辑器世界
 *     目的：方便后续搭建关卡
 *     相机自由飞行，用鼠标左键手动连线、生成蛛网
 *     关注：能否自由放置锚点、能否织出想要的网形
 *     键位：WASD/QE 飞行 · 左键连丝 · G 织网 · C 清空 · X 老化 · R 取消
 *
 *   Parkour 第三人称 —— 游戏世界
 *     目的：方便测试跑酷进度，是游戏本身
 *     玩家操纵一个球，自己发射/回收丝线、构建挂点地形
 *     关注：手感、摆荡是否爽、滑行是否顺畅
 *     键位：WASD 控球 · 左键建锚点 · B 发射 · 空格抓/放 · X 断自己发的线
 *            C 固化节点 · V 结网 · R 回起点
 *
 * ⚠ 两个世界的键位**故意不共享**，即便看起来能复用。
 *   因为需求完全不同：编辑时要「精确操作」，游戏时要「手感优先」。
 *   任何键位在两个世界都要独立声明，不许共用。
 * ================================================================
 */
/// <summary>操作模式。自由视角（编辑）与第三人称（游戏）是两个世界。</summary>
public enum SilkControlMode
{
    /// <summary>自由飞行相机：WASD/QE 移动，右键环绕。用于构建蛛网。</summary>
    FreeFly,

    /// <summary>第三人称：主角是一个球，自己发射/回收丝线，跑酷摆荡。</summary>
    Parkour,
}

/// <summary>
/// 把玩家当前位置固化成一个可粘附节点 —— 蜘蛛侠玩法的核心机制。
/// 固化后：
///   · 可作为静态结网的连接点（多道线共���此点）
///   · 可被动态丝线粘住，当作墙面上的挂点使用
///   · 玩家自己荡过去也可以粘上去
/// </summary>
public class SilkPinNodeSignal : ISilkEvent
{
    public Vector3 position;      // 要固化的位置

    /// <summary>是否把该点记为「连线起点」（下一步点另一个点就连线）。</summary>
    public bool selectAsStart = false;

    public SilkPinNodeSignal(Vector3 pos, bool selectAsStart = false)
    {
        position = pos;
        this.selectAsStart = selectAsStart;
    }

    public void Handle(SilkBuilder handler)
    {
        if (handler == null) return;
        // 两步流程：第一个点是起点，第二个点才连线
        handler.ExecuteNodeClick(this);
    }
}

/// <summary>
/// 静态结网：在两个已有节点之间生成一条线。
/// 用于「同时发射多道丝线，让自己成为两处丝线的共同节点」。
/// </summary>
public class SilkSpanSignal : ISilkEvent
{
    public Vector3 from;
    public Vector3 to;

    public SilkSpanSignal(Vector3 f, Vector3 t)
    {
        from = f; to = t;
    }

    public void Handle(SilkBuilder handler)
    {
        if (handler == null) return;
        handler.ExecuteSpan(this);
    }
}


/// <summary>
/// 全局事件总线 —— 统一控制不同事件。
///
/// 为什么不用 C# 的 event 直接订阅：
///   每加一种事件就要加一个 event 字段，发布处要写 N 个 if，
///   订阅方也得记住有哪些 event 名。事件一多就失控。
///
/// 本总线的做法：
///   · 所有事件实现 ISilkEvent（自带一个处理器方法）
///   · 处理器在构造时注册到总线
///   · 发布方只管 Post(evt)，总线按类型找到对应处理器
///   · 加新事件 = 新建一个实现 ISilkEvent 的类 + 一个处理器，**分发逻辑零改动**
///
/// 这也正是 Unity 官方 StateMachine 文档与 Mina Pecheux
/// "How to use events to implement a messaging system in 30 minutes"
/// 讲的模式：事件与处理解耦，发布方不认识处理器。
/// </summary>
public interface ISilkEvent
{
    /// <summary>处理这个事件。handler 通常是 SilkBuilder。</summary>
    void Handle(SilkBuilder handler);
}

/// <summary>事件总线。静态类，任何脚本可Post/Register，无装配顺序依赖。</summary>
public static class SilkEventBus
{
    // System.Type -> 处理该类型的处理器列表。
    // 用全限定名而非 using System：本文件里有 17 处 UnityEngine.Random，
    // 引入 System 会造成 Random 的歧义。
    static readonly Dictionary<System.Type, List<object>>
        handlers = new Dictionary<System.Type, List<object>>();

    /// <summary>注册一个能处理 T 类型事件的处理器。</summary>
    public static void Register<T>(object handler) where T : ISilkEvent
    {
        var t = typeof(T);
        if (!handlers.TryGetValue(t, out var list))
        {
            list = new List<object>();
            handlers[t] = list;
        }
        if (!list.Contains(handler)) list.Add(handler);
    }

    public static void Unregister<T>(object handler) where T : ISilkEvent
    {
        if (handlers.TryGetValue(typeof(T), out var list))
            list.Remove(handler);
    }

    /// <summary>发布事件。bus 会找到所有能处理该类型的处理器并逐个调用。</summary>
    public static void Post<T>(T evt) where T : ISilkEvent
    {
        if (evt == null) return;
        if (!handlers.TryGetValue(typeof(T), out var list)) return;
        // 复制一份再遍历：处理器里可能会注册/注销，避免集合被修改。
        // 手动复制而非 list.ToArray()，省掉 System.Linq 依赖。
        int n = list.Count;
        var snapshot = new object[n];
        for (int i = 0; i < n; i++) snapshot[i] = list[i];
        foreach (var h in snapshot)
            ((T)evt).Handle(h as SilkBuilder);
    }

    /// <summary>清空所有注册（重开场景时用，避免静态残留）。</summary>
    public static void Clear() => handlers.Clear();
}

/* ===================== 体素网格 ===================== */
public class VoxelGrid : MonoBehaviour
{
    public int size = 100;
    public float cellSize = 1f;
    public float GetHalfSize() => size * cellSize * 0.5f;
    public Vector3 GetCenter() => transform.position;

    public Vector3 VoxelToWorld(Vector3Int v)
        => transform.position + new Vector3(v.x * cellSize, v.y * cellSize, v.z * cellSize);

    public Vector3Int WorldToVoxel(Vector3 world)
    {
        Vector3 local = world - transform.position;
        return new Vector3Int(
            Mathf.RoundToInt(local.x / cellSize),
            Mathf.RoundToInt(local.y / cellSize),
            Mathf.RoundToInt(local.z / cellSize));
    }
}

/* ===================== 锚点 ===================== */
public class AnchorPoint : MonoBehaviour
{
    public Vector3Int position;
    public AnchorType type = AnchorType.Internal;
    public bool available = true;
    public Vector3 WorldPosition => transform.position;

    static Mesh _mesh;
    static Mesh Mesh
    {
        get
        {
            if (_mesh == null)
            {
                _mesh = Resources.GetBuiltinResource<Mesh>("New-Sphere.fbx");
                if (_mesh == null) _mesh = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");
                if (_mesh == null) _mesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            }
            return _mesh;
        }
    }

    // 共享基础材质：避免每个锚点都 new Material()（264 个锚点 = 264 个材质实例）。
    // 需要改色时在 SetColor 里克隆一份，避免共享材质串色。
    static Material _baseMat;
    static Material BaseMat
    {
        get
        {
            if (_baseMat == null)
            {
                Shader sh = Shader.Find("Universal Render Pipeline/Lit");
                if (sh == null) sh = Shader.Find("Standard");
                if (sh == null) sh = Shader.Find("Sprites/Default");
                if (sh == null) sh = Shader.Find("Hidden/InternalErrorShader");
                _baseMat = new Material(sh) { color = new Color(0.9f, 0.75f, 0.2f) };
            }
            return _baseMat;
        }
    }

    public void Setup(Vector3 worldPos, Vector3Int voxel, AnchorType t)
    {
        transform.position = worldPos;
        position = voxel;
        type = t;
        available = true;
        gameObject.name = "Anchor_" + t + "_" + voxel;

        var mf = gameObject.AddComponent<MeshFilter>();
        mf.sharedMesh = Mesh;
        var mr = gameObject.AddComponent<MeshRenderer>();
        mr.sharedMaterial = BaseMat;   // 先共享，用到改色时再克隆

        /* 视觉与碰撞分离。
         * 原来localScale=0.2 把 SphereCollider 也缩成了
         * 0.3 × 0.2 = **0.06 格**，只有锚点间距（8格）的 0.75%，
         * 鼠标几乎点不中 —— 这是「点不到锚点」的根本原因。
         * 现在：视觉保持小而精致，碰撞体按世界半径单独设置。*/
        transform.localScale = Vector3.one * VisualScale;

        var col = gameObject.AddComponent<SphereCollider>();
        col.radius = HitRadius / Mathf.Max(transform.lossyScale.x, 0.0001f);

        /* 远距离可见性（参考《消逝的光芒2》）
         *
         * 消光2 的可钩点在远处就有明显的黄色高亮，玩家能提前规划路线。
         * 我们原本是不发光的黄色小球，在 100³ 网格里远处几乎看不见 ->
         * 玩家不知道「哪个能钩」，只能靠自动瞄准黑箱式地选点。
         *
         * 在此处统一设置，而非在三处创建点各写一遍 —— 避免新增锚点路径时漏掉。
         * 只给 Wall 类型（可钩点）加，ChainNode 等内部节点不需要。*/
        if (t == AnchorType.Wall) SetEmissive(new Color(0.85f, 0.62f, 0.12f));
    }

    /// <summary>锚点视觉缩放（相对原生球半径 0.5）。
    /// 只影响看起来多大，不影响点击判定。</summary>
    public static float VisualScale = 0.9f;

    /// <summary>锚点的点击判定半径（世界空间单位）。
    /// 锚点间距 8 格，半径 1.5 格时直径占 37.5%，相邻球不重叠 ——
    /// 既好点又不会误选邻近锚点。</summary>
    public static float HitRadius = 1.5f;

    /// <summary>
    /// 把这个锚点标记为「可被玩家构建」，并放大碰撞体。
    ///
    /// 为什么需要：默认锚点半径 0.3 × localScale 0.2 = 实际仅 0.06 格，
    /// 射线几乎打不中 —— 玩家自己构建的节点就无法作为挂点被动态丝线粘住。
    /// 这里把碰撞体恢复到世界空间 1.5 格，让玩家节点真正可粘。
    /// </summary>
    public void MarkAsPlayerNode(float worldRadius = 1.5f)
    {
        type = AnchorType.PlayerNode;

        // 先设视觉缩放，再算碰撞半径 —— 顺序很重要：
        // col.radius 是**局部**半径，会被 localScale 缩放，
        // 必须先确定最终的 localScale 才能反算出正确的局部值。
        transform.localScale = Vector3.one * (VisualScale * 1.6f);   // 玩家节点视觉更大
        var col = GetComponent<SphereCollider>();
        if (col != null)
        {
            float s = transform.lossyScale.x;
            if (s < 0.0001f) s = 1f;
            col.radius = worldRadius / s;      // 保证世界半径 == worldRadius
        }
        // 玩家节点用醒目颜色，一眼能看出哪些是自己建的
        SetColor(new Color(0.4f, 1f, 0.5f));
        gameObject.name = "PlayerNode_" + position;
    }

    public void SetColor(Color c)
    {
        var mr = GetComponent<MeshRenderer>();
        if (!mr) return;
        // 仍在用共享材质时，先克隆一份专属的再改色
        if (mr.sharedMaterial == BaseMat)
            mr.material = new Material(BaseMat);
        if (mr.material != null) mr.material.color = c;
    }

    public void SetHighlight(bool on)
        => SetColor(on ? Color.green : new Color(0.9f, 0.75f, 0.2f));

    /// <summary>
    /// 让锚点在**远处也看得见**（自发光材质）。
    ///
    /// 【为什么需要】参考《消逝的光芒2》：它的可钩点在远处就有明显的
    /// **黄色高亮**，玩家能提前规划路线。我们原本的锚点是不发光的黄色小球，
    /// 在 100³网格里远处几乎看不见 -> 玩家不知道「那个能钩」，
    /// 只能靠自动瞄准黑箱式地选点。
    ///
    /// 这与参考笔记里「地标要可见」「功能可供性」是同一类问题：
    /// 可交互物必须**看起来可交互**。
    ///
    /// 用 Emission 而非改BaseColor：自发光不受场景光照影响，
    /// 在暗处/远处依然醒目，且不会影响本体颜色的辨识。
    /// </summary>
    public void SetEmissive(Color c)
    {
        var mr = GetComponent<MeshRenderer>();
        if (!mr) return;

        // 共享材质时先克隆，避免把颜色传染给所有锚点
        if (mr.sharedMaterial == BaseMat)
            mr.material = new Material(BaseMat);

        var mat = mr.material;
        if (mat == null) return;

        // URP 用 _EmissionColor，标准管线用 _Emission
        if (mat.HasProperty("_EmissionColor"))
        {
            mat.EnableKeyword("_EMISSION");
            mat.SetColor("_EmissionColor", c);
            // 同时开启发光关键字，否则 URP 下设置了也不生效
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        }
        else if (mat.HasProperty("_Emission"))
        {
            mat.EnableKeyword("_EMISSION");
            mat.SetColor("_Emission", c);
        }
    }

    /// <summary>锚点是否仍然存活（未被销毁）。Unity 的 C# null 判定
    /// 对已Destroy 的 UnityEngine.Object 仍返回 true，需用此属性判断。</summary>
    public bool AnchorAlive => this != null;
}

/* ===================== 丝线段 ===================== */
public class SilkSegment : MonoBehaviour
{
    public AnchorPoint from;
    public AnchorPoint to;
    public SilkLine parentLine;
    public SilkState state = SilkState.Intact;
    public float tension = 1f;

    public bool isFreeEnd = false;
    public bool pinnedAtFrom = true;
    public bool noSag = false;   // 约束链上的段：弧度由物理产生，不再叠加中点下垂

    [Header("垂落形态（家里蛛网的下垂感）")]
    [Tooltip("垂度占跨度的比例。真实蛛网约 0.05~0.10，0.07 是较自然的值")]
    [Range(0f, 0.25f)] public float sagRatio = 0.07f;

    [Tooltip("渲染采样段数。越大弧线越平滑，3 就够看出弧度，8 接近丝质")]
    [Range(2, 16)] public int sagSegments = 6;

    [Header("结构角色（静止观感的层次来源）")]
    [Tooltip("Radii 主丝绷紧承重 / Spiral 辅丝松垂装饰。混合两者网才有层次")]
    public SilkRole role = SilkRole.Spiral;

    [Tooltip("粗细倍率。真实蛛网主丝约为辅丝的 2~3 倍")]
    [Range(0.2f, 3f)] public float widthScale = 1f;

    [Tooltip("颜色微差异：老丝偏黄、沾灰。0=纯白 1=明显泛黄")]
    [Range(0f, 1f)] public float ageTint = 0f;

    LineRenderer lr;
    float fadeTimer;
    /* 被 SilkChain 接管渲染时为 true —— 本段不再自己画，
     * 顶点由 SilkChain 每帧写入。这是「一根线只有一个渲染者」的关键。*/
    public bool chainDriven = false;

    bool visible = true;

    /// <summary>显示/隐藏本段的渲染。同一根线只能有一个渲染来源，
    /// 否则会看到「一条悬链线 + 一条折线」的重影。</summary>
    /// <summary>本段是否应该由自己画线。
    /// 两个标记都要满足才画：
    ///   visible    —— 显式隐藏（如清场）
    ///   !chainDriven —— 没被 SilkChain 接管渲染
    /// </summary>
    public bool OwnsRender => visible && !chainDriven;

    public void SetVisible(bool on)
    {
        visible = on;
        // 同步 LineRenderer.enabled —— 光改 visible 不够，
        // 因为 chainDriven 期间 lr.enabled 可能是 false 状态残留
        if (lr != null) lr.enabled = OwnsRender;
    }

    /// <summary>解除渲染接管：把自己还给自己画。
    /// 断裂时必须对**所有**残留段调用 —— 否则被误标chainDriven 的段
    /// 会永远停在最后一帧的顶点上，变成一根僵死的「残影线」。
    /// 这正是「断裂后看到两条长度不一的线」的成因。</summary>
    public void ClearRenderClaim()
    {
        chainDriven = false;
        if (lr != null) lr.enabled = visible;
    }

    // 钟摆物理
    Vector3 freePendulumVel;
    bool freePendulumInited;

    static readonly Color[] BaseColor = new Color[]
    {
        new Color(1f, 1f, 1f),
        new Color(1f, 0.85f, 0.1f),
        new Color(1f, 0.2f, 0.1f),
    };

    void Awake()
    {
        lr = gameObject.AddComponent<LineRenderer>();
        Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) sh = Shader.Find("Sprites/Default");
        if (sh == null) sh = Shader.Find("Hidden/InternalErrorShader");
        lr.material = new Material(sh);

        // 真实蛛丝：两端细、中段略粗。
        // 关键约束：LineRenderer 在亚像素宽度下**不渲染**，会断裂成虚线。
        // 相机距网格约 190 格、画面 1000px 时 1 格 ≈ 5px，
        // 故最细处必须 ≥ 0.2 格（≈1px）。上一版调到 0.006 格导致整段消失。
        lr.widthCurve = new AnimationCurve(
            new Keyframe(0f, 0.22f),
            new Keyframe(0.15f, 0.42f),
            new Keyframe(0.5f, 0.55f),
            new Keyframe(0.85f, 0.42f),
            new Keyframe(1f, 0.22f)
        );
        lr.widthMultiplier = 1.0f;
        lr.numCornerVertices = 2;
        lr.numCapVertices = 2;
        lr.positionCount = 2;
        lr.useWorldSpace = true;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        lr.generateLightingData = true;

        ApplyRoleStyle();
    }

    /// 按结构角色与老化程度应用粗细与颜色。
    /// 静止观感的层次全靠这个 —— 193 根丝线参数全同，网就没有结构感。
    public void ApplyRoleStyle()
    {
        if (lr == null) return;

        /* 主丝粗、辅丝细。真实蛛网这个比例约 2~3 倍。
         * 下限受「亚像素不渲染」约束：辅丝 0.9 × 曲线最细 0.22 = 0.198 格，
         * 相机距 190 格时 1 格 ≈ 5px，即 0.99px —— 刚好在 1px 临界。
         * 再细（上一版 0.7 → 0.154 格 ≈ 0.77px）就会断裂成虚线。 */
        float roleWidth = role == SilkRole.Radii ? 1.8f : 0.9f;
        lr.widthMultiplier = roleWidth * widthScale;

        // 主丝偏白（新鲜、承重），辅丝略暗（细密、积灰）
        float baseV = role == SilkRole.Radii ? 0.95f : 0.88f;
        // ageTint 让老丝泛黄：R 降、B 降、G 基本不变
        float t = Mathf.Clamp01(ageTint);
        lr.material.color = new Color(
            Mathf.Lerp(baseV, 0.82f, t),
            Mathf.Lerp(baseV * 0.98f, 0.78f, t),
            Mathf.Lerp(baseV * 0.95f, 0.62f, t),
            0.85f);
    }

    public void StartFade()
    {
        state = SilkState.Fading;
        fadeTimer = 0f;
    }

    void Update()
    {
        if (from == null || to == null) { Object.Destroy(gameObject); return; }
        if (lr == null) { Object.Destroy(gameObject); return; }
        // 渲染已交给 SilkChain 时，本段完全不做事（不写顶点、不跑物理）
        if (!visible || chainDriven) return;

        // 自由端：纯重力自然掉落（Z 轴负方向）+ 绳长约束
        if (isFreeEnd && state == SilkState.Intact)
        {
            AnchorPoint free = pinnedAtFrom ? to : from;
            AnchorPoint pinned = pinnedAtFrom ? from : to;
            Vector3 pivot = pinned.WorldPosition;

            Vector3 toFree = free.WorldPosition - pivot;
            float ropeLen = toFree.magnitude;
            if (ropeLen < 0.01f) ropeLen = 0.01f;

            // 重力：Z 轴负方向
            Vector3 gravity = new Vector3(0, 0, -15f);

            // 切线加速度 = 重力 - 沿绳方向分量
            Vector3 tangentAcc = gravity - Vector3.Project(gravity, toFree.normalized);

            // 积分
            if (!freePendulumInited)
            {
                freePendulumVel = Vector3.zero; // 无初速度
                freePendulumInited = true;
            }

            freePendulumVel += tangentAcc * Time.deltaTime;

            // 轻微阻尼
            freePendulumVel *= 0.99f;

            // 更新位置
            Vector3 newPos = free.WorldPosition + freePendulumVel * Time.deltaTime;

            // 绳长约束
            Vector3 newToFree = newPos - pivot;
            float newDist = newToFree.magnitude;
            if (newDist > ropeLen)
            {
                newPos = pivot + newToFree.normalized * ropeLen;
            }

            free.transform.position = newPos;
        }

        if (state == SilkState.Fading)
        {
            fadeTimer += Time.deltaTime;
            float a = 1f - fadeTimer / 2.5f;
            transform.position += (new Vector3(0, 0, -0.4f) + new Vector3(
                Mathf.Sin(fadeTimer * 3f) * 0.15f, 0,
                Mathf.Cos(fadeTimer * 2.5f) * 0.15f)) * Time.deltaTime;
            if (lr.material != null)
            {
                Color c = lr.material.color;
                c.a = Mathf.Clamp01(a);
                lr.material.color = c;
            }
            if (a <= 0f) Object.Destroy(gameObject);
            return;
        }

        RenderSagCurve();
    }

    /* ============ 悬链线下垂 ============
     * 之前是 3 点直线 V 悬，中点固定下垂 (1-tension)*0.7 格。
     * 在跨度 90 的场景里这只有 0.35 格（跨度的 0.4%），肉眼看不出弯，
     * 且中点折角尖锐 —— 与真实蛛网的平缓弧线差 1~2 个数量级。
     *
     * 现改为按跨度比例下垂：sag = span * sagRatio * tensionFactor，
     * 默认 sagRatio = 0.07（真实蛛网约 0.05~0.10），
     * 用悬链线 y = catA*(cosh(x/catA) - 1) 采样成多点折线。
     * y 为负值，叠加时 +Vector3.forward * y 即向下（-Z）垂。
     *
     * 参考 Blender 蛛网模拟（BV18spMzmEJW）作者原话：
     *   "now this isn't really tension because it's just getting the
     *    average position ... so it's close enough to tension to where
     *    you can't really notice too much of a difference"
     * —— 用邻域平均位置近似张力，比硬物理约束更稳定，也更接近观感。
     *    另提到 "the stiffness will go down over time, as it ages it
     *    contracts"，说明真实蛛网随时间变松变收缩（暂未实现）。
     */
    void RenderSagCurve()
    {
        Vector3 a = from.WorldPosition;
        Vector3 b = to.WorldPosition;
        int n = Mathf.Max(2, sagSegments);

        if (state != SilkState.Intact || isFreeEnd || noSag)
        {
            // 不需要下垂：直连
            lr.positionCount = 2;
            lr.SetPosition(0, a);
            lr.SetPosition(1, b);
            return;
        }

        Vector3 span = b - a;
        float dist = span.magnitude;
        if (dist < 0.01f)
        {
            lr.positionCount = 2;
            lr.SetPosition(0, a);
            lr.SetPosition(1, b);
            return;
        }

        // 垂度 = 跨度 * sagRatio * tensionFactor
        // sagRatio 直接代表最终垂度比例（真实蛛网 0.05~0.10），
        // tensionFactor 只做 ±30% 的松紧调制，避免把垂度整体压到目标区间以下
        // （之前用 (1-tension) 直接乘，tension=Random(0.5,1) 会把 7% 压到 0.7~3.5%）
        float tensionFactor = Mathf.Lerp(1.3f, 0.7f, Mathf.Clamp01(tension));
        float ratio = sagRatio * tensionFactor;
        // 水平对齐的丝线（如左右面同高度的横丝）不该下垂
        if (Mathf.Abs(Vector3.Dot(span.normalized, Vector3.forward)) > 0.999f) ratio = 0f;

        float sag = dist * ratio;
        if (sag < 0.01f)
        {
            lr.positionCount = 2;
            lr.SetPosition(0, a);
            lr.SetPosition(1, b);
            return;
        }

        // 解悬链线参数 catA：sag = catA*(cosh(half/catA) - 1)
        // half 取半跨度。Newton 迭代，catA 初值 = half（对应 parabola 近似）。
        // Python 验算：ratio 0.03~0.15 区间误差 0.0000
        float half = dist * 0.5f;
        float catA = half;
        for (int it = 0; it < 12; it++)
        {
            float x = half / Mathf.Max(catA, 0.0001f);
            float ch = SilkChain.Cosh(x);
            float f = catA * (ch - 1f) - sag;
            float d = (ch - 1f) - x * SilkChain.Sinh(x);
            if (Mathf.Abs(d) < 0.0001f) break;
            catA -= f / d;
            catA = Mathf.Clamp(catA, half * 0.05f, half * 20f);
        }
        // 数值兜底：迭代不收敛时退回抛物线近似，保证不出现 NaN
        if (float.IsNaN(catA) || float.IsInfinity(catA)) catA = half;

        lr.positionCount = n + 1;
        for (int i = 0; i <= n; i++)
        {
            float t = i / (float)n;
            Vector3 p = Vector3.Lerp(a, b, t);
            if (i > 0 && i < n)
            {
                // 悬链线在竖直方向的偏移。cosh 在两端斜率不为 0，保证与端点自然衔接。
                // x0 最大为 half，half/catA 在 ratio=0.03 时约 0.12（安全）；
                // 但 catA 被 clamp 到 half*0.05 时 x 可达 20，cosh(20)≈2.4e8 接近 float 上限，
                // 故对 x 做上限保护，超出则该点贴到端点高度（视觉上仍是平滑弧）。
                float x0 = (t - 0.5f) * 2f * half;     // -half .. +half
                float x = Mathf.Clamp(x0 / catA, -12f, 12f);
                float y = catA * (SilkChain.Cosh(x) - 1f) - sag;
                if (!float.IsNaN(y) && !float.IsInfinity(y))
                    p += Vector3.forward * y;
            }
            lr.SetPosition(i, p);
        }
    }
}

/* ===================== 逻辑丝线 ===================== */
public class SilkLine
{
    public AnchorPoint rootFrom;
    public AnchorPoint rootTo;
    public SilkColor color;
    public BreakMode breakMode = BreakMode.Middle;

    /* 单一状态字段。行为完全由它决定 ——
     * 不再有 state/isFreeEnd/noSag 三个独立标记互相矛盾的问题。
     * hasBroken 保留为只读派生属性（兼容现有调用点）。 */
    public SilkLifeState life = SilkLifeState.Static;
    public bool hasBroken => life != SilkLifeState.Static;

    public List<SilkSegment> segments = new();
    public Vector3 breakPoint;
    public SilkChain chain;        // 非 Static 时接管运动的约束链
    public GameObject chainGO;     // 约束链的根 GameObject
    public Transform attached;     // Anchored/Swinging 时末端的挂点位置
    public SilkAttachable attachedBody;  // 挂载物本体（实现了接口的，可为 null）

    public SilkLine(AnchorPoint a, AnchorPoint b, SilkColor c, BreakMode mode = BreakMode.Middle)
    {
        rootFrom = a; rootTo = b; color = c; breakMode = mode;
        ComputeBreakPoint();
    }

    /// <summary>
    /// 唯一的状态修改入口。所有非法转换一律拒绝并警告 ——
    /// 这样「不该动的动了」「该断的不断」在源头就被拦住，
    /// 而不是等到画面上出现双重积分才发现。
    /// </summary>
    public bool TryTransition(SilkLifeState next, string reason = "")
    {
        if (life == next) return true;
        if (!IsTransitionAllowed(life, next))
        {
            Debug.LogWarning("[SilkLine] 拒绝非法状态转换 " + life + " → " + next +
                             (string.IsNullOrEmpty(reason) ? "" : "（" + reason + "）") +
                             "。这通常意味着逻辑漏了守卫。");
            return false;
        }
        life = next;
        return true;
    }

    static bool IsTransitionAllowed(SilkLifeState from, SilkLifeState to)
    {
        // Fading 是终态，不可离开
        if (from == SilkLifeState.Fading) return false;
        // 任何非 Fading 都可以开始淡出
        if (to == SilkLifeState.Fading) return true;
        // 不能「回到静止」—— 丝线一旦动过就不会再变静态
        if (to == SilkLifeState.Static) return false;

        switch (from)
        {
            case SilkLifeState.Static:
                // 静止的线可以断裂（→ FreeFall），也可以被抓住（→ Anchored）
                return to == SilkLifeState.FreeFall || to == SilkLifeState.Anchored;
            case SilkLifeState.Anchored:
                // 挂着时可施力摆动，也可松手脱手
                return to == SilkLifeState.Swinging || to == SilkLifeState.FreeFall;
            case SilkLifeState.Swinging:
                // 摆动中只能松手，不能被再次划断（避免双重断裂）
                return to == SilkLifeState.FreeFall;
            case SilkLifeState.FreeFall:
                // 脱手后只能淡出，不能再挂回去
                return false;
        }
        return false;
    }

    /// <summary>是否允许在当前状态下被划断。只有静止/挂着时可以。</summary>
    public bool CanBeCut => life == SilkLifeState.Static || life == SilkLifeState.Anchored;

    /// <summary>是否需要跑物理。只有非 Static 才有约束链。</summary>
    public bool NeedsPhysics => life != SilkLifeState.Static && chain != null;

    /* ============ 状态机公共 API（明天跑酷直接调） ============ */

    /// <summary>
    /// 挂载任意物体到丝线末端 —— 玩家、敌人、箱子、道具都行。
    /// 只需实现 SilkAttachable 接口（提供挂点 Transform）。
    /// 这是 SpiderShot 一类能力的扩展点：粘住什么就拉什么。
    /// </summary>
    public bool Attach(SilkAttachable target, SilkBuilder builder)
    {
        if (target == null || target.AttachPoint == null) return false;
        if (!TryTransition(SilkLifeState.Anchored, "挂载 " + target.name)) return false;

        attachedBody = target;
        attached = target.AttachPoint;
        EnsureChain(builder);
        // 挂载后把末端节点对齐到挂点并交给它驱动，
        // 避免第一帧从墙上「跳」到物体
        if (chain != null)
        {
            chain.SnapEndTo(attached.position);
            chain.DriveEndTo(attached);
        }
        return true;
    }

    /// <summary>简化版：直接挂一个 Transform（无需实现接口）。</summary>
    public bool Attach(Transform point, SilkBuilder builder, string label = "物体")
    {
        if (point == null) return false;
        if (!TryTransition(SilkLifeState.Anchored, "挂载 " + label)) return false;

        attachedBody = null;
        attached = point;
        EnsureChain(builder);
        if (chain != null)
        {
            chain.SnapEndTo(attached.position);
            // 末端交给挂载物驱动：丝线不再决定末端位置，改为跟随物体
            chain.DriveEndTo(attached);
        }
        return true;
    }

    /// <summary>抓住丝线：末端挂上玩家，链条转为 Anchored。</summary>
    public bool Grab(Transform player, SilkBuilder builder)
        => Attach(player, builder, "玩家");

    /// <summary>
    /// 把自己（球）作为**载荷**挂到丝线末端，但**不锁死末端**。
    ///
    /// 【与 Attach 的区别 —— 这是钩爪摆荡能成立的关键】
    ///   Attach(target)：末端 Drive 到 target.transform ——末端被钉死，
    ///                   丝线不会摆动。适合「粘在墙上」。
    ///   AttachSelf()：  末端自由，由约束求解决定位置。
    ///                   球每帧跟随末端（见 UpdateSwing），
    ///                   玩家施力改变摆动方向 —— 这才是摆荡。
    ///
    /// 【为什么需要它】用户反馈「钩爪莫名其妙连上又断开」，
    /// 根因就是用了 Attach 把末端钉死：线是「连上」了，但球
    /// 与末端位置脱钩，看起来就像断开了。
    /// </summary>
    public bool AttachSelf(SilkBuilder builder, string label = "玩家")
    {
        if (builder == null) return false;
        if (!TryTransition(SilkLifeState.Anchored, "挂载 " + label)) return false;

        attachedBody = null;

        /* 先建链，再解链。
         *
         * 【踩过的坑】原先写成直接改`endTarget = null; endDriven = false;`，
         * 但那是 **SilkChain 的字段**，而这段代码在 SilkLine 类里 ——
         * 跨类直接访问别人的私有字段编译不过（CS0103）。
         * 而且顺序也错了：EnsureChain 可能新建一条链（默认就是自由末端），
         * 若在它之前设置会被新建覆盖。*/
        EnsureChain(builder);

        // 每次新的摆荡都允许再收缩一次
        ResetReelIn();

        // 通过 chain 解引用：末端不 Drive，让它自由摆动。
        // 这与 Attach 的唯一区别。
        if (chain != null)
        {
            chain.DriveEndTo(null);   // 传 null 即解除驱动
        }
        return true;
    }

    /// <summary>挂到已有的锚点上（复用 CreateAnchorAt 建好的点）。
    /// 避免为了拿一个 Transform 而多建一个重复锚点。</summary>
    public bool Attach(AnchorPoint anchor, SilkBuilder builder, string label = "锚点")
    {
        if (anchor == null) return false;
        return Attach(anchor.transform, builder, label);
    }

    /// <summary>开始施力摆动。明天按 W/S/A/D 时调用。</summary>
    public bool StartSwing()
        => TryTransition(SilkLifeState.Swinging, "施力");

    /// <summary>松手：末端脱钩，转入自然下坠。</summary>
    public bool Release()
    {
        if (!TryTransition(SilkLifeState.FreeFall, "松手")) return false;
        // 末端交还物理：取消驱动后由重力 + 阻尼接管，
        // 此时末端速度就是玩家松手那一刻的真实速度
        if (chain != null) chain.DriveEndTo(null);
        attached = null;
        attachedBody = null;      // 脱钩，末端不再挂物体
        return true;
    }

    /// <summary>确保约束链存在。Anchored/Swinging/FreeFall 都需要它来跑物理。</summary>
    public void EnsureChain(SilkBuilder builder)
    {
        if (chain != null || builder == null) return;
        // 用当前两端点直接建链：抓住时原丝线可能还没断
        Vector3 a = rootFrom.WorldPosition;
        Vector3 b = rootTo.WorldPosition;
        if (Vector3.Distance(a, b) < 0.001f) return;

        chainGO = new GameObject("SilkChain_" + rootFrom.position + "_" + rootTo.position);
        chainGO.transform.SetParent(builder.transform);
        chain = chainGO.AddComponent<SilkChain>();
        chain.damping = 0.995f;
        chain.gravity = SilkPhysics.Gravity;   // 统一来源，避免与玩家重力不一致
        chain.subdivisions = 8;

        /* 参数必须与 SplitSegment 保持一致，否则同一根线在
         * 「抓住」和「断裂」两条路径下形态不同 —— 用户会看到
         * 抓住时线突然绷直、断裂时线保持弧度。
         * 抓住时线还没被破坏，所以沿用当前段的弧度与松弛系数。*/
        var seg0 = segments.Count > 0 ? segments[0] : null;
        chain.catenarySag = seg0 != null ? seg0.sagRatio : 0.07f;
        chain.slackScale = 1.15f;
        chain.maxStrain = 0.25f;

        /* ★ 建链顺序：锚点在前，球在后。
         *
         * 【为什么必须这样】SilkChain.Build(highAnchor, breakNode, ...) 的
         * 第二个参数是**末端**（自由端），GetEndPosition() 读的就是它。
         *
         * 本方法原先写成 Build(rootFrom, rootTo) —— 末端成了**锚点**。
         * 而 UpdateSwing 里球跟随 GetEndPosition()，于是：
         *     球瞬移到锚点位置 -> 再从这个位置建新线 -> 又是新锚点
         * 这正是用户反馈的「小球瞬移到锚点位置，并在新旧位置间建线」。
         *
         * 正确的物理关系（摆荡）：
         *     锚点（固定端，高处）
         *       └─ 丝线（约束链）
         *          └─ 球（自由端，挂在下面荡）
         * 所以固定端=锚点、末端=球。
         *
         * 注意与 SplitSegment 的区别：那里 Build(high, node) 的末端是
         * 「断点」——断裂产生的自由端，本来就该垂下来摆。
         * 两处语义不同，顺序也不同，别混用。*/
        chain.Build(rootTo, rootFrom, this, 1f);
        /* 渲染归属显式指定。
         *
         * Build() 不再自动认领 segments[0]（那会误伤别的段），
         * 所以这里必须明确交给「离玩家最近的那一段」——
         * 由 SilkSegment 自己算出弧线中点，取离末端最近的那个。
         * 找不到就干脆不接管渲染（线保持静态弧线，不会重影）。*/
        var segForRender = PickNearestSegmentTo(rootTo.WorldPosition);
        if (segForRender != null)
        {
            segForRender.noSag = true;      // 弧度交给约束链算，别再叠加中点下垂
            chain.SetRenderSegment(segForRender);
        }
    }

    /// <summary>取离指定世界坐标最近的一段。用于决定约束链接管哪一段的渲染。</summary>
    SilkSegment PickNearestSegmentTo(Vector3 worldPoint)
    {
        SilkSegment best = null;
        float bestD = float.MaxValue;
        foreach (var seg in segments)
        {
            if (seg == null) continue;
            float d = PointToSegment(worldPoint,
                                     seg.from.WorldPosition, seg.to.WorldPosition);
            if (d < bestD) { bestD = d; best = seg; }
        }
        return best;
    }

    /// <summary>把丝线恢复到静态状态：销毁约束链、解除渲染接管、
    /// 交回给 SilkSegment 的悬链线渲染。</summary>
    public void RestoreStatic()
    {
        // 1. 先解除渲染接管，否则约束链销毁后没人写顶点，线会僵住
        foreach (var seg in segments)
            if (seg != null)
            {
                seg.ClearRenderClaim();
                seg.noSag = false;        // 静态悬链线要恢复自己的下垂
                seg.SetVisible(true);
            }

        // 2. 销毁约束链（含 ChainNode 子物件）
        if (chain != null)
        {
            chain.DriveEndTo(null);
            if (chainGO != null) Object.Destroy(chainGO);
            else Object.Destroy(chain.gameObject);
        }
        chain = null;
        chainGO = null;
        attached = null;
        attachedBody = null;
        life = SilkLifeState.Static;
    }

    /// <summary>让静态悬链线重新可见（链条销毁时用）。</summary>
    public void RestoreStaticRender()
    {
        foreach (var seg in segments)
            if (seg != null) seg.SetVisible(true);
    }

    void ComputeBreakPoint()
    {
        float t = 0.5f;
        switch (breakMode)
        {
            case BreakMode.Middle:
                {
                    // 按 Z 排序，low = 低处，high = 高处
                    float zFrom = rootFrom.WorldPosition.z;
                    float zTo = rootTo.WorldPosition.z;
                    Vector3 high, low;
                    if (zFrom > zTo)
                    {
                        high = rootFrom.WorldPosition;
                        low = rootTo.WorldPosition;
                    }
                    else
                    {
                        high = rootTo.WorldPosition;
                        low = rootFrom.WorldPosition;
                    }
                    // 从低处往高处算 0.25~0.5
                    float breakT = Random.Range(0.25f, 0.5f);
                    breakPoint = Vector3.Lerp(low, high, breakT);
                    return;
                }
            case BreakMode.Quarter: t = 0.25f; break;
            case BreakMode.ThreeQuarter: t = 0.75f; break;
            case BreakMode.SpiderShot: t = 0.1f; break;
        }
        breakPoint = Vector3.Lerp(rootFrom.WorldPosition, rootTo.WorldPosition, t);
    }

    public bool BreakAtPresetPoint(SilkBuilder builder)
        => Break(builder, breakPoint, null);

    /// <summary>信号驱动的断裂入口。signal 为 null 时按预设断点、零初速度。</summary>
    public bool Break(SilkBuilder builder, Vector3 point, SilkBreakSignal signal)
    {
        // 统一状态守卫：只有静止/挂着的线可被划断
        if (!CanBeCut) return false;

        SilkSegment target = null;
        float bestD = float.MaxValue;
        foreach (var seg in segments)
        {
            if (seg == null) continue;
            float d = PointToSegment(point, seg.from.WorldPosition, seg.to.WorldPosition);
            if (d < bestD) { bestD = d; target = seg; }
        }
        if (target == null) return false;

        Vector3 vel = Vector3.zero;
        float boost = 1.6f;
        bool inject = false;
        if (signal != null)
        {
            vel = signal.initialVelocity;
            boost = signal.tipBoost;
            inject = signal.injectVelocity;
        }

        // 守卫：只有静止/挂着的线可被划断。
        // 摆动中或已脱手的再划一次会被拒绝 —— 防止二次断裂造成双重断裂。
        if (!CanBeCut) return false;

        SplitSegment(target, point, builder, vel, boost, inject);
        // 划断后统一转入 FreeFall（脱手下坠）。
        // 若失败说明状态不允许，此时已被 TryTransition 警告过。
        if (!TryTransition(SilkLifeState.FreeFall, "被划断"))
            Debug.LogWarning("[SilkLine] 划断后状态转换失败：" + life);
        return true;
    }

    /// <summary>按信号解析出断点世界坐标，然后断裂。</summary>
    public bool BreakBySignal(SilkBuilder builder, SilkBreakSignal signal)
    {
        if (signal == null) return false;
        // 状态守卫：摆动中/已脱手的线不再响应划断
        if (!CanBeCut) return false;

        Vector3 point;
        switch (signal.pointMode)
        {
            case SilkBreakPointMode.WorldPoint:
                point = signal.worldPoint;
                break;
            case SilkBreakPointMode.Normalized:
                {
                    // 从低处往高处算 normalizedT，与 ComputeBreakPoint 的约定一致
                    Vector3 a = rootFrom.WorldPosition, b = rootTo.WorldPosition;
                    Vector3 low = a.z <= b.z ? a : b;
                    Vector3 high = a.z <= b.z ? b : a;
                    point = Vector3.Lerp(low, high, signal.normalizedT);
                    break;
                }
            default: // Preset
                point = breakPoint;
                break;
        }

        return Break(builder, point, signal);
    }

    /// <summary>
    /// 把丝线从断点切开，保留「高处 → 断点」这半截交给 SilkChain。
    /// 末尾的初速度参数为明天跑酷预留：现在默认为零（自然摆动），
    /// 明天由 SilkBreakSignal.WithVelocity 注入。
    /// </summary>
    void SplitSegment(SilkSegment oldSeg, Vector3 bp, SilkBuilder builder,
                       Vector3 initialVelocity, float tipBoost, bool injectVelocity)
    {
        var grid = builder.grid;

        // 判定高处 / 低处：以世界 Z 为准，与重力方向一致
        bool fromIsHigh = oldSeg.from.WorldPosition.z >= oldSeg.to.WorldPosition.z;
        AnchorPoint high = fromIsHigh ? oldSeg.from : oldSeg.to;

        // 断点节点
        var midGO = new GameObject("BreakNode");
        midGO.transform.SetParent(builder.transform);
        var node = midGO.AddComponent<AnchorPoint>();
        node.Setup(bp, grid.WorldToVoxel(bp), AnchorType.SilkNode);
        node.SetColor(Color.white);

        /* 先解除**所有**段的渲染接管。
         *
         * 这是「断裂后两条线」的关键修复点。旧实现在这里只对 oldSeg 设
         * chainDriven = true，但 oldSeg 马上被销毁；而同一根线的其它段
         * 可能早先被约束链误标过 chainDriven（见 EnsureChain 的历史 bug），
         * 那些段没人写顶点、自身Update 又已return —— 于是一根根僵死的
         * 弧线留在屏幕上，长度各不相同，看起来就是「两条长度不一的线」。
         * 全部ClearRenderClaim() 让它们恢复自绘。*/
        foreach (var s in segments)
            if (s != null) s.ClearRenderClaim();
        segments.Remove(oldSeg);

        /* 断掉可能存在的旧约束链。
         *
         * 关键：这根线之前可能已经 Attach 过 -> EnsureChain 建过链 A。
         * 断裂时如果直接 chainGO = new GameObject(...)，
         * 链 A 的 GameObject 会**泄漏**，它仍在运行、
         * 仍每帧往同一条 LineRenderer 写顶点 —— 结果两条弧度不同
         * 的线叠在一起（用户实测：断裂后长度不一样、下面挂两个球）。
         *
         * 注意顺序：先 enabled=false 立即停掉它的 Update，
         * 再 Destroy —— Object.Destroy 要到帧末才生效，
         * 若只 Destroy，本帧两条链仍会各写一次顶点。*/
        if (chain != null) chain.enabled = false;
        if (chainGO != null) Object.Destroy(chainGO);
        else if (chain != null) Object.Destroy(chain.gameObject);
        chain = null;
        chainGO = null;

        // 上半截：保留为约束链，由 SilkChain 驱动自然摆动
        // 下半截：按需求直接丢弃，不创建任何段
        chainGO = new GameObject("SilkChain_" + rootFrom.position + "_" + rootTo.position);
        chainGO.transform.SetParent(builder.transform);
        chain = chainGO.AddComponent<SilkChain>();
        chain.damping = 0.995f;      // 原 0.985 半衰期仅 0.76s，摆荡 3 秒就没劲
        chain.gravity = SilkPhysics.Gravity;   // 统一来源，避免与玩家重力不一致
        chain.subdivisions = 8;      // 原 3 段只有 2 个折点，撑不起绳索的弧线甩动
        // 形态连续性：沿断裂前那条悬链线布点，而不是直线均分。
        // 这样断裂瞬间垂度不会归零，视觉上不会「弹一下」。
        chain.catenarySag = oldSeg.sagRatio;
        chain.slackScale = 1.15f;    // 略松于弧长，重力能把弧线拉直
        chain.Build(high, node, this, oldSeg.tension);

        /* 断裂后：由新链接管渲染。
         *
         * 注意不能沿用 oldSeg —— 它紧接着就被 Destroy 了，
         * renderSeg 会指向已销毁对象，ApplyNodesToRender 每帧访问它。
         * 所以新建一段专门给约束链画，旧的销毁。*/
        var renderGO = new GameObject("SegChain_" + rootFrom.position);
        renderGO.transform.SetParent(builder.transform);
        var renderSegNew = renderGO.AddComponent<SilkSegment>();
        renderSegNew.from = high;
        renderSegNew.to = node;
        renderSegNew.parentLine = this;
        renderSegNew.tension = oldSeg.tension;
        renderSegNew.noSag = true;          // 弧度交给约束链算
        renderSegNew.chainDriven = true;    // 自己不画，由 SilkChain 写顶点
        segments.Add(renderSegNew);         // 收入列表，清场时才不会漏
        chain.SetRenderSegment(renderSegNew);

        // 必须在 Build() 之后、首帧 Update 之前注入初速度。
        // Build 里 velocities 全部初始化为 0，此时施加冲量才正确；
        // 若改初始位置（污染 prevPositions）会首帧瞬移。
        if (injectVelocity && chain != null)
            chain.ApplyInitialVelocity(initialVelocity, tipBoost);

        // 旧段退场：先关渲染再销毁，避免本帧新旧两段同时被画
        oldSeg.chainDriven = true;
        if (oldSeg != null) oldSeg.SetVisible(false);
        Object.Destroy(oldSeg.gameObject);
    }

    SilkSegment CreateSegment(AnchorPoint a, AnchorPoint b, float tension, SilkBuilder builder)
    {
        var go = new GameObject("Seg");
        go.transform.SetParent(builder.transform);
        var seg = go.AddComponent<SilkSegment>();
        seg.from = a; seg.to = b; seg.parentLine = this;
        seg.tension = tension;
        segments.Add(seg);
        return seg;
    }

    static float PointToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        Vector3 ab = b - a;
        float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Vector3.Dot(ab, ab));
        return Vector3.Distance(p, a + ab * t);
    }
}

/* ===================== 断丝摆动约束链 ===================== */
/* 断裂后保留下来的「上半截」由本组件驱动：
 *   · 根节点固定在墙上（高处）
 *   · 无初速度时仅靠 Z 轴重力自然下坠（当前实现）
 *   · 显式速度积分 + 多轮距离约束，模拟真实蛛丝的甩动
 *   · 下半截在 SilkLine.SplitSegment 中已直接丢弃，不在此处处理
 *
 * 【为明日扩展预留】
 *   velocities / initialVelocity 已显式化：加初速度时只需在 Build() 中
 *   调用 ApplyInitialVelocity() 注入冲量，不要去改 prevPositions ——
 *   Verlet 的隐式速度靠位置差反推，改初始位置会导致首帧瞬移。       */
public class SilkChain : MonoBehaviour
{
    [Header("物理参数")]
    [Tooltip("重力加速度（格/秒²）。默认取全局统一值 SilkPhysics.Gravity，"
           + "保证丝线摆荡与玩家跳跃的重力一致")]
    public float gravity = SilkPhysics.Gravity;
    [Tooltip("每帧速度保留系数（60fps 下），越接近 1 摆得越久。\n"
        + "★ 这是**唯一**的速度衰减来源（帧率无关：pow(damping, dt*60)）。\n"
        + "  0.995 -> 半衰期 2.3 秒（推荐，接近标杆的「荡很久」）\n"
        + "  0.985 -> 半衰期 0.76 秒（偏短）\n"
        + "  0.95  -> 半衰期 0.23 秒（几乎荡不起来）\n"
        + "  注意：曾与 airDrag 相乘导致双重衰减、半衰期只剩 0.33 秒，\n"
        + "  已改为两者合并成一个系数。")]
    public float damping = 0.995f;

    [Tooltip("附加空气阻力（每帧的额外损失比例）。\n"
        + "与 damping **相乘**使用，故应保持很小的值。\n"
        + "0.02 + damping 0.995 时每帧合计损失 2.5% —— 半衰期仅 0.33 秒，\n"
        + "摆荡两秒就没劲。建议 0.005 ~ 0.01。")]
    public float airDrag = 0.005f;
    public float stiffness = 1f;       // 距离约束刚度，1=完全不可拉伸

    [Header("求解器")]
    public int subdivisions = 3;      // 段细分数，越大越柔软
    public int solverIterations = 8;  // 距离约束迭代次数

    /* Unity 的 Mathf 没有 Cosh / Sinh。
     * 悬链线 y = a*(cosh(x/a) - 1) 需要这两个函数，
     * 放在这里供 SilkChain 与 SilkSegment 共用。
     * x 已在调用侧 clamp 到 ±12，此处再兜一层防溢出。 */
    public static float Sinh(float x)
    {
        x = Mathf.Clamp(x, -12f, 12f);
        return (Mathf.Exp(x) - Mathf.Exp(-x)) * 0.5f;
    }

    public static float Cosh(float x)
    {
        x = Mathf.Clamp(x, -12f, 12f);
        return (Mathf.Exp(x) + Mathf.Exp(-x)) * 0.5f;
    }

    readonly List<AnchorPoint> nodes = new();

    /* 唯一的渲染者：一根 SilkSegment。
     * 它的顶点每帧由本组件写入 —— 形状完全由物理（重力/阻尼/约束）决定。
     * 旧实现是「SilkSegment 弧线 + 8 段 ChainSeg 直线」同时渲染 -> 重影。*/
    SilkSegment renderSeg;

    [Tooltip("形变上限：绳索最多能拉伸的比例，超过就绷直不再伸长。"
           + "真实蛛丝断裂伸长率约 20~30%")]
    [Range(0f, 0.5f)] public float maxStrain = 0.25f;

    /* 形变与断裂是**两件独立的事**（用户明确要求）：
     *   形变 —— 受力导致，受 maxStrain 限制，永远不会自己断
     *   断裂 —— 只由按键 / 信号触发（右键、断视线线、断自己发的线）
     *
     * 真实蛛丝拉过头会断，但本作断裂是**玩法控制**——
     * 如果做成「力太大就自己断」，玩家摆荡时会莫名断线，手感全毁。*/
    [Tooltip("拉到形变上限时是否自动断裂。本作=false：断裂只由按键/信号触发")]
    public bool breakOnOverstretch = false;
    readonly List<float> restLengths = new();
    readonly List<Vector3> velocities = new();   // 显式速度（格/秒）

    AnchorPoint root;   // 固定端（墙上）
    bool initialized = false;

    [Header("形态连续性")]
    [Tooltip("断裂瞬间沿悬链线布点时使用的垂度比例，需与断裂前 SilkSegment 的 sagRatio 一致，"
           + "否则断裂帧会有形态跳变（视觉上「弹一下」）。0=纯直线")]
    public float catenarySag = 0.07f;

    [Tooltip("松弛系数：段长 = 弧长 × 该值。>1 让绳索略长于弧线，"
           + "重力才能把弧线拉直（断裂后失去张力而展开）。1=锁死弧形，2=明显展开")]
    [Range(1f, 1.5f)] public float slackScale = 1.15f;

    /// 悬链线布点结果：节点位置 + 各段弧长
    struct CatenaryLayout
    {
        public List<Vector3> points;         // 长度 n+1，含两端
        public List<float> restLengths;      // 长度 n
    }

    /// 沿悬链线布点并按弧长分配段长。
    /// sagRatio <= 0 或求解失败时退化为直线均分。
    CatenaryLayout BuildCatenaryLayout(Vector3 a, Vector3 b, int n, float sagRatio)
    {
        var res = new CatenaryLayout();
        Vector3 span = b - a;
        float dist = span.magnitude;

        res.points = new List<Vector3>();
        res.restLengths = new List<float>();
        if (dist < 0.0001f)
        {
            for (int i = 0; i <= n; i++) res.points.Add(a);
            for (int i = 0; i < n; i++) res.restLengths.Add(0f);
            return res;
        }

        // 退化条件：垂度为 0，或丝线水平对齐（无重力方向分量）
        bool degenerate = sagRatio <= 0.0001f ||
                         Mathf.Abs(Vector3.Dot(span.normalized, Vector3.forward)) > 0.999f;

        if (!degenerate)
        {
            float sag = dist * sagRatio;
            float half = dist * 0.5f;
            float catA = half;
            for (int it = 0; it < 12; it++)
            {
                float x = half / Mathf.Max(catA, 0.0001f);
                float ch = Cosh(x);
                float f = catA * (ch - 1f) - sag;
                float d = (ch - 1f) - x * Sinh(x);
                if (Mathf.Abs(d) < 0.0001f) break;
                catA -= f / d;
                catA = Mathf.Clamp(catA, half * 0.05f, half * 20f);
            }
            if (float.IsNaN(catA) || float.IsInfinity(catA)) degenerate = true;
            else
            {
                for (int i = 0; i <= n; i++)
                {
                    float t = i / (float)n;
                    Vector3 p = Vector3.Lerp(a, b, t);
                    if (i > 0 && i < n)
                    {
                        float x0 = (t - 0.5f) * 2f * half;
                        float x = Mathf.Clamp(x0 / catA, -12f, 12f);
                        float y = catA * (Cosh(x) - 1f) - sag;
                        if (!float.IsNaN(y) && !float.IsInfinity(y))
                            p += Vector3.forward * y;
                    }
                    res.points.Add(p);
                }
                // 段长按弧长分配：悬链线上下不对称，不能等分。
                // slackScale 是「绳索比当前弧线长多少」——
                // 它决定绳索有多松（rest 越大越松、垂得越厉害），
                // 与 maxStrain（弹性上限）配合：
                //   静垂度由 slackScale 决定
                //   挂重物后能否绷直由 maxStrain 决定
                for (int i = 0; i < n; i++)
                    res.restLengths.Add(
                        (res.points[i + 1] - res.points[i]).magnitude * slackScale);
                return res;
            }
        }

        // 退化：直线均分
        for (int i = 0; i <= n; i++) res.points.Add(a + span * (i / (float)n));
        for (int i = 0; i < n; i++) res.restLengths.Add(dist / n);
        return res;
    }

    /// 由 SilkLine 调用：高处固定点 → 断点，细分并建段
    public void Build(AnchorPoint highAnchor, AnchorPoint breakNode, SilkLine owner, float tension)
    {
        root = highAnchor;

        Vector3 a = highAnchor.WorldPosition;
        Vector3 b = breakNode.WorldPosition;
        Vector3 total = b - a;
        float totalLen = total.magnitude;
        if (totalLen < 0.001f) { enabled = false; return; }

        int n = Mathf.Max(1, subdivisions);

        /* 断裂瞬间的形态连续性（静止 → 动态 的关键）：
         * 断裂前丝线是悬链线（两端固定、中部下垂），若这里按直线均分节点，
         * 垂度会在断裂帧瞬间归零，视觉上「弹一下」。
         * 因此这里沿同一条悬链线布点，并按弧长分配 restLengths，
         * 让断裂前后的形状严格连续。*/
        var layout = BuildCatenaryLayout(a, b, n, catenarySag);

        // 断点本身作为末端节点保留
        nodes.Add(highAnchor);
        for (int i = 1; i < n; i++)
        {
            // 防御：layout.points 理论上必有 n+1 个元素，但结构体默认值可能为 null
            Vector3 p = (layout.points != null && i < layout.points.Count)
                        ? layout.points[i]
                        : Vector3.Lerp(a, b, i / (float)n);
            var go = new GameObject("ChainNode_" + i);
            go.transform.SetParent(transform);
            var node = go.AddComponent<AnchorPoint>();
            node.Setup(p, highAnchor.position, AnchorType.SilkNode);
            // 内部节点不需要球体渲染，缩到极小避免视觉噪点
            node.transform.localScale = Vector3.one * 0.02f;
            node.SetColor(new Color(0.95f, 0.93f, 0.9f, 0.85f));
            // 移除碰撞体：内部节点只是物理求解的中间点，
            // 不应被左键点选命中，也不该参与 CreateAnchorAt 的距离去重
            var col = node.GetComponent<SphereCollider>();
            if (col != null) Destroy(col);
            nodes.Add(node);
        }
        nodes.Add(breakNode);

        // 段长按弧长分配：悬链线上半段更陡、下半段更平，段长不等
        if (layout.restLengths != null && layout.restLengths.Count == n)
        {
            for (int i = 0; i < n; i++) restLengths.Add(layout.restLengths[i]);
        }
        else
        {
            // 兜底：退化为等分
            float segLen = totalLen / n;
            for (int i = 0; i < nodes.Count - 1; i++) restLengths.Add(segLen);
        }

        // 显式速度初始化为 0 —— 当前是「无初速度」的自然摆动
        for (int i = 0; i < nodes.Count; i++)
            velocities.Add(Vector3.zero);

        /* 渲染责任：**不再在这里自动认领任何段**。
         *
         * 旧实现是 `renderSeg = owner.segments[0]`，这有两个致命问题：
         *   1) segments[0] 未必是被断的那一段。一根 SilkLine 可能有多段，
         *      断裂时 oldSeg 已从列表移除，segments[0] 变成**另一段完好的丝线**，
         *      于是它被误标 chainDriven —— 而本组件并不会写它的顶点，
         *      它就永远僵死在最后一帧的弧线上，变成一根长度不一的「残影线」。
         *      这就是用户反复看到「断裂后是两条线、长度还不一样」的根因。
         *   2) 自动认领让「谁渲染」这件事变得不可控。
         *
         * 现在统一由调用方显式 SetRenderSegment(段) 指定，
         * 本组件只负责往那一个段写顶点。*/
        if (owner != null)
            foreach (var s in owner.segments)
                if (s != null) s.ClearRenderClaim();

        initialized = true;
    }

    /// 明日扩展：给整条链注入初速度。
    /// 沿 root → 末端方向线性衰减（末端摆动最大），符合蛛丝甩鞭的受力分布。
    /// 调用时机必须在 Build() 之后、第一帧 Update 之前。
    /// </summary>
    public void ApplyInitialVelocity(Vector3 baseVelocity, float tipBoost = 1.6f)
    {
        if (!initialized || nodes.Count < 2) return;
        for (int i = 1; i < nodes.Count; i++)
        {
            float t = i / (float)(nodes.Count - 1);      // 0=根 1=末端
            velocities[i] = baseVelocity * Mathf.Lerp(1f, tipBoost, t);
        }
    }

    void Update()
    {
        if (!initialized || nodes.Count < 2) return;

        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        // 1. 显式速度积分：重力 + 空气阻力，根节点不参与
        //    重力沿 -Z（项目约定：Z = 高度），不是 Unity 默认的 Vector3.down
        //末节点在被外部驱动（挂载物）时不施加重力 —— 它由挂载物驱动
        int last = nodes.Count - 1;
        /* 【★ 阻尼双重衰减的修复】
         *
         * 原代码是：
         *     velocities[i] *= damping;          // 0.995
         *     velocities[i] *= dragFactor;       // 1 - 0.02*dt*60 ≈ 0.98
         * 每帧合计损失 = 1 - 0.995×0.98 ≈ 2.5%
         * -> 半衰期仅 20 帧 = **0.33 秒**。摆荡两三秒就没劲，
         *    全靠玩家泵力硬撑 —— 这正是用户反馈「摆荡太慢」的原因之一。
         *
         * 问题在于 airDrag 被当成了「每帧固定衰减率」，
         * 而 damping 也是。两个同义的衰减相乘，效果翻倍且难以调参。
         *
         * 修法：把两者合并成**一个**每秒衰减系数，按指数衰减：
         *     v *= pow(damping, dt*60)   —— 每帧等价，统一且帧率无关
         * airDrag 保留但降为「附加阻尼」，默认调小。
         */
        float dampCoef = Mathf.Pow(damping, dt * 60f) * Mathf.Max(0f, 1f - airDrag);
        for (int i = 1; i <= last; i++)
        {
            bool isDrivenEnd = (i == last && endDriven);
            if (!isDrivenEnd)
                velocities[i] += new Vector3(0, 0, -gravity * dt);
            else if (endDriven)
                velocities[i] = drivenVelocity;      // 完全交给外部（玩家输入）
            velocities[i] *= dampCoef;
            nodes[i].transform.position += velocities[i] * dt;
        }

        // 2. 多轮距离约束：从根到端依次修正，形成自然弧线
        for (int it = 0; it < solverIterations; it++)
        {
            for (int i = 0; i < last; i++)
            {
                Vector3 a = nodes[i].WorldPosition;
                Vector3 b = nodes[i + 1].WorldPosition;
                Vector3 d = b - a;
                float len = d.magnitude;
                if (len < 0.0001f) continue;

                float rest = restLengths[i];

                /* 弹性绳索：只会被拉长，不会被压短。
                 *
                 * rest = 该段的自然长度，允许拉伸到 rest*(1+maxStrain)：
                 *   len < rest        -> 松弛，不修正（重力让它自然下垂）
                 *   rest <= len <= max -> 正常拉伸，不修正（弹性范围内）
                 *   len > max         -> 拉回 max（绷直，不再伸长）
                 *
                 * 注意：形变到极限也**不会断裂** —— 断裂是独立机制，
                 * 由按键/信号触发（BreakAtPresetPoint），不由物理触发。
                 * 真实蛛丝被拉到极限会断，但本作的断裂是玩法控制，
                 * 不做成「力太大就自己断」，否则玩家会在摆荡中莫名断线。
                 */
                float maxLen = rest * (1f + maxStrain);
                float diff = 0f;
                if (len > maxLen)
                    diff = ((len - maxLen) / len) * stiffness;
                if (diff <= 0f) continue;      // 松弛或正常拉伸，无需修正

                if (i == 0)
                {
                    // 根固定：只动子节点
                    Vector3 nb = b - d * diff;
                    // 同步修正速度，避免约束把能量"吃掉"后失真
                    velocities[i + 1] = (nb - b) / dt;
                    nodes[i + 1].transform.position = nb;
                }
                else
                {
                    // 内部节点：按权重分摊修正量，保持链长
                    Vector3 na = a + d * diff * 0.5f;
                    Vector3 nb = b - d * diff * 0.5f;
                    velocities[i] += (na - a) / dt;
                    velocities[i + 1] += (nb - b) / dt;
                    nodes[i].transform.position = na;
                    nodes[i + 1].transform.position = nb;
                }
            }

            // 每轮都把根节点钉回墙上，防止数值漂移
            nodes[0].transform.position = root.WorldPosition;
            velocities[0] = Vector3.zero;
        }

        // 3. 末端被驱动时，每帧末尾把它对齐到挂载点，
        //    约束求解产生的位置修正会写回 drivenVelocity，
        //    这样玩家的输入与绳索约束形成闭环（明天跑酷的核心）。
        if (endDriven && endTarget != null)
        {
            Vector3 cur = nodes[last].WorldPosition;
            Vector3 want = endTarget.position;
            nodes[last].transform.position = want;
            drivenVelocity = (want - cur) / dt;
        }

        /* ============================================================
         *  4. ★ 最低点绳长收缩（Reel-In）
         * ============================================================
         *
         * 【为什么需要这一步 —— 对标蜘蛛侠2 / 消逝之光2】
         *
         * 纯单摆在数学上**能量守恒**：荡到最高点动能全部转成势能，
         * 再荡回来势能全部转回动能，**永远荡不高**。
         * 那为什么蜘蛛侠能越荡越高？
         *
         * 因为他在**最低点**（速度最大的瞬间）主动收绳：
         *     角动量守恒 L = m·v·r = 常数
         * 半径 r 减小 -> 线速度 v 增大：v' = v × (r / r')
         * 收缩 20% -> 速度提升 25%。这是「荡得又高又远」的真实机制。
         *
         * 【与约束求解器的关系】
         * 上面的约束求解器**只处理拉伸**（len > rest*(1+maxStrain)），
         * 明确「松弛时不修正，让重力自然下垂」。
         * 所以收缩必须在这里**独立实现** —— 通过缩短 restLengths。
         *
         * 【为什么不能直接拉末端位置】
         * 那样会凭空注入能量（位置突变，速度飙升），玩家会被甩飞。
         * 改 restLengths 是「改变绳子的自然长度」，
         * 由约束求解器温柔地把末端拉回来 —— 速度增益是物理的，不是伪造的。
         */
        ApplyReelIn(dt);

        // 5. 渲染：把物理算出的节点位置写进那一个 SilkSegment。
        //    一根线，形状完全由上面的重力/阻尼/约束决定。
        ApplyNodesToRender();
    }

    /* ---------- 最低点绳长收缩 ---------- */

    /// <summary>
    /// 荡到最低点时自动收绳，靠角动量守恒换取速度。
    ///
    /// 【★ 触发条件：垂直速度接近零】
    ///
    /// 【踩过的坑】最初用「末端到锚点的距离 / 总绳长 > 0.92」判断最低点，
    /// Python 实算证明这是**错的**：
    ///     单摆过程中绳长恒等于自然长度，dist/totalLen 永远是 1.0，
    ///     该条件在任何角度都成立 —— 等于没有条件，
    ///     球在空中乱飞时也会误触发收缩。
    ///
    /// 【正确的判据】最低点的物理特征是「速度方向水平」：
    ///     垂直分量 v.z ≈ 0（此刻重力全部用于改变速度方向，不做功）
    /// 配合「必须足够快」，才精确对应摆荡的底部。
    /// </summary>
    void ApplyReelIn(float dt)
    {
        if (reelInRatio <= 0f || nodes.Count < 2 || root == null) return;
        if (endDriven && endTarget != null) return;   // 被挂载物驱动时不干预

        int last = nodes.Count - 1;

        Vector3 v = velocities[last];

        // 条件 1：速度足够大（没有动能就无从放大）
        float speed = v.magnitude;
        if (speed < reelInMinSpeed) return;

        // 条件 2：垂直分量接近 0 —— 即「摆到了最低点」
        //         用相对阈值：|v.z| / |v| < 0.3 表示速度以水平为主
        float vRatio = speed > 0.0001f ? Mathf.Abs(v.z) / speed : 1f;
        if (vRatio > reelInMaxVerticalRatio) return;

        // 条件 3：本次滞空只收缩一次（避免连续收缩把绳缩到极短）
        if (reeledThisSwing) return;

        /* 执行收缩：按比例缩短每段 restLengths。
         *
         * 【为什么不直接拉末端位置】
         * 那样会凭空注入能量（位置突变，速度飙升），玩家会被甩飞。
         * 改 restLengths 是「改变绳子的自然长度」，
         * 由约束求解器温柔地把末端拉回来 —— 速度增益是物理的，
         * 且下一帧的 a = v²/r 会自然生效，玩家能感到「越荡越快」。
         *
         * 均匀收缩以保持弧线形状稳定，避免视觉抖动。*/
        float totalLen = 0f;
        for (int i = 0; i < restLengths.Count; i++) totalLen += restLengths[i];
        if (totalLen < 0.0001f) return;

        float target = totalLen * (1f - reelInRatio);
        float scale = target / totalLen;
        for (int i = 0; i < restLengths.Count; i++)
            restLengths[i] *= scale;

        reeledThisSwing = true;
        lastReelInAmount = reelInRatio;

        if (verboseReelInLog)
            Debug.Log("[ReelIn] 最低点收缩 " + (reelInRatio * 100f).ToString("F0") +
                      "%：" + totalLen.ToString("F1") + " → " + target.ToString("F1") +
                      " 格（末端速度 " + speed.ToString("F0") + " 格/秒，" +
                      "垂直占比 " + (vRatio * 100f).ToString("F0") + "%，" +
                      "理论新速度 ≈ " + (speed / scale).ToString("F0") + " 格/秒）");
    }

    /// <summary>起摆时重置「本次已收缩」标记 —— 每次新的摆荡都能再收缩一次。</summary>
    void ResetReelIn()
    {
        reeledThisSwing = false;
        lastReelInAmount = 0f;
    }

    /// <summary>本次滞空是否已收缩过（防止连缩到绳断）。</summary>
    bool reeledThisSwing = false;

    /// <summary>最近一次收缩的比例（供 UI/调试读取）。</summary>
    float lastReelInAmount = 0f;

    [Header("最低点收缩")]
    [Tooltip("荡到最低点时收绳的比例（0 = 关闭，0.15~0.2 推荐）。\n"
        + "原理：角动量守恒 L = m·v·r，收缩 r 会放大 v。\n"
        + "  收缩 15% -> 速度 ×1.18，周期缩短 8%\n"
        + "  收缩 20% -> 速度 ×1.25，周期缩短 11%\n"
        + "这是《蜘蛛侠2》「越荡越高」的真实机制 ——\n"
        + "纯单摆能量守恒，永远荡不高，必须靠收绳注入额外动能。\n"
        + "收缩太猛会让玩家被甩飞，建议从 0.15 试起。")]
    [Range(0f, 0.4f)] public float reelInRatio = 0.15f;

    [Tooltip("触发收缩所需的最小末端速度（格/秒）。\n"
        + "速度太低时收缩没有意义（无动能可放大），还会让绳莫名变短。")]
    public float reelInMinSpeed = 15f;

    [Tooltip("触发收缩的速度垂直占比上限（|v.z| / |v|）。\n"
        + "「摆到最低点」= 速度方向变为水平，垂直分量≈ 0。\n"
        + "  0.3 = 速度与水平夹角约 17° 以内算「到底部」（推荐）\n"
        + "  0.5 = 约 30°，触发更早，收缩更频繁\n"
        + "★ 曾用「dist/totalLen > 0.92」判断最低点，实算证明是错的 ——\n"
        + "   单摆全程 dist/totalLen 恒等于 1.0，条件形同虚设。")]
    [Range(0.05f, 0.9f)] public float reelInMaxVerticalRatio = 0.3f;

    [Tooltip("输出最低点收缩的诊断日志（调Reel-In 手感时临时开启）。\n"
        + "★ 注意：本开关属于 SilkChain 自己的字段，不能用\n"
        + "  SilkParkourController.verboseFireLog —— 那是别的类的成员，\n"
        + "  跨类访问会CS0103（编译错误）。")]
    public bool verboseReelInLog = false;

    /// <summary>把 nodes 的位置写进 LineRenderer 顶点。
    /// 只有一个渲染源，所以永远不会出现「两条线」。</summary>
    void ApplyNodesToRender()
    {
        if (renderSeg == null) return;
        var lr = renderSeg.GetComponent<LineRenderer>();
        if (lr == null) return;

        /* 自愈：渲染器必须开着。
         * 本组件是这一段的唯一渲染来源，若 lr.enabled 被别处置成 false，
         * 线就会凭空消失。这里每帧兜底保证开启 —— 这类 bug 曾反复出现。*/
        if (!lr.enabled) lr.enabled = true;

        int n = nodes.Count;
        lr.positionCount = n;
        for (int i = 0; i < n; i++)
            lr.SetPosition(i, nodes[i].WorldPosition);
    }

    /* ============ 末端驱动（跑酷挂载物扩展） ============ */

    /// <summary>指定由哪个 SilkSegment 承担渲染。
    /// 断裂时 Build 里取的 segments[0] 可能不是被断的那一段，
    /// 所以由调用方显式指定。</summary>
    public void SetRenderSegment(SilkSegment seg)
    {
        renderSeg = seg;
        if (seg == null) return;
        /* 被接管的段不再自己渲染，但要**保持 LineRenderer 开启**。
         *
         * 这里原来写的是 lr.enabled = false —— 那是错的：
         * 本组件正是要往这条LineRenderer 里写顶点（ApplyNodesToRender），
         * 关掉它等于把唯一的渲染源也关掉了，整根线会消失。
         *
         * 「不要两个渲染源」靠的是 chainDriven 标记：
         * SilkSegment.Update 开头看到 chainDriven 就return，不再自己画弧线。
         * 渲染器本身必须留着给本组件用。*/
        seg.chainDriven = true;
        var lr = seg.GetComponent<LineRenderer>();
        if (lr != null) lr.enabled = true;
    }

    /// <summary>是否已接管渲染（诊断用：重影排查要看渲染源到底有几个）</summary>
    public bool HasRenderSegment() => renderSeg != null;

    /// <summary>末端是否由外部驱动。true 时末端不受重力，由挂载物决定位置。</summary>
    public bool endDriven = false;

    /// <summary>驱动末端的挂载点（玩家手部等）。</summary>
    public Transform endTarget = null;

    /// <summary>由外部写入的末端速度（每帧 Solve 后回读）。</summary>
    public Vector3 drivenVelocity = Vector3.zero;

    /// <summary>让末端跟随某个挂载点。传 null 恢复自由摆动。</summary>
    public void DriveEndTo(Transform target)
    {
        endTarget = target;
        endDriven = target != null;
    }

    /// <summary>
    /// 读取末端当前速度（格/秒）。松手时用它给玩家初速度 ——
    /// 这是「甩出去」手感的来源：玩家离线的瞬间速度就是丝线末端的速度。
    /// </summary>
    public Vector3 GetEndVelocity()
    {
        if (nodes.Count < 2) return Vector3.zero;
        if (endDriven && endTarget != null)
        {
            // 被驱动时 drivenVelocity 由位置反馈算出，即末端真实速度
            return drivenVelocity;
        }
        return velocities[nodes.Count - 1];
    }

    /// <summary>
    /// 读取末端当前**位置**（格）。
    ///
    /// 【为什么必须有这个方法】
    /// 摆荡时玩家（球）是挂在末端的载荷，位置必须**等于**末端位置。
    /// 原实现里球只靠 `transform.position += ... * accel * dt * dt` 积分，
    /// **从不跟随末端** —— 于是球和丝线是两个互不相干的物体，
    /// 视觉上分离，看起来就是「线断了」。
    ///
    /// 被驱动时（endDriven）直接读目标位置；
    /// 自由摆动时读最后一个节点 —— 那是约束求解出来的真实位置。
    /// </summary>
    public Vector3 GetEndPosition()
    {
        if (endDriven && endTarget != null) return endTarget.position;
        if (nodes.Count < 2) return transform.position;
        // nodes 是 List<AnchorPoint>，用 WorldPosition 取实际世界坐标
        return nodes[nodes.Count - 1].WorldPosition;
    }

    /// <summary>
    /// 给末端施加一次**速度冲量**（格/秒）。
    ///
    /// 【为什么是速度冲量而不是位移】
    /// 原实现用 `transform.position += accel * dt * dt` 直接位移球，
    /// 在 dt = 0.016 时 dt² = 0.00026，实际效果微乎其微
    ///（实测 1 秒只累积 1.8 格，是目标值的 14%），泵力几乎无效。
    ///
    /// 改成对末端的速度冲量（dt 的一次项）后，每帧稳定生效。
    /// 同时语义更正确：玩家按键是「对丝线施力」，
    /// 球的位置由约束求解自然得出，而不是被硬推。
    /// </summary>
    public void AddEndVelocity(Vector3 impulse)
    {
        if (impulse.sqrMagnitude < 1e-8f) return;
        if (nodes.Count < 2) return;

        int last = nodes.Count - 1;
        // 末端被 Drive 时不该由这里改速度（下一帧会被覆盖），直接返回
        if (endDriven && endTarget != null) return;

        // 本项目用**显式速度积分**（velocities[i] += a * dt，
        // 位置 += velocities[i] * dt），不是 Verlet 的位置差。
        velocities[last] += impulse;
    }

    /// <summary>
    /// 对整条链施加一次力（跑酷的施力入口）。
    /// 沿 root→末端线性衰减注入，末端受力最大（符合甩鞭的受力分布）。
    /// 泵力（沿绳）传 force = 切线方向；横推传垂直方向。
    /// </summary>
    public void ApplyForce(Vector3 force, float tipBoost = 1.6f)
    {
        if (force.sqrMagnitude < 1e-8f) return;
        float dt = Time.deltaTime > 0f ? Time.deltaTime : 0.02f;

        if (endDriven)
        {
            // 末端被驱动时，玩家输入直接改变末端速度。
            // 但玩家本身是位置控制（有速度上限），所以这里只轻微加成，
            // 主要加速度仍来自玩家 transform 的移动。
            drivenVelocity += force * dt;
            return;
        }

        // 自由摆动时按位置权重注入到各节点
        int last = nodes.Count - 1;
        for (int i = 1; i <= last; i++)
        {
            float t = i / (float)last;
            velocities[i] += force * Mathf.Lerp(1f, tipBoost, t) * dt;
        }
    }

    /// <summary>
    /// 沿绳方向的切向单位向量（泵力用）与水平垂直方向（横推用）。
    /// 供跑酷控制器计算输入方向，外部不必自己算。
    /// </summary>
    public void GetSwingAxes(out Vector3 tangential, out Vector3 lateral)
    {
        tangential = Vector3.forward;
        lateral = Vector3.right;
        if (nodes.Count < 2) return;

        Vector3 rootPos = nodes[0].WorldPosition;
        Vector3 endPos = nodes[nodes.Count - 1].WorldPosition;
        Vector3 toEnd = endPos - rootPos;
        float len = toEnd.magnitude;
        if (len < 0.0001f) return;

        Vector3 dir = toEnd / len;
        // 重力沿 -Z（项目约定），取绳方向在水平面内的投影作为横向轴
        Vector3 gravityDir = new Vector3(0, 0, 1f);
        Vector3 tangent = Vector3.Cross(gravityDir, dir);
        if (tangent.sqrMagnitude < 0.0001f) tangent = Vector3.right;
        lateral = tangent.normalized;

        // 切向 = 与 lateral 和 dir 都垂直
        tangential = Vector3.Cross(dir, lateral).normalized;
    }

    /// <summary>把末端节点瞬移到指定位置（挂载瞬间用，避免第一帧跳变）。</summary>
    public void SnapEndTo(Vector3 worldPos)
    {
        if (nodes.Count < 2) return;
        int last = nodes.Count - 1;
        // 重算末端各段长度，保持 restLengths 与实际位置一致
        for (int i = 0; i < last; i++)
        {
            if (i + 1 == last) restLengths[i] =
                Vector3.Distance(nodes[i].WorldPosition, worldPos);
        }
        nodes[last].transform.position = worldPos;
        velocities[last] = Vector3.zero;
        drivenVelocity = Vector3.zero;
    }

    void OnDestroy()
    {
        // 只清引用 —— 渲染的那根 SilkSegment 属于 SilkLine，
        // 由 SilkLine 自己管理生命周期，约束链销毁不该连带销毁它。
        // （旧实现在这里销毁 renderSegs 里的 8 段 ChainSeg，
        //   而现在不再有那些段。）
        renderSeg = null;
    }
}

/* ===================== 空间哈希 ===================== */
public class SilkSpatialHash
{
    readonly Dictionary<Vector3Int, List<SilkSegment>> cells = new();
    readonly float cellSize;

    public SilkSpatialHash(float cell = 8f) { cellSize = cell; }
    public void Clear() => cells.Clear();

    Vector3Int Key(Vector3 p) => new Vector3Int(
        Mathf.FloorToInt(p.x / cellSize),
        Mathf.FloorToInt(p.y / cellSize),
        Mathf.FloorToInt(p.z / cellSize));

    public void Insert(SilkSegment seg)
    {
        var a = Key(seg.from.WorldPosition);
        var b = Key(seg.to.WorldPosition);
        AddToCell(a, seg);
        if (a != b) AddToCell(b, seg);
    }

    void AddToCell(Vector3Int k, SilkSegment seg)
    {
        if (!cells.TryGetValue(k, out var list))
        {
            list = new List<SilkSegment>();
            cells[k] = list;
        }
        if (!list.Contains(seg)) list.Add(seg);
    }

    public IEnumerable<SilkSegment> Query(Ray ray, float maxDist)
    {
        Vector3 ro = ray.origin;
        Vector3 rd = ray.direction;
        float t = 0f;
        HashSet<Vector3Int> visited = new HashSet<Vector3Int>();
        while (t < maxDist)
        {
            Vector3 p = ro + rd * t;
            Vector3Int k = Key(p);
            if (!visited.Contains(k))
            {
                visited.Add(k);
                if (cells.TryGetValue(k, out var list))
                    foreach (var seg in list) yield return seg;
            }
            t += cellSize * 0.5f;
        }
    }
}

/* ===================== 主构建器 ===================== */
public class SilkBuilder : MonoBehaviour
{
    public VoxelGrid grid;
    public SilkColor defaultColor = SilkColor.White;
    public LayerMask anchorLayer;
    public LayerMask wallLayer;
    public float cutMaxDistance = 500f;
    public float pickRadius = 15f;

    [Header("蛛网结构（静止观感的层次）")]
    [Tooltip("主丝（绷紧承重）占比。默认 0 = 只生成垂落丝，"
           + "垂度调大后主丝也明显下垂会与辅丝混淆。拖到 0.3 可恢复主辅分层")]
    [Range(0f, 1f)] public float primaryRatio = 0f;

    [Tooltip("底面锚点连线数量。144 个底面锚点全连会得到约 170 根总丝线，"
           + "是真实蛛网（30~60 根）的 3 倍，画面上挤成一片看不出单根形态")]
    [Range(4, 144)] public int bottomLinkCount = 36;

    readonly List<AnchorPoint> anchors = new();
    readonly List<SilkLine> silkLines = new();

    /// <summary>只读访问所有丝线。跑酷控制器用它找可抓的线。
    /// 暴露为属性而非 public 字段，外部无法直接改集合。</summary>
    public IReadOnlyList<SilkLine> Lines => silkLines;

    // 丝线去重集合。必须在 ClearAll 里同步清空 ——
    // 否则重新织网时会被上一轮的记录挡住，织不出线。
    readonly HashSet<long> lineKeys = new();

    SilkSpatialHash spatialHash;

    [Tooltip("玩家自建节点的世界空间碰撞半径。默认锚点仅 0.06 格，射线打不中，固化时必须放大才能被动态丝线粘住")]
    public float playerNodeRadius = 1.5f;

    [Tooltip("是否生成跑酷测试关卡（4 个基础区段+ 摆荡进阶区，见 SilkParkourStage）。"
           + "临时功能，删除 SilkParkourStage.cs 后请把这里也移除")]
    public bool createTestLevel = true;

    /// <summary>第三人称跑酷模式。为 true 时本组件不响应鼠标左键与 R，
    /// 避免与玩家的「发射丝线」「重置」冲突。由控制器在切模式时设置。</summary>
    public bool parkourMode = false;

    AnchorPoint firstAnchor;

    /// <summary>最近一次由「选中-连线」流程创建的丝线。
    /// Parkour 世界的发射用它接管摆荡。</summary>
    public SilkLine lastCreatedLine = null;
    bool isFirstSelected;
    LineRenderer previewLine;
    GameObject previewGO;
    Camera mainCam;
    Vector3 rightClickStartPos;
    float rightClickStartTime;

    void Awake()
    {
        /* 【实例唯一性守卫】
         *
         * 若场景里有两个 SilkBuilder，两者的 Update 都会响应同一次左键，
         * 且各自的 lineKeys 互不干扰 -> 同一对锚点会被各建一条线，
         * 视觉上就是「一次点击出现两根分开的线」，而[NewLine] 总数持续增长。
         *
         * 这里让后来者直接失效：先到者保留完整功能，后来者退场。
         * 比在两处 Build 入口加检查更稳（覆盖所有可能的创建路径）。*/
        var all = FindObjectsOfType<SilkBuilder>();
        foreach (var other in all)
        {
            if (other == null || other == this) continue;
            if (other.GetInstanceID() < GetInstanceID())
            {
                // 已存在更早的实例 -> 我是多余的，自杀
                Debug.LogWarning("[SilkBuilder] 检测到重复实例，销毁后来者 " +
                                 gameObject.name + "（保留 " + other.gameObject.name + "）");
                Destroy(gameObject);
                return;
            }
        }

        if (grid == null) grid = FindObjectOfType<VoxelGrid>();
        if (grid == null)
        {
            Debug.LogError("[SilkBuilder] VoxelGrid not found!");
            enabled = false;
            return;
        }

        mainCam = null;
        spatialHash = new SilkSpatialHash(8f);

        // 向事件总线注册各类事件的处理器。
        // 外部（玩家控制器、敌人、老化系统）只需 SilkEventBus.Post(evt)，
        // 不必认识 SilkBuilder 的任何内部 API。
        // 加新事件类型时在这里加一行 Register 即可，总线分发逻辑不动。
        SilkEventBus.Register<SilkBreakSignal>(this);
        SilkEventBus.Register<SilkGrabSignal>(this);
        SilkEventBus.Register<SilkReleaseSignal>(this);
        SilkEventBus.Register<SilkForceSignal>(this);
        SilkEventBus.Register<SilkFireSignal>(this);
        SilkEventBus.Register<SilkPinNodeSignal>(this);
        SilkEventBus.Register<SilkSpanSignal>(this);

        previewGO = new GameObject("PreviewLine");
        previewGO.transform.SetParent(transform);
        previewLine = previewGO.AddComponent<LineRenderer>();
        Shader sh = Shader.Find("Sprites/Default");
        if (sh == null) sh = Shader.Find("Hidden/InternalErrorShader");
        previewLine.material = new Material(sh) { color = Color.cyan };
        previewLine.widthCurve = new AnimationCurve(new Keyframe(0, 0.03f), new Keyframe(1, 0.03f));
        previewLine.positionCount = 2;
        previewLine.useWorldSpace = true;
        previewGO.SetActive(false);
    }

    void Start()
    {
        anchorLayer = LayerMask.GetMask("Default");
        wallLayer = LayerMask.GetMask("Default");
    }

    void Update() => HandleInput();

    void HandleInput()
    {
        if (mainCam == null)
        {
            mainCam = Camera.main;
            if (mainCam == null)
            {
                Debug.LogError("[SilkBuilder] Main Camera not found!");
                return;
            }
        }

        // Parkour 模式下左键归玩家（发射丝线），
        // 否则会同时触发「连丝」和「发射」两件事
        if (Input.GetMouseButtonDown(0) && !parkourMode) TryPickOrCreateAnchor();

        if (isFirstSelected && firstAnchor != null)
        {
            previewGO.SetActive(true);
            previewLine.SetPosition(0, firstAnchor.WorldPosition);
            previewLine.SetPosition(1, MouseWorldPoint());
        }
        else previewGO.SetActive(false);

        /* 右键单击 = 断丝（仅 FreeFly 世界）。
         * Parkour 下右键属于相机环绕 + 取消选中，
         * 断丝改由X 键「断自己发射的线」承担 —— 两个世界不共享右键语义。*/
        if (!parkourMode)
        {
            if (Input.GetMouseButtonDown(1))
            {
                rightClickStartPos = Input.mousePosition;
                rightClickStartTime = Time.time;
            }
            if (Input.GetMouseButtonUp(1))
            {
                float dragDist = Vector3.Distance(Input.mousePosition, rightClickStartPos);
                if (Time.time - rightClickStartTime < 0.3f && dragDist < 10f)
                {
                    TryCutNearest(mainCam.ScreenPointToRay(Input.mousePosition));
                }
            }
        }

        /* 以下是 FreeFly（编辑器世界）的键位 —— 必须全部加 !parkourMode。
         * Parkour 下这些键属于另一个世界：X=断自己发的线、C=固化节点。
         * 之前只有 R 加了门控，导致按 C 会同时「清空蛛网」和「固化节点」。*/
        if (!parkourMode)
        {
            if (Input.GetKeyDown(KeyCode.R)) ResetSelection();
            if (Input.GetKeyDown(KeyCode.C)) ClearAll();
            if (Input.GetKeyDown(KeyCode.G)) GenerateWeb();
            if (Input.GetKeyDown(KeyCode.X)) AgeWeb(0.15f);
        }
    }

    /// <summary>
    /// 左键：只**选中已有锚点**，不创建。
    ///
    /// 原实现在射线打空时调CreateAnchorAt(hit.point) 凭空在墙面上造点 ——
    /// 这违反了「两个世界都不允许凭空创建锚点」的规则：
    ///   FreeFly 应该只连已布好的锚点（关卡编辑）
    ///   Parkour 应该用自身位置建点（由 PlaceNode 负责），不靠鼠标点击
    /// 打空时直接忽略：既不选中也不创建。
    /// </summary>
    void TryPickOrCreateAnchor()
    {
        Ray ray = mainCam.ScreenPointToRay(Input.mousePosition);
        if (Physics.Raycast(ray, out RaycastHit hit, 2000f, anchorLayer))
        {
            var a = hit.collider.GetComponent<AnchorPoint>();
            if (a != null) { OnAnchorPicked(a); return; }
        }
        // 打空：什么都不做 —— 不凭空建点
    }

/// <summary>
    /// Parkour 世界用：**一次性**建线 from→to，不走「两次点击」状态机。
    ///
    /// 【为什么必须独立，不能复用 OnAnchorPicked】
    /// OnAnchorPicked 是 FreeFly 的「两次点击」状态机，靠共享字段
    /// isFirstSelected / firstAnchor 记录进度。而切模式时这两个字段
    /// 不会自动复位，于是产生这种错乱（用户实测：一次点击出两根线）：
    ///
    ///   FreeFly 单击过一次        -> isFirstSelected = true（脏状态残留）
    ///   切到 Parkour（未复位）
    ///   Parkour 左键 call1(self)  -> 被当成「第二个点」-> 建出 旧锚点→自己 的线
    ///                    call2(target) -> call1 已ResetSelection，只记住目标（半污染）
    ///   下一次左键 call1(self)    -> 又被当成「第二个点」-> 建出 上次目标→自己 的线
    ///
    /// 结果：每次点击都会多建一根「历史点→自己」的线，
    /// 终点不是你点的那个锚点，于是屏幕上出现多根朝向不同的线。
    ///
    /// 现在：Parkour 用这个独立入口，语义清晰且无跨世界状态污染。
    /// 走的是同一个 CreateSilkLine，所以距离校验与去重依然生效。
    /// </summary>
    public SilkLine ConnectForParkour(AnchorPoint from, AnchorPoint to)
    {
        if (from == null || to == null) return null;
        if (from == to) return null;

        // 顺手清掉任何残留的选中态，避免影响 FreeFly
        ResetSelection();

        if (VoxelDistance(from.position, to.position) < 5)
        {
            Debug.Log("[SilkBuilder] 锚点太近（<5格），拒绝连线");
            return null;
        }

        lastCreatedLine = CreateSilkLine(from, to, defaultColor, BreakMode.Middle);
        return lastCreatedLine;
    }

    /// <summary>清理选中态。切模式 / 发射 / 清场时都必须调用，
    /// 否则 isFirstSelected 会跨世界残留，造成建线错乱。</summary>
    public void ClearSelectionState() => ResetSelection();

    void OnAnchorPicked(AnchorPoint a)
    {
        if (!isFirstSelected)
        {
            firstAnchor = a;
            isFirstSelected = true;
            a.SetHighlight(true);
        }
        else
        {
            if (a != firstAnchor)
            {
                if (VoxelDistance(firstAnchor.position, a.position) >= 5)
                {
                    // 记下这条线：Parkour 世界的「发射」要拿到它才能进入摆荡
                    lastCreatedLine = CreateSilkLine(firstAnchor, a,
                                                     defaultColor, BreakMode.Middle);
                }
                else Debug.Log("[SilkBuilder] 锚点太近（<5格），拒绝连线");
            }
            ResetSelection();
        }
    }

    void ResetSelection()
    {
        if (firstAnchor) firstAnchor.SetHighlight(false);
        firstAnchor = null;
        isFirstSelected = false;
        previewGO.SetActive(false);
    }

    void OnDestroy()
    {
        // 事件总线是静态的，必须退订，否则 SilkBuilder 销毁后
        // 仍被总线持有（内存泄漏 + 幽灵调用）
        SilkEventBus.Unregister<SilkBreakSignal>(this);
        SilkEventBus.Unregister<SilkGrabSignal>(this);
        SilkEventBus.Unregister<SilkReleaseSignal>(this);
        SilkEventBus.Unregister<SilkForceSignal>(this);
        SilkEventBus.Unregister<SilkFireSignal>(this);
        SilkEventBus.Unregister<SilkPinNodeSignal>(this);
        SilkEventBus.Unregister<SilkSpanSignal>(this);
    }

    /* ============ 事件执行器（由 SilkEventBus 统一调用） ============ */
    /* 每个事件类型一个 Execute 方法。加新事件时：
     *   1) 新建 class XxxSignal : ISilkEvent，实现 Handle 里调 handler.ExecuteXxx(this)
     *   2) 在这里加对应的 ExecuteXxx
     *   3) 在 Awake 里加一行 Register<XxxSignal>(this)
     * SilkEventBus 的分发逻辑完全不用改 —— 这就是「统一信号控制」的收益。*/

    /// <summary>执行断裂。signal.target 为空则广播到所有可断的线。</summary>
    public void ExecuteBreak(SilkBreakSignal signal)
    {
        if (signal == null) return;
        int count = 0;

        if (signal.target != null)
        {
            // 指定线
            if (signal.target.BreakBySignal(this, signal)) count = 1;
        }
        else
        {
            // 广播：对所有状态允许断裂的线执行
            var snapshot = new List<SilkLine>(silkLines);
            foreach (var line in snapshot)
            {
                if (line == null || !line.CanBeCut) continue;
                if (line.BreakBySignal(this, signal)) count++;
            }
        }

        if (count > 0)
        {
            RebuildSpatialHash();
            Debug.Log("[Event] 断裂 " + signal.cause + " × " + count +
                      (signal.target != null ? "（指定线）" : "（广播）"));
        }
    }

    /// <summary>连线的起点（等待第二个点）。null = 当前没有待连线的点。</summary>
    AnchorPoint pendingNode = null;

    /// <summary>
    /// 第 1 步：固化一个节点（只放点，**不连线**）。
    /// 同位置重复调用会复用已有节点（5格去重），
    /// 因此多道线可以共用同一个节点 —— 这正是「结网」需要的。
    ///
    /// 若 signal.selectAsStart 为真，则把它记为连线起点，
    /// 下次玩家点第二个点时才会真正建线。
    /// </summary>
    public AnchorPoint ExecutePinNode(SilkPinNodeSignal signal)
    {
        if (signal == null) return null;

        var anchor = CreateAnchorAt(signal.position);
        if (anchor == null) return null;

        // 只在首次创建时标记；复用的节点已经是 PlayerNode
        if (anchor.type != AnchorType.PlayerNode)
        {
            anchor.MarkAsPlayerNode(playerNodeRadius);
            Debug.Log("[Node] 固化节点 " + anchor.position);
        }

        if (signal.selectAsStart)
        {
            pendingNode = anchor;
            anchor.SetHighlight(true);
            Debug.Log("[Node] 已选为连线起点，再点一个点即可连线");
        }
        return anchor;
    }

    /// <summary>
    /// 第 2 步：把刚点出的节点与上一个选中点连起来。
    /// 若没有待连线的起点，则把这个点记为新的起点（等下一个点）。
    /// 这样「点A → 点B → 连线；点C → 点D → 连线」可以连续做。
    /// </summary>
    public AnchorPoint ExecuteNodeClick(SilkPinNodeSignal signal)
    {
        if (signal == null) return null;

        var anchor = CreateAnchorAt(signal.position);
        if (anchor == null) return null;
        if (anchor.type != AnchorType.PlayerNode)
        {
            anchor.MarkAsPlayerNode(playerNodeRadius);
            Debug.Log("[Node] 固化节点 " + anchor.position);
        }

        // 还没有起点 -> 这一下只是选起点
        if (pendingNode == null)
        {
            pendingNode = anchor;
            anchor.SetHighlight(true);
            Debug.Log("[Node] 选为起点 (" + anchor.position + ")，再点一个点连线");
            return anchor;
        }

        // 已有起点 -> 连线
        if (pendingNode == anchor)
        {
            Debug.Log("[Node] 点了同一个点，取消选中");
            CancelPendingNode();
            return anchor;
        }

        var line = CreateSilkLine(pendingNode, anchor, defaultColor, BreakMode.Middle);
        Debug.Log("[Link] " + pendingNode.position + " → " + anchor.position +
                  (line != null ? " 连线成功" : " 已有线（去重）"));
        CancelPendingNode();
        return anchor;
    }

    /// <summary>取消待连线的起点。</summary>
    public void CancelPendingNode()
    {
        if (pendingNode != null) pendingNode.SetHighlight(false);
        pendingNode = null;
    }

    /// <summary>当前是否有待连线的起点（供 UI 查询）。</summary>
    public bool HasPendingNode => pendingNode != null;

    /// <summary>
    /// 结网：在两个点之间生成静态丝线（一步到位，供脚本/调试用）。
    /// 玩家交互走 ExecuteNodeClick 的两步流程。
    /// </summary>
    public void ExecuteSpan(SilkSpanSignal signal)
    {
        if (signal == null) return;
        float len = Vector3.Distance(signal.from, signal.to);
        if (len < 1f) return;

        var a = CreateAnchorAt(signal.from);
        var b = CreateAnchorAt(signal.to);
        if (a == null || b == null || a == b) return;

        CreateSilkLine(a, b, defaultColor, BreakMode.Middle);
        Debug.Log("[Span] 结网 " + a.position + " ↔ " + b.position +
                  " 长度 " + len.ToString("F1"));
    }

    /// <summary>
    /// 执行发射：在 from 与 to 之间生成一条新的静态丝线。
    /// 这是 SpiderShot 与后续「结网」功能的共同基础。
    /// </summary>
    public void ExecuteFire(SilkFireSignal signal)
    {
        if (signal == null) return;

        // 距离太短不生成（避免退化线段导致除零）
        float len = Vector3.Distance(signal.from, signal.to);
        if (len < 1f) return;

        // 命中点若已有锚点就复用，否则新建一个（CreateAnchorAt 内含 5 格去重）
        var anchor = CreateAnchorAt(signal.to);

        // 起点**永远**是from（蓝球当前位置），不依赖 firstAnchor ——
        // 那个字段属于 FreeFly 的连丝选中态，混进来会让丝线凭空出现在别处
        var fromAnchor = CreateAnchorAt(signal.from);

        if (fromAnchor == null || anchor == null) return;
        if (fromAnchor == anchor) return;

        var line = CreateSilkLine(fromAnchor, anchor, defaultColor, BreakMode.Middle);
        if (line == null) return;   // 去重命中（同一对点已有线）

        if (signal.autoAttach)
        {
            // 新线还挂在 fromAnchor 上，把它转成可摆动的约束链并挂到发射点
            var tip = CreateAnchorAt(signal.from);
            if (tip != null) line.Attach(tip, this, "发射点");
        }

        Debug.Log("[Event] 发射丝线 " + fromAnchor.position + " → " + anchor.position +
                  " 长度 " + len.ToString("F1"));
    }

    /// <summary>执行抓住：把末端挂到指定点上。</summary>
    public void ExecuteGrab(SilkGrabSignal signal)
    {
        if (signal == null || signal.attachPoint == null) return;

        SilkLine best = null;

        // 优先用发布方指定的线 —— 否则两处搜索半径不同
        // （发布方 grabRange=30 / 本方法 pickRadius=15）会抓到不同的线，
        // 导致「发布方以为抓住了、实际没挂上」的状态错乱。
        if (signal.target != null &&
            signal.target.life == SilkLifeState.Static)
        {
            best = signal.target;
        }
        else
        {
            // 回退：自行搜索最近的可挂载线
            float bestD = pickRadius;
            Vector3 p = signal.attachPoint.position;
            foreach (var line in silkLines)
            {
                if (line == null || line.life != SilkLifeState.Static) continue;
                float d = Vector3.Distance(p, (line.rootFrom.WorldPosition + line.rootTo.WorldPosition) * 0.5f);
                if (d < bestD) { bestD = d; best = line; }
            }
        }
        if (best == null) return;

        if (best.Attach(signal.attachPoint, this, signal.label))
        {
            if (signal.startSwing) best.StartSwing();
            Debug.Log("[Event] 抓住 " + signal.label);
        }
    }

    /// <summary>执行松手：所有已挂载的线脱钩下坠。</summary>
    public void ExecuteRelease()
    {
        int count = 0;
        var snapshot = new List<SilkLine>(silkLines);
        foreach (var line in snapshot)
        {
            if (line == null) continue;
            if (line.life == SilkLifeState.Anchored || line.life == SilkLifeState.Swinging)
                if (line.Release()) count++;
        }
        if (count > 0) Debug.Log("[Event] 松手 " + count + " 根");
    }

    /// <summary>执行施力：对所有可动线施加一次力。</summary>
    public void ExecuteForce(SilkForceSignal signal)
    {
        if (signal == null) return;
        var snapshot = new List<SilkLine>(silkLines);
        foreach (var line in snapshot)
        {
            if (line == null || !line.NeedsPhysics) continue;
            line.chain.ApplyForce(signal.force, signal.tipBoost);
        }
    }

    /* ============ 右键断丝：找最近丝线 → 发一个 ManualCut 信号 ============
     * 保留了「右键切最近丝线」的交互，但改为发信号而非直接断裂，
     * 这样走的是同一条代码路径，与跑酷的信号驱动保持一致。
     */
    void TryCutNearest(Ray ray)
    {
        if (silkLines.Count == 0)
        {
            Debug.Log("[Cut] 没有丝线");
            return;
        }

        SilkLine bestLine = null;
        float bestDist = pickRadius;

        foreach (var line in silkLines)
        {
            if (!line.CanBeCut) continue;   // 只挑静止/挂着的线

            Vector3 mid = (line.rootFrom.WorldPosition + line.rootTo.WorldPosition) * 0.5f;
            Vector3 closestPoint = ray.origin + ray.direction * Vector3.Dot(mid - ray.origin, ray.direction);
            float dist = Vector3.Distance(mid, closestPoint);

            if (dist < bestDist)
            {
                bestDist = dist;
                bestLine = line;
            }
        }

        if (bestLine == null)
        {
            Debug.Log("[Cut] 附近没有找到丝线");
            return;
        }

        // 发信号：右键 = ManualCut + 预设断点 + 零初速度
        // 走统一事件总线；指定线用 On()，不指定则广播
        SilkEventBus.Post(new SilkBreakSignal(SilkBreakCause.ManualCut).On(bestLine));
    }

    public AnchorPoint CreateAnchorAt(Vector3 worldPoint)
    {
        Vector3 local = worldPoint - grid.transform.position;
        Vector3 snapped = grid.transform.position + new Vector3(
            Mathf.Round(local.x), Mathf.Round(local.y), Mathf.Round(local.z));
        float h = grid.GetHalfSize();
        snapped.x = Mathf.Clamp(snapped.x, grid.transform.position.x - h, grid.transform.position.x + h);
        snapped.y = Mathf.Clamp(snapped.y, grid.transform.position.y - h, grid.transform.position.y + h);
        snapped.z = Mathf.Clamp(snapped.z, grid.transform.position.z - h, grid.transform.position.z + h);

        var voxel = grid.WorldToVoxel(snapped);
        foreach (var a in anchors)
            if (VoxelDistance(a.position, voxel) < 5)
                return a;

        var go = new GameObject();
        go.transform.SetParent(transform);
        var anchor = go.AddComponent<AnchorPoint>();
        anchor.Setup(snapped, voxel, AnchorType.Internal);
        anchors.Add(anchor);
        return anchor;
    }

    public SilkLine CreateSilkLine(AnchorPoint a, AnchorPoint b, SilkColor color, BreakMode mode = BreakMode.Middle)
    {
        /* 每根线的创建都留痕（含调用栈）。
         *
         * 【为什么要打栈】实测发现同一对锚点被建了两次，且两次都走的是
         * 本方法（来源=正常建线）、端点是同一批 AnchorPoint 对象，
         * 但父线是两个不同的 SilkLine。按去重逻辑第二次必被Contains
         * 挡住，所以「两次都成功」只可能是 lineKeys 被清过。
         * 光看现象无法判断是谁清的 —— 直接把调用栈打出来，
         * 由Unity 告诉我们调用来源，不靠推测。
         *
         * 判读：
         *   - 栈里出现 CreateSilkLine 两次且路径不同 -> 入口被触发多次
         *   - 第二次紧跟 ClearAll/GenerateWeb-> 清空后重建（设计如此）
         *   - 两次调用路径完全一样 -> 是状态/时序问题，不是入口问题*/
        Debug.Log("[NewLine] " + a.position + " -> " + b.position +
                  " 总数 " + (silkLines.Count + 1) +
                  " 去重表size=" + lineKeys.Count +
                  "\n" + System.Environment.StackTrace);

        // 去重：key 基于体素坐标，与对象生命周期解耦
        if (lineKeys.Contains(GetPairKey(a, b)))
        {
            Debug.Log("[NewLine]被去重挡下: " + a.position + " -> " + b.position);
            return null;
        }
        lineKeys.Add(GetPairKey(a, b));

        var line = new SilkLine(a, b, color, mode);
        silkLines.Add(line);
        var go = new GameObject("Seg_" + a.position + "_" + b.position);
        go.transform.SetParent(transform);
        var seg = go.AddComponent<SilkSegment>();
        seg.from = a; seg.to = b; seg.parentLine = line;
        /* 角色分配：默认全部生成辅丝（垂落丝）。
         * 用户反馈「同时出现主丝和垂落丝」是多余的 —— 垂度调大后主丝
         * 也明显下垂，与辅丝难以区分，反而成了杂线。
         * 因此 primaryRatio 默认 0；主丝（Radii）样式保留，
         * 拖到 0.3 左右可恢复主辅分层，或手动指定。
         * 垂度按用户反馈加大：之前 0.02~0.16 视觉上「不够多、不够明显」。*/
        bool isRadii = Random.value < primaryRatio;
        seg.role = isRadii ? SilkRole.Radii : SilkRole.Spiral;
        // 主丝绷紧（tension 高 -> 垂度小），辅丝松垂
        seg.tension = isRadii ? Random.Range(0.75f, 1.0f) : Random.Range(0.25f, 0.60f);
        // 垂度：主丝 0.10~0.18（仍偏直但可见弯），辅丝 0.22~0.32（明显松垂）
        seg.sagRatio = isRadii ? Random.Range(0.10f, 0.18f) : Random.Range(0.22f, 0.32f);
        // 老化程度：辅丝更旧更暗（细密结构先积灰）
        seg.ageTint = isRadii ? Random.Range(0f, 0.25f) : Random.Range(0.15f, 0.6f);
        seg.ApplyRoleStyle();
        line.segments.Add(seg);
        spatialHash.Insert(seg);
        return line;
    }

    /// 丝线去重键：**基于体素坐标**，不用 GetHashCode。
    ///
    /// 为什么不用 GetHashCode：Unity 的 MonoBehaviour.GetHashCode 返回
    /// InstanceID，而 InstanceID 在对象销毁后会被复用 ——
    /// 断裂时 BreakNode 被销毁，下一轮新建的锚点可能拿到相同 ID，
    /// 导致两个不同的点对被误判为重复（该连的线被吞掉）。
    ///
    /// 位打包：每个坐标 8 位（+64 偏移，覆盖 ±50 的100³ 网格），
    /// 点 A 占高 24 位、点 B 占低 24 位，顺序无关由「排序后再打包」保证。
    /// 用 long 而非 string，避免每次比较都产生 GC。
    static long GetPairKey(AnchorPoint a, AnchorPoint b)
    {
        Vector3Int pa = a.position, pb = b.position;
        // 先排序，保证 (A,B) 与 (B,A) 得到同一个 key
        bool swap = pa.x > pb.x ||
                    (pa.x == pb.x && (pa.y > pb.y ||
                                    (pa.y == pb.y && pa.z > pb.z)));
        if (swap) { Vector3Int t = pa; pa = pb; pb = t; }

        long ka = ((long)(pa.x + 64) << 16) | ((pa.y + 64) << 8) | (pa.z + 64);
        long kb = ((long)(pb.x + 64) << 16) | ((pb.y + 64) << 8) | (pb.z + 64);
        return (ka << 24) | kb;
    }

    public void ClearAll()
    {
        /* 清空去重表是「允许重新建线」的关键动作，必须留痕。
         * 若日志里出现「两次 [NewLine] 都成功」而中间夹着这条，
         * 就证明重复建线是清空时序造成的，而不是入口被点多次。*/
        Debug.Log("[ClearAll] 清空去重表(" + lineKeys.Count + " 项)" +
                  " 线(" + silkLines.Count + " 根)\n" +
                  System.Environment.StackTrace);

        foreach (var line in silkLines)
        {
            foreach (var seg in line.segments)
                if (seg) seg.StartFade();
            // 清理该线残留的约束链，避免反复清空时 GameObject 堆积
            if (line.chain != null) Object.Destroy(line.chain.gameObject);
            line.chain = null;
            line.chainGO = null;
            // 链条销毁后完整回到静态：恢复渲染 + 停止物理更新
            line.RestoreStatic();
        }
        silkLines.Clear();
        // 去重集合必须同步清空，否则重新织网会被上一轮的记录挡住
        lineKeys.Clear();
        spatialHash.Clear();
    }

    /// 立即销毁场景中所有 SilkSegment（含正在淡出的残留）。
    /// ClearAll 的 StartFade 是「标记淡出，2.5 秒后才 Destroy」，
    /// 在此期间这些 GameObject 仍可见但已从 silkLines 移除、不受管理，
    /// 重新织网时会与新线重叠。重新织网前调用本方法彻底清场。
    void DestroyFadingSegments()
    {
        var segs = FindObjectsOfType<SilkSegment>();
        foreach (var seg in segs)
            if (seg) Object.Destroy(seg.gameObject);

        // ChainSeg / ChainNode 也一并清掉，避免约束链残留
        var chains = FindObjectsOfType<SilkChain>();
        foreach (var ch in chains)
            if (ch) Object.Destroy(ch.gameObject);

        // 断裂产生的 BreakNode 同理
        var nodes = FindObjectsOfType<AnchorPoint>();
        foreach (var nd in nodes)
            if (nd != null && nd.type == AnchorType.SilkNode) Object.Destroy(nd.gameObject);
    }

    /// <summary>
    /// Bootstrap 生成的墙面锚点不会自动进入 anchors 列表，
    /// 这里从场景中补登记一次，避免 GenerateWeb 取到空列表。
    /// </summary>
    void SyncAnchorsFromScene()
    {
        foreach (var a in FindObjectsOfType<AnchorPoint>())
        {
            if (a == null || a.type != AnchorType.Wall) continue;
            if (!anchors.Contains(a)) anchors.Add(a);
        }
    }

    public void GenerateWeb()
    {
        /* 重新织网前，把上一轮「正在淡出但已不受管理」的残留彻底清掉。
         * 否则 silkLines.Clear() 只清列表，旧的 Seg GameObject 还会存活 2.5 秒，
         * 与新一轮的线重叠 —— 视觉上就是「同一个点对之间多条线」。
         * （配合基于体素坐标的 GetPairKey，从根本上避免重复建线）*/
        DestroyFadingSegments();
        ClearAll();
        SyncAnchorsFromScene();
        var wallAnchors = anchors.FindAll(a => a.type == AnchorType.Wall);
        if (wallAnchors.Count < 6) { Debug.LogWarning("[SilkBuilder] 壁面锚点不足"); return; }

        // 坐标约定：X = 左右，Y = 前后，Z = 高度（重力沿 -Z）
        Vector3 c = grid.GetCenter();
        float h = grid.GetHalfSize();
        const float EPS = 1.5f;

        // 顶面：Z 接近 +h 且 X/Y 都在中心
        var topAnchors = wallAnchors.FindAll(a =>
            Mathf.Abs(a.WorldPosition.x - c.x) < 3f &&
            Mathf.Abs(a.WorldPosition.y - c.y) < 3f &&
            a.WorldPosition.z > c.z + h - EPS);

        // 底面：Z 接近 -h（X/Y 网格铺满）
        var bottomAnchors = wallAnchors.FindAll(a =>
            a.WorldPosition.z < c.z - h + EPS);

        // 左右面高区：X 贴 ±h，且 Z 在 3/4~1 区间
        var highAnchors = wallAnchors.FindAll(a =>
            (Mathf.Abs(a.WorldPosition.x - (c.x + h)) < EPS ||
             Mathf.Abs(a.WorldPosition.x - (c.x - h)) < EPS) &&
            a.WorldPosition.z > c.z + h * 0.75f - EPS);

        // 左右面整体（用于同面纵向连接）
        var sideAnchors = wallAnchors.FindAll(a =>
            Mathf.Abs(a.WorldPosition.x - (c.x + h)) < EPS ||
            Mathf.Abs(a.WorldPosition.x - (c.x - h)) < EPS);

        // ===== 1. 顶面正中 → 左右高区 =====
        if (topAnchors.Count > 0 && highAnchors.Count > 0)
        foreach (var top in topAnchors)
        {
            for (int i = 0; i < 4; i++)
            {
                var target = highAnchors[Random.Range(0, highAnchors.Count)];
                if (target != top) CreateSilkLine(top, target, SilkColor.White, BreakMode.Middle);
            }
        }

        // ===== 2. 底面网格 → 左右高区（纵向贯穿，玩家所在真实高度层） =====
        // 数量受 bottomLinkCount 控制：144 个底面锚点全连会得到 144 根线，
        // 叠加其他循环后总计约 170 根 —— 是真实蛛网（30~60 根）的 3 倍，
        // 画面上挤成一片，看不出单根丝的形态。
        if (bottomAnchors.Count > 0 && highAnchors.Count > 0)
        {
            int n = Mathf.Min(bottomLinkCount, bottomAnchors.Count);
            for (int i = 0; i < n; i++)
            {
                var bot = bottomAnchors[Random.Range(0, bottomAnchors.Count)];
                var target = highAnchors[Random.Range(0, highAnchors.Count)];
                if (target != bot) CreateSilkLine(bot, target, SilkColor.White, BreakMode.Middle);
            }
        }

        // ===== 3. 左面 ↔ 右面（高区横连，跨越城市） =====
        if (highAnchors.Count > 1)
        for (int i = 0; i < 10; i++)
        {
            var a = highAnchors[Random.Range(0, highAnchors.Count)];
            var b = highAnchors[Random.Range(0, highAnchors.Count)];
            if (a != b && VoxelDistance(a.position, b.position) >= 5)
                CreateSilkLine(a, b, SilkColor.White, BreakMode.Middle);
        }

        // ===== 4. 同面纵向辐射（左面内部 / 右面内部） =====
        for (int i = 0; i < 12; i++)
        {
            var a = sideAnchors[Random.Range(0, sideAnchors.Count)];
            var b = sideAnchors[Random.Range(0, sideAnchors.Count)];
            // 同一面（X 相同）且高度差大于 5 格 → 纵向丝线
            if (a != b &&
                Mathf.Abs(a.WorldPosition.x - b.WorldPosition.x) < 1f &&
                Mathf.Abs(a.WorldPosition.z - b.WorldPosition.z) > 5f)
                CreateSilkLine(a, b, SilkColor.White, BreakMode.Middle);
        }

        RebuildSpatialHash();
        Debug.Log("[SilkBuilder] 织网完成，共 " + silkLines.Count + " 根丝线" +
                  "（顶面 " + topAnchors.Count + " / 底面 " + bottomAnchors.Count +
                  " / 高区 " + highAnchors.Count + "）");
    }

    void RebuildSpatialHash()
    {
        spatialHash = new SilkSpatialHash(8f);
        foreach (var line in silkLines)
            foreach (var seg in line.segments)
                if (seg && seg.state == SilkState.Intact)
                    spatialHash.Insert(seg);
    }

    public void AgeWeb(float breakChance)
    {
        List<SilkLine> candidates = new List<SilkLine>();
        foreach (var line in silkLines) if (line.CanBeCut) candidates.Add(line);
        int count = 0;
        // 老化同样走信号路径，与右键/跑酷保持一致
        var signal = new SilkBreakSignal(SilkBreakCause.Aging);
        foreach (var line in candidates)
        {
            if (Random.value < breakChance)
            {
                if (line.BreakBySignal(this, signal)) count++;
            }
        }
        RebuildSpatialHash();
        Debug.Log("[SilkBuilder] 老化断丝：" + count + " 根");
    }

    int VoxelDistance(Vector3Int a, Vector3Int b)
        => Mathf.Abs(a.x - b.x) + Mathf.Abs(a.y - b.y) + Mathf.Abs(a.z - b.z);

    Vector3 MouseWorldPoint()
    {
        Ray ray = mainCam.ScreenPointToRay(Input.mousePosition);
        // 用当前相机的正对面，而非硬编码 Vector3.forward：
        // 相机改为 Z-up 且斜视后，固定法线的平面会让预览线终点偏离光标
        Vector3 camFwd = mainCam.transform.forward;
        if (Mathf.Abs(Vector3.Dot(camFwd.normalized, Vector3.forward)) < 0.01f)
            camFwd = Vector3.right;
        new Plane(camFwd.normalized, grid.GetCenter()).Raycast(ray, out float dist);
        return ray.GetPoint(dist);
    }
}

/* ===================== 场景一键构建 ===================== */
public class SilkWorldBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void AutoBuild()
    {
        if (Object.FindObjectOfType<SilkBuilder>() != null) return;
        Build();
    }

#if UNITY_EDITOR
    [UnityEditor.MenuItem("Tools/Silk Web Builder")]
    public static void BuildFromMenu()
    {
        foreach (var go in Object.FindObjectsOfType<GameObject>())
            if (go.scene.isLoaded) Object.DestroyImmediate(go);
        Build();
    }
#endif

    public static void Build()
    {
        /* 【幂等守卫】必须放在最前面。
         *
         * 根因：RuntimeInitializeOnLoadMethod(BeforeSceneLoad) 在
         * **每次场景加载前**都会执行。原先只有 AutoBuild 里有
         * FindObjectOfType 检查，而 Tools 菜单的 BuildFromMenu 完全没有，
         * 且 Build() 本身不做任何检查 —— 于是可能并存**两个 SilkBuilder**：
         *
         *   · silkLines 各数各的        -> [NewLine]「总数」一直增长
         *   · lineKeys 各清各的         -> 去重完全失效，同对锚点各建一条线
         *   · 两者的 Update 都响应左键   -> 一次点击触发两次建线入口
         *
         * 这完美解释了「只建了一条线却出现两条、且总数持续增长」。
         * 在 Build() 内部守卫，任何入口（自动/ 菜单 / 代码）都受保护，
         * 比在每个调用点各写一遍检查更可靠。*/
        var existing = Object.FindObjectOfType<SilkBuilder>();
        if (existing != null)
        {
            Debug.LogWarning("[Bootstrap] 已存在 SilkBuilder(" +
                existing.gameObject.name + ")，跳过重复构建。" +
                " 重复实例会导致去重表各自独立，同一锚点对被建多次。");
            return;
        }

        var world = new GameObject("SilkWorld");
        var grid = world.AddComponent<VoxelGrid>();
        grid.size = 100; grid.cellSize = 1f;

        CreateWireCube(world.transform, grid.GetHalfSize());
        CreateInnerWalls(world.transform, grid.GetHalfSize());
        GenerateAnchors(grid, 8);

        /* 跑酷测试关卡（临时）—— 属于**游戏世界**，FreeFly 下不该出现。
         * 所以改为在 HandleModeSwitch 里按模式建/清，不在 Build 里无条件创建。
         * 删除：删掉这两处调用 + SilkParkourStage.cs 整个文件即可。*/

        var builderGO = new GameObject("SilkBuilder");
        var builder = builderGO.AddComponent<SilkBuilder>();
        builder.grid = grid;

        var camGO = new GameObject("Main Camera");
        camGO.tag = "MainCamera";
        var cam = camGO.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.06f, 0.06f, 0.1f);
        // 摄像机摆在 XY 平面内、Z 略高，配合 up=+Z 实现「Z 朝屏幕上方」
        camGO.transform.position = new Vector3(120, -120, 85);
        // 用 LookAt 的双参数重载显式指定参考上轴。
        // 不要先给 transform.up 赋值再 LookAt —— 那是两次独立重算 rotation，
        // 叠加后朝向不可控，表现为「镜头转过去看不到立方体」。
        camGO.transform.rotation = Quaternion.LookRotation(
            (Vector3.zero - camGO.transform.position).normalized, Vector3.forward);
        camGO.AddComponent<SimpleOrbitCamera>();

        // 玩家球：第三人称跑酷用。必须自动创建 ——
        // 否则控制器不存在，Tab 切模式与发射丝线都无法测试。
        var playerGO = new GameObject("SilkPlayer");
        playerGO.AddComponent<SilkParkourController>();

        Debug.Log("[Bootstrap] 100³ 蛛网战场就绪\n" +
                  "左键 连丝 | 右键单击 断丝（1/4~1/2自然掉落）| 右键拖拽 旋转\n" +
                  "WASD 飞行 | QE 升降 | Shift加速 | 滚轮微移\n" +
                  "R 取消 | C 清空 | G 织网 | X 老化 | F 切换拖拽方向");
    }

    static void CreateWireCube(Transform parent, float h)
    {
        var go = new GameObject("WireCube");
        go.transform.SetParent(parent);
        var lr = go.AddComponent<LineRenderer>();
        Shader sh = Shader.Find("Sprites/Default");
        if (sh == null) sh = Shader.Find("Hidden/InternalErrorShader");
        lr.material = new Material(sh) { color = new Color(0.3f, 0.6f, 1f, 0.5f) };
        lr.widthCurve = new AnimationCurve(new Keyframe(0, 0.06f), new Keyframe(1, 0.06f));
        lr.useWorldSpace = true;

        Vector3[] c = new Vector3[8];
        int i = 0;
        for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
                for (int z = -1; z <= 1; z += 2)
                    c[i++] = parent.position + new Vector3(x * h, y * h, z * h);

        int[] e = { 0, 1, 1, 3, 3, 2, 2, 0, 4, 5, 5, 7, 7, 6, 6, 4, 0, 4, 1, 5, 2, 6, 3, 7 };
        lr.positionCount = e.Length;
        for (int k = 0; k < e.Length; k++) lr.SetPosition(k, c[e[k]]);
    }

    static void CreateInnerWalls(Transform parent, float h)
    {
        // 命名按项目坐标约定：X=左右 / Y=前后 / Z=高度
        string[] names = { "Right_X", "Left_X", "Front_Y", "Back_Y", "Top_Z", "Bottom_Z" };
        Vector3[] n = { Vector3.right, Vector3.left, Vector3.forward, Vector3.back, Vector3.up, Vector3.down };
        for (int i = 0; i < 6; i++)
        {
            var wall = GameObject.CreatePrimitive(PrimitiveType.Quad);
            wall.name = "InnerWall_" + names[i];
            wall.transform.SetParent(parent);
            wall.transform.position = parent.position + n[i] * h;
            wall.transform.rotation = Quaternion.LookRotation(-n[i]);
            var mr = wall.GetComponent<MeshRenderer>();
            Shader sh = Shader.Find("Universal Render Pipeline/Lit");
            if (sh == null) sh = Shader.Find("Standard");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            mr.material = new Material(sh) { color = new Color(0.15f, 0.15f, 0.2f, 0.3f) };
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            /* 关键：CreatePrimitive 只有 Cube/Sphere/Capsule/Cylinder 会自动加
             * Collider，**Quad 不会** —— 所以墙是没有实体的，球能直接穿过去。
             * 这里手动加 BoxCollider 做实体阻挡：
             *   -厚 1 格的薄盒，正好贴在墙面位置
             *   - 尺寸取立方体全宽，保证整面墙都能挡
             */
            var box = wall.AddComponent<BoxCollider>();
            box.size = new Vector3(h * 2f, h * 2f, 1f);
        }

        // 地板与顶板单独处理：Quad 是平面片，BoxCollider 沿它的局部 Z 方向最薄，
        // 而上面已经把局部 Z 对齐到法线，故 Top/Bottom 的薄方向是正确的。
        // 但左右前后四壁的 Quad 同样适用（局部 Z = 墙面法线）。
    }

    /* ============ 锚点：底面满网格 / 左右面 3/4~1 高区 / 顶面中心一点 ============
     *
     * 坐标约定（全项目统一）：X = 左右，Y = 前后，Z = 高度（重力沿 -Z）。
     * 之前此方法把 Vector3.up 当高度、Vector3.forward 当前后，导致：
     *   · 左右面的高度区间乘到了 Y 轴上（实际成了"前后区间"）
     *   · "顶面"点写到了 Y=+h 的前面板上，"底面"点写到了 Y=-h 的后面板上
     * 现在全部按 Z 为高度重写。
     */
    static void GenerateAnchors(VoxelGrid grid, int spacing)
    {
        Vector3 c = grid.transform.position;
        float h = grid.GetHalfSize();
        int count = 0;

        // ===== 左右面（X = ±h）：仅 3/4~1 高区，Y 前后多排 =====
        // 模拟城市内部不同高度的高楼：只有高处有锚点，低处留空给玩家活动
        float zHighStart = h * 0.75f;              // 高区起点
        float zHighEnd   = h;                       // 高区终点（顶部）

        for (int face = 0; face < 2; face++)
        {
            float x = (face == 0) ? h : -h;         // 左右两面

            for (float y = -h + spacing; y < h; y += spacing)          // Y 前后多排
            {
                for (float z = zHighStart; z <= zHighEnd; z += spacing) // Z 高度：高区
                {
                    var world = c + new Vector3(x, y, z);
                    var go = new GameObject();
                    go.transform.SetParent(grid.transform);
                    var a = go.AddComponent<AnchorPoint>();
                    a.Setup(world, grid.WorldToVoxel(world), AnchorType.Wall);
                    count++;
                }
            }
        }

        // ===== 底面（Z = -h）：X/Y 网格铺满 =====
        for (float x = -h + spacing; x < h; x += spacing)
        {
            for (float y = -h + spacing; y < h; y += spacing)
            {
                var world = c + new Vector3(x, y, -h);
                var go = new GameObject();
                go.transform.SetParent(grid.transform);
                var a = go.AddComponent<AnchorPoint>();
                a.Setup(world, grid.WorldToVoxel(world), AnchorType.Wall);
                count++;
            }
        }

        // ===== 顶面（Z = +h）：仅正中心一点 =====
        {
            var world = c + new Vector3(0, 0, h);
            var go = new GameObject();
            go.transform.SetParent(grid.transform);
            var a = go.AddComponent<AnchorPoint>();
            a.Setup(world, grid.WorldToVoxel(world), AnchorType.Wall);
            count++;
        }

        // 需求明确：前后面（Y = ±h）完全无锚点，此处刻意不生成

        Debug.Log("[Bootstrap] 锚点：" + count + " 个\n" +
                  "底面(Z=-h)：X/Y 网格铺满\n" +
                  "左右面(X=±h)：仅 3/4~1 高区，Y 多排（模拟高楼）\n" +
                  "顶面(Z=+h)：正中心 1 点\n" +
                  "前后面(Y=±h)：无锚点");
    }
}

/* ===================== 自由飞行摄像机 =====================
 * Z 为高度轴，因此这里的旋转不能用 Unity 默认的 Euler(x, y, 0)，
 * 否则右键环绕一次就会把 Z 轴转到屏幕侧面。
 * 改为绕 Z 偏航(yaw) + 绕水平轴俯仰(pitch)，并强制 up = forward。
 */
public class SimpleOrbitCamera : MonoBehaviour
{
    public float moveSpeed = 40f;
    public float fastMoveSpeed = 120f;
    public float rotateSpeed = 4f;
    public float scrollZoomSpeed = 20f;

    [Tooltip("拖拽跟手：鼠标往哪拖，物体就往哪转。Z-up 下需与 Unity 默认 Y-up 的符号相反")]
    public bool dragFollowsMouse = true;

    [Tooltip("按 F 切换拖拽方向")]
    public KeyCode flipKey = KeyCode.F;

    /// <summary>光标是否被锁定。锁定时鼠标移动直接转视角（第一人称手感）。</summary>
    public static bool cursorLocked = false;

    /// <summary>锁定/解锁光标。Parkour 模式用锁定，FreeFly 用自由光标（要点击操作）。</summary>
    public static void SetCursorLocked(bool locked)
    {
        cursorLocked = locked;
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }

    /// <summary>
    /// 相机是否响应输入。Parkour 模式下必须关掉 ——
    /// 否则 WASD/QE/Space 会同时被相机和玩家读取，两边一起动。
    /// </summary>
    public static bool inputEnabled = true;

    float yaw, pitch;   // yaw 绕 Z，pitch 仰角

    /// <summary>当前 yaw。第三人称相机跟随用它算「后方」。</summary>
    public float Yaw => yaw;

    /// <summary>视线方向的水平单位向量。
    /// 用它而不是 transform.forward 算相机位置 ——
    /// 后者会被 FollowCamera 的位置写入与 ApplyRotation 的旋转写入互相拉扯，
    /// 表现为画面抖动。</summary>
    public Vector3 LookDirFlat
    {
        get
        {
            Vector3 d = new Vector3(Mathf.Cos(yaw * Mathf.Deg2Rad),
                                   Mathf.Sin(yaw * Mathf.Deg2Rad), 0f);
            return d.sqrMagnitude < 0.0001f ? Vector3.right : d.normalized;
        }
    }

    /// <summary>含俯仰角的完整视线方向（yaw + pitch）。
    /// OrbitAround 用它算相机位置 —— 必须是 3D 的，
    /// 否则俯仰角会被丢掉，相机只能左右转、不能上下环绕。
    /// 直接由 yaw/pitch 现算，不依赖 ApplyRotation 的执行顺序。</summary>
    public Vector3 LookDir
    {
        get
        {
            float p = pitch * Mathf.Deg2Rad;
            float y = yaw * Mathf.Deg2Rad;
            return new Vector3(
                Mathf.Cos(p) * Mathf.Cos(y),
                Mathf.Cos(p) * Mathf.Sin(y),
                Mathf.Sin(p));
        }
    }

    void Start()
    {
        // Z-up 世界约定：up = +Z。**只在 Start 设一次**——
        // 每帧写transform.up 会让 Transform 重算 rotation，
        // 与ApplyRotation 的 LookRotation 打架，导致视角锁死。
        transform.up = Vector3.forward;

        // 从当前朝向反解 yaw/pitch，保证 Start 后画面不跳变
        Vector3 f = transform.forward;
        pitch = Mathf.Asin(Mathf.Clamp(f.z, -1f, 1f)) * Mathf.Rad2Deg;
        pitch = Mathf.Clamp(pitch, pitchMin, pitchMax);
        yaw = Mathf.Atan2(f.y, f.x) * Mathf.Rad2Deg;
    }

    void Update()
    {
        /* Parkour 模式：只保留**视角**控制，键盘移动交给玩家控制器。
         *
         * 【曾经的 bug】原来这里是 `if (!inputEnabled) return;` 一刀切，
         * 而 inputEnabled 在 Parkour 下为 false —— 结果连
         * HandleLookOnly() 都没被调用过，**鼠标视角完全不生效**。
         * 方法上方的注释写着「HandleLookOnly 仍响应右键环绕」，
         * 但它根本没有调用点，注释与实现矛盾（同类问题见FollowCamera）。
         *
         * 现在的分工：
         *   视角（鼠标）-> 本类负责，两个世界各自的习惯不同
         *   移动（WASD）-> 只有 FreeFly 由本类负责；
         *Parkour 交给 SilkParkourController（两个世界键位故意不共享）
         */
        if (!inputEnabled)
        {
            /* 【关键】自动修复光标锁定状态。
             *
             * Unity 在按 Escape 时会**自动把光标解锁**（lockState -> None），
             * 但我们本地的 lockCursorForLook 标志仍是 true。
             * 此时 Input.GetAxis("Mouse X/Y") 在光标未锁定时**恒返回 0** ->
             * 视角永久失效，且没有任何报错，极难察觉。
             *
             * 这很可能就是用户「视角还是不联动」的真凶：
             * 玩着玩着按了下Escape（或点了编辑器其它面板），
             * 此后鼠标就再也不转视角了。
             *
             * 每帧检查并重新锁定即可自愈，无需玩家手动干预。*/
            if (lockCursorForLook && Cursor.lockState != CursorLockMode.Locked)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
                cursorLocked = true;
            }

            HandleLookOnly();   // Parkour：鼠标控制视角
            return;
        }

        if (Input.GetKeyDown(flipKey))
        {
            dragFollowsMouse = !dragFollowsMouse;
            Debug.Log("[Camera] 拖拽跟手 = " + (dragFollowsMouse ? "开（物体跟手）" : "关（同 Unity 默认 Y-up）"));
        }

        if (Input.GetMouseButton(1))
        {
            /* 拖拽方向：轨道相机旋转时目标恒在画面中心，
             * 用户实际看到的是「场景反向移动」。
             * Python 按屏幕位移实测（相机在 (120,-120,85) 原地转 10°）：
             *   yaw+10  -> 场景横移 −29.5（偏左）   ✓ 鼠标右拖时想要的效果
             *   pitch+10 -> 场景纵移 −33.0（偏下）  ✓ 鼠标下拖时想要的效果
             * 所以两个轴都取 +1。
             * 前一版误按「物体跟手」取符号（−1/+1），实测背景反着走。
             */
            float signX = dragFollowsMouse ? 1f : -1f;
            float signY = dragFollowsMouse ? 1f : -1f;
            yaw += signX * Input.GetAxis("Mouse X") * rotateSpeed;
            pitch += signY * Input.GetAxis("Mouse Y") * rotateSpeed;
            pitch = Mathf.Clamp(pitch, -85f, 85f);
            ApplyRotation();
        }

        float speed = Input.GetKey(KeyCode.LeftShift) ? fastMoveSpeed : moveSpeed;
        Vector3 move = Vector3.zero;

        if (Input.GetKey(KeyCode.W)) move += transform.forward;
        if (Input.GetKey(KeyCode.S)) move -= transform.forward;
        if (Input.GetKey(KeyCode.A)) move -= transform.right;
        if (Input.GetKey(KeyCode.D)) move += transform.right;
        if (Input.GetKey(KeyCode.E)) move += transform.up;      // up ≈ +Z（升高）
        if (Input.GetKey(KeyCode.Q)) move -= transform.up;      // 降低

        transform.position += move.normalized * speed * Time.deltaTime;

        float scroll = Input.GetAxis("Mouse ScrollWheel");
        if (Mathf.Abs(scroll) > 0)
            transform.position += transform.forward * scroll * scrollZoomSpeed;
    }

    /// <summary>按当前 rotation 反解 yaw/pitch。
    ///
    /// 【关键：必须 clamp，否则会锁死俯仰】
    /// 若不夹紧，相机接近正上方/正下方时反解出的 pitch 会逼近 ±90，
    /// 而 pitch=±90 时 LookRotation(dir=(0,0,∓1), up=(0,0,1)) 因dir 与 up
    /// 反向而**退化**，朝向变成垃圾值 -> 球离屏 -> 再次 ResetOrientation，
    /// 形成死循环，把视角锁死在「正上方俯视」。
    /// 用户实测症状：只能在正向/侧向之间徘徊 + 永远俯视着球。
    ///
    /// clamp 到±(pitchMax) 留余量，与 HandleLookOnly 用同一组上下限。
    /// </summary>
    public void ResetOrientation()
    {
        Vector3 f = transform.forward;
        if (f.sqrMagnitude < 0.0001f) return;
        pitch = Mathf.Asin(Mathf.Clamp(f.z, -1f, 1f)) * Mathf.Rad2Deg;
        // 关键：夹紧。留 1 度余量，绝不允许到 ±90（LookRotation 会退化）
        pitch = Mathf.Clamp(pitch, pitchMin, pitchMax);
        yaw = Mathf.Atan2(f.y, f.x) * Mathf.Rad2Deg;
    }

    /// <summary>
    /// Parkour 模式下的视角控制：**鼠标锁定式**（第一人称手感）。
    ///
    /// 与 FreeFly 的关键区别：
    ///   FreeFly —— 按住右键拖动才转视角（编辑用的精确操作）
    ///   Parkour —— 鼠标移动直接转视角，**不需要按任何键**（游戏手感）
    ///
    /// 这是两个世界需求不同的又一体现 —— 编辑要精确，游戏要顺手。
    /// </summary>
    void HandleLookOnly()
    {
        // 鼠标锁定：光标锁在屏幕中心，用 Mouse X/Y 增量直接驱动视角。
        // Cursor.lockState 让 Input.GetAxis("Mouse X/Y") 持续返回增量值。
        if (!lockCursorForLook && !Input.GetMouseButton(1)) return;

        float mx = Input.GetAxis("Mouse X");
        float my = Input.GetAxis("Mouse Y");

        /* 诊断：直接测出「鼠标到底有没有被读到」。
         *
         * 【为什么必须实测】上一轮修了调用点（HandleLookOnly 确实接上了），
         * 但用户仍报告「视角和鼠标不联动」。可能原因有多个且外部看不出：
         *   a) Cursor.lockState 没生效 / Game 窗口没焦点 -> 增量恒为 0
         *   b) Update 顺序导致一帧延迟（仍能动，非全死）
         *   c) 光标被 Escape 释放后未重新锁定
         * 光看代码无法区分，必须把原始输入值打出来。
         *
         * 判读：
         *   mx/my 恒为 0 + lockState 不是 Locked -> 鼠标输入根本没进来（情况 a/c）
         *   mx/my 有值但视角不动-> 旋转写入被覆盖（另查）
         */
        lookDiagTimer += Time.deltaTime;
        if (verboseLookLog && lookDiagTimer > 0.5f)
        {
            lookDiagTimer = 0f;
            /* 【诊断增强】增加「实际朝向 vs 期望朝向」对比。
             *
             * 实测教训：上一版日志只打camFwd，导致我误判为「输入没进来」，
             * 实际真正的问题是 camFwd 被 transform.up 覆盖 —— 
             * yaw/pitch 在变、朝向不变，但因为没打「期望值」看不出矛盾。
             *
             * 现在直接算出期望朝向并与实际对比：
             *   一致 -> ApplyRotation 正常生效
             *   不一致 -> 有别的东西在覆盖 rotation（定位覆盖者）
             * 另外打camUp，因为 transform.up 正是被覆盖的元凶。*/
            Vector3 wantFwd = LookDir;
            Vector3 realFwd = transform.forward;
            float diff = Vector3.Angle(wantFwd, realFwd);

            Debug.Log("[Look] mx=" + mx.ToString("F3") +
                      " my=" + my.ToString("F3") +
                      " | lock=" + Cursor.lockState +
                      " 光标可见=" + Cursor.visible +
                      " | yaw=" + yaw.ToString("F1") +
                      " pitch=" + pitch.ToString("F1") +
                      " 灵敏度=" + lookSensitivity +
                      "\n       期望朝向=" + wantFwd.ToString("F2") +
                      " 实际朝向=" + realFwd.ToString("F2") +
                      " 偏差=" + diff.ToString("F1") + "度" +
                      (diff > 1f ? "  <<< 朝向被覆盖！" : "  (正常)") +
                      "\n       camUp=" + transform.up.ToString("F2") +
                      " camPos=" + transform.position.ToString("F1"));
        }

        if (Mathf.Abs(mx) < 0.0001f && Mathf.Abs(my) < 0.0001f) return;

        float rate = lockCursorForLook ? lookSensitivity : rotateSpeed;
        // Y 为负：鼠标向上推 -> pitch 增大 -> 视线抬高
        // （sign 沿用 dragFollowsMouse，与 FreeFly 已实测的方向一致）
        float signX = dragFollowsMouse ? 1f : -1f;
        float signY = dragFollowsMouse ? 1f : -1f;
        yaw += signX * mx * rate;
        pitch = Mathf.Clamp(pitch + signY * my * rate, pitchMin, pitchMax);

        ApplyRotation();
    }

    [Tooltip("Parkour 模式下锁定光标，让鼠标移动直接控制视角（第一人称手感）")]
    public bool lockCursorForLook = true;

    [Tooltip("光标锁定时的视角灵敏度（度/像素）。右键拖动用rotateSpeed。"
        + "2.2偏慢：跨越 160 度的完整俯仰范围需要约 70 像素移动，"
        + "调到 6.0 后约 27 像素即可转完，接近主流第三人称手感")]
    public float lookSensitivity = 6f;

    [Tooltip("每 0.5 秒输出一次视角诊断（排查「视角不动」时临时开启）。"
           + "**默认关闭** —— 排查完记得关掉，否则会刷满 Console")]
    public bool verboseLookLog = false;

    float lookDiagTimer;

    [Tooltip("俯仰角下限（度）。负值 = 相机升到高处俯视。"
           + "**不要达到 -90**：LookRotation 在 ±90 时因 dir 与 up 反向而退化，"
           + "朝向会变成垃圾值。-80 已足够俯视")]
    public float pitchMin = -80f;

    [Tooltip("俯仰角上限（度）。正值 = 相机降到低处仰视。"
           + "同样不要达到 +90。80 已足够仰视")]
    public float pitchMax = 80f;

    /// 由 yaw(绕Z) + pitch(仰角) 直接构造朝向，+Z 为上。
    /// 不能用 Quaternion.Euler —— 那是 Y-up 硬编码。
    /// 也不能先 qYaw 再 qPitch 叠加：那样 pitch 的基准轴会随 qYaw 漂移，
    /// 实测朝向误差 1.67（几乎反向），画面会转到看不到立方体的地方。
    void ApplyRotation()
    {
        /* 【必须在函数内部夹紧】注释一直写着「先夹紧 pitch」，
         * 但代码从未夹过 —— 又一次注释与实现不一致。
         *
         * 为什么必须在这里（而不是只在 HandleLookOnly）夹：
         * pitch 有多个写入源（HandleLookOnly / ResetOrientation /
         * 外部直接赋值），任何一处漏夹都可能传到 ±90。
         * 而 pitch = ±90 时 cos(p) ≈ 0 -> dir ≈ (0,0,±1)，
         * 与 up=(0,0,1) 反向 -> LookRotation **退化**，
         * 朝向变成不可预测的垃圾值。
         *
         * 实测症状：视角在正向/侧向之间反复跳 + 永远俯视着球
         *（ResetOrientation 反复反解出接近 ±90 的 pitch 所致）。
         *
         * 留 5 度余量：即使用外部绕过 clamp，也退化不了。
         */
        pitch = Mathf.Clamp(pitch, pitchMin, pitchMax);

        float p = pitch * Mathf.Deg2Rad;
        float y = yaw * Mathf.Deg2Rad;
        Vector3 dir = new Vector3(
            Mathf.Cos(p) * Mathf.Cos(y),
            Mathf.Cos(p) * Mathf.Sin(y),
            Mathf.Sin(p));

        // 兜底：dir 退化（长度≈0）时直接返回，保持上一帧有效朝向
        if (dir.sqrMagnitude < 0.0001f) return;

        // 第二参数为 up 参考轴：与 dir 接近平行时 LookRotation 会退化（已由上面 clamp 保证）
        transform.rotation = Quaternion.LookRotation(dir, Vector3.forward);
    }

    /* ---------- Parkour 环绕定位 ---------- */

    /// <summary>
    /// 按当前 yaw/pitch 把相机摆到目标点**背后**，形成第三人称环绕。
    ///
    /// 【为什么必须由朝向推导位置，而不是固定摆位】
    /// 旧实现每帧写 `cam.position = 球 + (0,-dist,h)` + LookAt(球)，
    /// 那是「固定机位 + 强制朝向球」—— 视角被钉死，鼠标完全没用。
    ///
    /// 正确做法（Unity 官方 / 社区共识）：
    ///   1. 鼠标增量 -> yaw/pitch
    ///   2. 相机朝向 = 由 yaw/pitch 构造（ApplyRotation）
    ///   3. **相机位置 = 焦点 - (相机朝向 × 距离)**
    /// 位置由朝向推导，转视角时相机自然绕着目标转，目标始终在画面里。
    ///
    /// 【不要把相机 parent 到目标上】会引入依赖循环（移动相对相机、
    /// 相机又相对目标），造成抖动/ 卡顿，官方明确不建议。
    /// </summary>
    public void OrbitAround(Vector3 targetPos, float distance, float height)
    {
        Vector3 focus = targetPos + Vector3.forward * height;

        /* 用 LookDir（由 yaw/pitch 现算）而不是 transform.forward。
         *
         * 【为什么】transform.forward 依赖 ApplyRotation 已执行，
         * 而本组件 Update 与 ParkourController.LateUpdate 的顺序不保证
         * （Unity 不保证不同组件的 Update 先后）。若LateUpdate 先跑，
         * 读到的就是**上一帧**的朝向 -> 视角更新有一帧延迟，
         * 快速转视角时会有明显的「拖影感」。
         * LookDir（含 pitch）直接由 yaw/pitch 计算，与执行顺序无关。*/
        Vector3 lookDir = LookDir;

        // 反向偏移：相机在焦点后方 = -lookDir * distance
        Vector3 back = -lookDir * distance;

        // 防止穿进场景：从焦点往相机方向探，撞到就贴到命中点前
        RaycastHit hit;
        if (Physics.SphereCast(focus, orbitProbeRadius, back.normalized,
                               out hit, distance, orbitMask,
                               QueryTriggerInteraction.Ignore))
        {
            back = back.normalized * Mathf.Max(hit.distance, 0.5f);
        }

        transform.position = focus + back;
    }

    [Tooltip("环绕时的相机探测球半径（避免相机穿进墙壁）")]
    public float orbitProbeRadius = 2f;

    [Tooltip("相机避障检测层")]
    public LayerMask orbitMask = ~0;
}
/* ===================== 跑酷挂点控制器 =====================
 *
 * 演示「末端挂物体 + 玩家施力」的完整闭环，也是明天跑酷的最小可玩版本。
 *
 * 【设计要点】
 * 玩家不是 Rigidbody，而是一个**位置控制的挂点**：
 * 每帧按输入移动 transform，丝线的末端跟随它。
 * 这样做的好处是天然带速度上限（不会像施力那样无限加速），
 * 物理更稳定，且松手时把末端速度直接交给玩家即可获得「甩出」手感。
 *
 * 【按键】
 *   空格      抓住最近的丝线 / 松手
 *   W/ S      泵力（沿切线加速，荡秋千的「起」与「刹」）
 *   A / D     横推（改变摆动相位）
 *   R         回到起始位置
 *
 * 【物理量级】(Python 实算)
 *   gravity=15、跨度 90 时，速度 25 格/s 对应向心加速度 13.9 = 0.93g，
 *   落在跑酷手感的目标区间 0.5~1.5g 内。故swingSpeed 默认 25。
 */
public class SilkParkourController : MonoBehaviour
{
    [Header("移动")]
    [Tooltip("沿绳摆动速度。25 格/s 约 0.9g，是跑酷手感的目标区间")]
    public float swingSpeed = 25f;

    [Tooltip("泵力加速度（沿切线，格/秒²）。越大越容易把摆荡「起」起来。\n"
        + "★ 用户反馈「摆荡太慢、怎么按都不动」，故从 30 提到 **90**：\n"
        + "   原值下每帧只注入 30×0.0167 = 0.5 格/秒，\n"
        + "   而阻尼每帧吃掉 2.5% —— 按住 4 秒才到 70 格/秒，反馈太弱。\n"
        + "   90 → 每帧 1.5 格/秒，2 秒内可达 ~90 格/秒，接近基础跑速 3.6 倍。\n"
        + "   参照：90 ≈ 1.8g，与 swingSpeed 25（约 0.9g）同一量级。")]
    public float pumpAccel = 90f;

    [Tooltip("横推加速度（垂直绳方向）。控制摆动相位")]
    public float lateralAccel = 18f;

    [Header("脱手飞行")]
    [Tooltip("松手后自由飞行时施加的重力倍率（1=正常重力）")]
    public float flightGravityScale = 1f;

    [Tooltip("脱手飞行的水平速度衰减（每秒保留比例）")]
    public float flightDamping = 0.995f;

    [Header("抓取")]
    public float grabRange = 30f;

    [Header("发射丝线")]
    [Tooltip("射线检测距离，决定最远能粘到哪")]
    public float fireRange = 200f;

    [Tooltip("射线检测层。~0 = 所有层（内墙在 Default 上，能命中）")]
    public LayerMask fireMask = ~0;

    [Tooltip("发射距离过近则忽略，避免退化线段")]
    public float minFireLength = 5f;

    [Header("初始位置")]
    /// <summary>初始位置：落在「区0 平地」的顶面上。
    ///
    /// 平地：中心 (-30, 0, -38)，尺寸 30×44×8 -> 顶面 z = -38 + 4 = -34。
    /// 球半径 4.5 -> **球心 z = -34 + 4.5 = -29.5**。
    ///
    /// 【必须严格等于顶面 + 半径】否则会出现两种问题：
    ///   · 偏高 -> 开局先掉一段，玩家以为「控制不了」
    ///   · 偏低 -> 开局卡在地面里，射线检测异常
    /// 这一条已用 Python 核算：z=-25 会悬空 4.5 格（正好一个半径），
    /// 明显不对，现改为 -29.5。</summary>
    public Vector3 startPosition = new Vector3(-30f, 0f, -29.5f);

    [Header("外观")]
    [Tooltip("自动创建可见球体。没有它就只能从日志判断状态，看不到玩家在哪")]
    public bool autoCreateVisual = true;

    [Tooltip("球的视觉半径（格）。**同时也是碰撞半径、地面吸附高度、边界限制**"
           + "—— 改它等于整体等比缩放玩家。6 -> 4.5（用户要求缩到 3/4）。"
           + "平台间距 30 格，4.5 让球在平台间显得更小、更灵活")]
    public float visualRadius = 4.5f;

    [Tooltip("状态配色：蓝=自由移动 / 黄=抓着丝线 / 绿=正在建锚点")]
    public Color freeColor = new Color(0.4f, 0.8f, 1f);
    public Color attachedColor = new Color(1f, 0.75f, 0.2f);
    public Color buildColor = new Color(0.4f, 1f, 0.5f);
    [Tooltip("朝向指示器的颜色（深红），指向球的正前方")]
    public Color noseColor = new Color(0.9f, 0.25f, 0.2f);

    SilkBuilder builder;
    SilkLine grabbed;                 // 当前抓着哪根线
    Vector3 flightVel;                // 脱手后的飞行速度
    bool isFlying;
    Vector3 bodyVelocity;             // 挂载期间的自身速度（由位置差反推）
    Transform visual;                 // 主角根节点（含身体+朝向指示）
    MeshRenderer bodyRenderer;        // 身体球（状态配色用）
    MeshRenderer noseRenderer;        // 朝向前锥
    SimpleOrbitCamera cam;            // 第三人称时由它跟随
    Camera camComp;                   // ScreenPointToRay 等方法在 Camera 上，不在控制器上

    /// <summary>切回 FreeFly 时相机要还原到的位置（立方体中心附近的斜上方）。</summary>
    Vector3 freeFlyCameraPos = new Vector3(120f, -120f, 85f);

    [Header("构建锚点")]
    [Tooltip("需要先发射多少次丝线，才允许在当前位置构建锚点。"
           + "0=立刻可建，1=先射一次，2=先射两次")]
    [Range(0, 5)] public int shotsToPin = 2;

    /// <summary>已发射次数（不管有没有命中）。达到 shotsToPin 才能就地建锚点。</summary>
    public int shotsFired = 0;

    /// <summary>自己发射出去的丝线。只能断这些，不能断系统生成的网。</summary>
    public List<SilkLine> firedLines = new();

    void Start()
    {
        builder = FindObjectOfType<SilkBuilder>();
        cam = FindObjectOfType<SimpleOrbitCamera>();
        // ScreenPointToRay 定义在 Camera 上，必须单独取
        camComp = cam != null ? cam.GetComponent<Camera>() : Camera.main;

        /* 初始是 FreeFly（编辑器世界）—— **不建球**。
         * 球只属于游戏世界，在编辑器世界里出现会污染画面。
         * 这是之前的设计缺陷：CreateVisual 在 Start 里无条件调用，
         * 导致 FreeFly 下也能看到球。*/
        if (mode == SilkControlMode.Parkour)
        {
            transform.position = startPosition;
            CreateVisual();
            UpdateVisualColor();
            FollowCamera();
        }
    }

    /// <summary>自动创建一个可见球体代表「玩家」。
    /// 没有它就只能靠 Debug.Log 判断状态，看不到本体在哪、
    /// 也判断不出抓丝瞬间有没有跳变。</summary>
    void CreateVisual()
    {
        if (!autoCreateVisual || visual != null) return;

        /* 主角 = 球体 + 朝向指示器。
         * 之前只有一个纯球，看不出朝向 —— 操作时无法判断「面朝哪边」，
         * 也不知道 WASD 往哪个方向走。加入朝向前锥后就直观了。*/
        var go = new GameObject("ParkourBody");
        /* worldPositionStays:false —— 关键！
         * 默认 true 会让 go 保持新建时的世界位置 (0,0,0)不变，
         * 球就被建在了世界原点而不是父物体所在的 (-30,0,-22)，
         * 结果球在画面外（相机跟着父物体走，够不到原点）。
         * 传false 才是「local 归零、世界位置跟随父物体」。*/
        go.transform.SetParent(transform, false);
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = Vector3.one;
        visual = go.transform;

        // 身体：球
        var body = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        body.name = "Body";
        body.transform.SetParent(visual, false);   // 同上：不能保持世界位置
        body.transform.localPosition = Vector3.zero;
        body.transform.localScale = Vector3.one * visualRadius;
        var bc = body.GetComponent<Collider>();
        if (bc != null) Object.Destroy(bc);   // 位置由脚本控制，物理碰撞会打架
        Paint(body, freeColor);
        bodyRenderer = body.GetComponent<MeshRenderer>();

        /* 【地面检测用的触发器碰撞体】
         *
         * 上面把 Collider 销毁了（原意：位置由脚本控制，物理会打架），
         * 但这样一来「向下探测地面的射线」打不到任何东西 ->
         * 球会直接穿过平台掉下去（用户报告「控制不了小球」）。
         *
         * 解法：单独挂一个 isTrigger 的 SphereCollider。
         *   · isTrigger = 不产生碰撞响应，不与物理体相互作用，不会打架
         *   · 能被 Physics.Raycast 命中 -> 地面吸附可用
         *   · 仍会挡住「点选自己」的射线（配合 layer 使用）
         * 半径略小于视觉球，贴合感更好。
         */
        var groundProbe = body.AddComponent<SphereCollider>();
        groundProbe.isTrigger = true;
        groundProbe.radius = 0.9f / Mathf.Max(body.transform.lossyScale.x, 0.0001f);

        // 朝向指示：一个压扁的球体，放在「前方」提示朝向
        // 高度轴是 +Z（项目约定），所以前方用相机水平朝向
        var nose = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        nose.name = "Nose";
        nose.transform.SetParent(visual, false);   // 同上
        nose.transform.localPosition = Vector3.zero;
        nose.transform.localScale = new Vector3(visualRadius * 0.5f,
                                                visualRadius * 0.5f,
                                                visualRadius * 0.9f);
        var nc = nose.GetComponent<Collider>();
        if (nc != null) Object.Destroy(nc);
        Paint(nose, noseColor);
        noseRenderer = nose.GetComponent<MeshRenderer>();

        UpdateVisualFacing();

        /* 诊断：直接验证球是否真的被创建、是否真的会被渲染。
         * 之前反复「看不到球」，而我只能看到 [Follow] 日志 ——
         * 它只证明相机在球外，**不证明球存在或可见**。
         * 这里把球的真实状态全部打出来。*/
        if (verboseFollowLog && body != null)
        {
            var br = body.GetComponent<MeshRenderer>();
            Debug.Log("[Visual] 球已创建" +
                      "\n  body 世界位置 = " + body.transform.position +
                      "\n  body 世界缩放 = " + body.transform.lossyScale +
                      "\n  body 激活 = " + body.activeInHierarchy +
                      "\n  渲染器 = " + (br != null ? "有" : "!! 无") +
                      "\n  渲染器启用 = " + (br != null ? br.enabled.ToString() : "-") +
                      "\n  Shader = " + (br != null && br.material != null
                          ? br.material.shader.name : "!! 无材质") +
                      "\n  颜色 = " + (br != null && br.material != null
                          ? br.material.color.ToString() : "-"));
        }
    }

    /// <summary>给刚建的物件上色。</summary>
    static void Paint(GameObject go, Color c)
    {
        var mr = go.GetComponent<MeshRenderer>();
        if (mr == null) return;
        Shader sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null) sh = Shader.Find("Standard");
        if (sh == null) sh = Shader.Find("Sprites/Default");
        if (sh != null) mr.material = new Material(sh) { color = c };
    }

    /// <summary>
    /// 让「鼻尖」指向球的前进方向。
    /// 优先用运动方向；静止时用相机水平朝向 ——
    /// 这样即使不动，按 W 也有明确的前进方向。
    /// </summary>
    void UpdateVisualFacing()
    {
        if (visual == null) return;

        Vector3 f = bodyVelocity;                 // 挂丝线时的运动方向
        f.z = 0f;
        // 与MoveForward 同源，保证鼻尖指向与实际移动方向一致
        if (f.sqrMagnitude < 0.5f) f = MoveForward;
        if (f.sqrMagnitude < 0.0001f) f = Vector3.right;

        visual.rotation = Quaternion.LookRotation(f.normalized, Vector3.forward);
    }

    /// <summary>
    /// 主角状态配色：
    ///   蓝 = 自由移动　黄 = 抓着丝线　绿 = 正在建锚点
    /// </summary>
    void UpdateVisualColor()
    {
        if (visual == null) return;

        bool building = builder != null && builder.HasPendingNode;
        Color c = building ? buildColor
                : (isFlying ? freeColor : attachedColor);

        if (bodyRenderer != null && bodyRenderer.material != null)
            bodyRenderer.material.color = c;

        UpdateVisualFacing();
    }

    void Update()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        // 模式切换必须在处理输入之前 —— 它决定本帧谁读键盘
        HandleModeSwitch();

        // 只在 Parkour 模式下才跑玩家逻辑，
        // FreeFly 时相机自己响应 WASD，玩家必须完全静止
        if (mode == SilkControlMode.Parkour)
        {
            HandleKeys();
            TickDashCooldown(dt);   // 冲刺冷却与物理同频递减
            CheckFallRespawn();     // 掉出关卡自动回起点（必须在移动之后判断）

            /* 【调度重构】移动不再依赖 isFlying。
             *
             * 原逻辑：`if (isFlying) { ...; UpdateFreeMove(); }`
             * 而发射丝线会把 isFlying 置false —— 于是**只要发射过一次，
             * 就再也走不动了**。这是玩家「控制不了小球」的直接原因。
             *
             * 现在按「有没有抓着丝线」分流，三种状态互不抢控制权：
             *   抓着丝线-> 摆荡物理（UpdateSwing）
             *   空中且有惯性  -> 惯性飞行（UpdateFlight）
             *   其它          -> 地面移动（UpdateFreeMove，永远可控）
             *
             * 【蜘蛛侠2 的动量守恒—— 本次修改的重点】
             * 原代码在「有方向键」时执行 `flatVel = Vector3.zero`，
             * 也就是**玩家一按键就把摆荡攒下的速度清零**，
             * 瞬间从数百格/秒掉回 moveSpeed（当时是450，现为 40），
             * 而且每帧都清、永远追不回。
             *
             * 这违背了《漫威蜘蛛侠2》的核心设计（官方原话）：
             *   "momentum carries between swings"（动量在摆荡之间保留）
             * 玩家的加速来源应该是【摆荡】，而不是【地面跑动】——
             * 官方甚至说明「密集区域地面跑动比反复短摆荡更快」，
             * 说明地面速度只是补充量级。
             *
             * 现在改为：按方向键只**转向**，不清速度；
             * 超出 moveSpeed 的部分由 momentumBleed 平滑收敛，
             * 让摆荡收益逐渐过渡而不是瞬间消失。*/
            if (grabbed != null && !isFlying)
            {
                UpdateSwing(dt);          // 摆荡：末端被丝线驱动
            }
            else if (isFlying)
            {
                if (flightVel.sqrMagnitude > 1f) UpdateFlight(dt);   // 惯性

                /* 玩家主动操控：接管速度但**保留动量**。
                 * 把 flightVel 的水平分量并入 flatVel 而不是丢弃 ——
                 * 这样摆荡攒下的速度不会消失。*/
                if (AnyDirectionKey()) AdoptFlightMomentum();

                UpdateFreeMove(dt);
            }
            else
            {
                UpdateFreeMove(dt);        // 地面常态：始终可控
            }

            UpdateVisualColor();
        }

        /* 相机跟随已移到 LateUpdate —— Unity 的 Update 顺序不保证，
         * 在 Update 里写位置会被 SimpleOrbitCamera 后续的 Update 覆盖。*/
    }

    /// <summary>
    /// LateUpdate 而非 Update：Unity 保证所有 Update 之后才执行 LateUpdate，
    /// 此时相机的位置写入不会被自己的旋转逻辑覆盖，球一定在画面里。
    /// </summary>
    void LateUpdate()
    {
        if (mode == SilkControlMode.Parkour)
        {
            FollowCamera();
            UpdateAimIndicator();   // 自动瞄准的目标指示（视觉引导）
        }

        /*诊断类扫描统一降频。
         *
         * 【为什么必须降频】这三个 Scan* 都调用 FindObjectsOfType<>()，
         * 那是**全场景遍历**，每帧调用在场景复杂时开销明显。
         * 且它们已默认关闭（renderScan 等），这里再加时间闸作为双保险，
         * 避免以后有人打开开关后忘记性能影响。
         * 注意 ScanHealth 自己有 5 秒闸门，无需重复。*/
        if (Time.time - lastScan >= 2f)
        {
            ScanRenderers();
            ScanDuplicates();
            lastScan = Time.time;
        }

        // ScanHealth 自带 5 秒闸门，可直接调用
        ScanHealth();
    }

    float lastScan = -99f;

    /// <summary>实例体检：诊断「同一对锚点被建两次」的**根本原因**。
    ///
    /// 【为什么必须查实例数】实测日志显示：
    ///   [NewLine] 总数 1  去重表size=0
    ///   [Dup] 同一锚点对却有 2 个段、2 个不同父线
    /// 「总数 1」说明这个 SilkBuilder 只建了 1 条线，那第2 条来自**别处**。
    ///
    /// 若场景里有**两个 SilkBuilder** 实例（Bootstrap 的
    /// RuntimeInitializeOnLoadMethod 每次场景加载都执行）：
    ///   · silkLines 各数各的-> 「总数」一直增长
    ///   · lineKeys 各清各的     -> 去重完全失效，同对锚点各建一条
    ///   · anchorLayer 各自一份  -> 两个 builder 的 Update 都响应同一次点击
    /// 这完美解释了「只建一条线却出现两条」且「总数持续增长」。
    ///
    /// 每5 秒打一次，让趋势一目了然。*/
    float lastHealth = -99f;

    void ScanHealth()
    {
        if (Time.time - lastHealth < 5f) return;
        lastHealth = Time.time;

        var builders = FindObjectsOfType<SilkBuilder>();
        var ctrl = FindObjectsOfType<SilkParkourController>();
        var grids = FindObjectsOfType<VoxelGrid>();
        var allSeg = FindObjectsOfType<SilkSegment>();
        var allAnchor = FindObjectsOfType<AnchorPoint>();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[Health] ===== 场景体检 =====");
        sb.AppendLine("  SilkBuilder 实例=" + builders.Length +
                      "  SilkParkourController=" + ctrl.Length +
                      "  VoxelGrid=" + grids.Length);
        sb.AppendLine("  SilkSegment=" + allSeg.Length +
                      "  AnchorPoint=" + allAnchor.Length +
                      "  SilkChain=" + FindObjectsOfType<SilkChain>().Length);
        foreach (var b in builders)
        {
            if (b == null) continue;
            sb.AppendLine("  · SilkBuilder '" + b.gameObject.name +
                          "' 线=" + b.Lines.Count +
                          " 根节点=" + (b.transform.parent != null
                                       ? b.transform.parent.name : "无") +
                          " 世界坐标=" + b.transform.position);
        }
        sb.AppendLine("  >>> SilkBuilder>1 就是本bug 的根因：两份去重表互不干扰");

        /*统计每个 builder 各自建了多少段—— 若某个段的 parentLine
         * 不属于任何 builder 的 silkLines，说明它是孤儿（残留段）。*/
        Debug.Log(sb.ToString());
    }

    /// <summary>全面扫描：列出场景里所有约束链与所有可见丝线段。
    /// 重影问题需要知道「到底有几个渲染源」才能定位，
    /// 凭猜测改代码效率太低。
    ///
    /// 【节流已上移到 LateUpdate】原先这里自带 `Time.time - lastScan < 2f`
    /// /// 闸门，与外层共用同一个 lastScan 变量 ->
    /// ScanRenderers 更新了它，导致 ScanDuplicates 被永久跳过。
    /// 现在统一由 LateUpdate 控制频率（2 秒一次），内部不再重复判断。</summary>
    void ScanRenderers()
    {
        if (!renderScan) return;

        /* 1. 约束链 —— 泄漏的话这里会 >1 */
        var chains = FindObjectsOfType<SilkChain>();
        Debug.Log("[Scan] 约束链数量 = " + chains.Length);
        foreach (var ch in chains)
        {
            if (ch == null) continue;
            Debug.Log("[Scan]   链 " + ch.gameObject.name +
                      " active=" + ch.gameObject.activeInHierarchy +
                      " enabled=" + ch.enabled +
                      " 有渲染段=" + (ch.HasRenderSegment()));
        }

        /* 2. 可见丝线段 —— 逐段列出两端点，重合的就是重影 */
        int visible = 0, hidden = 0, ghost = 0;
        var groups = new Dictionary<string, int>();
        foreach (var seg in FindObjectsOfType<SilkSegment>())
        {
            if (seg == null) continue;
            var lr = seg.GetComponent<LineRenderer>();
            bool on = lr != null && lr.enabled;
            if (!on) { hidden++; continue; }
            visible++;

            /* 残影检测：chainDriven=true 意味着「我不再自己画」，
             * 顶点应由 SilkChain 每帧写入。若没有任何链接管这一段，
             * 它就永远停在最后一帧的顶点上 —— 这就是那根
             * 「长度不一样、僵在屏幕上的第二条线」。*/
            if (seg.chainDriven)
            {
                ghost++;
                Debug.Log("[Scan] !! 残影段 " + seg.gameObject.name +
                          " 被标记 chainDriven 但顶点无人写入，会僵死" +
                          " from=" + seg.from.position + " to=" + seg.to.position);
            }

            if (seg.from == null || seg.to == null) continue;
            var pa = seg.from.position; var pb = seg.to.position;
            string key = (pa.x < pb.x || (pa.x == pb.x && pa.y < pb.y))
                ? pa + "|" + pb : pb + "|" + pa;
            if (!groups.ContainsKey(key)) groups[key] = 0;
            groups[key]++;
        }
        foreach (var kv in groups)
            if (kv.Value > 1)
                Debug.Log("[Scan] !! 同端点被渲染 " + kv.Value + " 层: " + kv.Key);
        Debug.Log("[Scan] SilkSegment 可见 " + visible + " / 隐藏 " + hidden +
                  " / 残影 " + ghost + "，可见点对 " + groups.Count);
    }

/* 3. 精确定位「同一对锚点被建了两次」的来源。
     *
     * 2026-10-06 实测日志：同端点被渲染 2 层，可见 2 / 残影 0。
     * 去重是按体素坐标做的，同一对锚点本不该同时存在 ->
     * 必然是「去重被绕过」或「有段不走CreateSilkLine」。
     *
     * 判据：GameObject 名字直接记录了创建来源
     *   "Seg_A_B"      = SilkBuilder.CreateSilkLine（走走去重，应被挡住）
     *   "SegChain_A"   = SilkLine.SplitSegment断裂时新建（**绕过 lineKeys**）
     *   "Seg"          = SilkLine.CreateSegment（备用，暂未接线）
     * 名字前缀一看就知道是谁干的，无需猜测。
     *
     * 同时打印世界坐标：体素相同但世界坐标不同 = 两个不同的 AnchorPoint
     * 顶到了同一格，这才是「看起来分开」的真正原因。*/
    void ScanDuplicates()
    {
        var groups = new Dictionary<string, List<SilkSegment>>();
        foreach (var seg in FindObjectsOfType<SilkSegment>())
        {
            if (seg == null) continue;
            if (seg.from == null || seg.to == null) continue;
            var pa = seg.from.position; var pb = seg.to.position;
            string key = (pa.x < pb.x || (pa.x == pb.x && pa.y < pb.y))
                ? pa + "|" + pb : pb + "|" + pa;
            if (!groups.ContainsKey(key)) groups[key] = new List<SilkSegment>();
            groups[key].Add(seg);
        }

        foreach (var kv in groups)
        {
            if (kv.Value.Count < 2) continue;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[Dup]锚点对 " + kv.Key + " 被建了 " + kv.Value.Count + " 次：");
            for (int i = 0; i < kv.Value.Count; i++)
            {
                var seg = kv.Value[i];
                string src = seg.gameObject.name.StartsWith("SegChain_") ? "断裂生成"
                           : seg.gameObject.name.StartsWith("Seg_") ? "正常建线"
                           : "其它";
                sb.AppendLine("   #" + i + " 来源=" + src +
                              " 名字=" + seg.gameObject.name +
                              " 状态=" + seg.state +
                              " chainDriven=" + seg.chainDriven +
                              "\n       from 世界坐标=" + seg.from.WorldPosition +
                              " to 世界坐标=" + seg.to.WorldPosition +
                              "\n       父线=" + (seg.parentLine != null
                                       ? seg.parentLine.life.ToString() : "无") +
                              // SilkLine 是普通 C# 类（不是 MonoBehaviour），
                              // 没有 GetInstanceID。用引用地址 + 父线两端点
                              // 代替，足以判断「是不是同一个 SilkLine 对象」。
                              " 父线引用=" + (seg.parentLine != null
                                       ? seg.parentLine.GetHashCode().ToString() : "无") +
                              " 父线两端=" + (seg.parentLine != null
                                       ? seg.parentLine.rootFrom.position + "->" +
                                         seg.parentLine.rootTo.position
                                       : "无"));
            }
            // 两段的 from/to 是否是同一个 AnchorPoint 对象？
            var a0 = kv.Value[0];
            var a1 = kv.Value[1];
            sb.Append("   端点是否同一对象: from " +
                      (a0.from == a1.from ? "同一" : "不同(体素撞车)") +
                      " / to " + (a0.to == a1.to ? "同一" : "不同(体素撞车)"));
            Debug.Log(sb.ToString());
        }
    }

    [Tooltip("扫描场景里被重复渲染的丝线（排查重影）。**默认关闭** —— "
           + "排查重影时临时开启，每2 秒一次输出，排查完记得关掉")]
    public bool renderScan = false;

    SilkControlMode mode = SilkControlMode.FreeFly;

    /// <summary>切模式：Tab 键在自由视角 / 第三人称跑酷之间切换。</summary>
    void HandleModeSwitch()
    {
        if (!Input.GetKeyDown(KeyCode.Tab)) return;

        mode = mode == SilkControlMode.FreeFly
            ? SilkControlMode.Parkour
            : SilkControlMode.FreeFly;

        // Parkour 时关掉相机输入，否则两边同时响应 WASD/QE
        SimpleOrbitCamera.inputEnabled = (mode == SilkControlMode.FreeFly);
        // Parkour 锁光标（鼠标直接控制视角），FreeFly 放光标（要点击建点）
        SimpleOrbitCamera.SetCursorLocked(mode == SilkControlMode.Parkour);

        // 同步告知 SilkBuilder：跑酷模式下左键/R 归玩家，它别抢
        if (builder != null)
        {
            builder.parkourMode = (mode == SilkControlMode.Parkour);
            /* 必须清掉 FreeFly 的「两次点击」选中态。
             * isFirstSelected / firstAnchor 不会自动复位，跨模式残留会让
             * 下一次建线错乱 —— 这是「一次点击出两根线」的根因之一。
             * 切模式是天然的状态边界，任何跨世界的临时状态都该在此清零。*/
            builder.ClearSelectionState();
        }

        if (mode == SilkControlMode.Parkour)
        {
            /* 进游戏世界：建球（之前是 Start 里无条件建，
             * 导致 FreeFly 下也能看到球 —— 两个世界被混在一起了）。*/
            transform.position = startPosition;
            flightVel = Vector3.zero;
            flatVel = Vector3.zero;   // 地面速度也要清，否则带着上一世界的惯性
            vertVel = 0f;         // 垂直速度同理，否则切回来时悬空或卡在跳跃中
            grounded = false;
            ResetDashState();
            isFlying = true;
            CreateVisual();       // 幂等：已存在则直接返回
            UpdateVisualColor();
            logFollowOnce = true; // 每次切模式重打一次诊断
            lookAlignedOnce = false;   // 新世界需重新对齐一次视角
            FollowCamera();       // 立刻摆相机，当帧就能看到球
            // 测试关卡属于游戏世界，FreeFly 下不该存在
            if (builder != null && builder.createTestLevel) SilkParkourStage.Create(StageHalfSize());
        }
        else
        {
            /* 回编辑器世界：销毁球与测试关卡，保持画面干净。
             * FreeFly 属于关卡编辑，不该有玩家角色和跑酷台子。*/
            DestroyVisual();
            SilkParkourStage.Clear();
            if (cam != null)
            {
                cam.transform.position = freeFlyCameraPos;
                cam.ResetOrientation();
            }
        }
        Debug.Log("[Mode] 切换为 " + mode + " | 球 " +
                  (visual != null ? "存在" : "已销毁"));
    }

    /// <summary>测试关卡用的立方体半高。从 SilkBuilder 拿，保持与网格一致。</summary>
    float StageHalfSize()
    {
        var g = builder != null ? builder.grid : null;
        return g != null ? g.GetHalfSize() : 50f;
    }

    /// <summary>销毁主角球。回FreeFly 时调用，让编辑器世界保持干净。</summary>
    void DestroyVisual()
    {
        if (visual != null)
        {
            Object.Destroy(visual.gameObject);
            visual = null;
        }
        bodyRenderer = null;
        noseRenderer = null;
    }

    /// <summary>
    /// 第三人称跟随：**位置由相机朝向环绕推导，绝不写rotation**。
    ///
    /// 【三轮修复的教训】
    ///   第1 轮：每帧 LookAt(球)      -> 覆盖 yaw/pitch，视角锁死
    ///   第 2 轮：只写 position       -> 位置固定在 -Y 侧，转视角时球飘出画面
    ///   第 3 轮（本轮）：OrbitAround -> 位置由朝向推导，转视角相机自然绕球
    ///
    /// 关键：**不要写 rotation**。朝向由 SimpleOrbitCamera 的
    /// yaw/pitch + ApplyRotation 决定（Parkour 下走 HandleLookOnly）。
    /// 本方法只负责把相机摆到「当前朝 向的背后」。
    ///
    /// 另：切模式时做一次初始对齐 —— 此时 yaw/pitch 还是 FreeFly
    /// 留下的旧值，需要先对准球，之后玩家就能自由转视角了。
    /// </summary>
    void FollowCamera()
    {
        if (cam == null)
        {
            if (!warnedNoCam)
            {
                warnedNoCam = true;
                Debug.LogWarning("[Parkour] cam 为空（Start 未执行或相机未找到），无法跟随。球仍会移动。");
            }
            return;
        }

        // 切进Parkour 的第一帧：把相机对准球，之后不再干预玩家视角
        if (!lookAlignedOnce)
        {
            lookAlignedOnce = true;
            AlignCameraToBall();
            if (camComp == null) camComp = cam.GetComponent<Camera>();
        }

        /*环绕定位：相机摆在「当前朝 向的背后」。
         * 朝向由鼠标控制，这里只用它算位置 —— 两边职责清晰，互不覆盖。
         *
         * 【删除了 cam.transform.up = Vector3.forward】
         * 这行是我上轮加的，看似无害，实则每帧改写 transform 的up，
         * 而 Transform 会据此**重算 rotation** —— 与 ApplyRotation 写入的
         * 朝向打架。实测日志证据：
         *   pitch=-17.6 时camFwd 恒为 (0,-1,0)，而按公式应为 (+0.93,+0.21,-0.30)
         * 说明 ApplyRotation 的结果被覆盖了 -> 画面永远朝一个方向，
         * 正是「视角锁死」的直接原因。
         *
         * up 只在 Start 里设置一次即可（Z-up 世界约定），
         * 之后交给ApplyRotation 的 LookRotation(dir, Vector3.forward)。*/
        cam.OrbitAround(transform.position, camDistance, camHeight);

        /* 【已移除】BallOffScreen() -> AlignCameraToBall() 的「安全网」。
         *
         * 它原本是想兜住「球跑出画面」，但实测它本身就是**病根**：
         *   OrbitAround 依据 pitch 把相机摆高-> 球可能短暂离屏
         *   -> AlignCameraToBall 调 ResetOrientation 反解 pitch
         *   -> 反解出的 pitch 逼近 ±90（未clamp）
         *   -> LookRotation 因 dir 与 up 反向而退化，朝向变垃圾
         *   -> 球更离屏 -> 再次 AlignCameraToBall -> **死循环**
         *
         * 用户症状正是这个循环的结果：视角在正向/侧向之间徘徊，
         * 且永远只能俯视着球。
         *
         * 现在修正顺序：ResetOrientation 已加 clamp（根本解决），
         * 且 OrbitAround 由朝向推导位置，天然保证球在画面内，
         * 不需要这个「安全网」。它反而破坏了玩家的视角控制。
         */

        /* 诊断：确认相机在球外且球在画面内。
         * 用户报告「变成第一人称」—— 若相机在球内（距离 < 球半径），
         * 球会填满整个视野或被 near plane 裁掉，看起来就是「看不到球」。
         * 只在切模式时打一次 —— 放在 LateUpdate 里每帧打会刷屏（实测刷了 3000+ 次）。*/
        if (logFollowOnce)
        {
            logFollowOnce = false;
            float dist = Vector3.Distance(cam.transform.position, transform.position);
            bool inside = dist < visualRadius;
            Debug.Log("[Follow] 相机到球 " + dist.ToString("F1") + " 格" +
                      " | 球半径 " + visualRadius +
                      " | " + (inside ? "!! 相机在球内（第一人称）" : "相机在球外（第三人称）") +
                      " | 相机near=" + (camComp != null ? camComp.nearClipPlane.ToString("F2") : "无Camera组件") +
                      " | 球前表面距相机 " + (dist - visualRadius).ToString("F1") + " 格");
        }
    }

    /// <summary>把相机对准球，并同步 yaw/pitch。
    /// 用 ResetOrientation 让 SimpleOrbitCamera 的内部状态与新朝向一致，
    /// 否则它下一帧又会用旧 yaw/pitch 把朝向转回去。
    /// 仅在切进 Parkour 的第一帧调用，之后完全交给玩家。</summary>
    void AlignCameraToBall()
    {
        Vector3 toBall = transform.position - cam.transform.position;
        if (toBall.sqrMagnitude < 0.0001f) return;
        cam.transform.rotation =
            Quaternion.LookRotation(toBall.normalized, Vector3.forward);
        cam.ResetOrientation();
    }

    /// <summary>每次切到 Parkour 时重置，让 [Follow] 诊断重新打一次。</summary>
    bool logFollowOnce = true;

    /// <summary>是否已完成过一次初始对齐（避免每帧干预玩家视角）。</summary>
    bool lookAlignedOnce = false;

    [Tooltip("输出相机跟随诊断（排查「看不到球」时临时开启）。**默认关闭**")]
    public bool verboseFollowLog = false;

    bool warnedNoCam = false;

    [Header("蜘蛛侠式发射")]
    [Tooltip("发射方向的向上抬升角（度）。0=水平前，45=斜上 45 度。"
           + "蜘蛛侠荡过沟壑时是斜向上方发射")]
    [Range(0f, 80f)] public float fireElevation = 35f;

    [Tooltip("勾住目标时的搜索半径。从玩家位置向运动前方找可挂点")]
    public float seekRadius = 60f;

    [Tooltip("左键「以自身为锚点」的吸附半径。身边这个距离内已有可挂点才生效，"
           + "**不会凭空创建锚点**")]
    public float selfSnapRange = 25f;

    [Tooltip("勾住后是否自动进入摆动（否则只是挂着）")]
    public bool autoSwingAfterHook = true;

    [Header("第三人称相机")]
    /* 【换载具时的参数速查】
     *
     * 用户规划：后续要把球换成跑动的人 / 飞机 / 汽车。
     * 这几项**互相耦合**，换载具时必须一起调，只改一个会失衡：
     *
     *   项目          尺寸     camDistance   moveSpeed   visualRadius
     *   跑动的人      高 1.8     4~7         8~12       0.6~0.9
     *   汽车          长 4.5    11~18        30~50      1.2~1.8
     *   球(当前)      直径 9     22~36       350~450    4.5
     *   飞机          翼展 12   30~48        80~150     2~4
     *
     * 经验公式：camDistance ≈ 载具长度 × 2.5~4
     *          moveSpeed   ≈ 载具长度 × 50（跑动的人约 10、汽车约 25）
     *
     * ⚠ visualRadius 是「玩家整体尺寸」，同时用于碰撞半径、
     *   地面吸附高度、场景边界限制 —— 换载具后这项影响最大。
     * ⚠ 高速时务必检查 ResolveGround 的 probe：
     *   单帧位移（moveSpeed/60）不能超过探测范围，
     *   现有实现已用 max(容差+半径, 单帧位移×1.5+半径)兜底。*/
    [Tooltip("相机水平跟随距离（格）。球半径从 6 缩到 4.5 后，"
           + "若不调近则球在画面里显得比原来小 —— 30 -> 24 正好抵消缩小量。"
           + "换载具时按「载具尺寸 × 2.5~4」重设（见上方速查表）")]
    public float camDistance = 20f;

    [Tooltip("相机高于球的高度。必须 < 20（球到顶棚的距离），否则相机穿出顶棚")]
    public float camHeight = 10f;

    /* ---------- 脱手飞行：纯重力 + 阻尼 + 撞墙反弹 ---------- */
    void UpdateFlight(float dt)
    {
        /* ★ 用全局统一的 gravity，不要硬编码 -15f。
         *
         * 【曾存在的 bug】这里原写 `flightVel += new Vector3(0, 0, -15f)`，
         * 那是**重力从 15 时代残留的常数**。后来全局 gravity 改成 50
         * （为解决「上升下降太慢」），这里却没跟着改——
         * 于是松手后球的下落加速度只有正常值的 **30%**，
         * 球像在慢动作里飘荡，用户反馈「摆荡太慢」。
         *
         * 这正是「物理常量在多处写死」的典型后果，与之前
         * `chain.gravity = 15f` 是同一类问题，故统一走 SilkPhysics.Gravity。*/
        flightVel += new Vector3(0, 0, -gravity * flightGravityScale) * dt;
        flightVel *= Mathf.Pow(flightDamping, dt * 60f);

        // 撞墙反弹：直接积分会穿墙（加 Collider 只是让射线能命中，
        // 不会自动阻止 transform 被移过去）。
        // 做法：位移后若已越过墙面（|坐标| > 半高 - 球半径），
        // 退回墙面内侧并把该轴速度取反 —— 相当于「弹一下」。
        Vector3 next = transform.position + flightVel * dt;
        float h = builder != null ? builder.grid.GetHalfSize() : 50f;
        float limit = h - visualRadius;

        if (Mathf.Abs(next.x) > limit)
        {
            next.x = Mathf.Sign(next.x) * limit;
            flightVel.x = -flightVel.x * wallBounce;
        }
        if (Mathf.Abs(next.y) > limit)
        {
            next.y = Mathf.Sign(next.y) * limit;
            flightVel.y = -flightVel.y * wallBounce;
        }
        if (Mathf.Abs(next.z) > limit)
        {
            next.z = Mathf.Sign(next.z) * limit;
            flightVel.z = -flightVel.z * wallBounce;
        }

        transform.position = next;
    }

    [Tooltip("撞墙后的速度保留比例。0=完全弹停，1=原速反弹，<1 有能量损失")]
    [Range(0f, 1f)] public float wallBounce = 0.4f;

    /* ---------- 地面移动：WASD 控球 ---------- */
    /* 【为什么重写】原实现有三个问题，导致玩家「控制不了球」：
     *
     * 1. UpdateFreeMove 只在 `if (isFlying)` 里被调用。
     *    而发射丝线时（FireAtAnchor / TryFireAndHook）会设 isFlying=false
     *    —— 于是**只要发射过一次，就再也走不动了**，只能按 R 重置。
     *    这是「控制不了小球」的直接原因。
     *
     * 2. 没有地面检测：`transform.position += ...` 直接改坐标，
     *    球会笔直穿过平台掉下去。
     *
     * 3. 没有速度插值：按下即瞬移到满速，松开即停，没有加减速。
     *
     * 现在：移动与 isFlying **完全解耦** —— 只要没抓着丝线就始终可控。
     * 抓着丝线时由UpdateSwing 接管（摆荡），松手后自动交还给移动。
     */
    void UpdateFreeMove(float dt)
    {
        // 目标方向：相机水平朝向为「前」，左右取其垂直方向
        Vector3 wish = Vector3.zero;
        if (Input.GetKey(KeyCode.W)) wish += MoveForward;
        if (Input.GetKey(KeyCode.S)) wish -= MoveForward;
        if (Input.GetKey(KeyCode.A)) wish -= MoveRight;
        if (Input.GetKey(KeyCode.D)) wish += MoveRight;
        if (Input.GetKey(KeyCode.E)) wish += Vector3.forward;   // 升高
        if (Input.GetKey(KeyCode.Q)) wish -= Vector3.forward;   // 降低

        bool hasInput = wish.sqrMagnitude > 0.0001f;

        /* 速度用插值逼近目标，而不是直接赋值。
         * 直接赋值 = 按下瞬间满速、松开瞬间停死，非常生硬。
         * 用加速度 + 减速度分开控制，松手有惯性余韵，手感更自然。*/
        Vector3 targetVel = hasInput
            ? wish.normalized * moveSpeed * (Input.GetKey(KeyCode.LeftShift) ? sprintMultiplier : 1f)
            : Vector3.zero;

        /* 【动量守恒 · 关键】先在**同方向**上收敛到目标速度，
         * 只对超出上限的部分做减速 —— 这样摆荡攒下的高速
         * 不会被MoveTowards 一帧砍掉，而是平滑地衰减下来。
         *
         * 蜘蛛侠2 的核心是「动量在摆荡之间保留」，
         * 玩家加速来自摆荡而非地面跑动。
         * 若这里直接MoveTowards(flatVel, moveSpeed)，
         * 摆荡速度会在一帧内掉到 moveSpeed，手感完全不对。*/
        if (flatVel.sqrMagnitude > targetVel.sqrMagnitude + 0.01f &&
            Vector3.Dot(flatVel, targetVel) > 0f)
        {
            // 超速但方向大致一致 -> 只削减超出量，保留动量
            float excess = flatVel.magnitude - targetVel.magnitude;
            float bleed = Mathf.Min(excess, momentumBleed * dt);
            if (bleed > 0f)
                flatVel = flatVel.normalized * (flatVel.magnitude - bleed);
        }
        else
        {
            // 未超速（或反向）-> 正常插值
            float rate = hasInput ? groundAccel : groundDecel;
            flatVel = Vector3.MoveTowards(flatVel, targetVel, rate * dt);
        }

        if (flatVel.sqrMagnitude > 0.0001f || Mathf.Abs(vertVel) > 0.0001f)
        {
            /* 垂直运动：重力 + 起跳初速度。
             * 与水平速度分开积分 —— 跳跃是抛物线，
             * 水平速度不该被垂直方向影响。*/
            if (!grounded || vertVel > 0f)
            {
                vertVel -= gravity * dt;
                // 最高点之后 vertVel 会变负 —— 那就是下落
                if (vertVel < -terminalVel) vertVel = -terminalVel;
            }

            Vector3 next = transform.position
                         + flatVel * dt
                         + Vector3.forward * (vertVel * dt);

            /* 地面吸附：向下探一小段，若脚下有面就把球贴上去。
             * 没有这一步的话，纯坐标改写会让球直接穿过平台。
             * 跳跃下落时同样依赖它来判定落地。*/
            bool wasAirborne = !grounded;
            next = ResolveGround(next, wasAirborne, ref vertVel);

            transform.position = next;
        }

        bodyVelocity = flatVel;
        UpdateVisualFacing();
    }

    [Tooltip("重力加速度（格/秒²）。默认取 SilkPhysics.Gravity（全局统一值 50）。"
        + "**用户反馈「上升和下降都太慢、跳跃有延迟」-> 原 15 提到 50。**"
        + "原值下上升 1 秒 + 下降 1 秒（来回 2 秒），"
        + "视觉上像「没跳起来」，落地后又被地面吸附粘住，整体发糊。"
        + "改这里会同时影响丝线摆荡（建链时读 SilkPhysics.Gravity）。")]
    public float gravity = SilkPhysics.Gravity;

    [Tooltip("下落的最大速度（格/秒），避免越掉越快。"
        + "重力 50 时从6.8 格落下约需 v=√(2×50×6.8)≈26 格/秒，"
        + "故 120 有充足余量；仅在从极高处坠落时才会触发")]
    public float terminalVel = 120f;

    /// <summary>
    /// 地面判定与落地处理。跳跃加入后，这一件事要同时负责：
    ///   1. 走上平台时把球吸上去（防穿透）
    ///   2. 下落时检测到地面 -> 置 grounded、垂直速度归零
    ///
    /// 【探测距离】必须至少覆盖**单帧位移**（水平 + 垂直），
    /// 否则高速/高跳时会「跳过」地面：
    ///   moveSpeed=450、dt=1/60 -> 单帧水平 7.5 格；
    ///   垂直速度 120 时单帧 2 格 -> 合计近 10 格，
    ///   而原 probe 只有 0.6 + 4.5 = 5.1 格 —— 会漏检。
    ///
    /// ref vertVel：落地时把垂直速度按 landBounce 衰减（默认完全归零）。
    /// </summary>
    Vector3 ResolveGround(Vector3 desired, bool wasAirborne, ref float vertVel)
    {
        if (camComp == null)
        {
            grounded = false;
            return desired;
        }

        float radius = visualRadius;
        Vector3 origin = desired + Vector3.forward * radius;

        // 探测距离 = 半径 + 容差 + 单帧位移（水平与垂直都要算）
        float frameMove = (flatVel.magnitude + Mathf.Abs(vertVel)) * Time.deltaTime;
        float probe = groundSnapDistance + radius + frameMove * 1.5f;

        bool hitGround = Physics.Raycast(origin, -Vector3.forward, out RaycastHit hit,
                                         probe, groundMask,
                                         QueryTriggerInteraction.Ignore);

        if (hitGround)
        {
            Vector3 surface = hit.point + Vector3.forward * radius;

            // 上升时不吸附（要往上跳，不能被地面拉住）
            if (vertVel > 0f && desired.z > surface.z) { grounded = false; return desired; }

            // 下落或贴地 -> 吸附到表面
            if (wasAirborne && vertVel < 0f)
            {
                // 落地：垂直速度按 landBounce 衰减（默认 0 = 完全弹停）
                vertVel *= landBounce;
                grounded = true;
                // 落地时解除「本次滞空只能用一次冲刺」的限制
                ResetDashOnLanding();
                if (verboseFireLog && Mathf.Abs(vertVel) > 1f)
                    Debug.Log("[Jump] 落地，保留垂直速度 " + vertVel.ToString("F1"));
            }
            else grounded = true;

            return surface;
        }

        // 没打到地面：空中
        grounded = false;
        return desired;
    }

    [Tooltip("地面吸附的容差（格）。实际探测距离会再加上「单帧位移 × 1.5」，"
           + "保证高速与跳跃时也不会漏检地面")]
    public float groundSnapDistance = 0.6f;

    [Tooltip("地面层（用于向下吸附，防止球穿过平台）。默认全部，"
           + "测试关卡的方块未设自定义层，用 Everything 最稳")]
    public LayerMask groundMask = ~0;

    [Tooltip("地面加速度（格/秒²）。**约为 moveSpeed 的 10 倍**"
        + "（即约 0.10 秒到全速），手感干脆不拖沓。\n"
        + "★ 与 moveSpeed 同比例下调（50→25 时 500→250）：\n"
        + "   若不同步降，速度减半后到全速只需 0.05 秒，\n"
        + "   球会「一按就粘在地上」，失去加速的推力感。\n"
        + "每次改 moveSpeed 都要按比例同步调整本值")]
    public float groundAccel = 250f;

    [Tooltip("地面减速度（格/秒²）。略低于加速度，松开后有短暂余韵。"
        + "同样需与 moveSpeed 保持约 8.4 : 10 的比例")]
    public float groundDecel = 210f;

    [Tooltip("按住左 Shift 的速度倍率（加速跑）。\n"
        + "★ 与冲刺共用左 Shift，这是**有意为之**：\n"
        + "   按下瞬间 -> 冲刺给一个瞬时速度冲量（DoDash）\n"
        + "   按住期间 -> 持续加速到 moveSpeed × 本倍率\n"
        + "两者叠加手感连贯：先「弹」出去，再「推」着走。")]
    public float sprintMultiplier = 1.8f;

    [Tooltip("超出 moveSpeed 的动量每秒衰减多少（格/秒）。"
           + "摆荡攒下的速度会以此平滑收敛到 moveSpeed，"
           + "而不是被 MoveTowards 一帧砍掉 —— 这是蜘蛛侠2「动量守恒」的关键")]
    public float momentumBleed = 260f;

    /// <summary>
    /// 玩家在惯性飞行中按下方向键时调用：**接管但保留动量**。
    ///
    /// 【为什么不能直接 flatVel = Vector3.zero】
    /// 旧代码一按方向键就把水平速度清零，玩家从摆荡攒下的高速
    /// 瞬间掉回 moveSpeed，摆荡收益全部消失 ——
    /// 这正是「感觉移动很慢」的病根（数值调再高也没用）。
    ///
    /// 现在把 flightVel 的水平分量并入 flatVel，
    /// 后续由 UpdateFreeMove 里的 momentumBleed 平滑收敛。
    /// </summary>
    void AdoptFlightMomentum()
    {
        Vector3 horiz = new Vector3(flightVel.x, flightVel.y, 0f);
        if (horiz.sqrMagnitude <= 0.0001f) return;

        // 取两者较大值：若玩家已跑得比飞行快，不应被拉慢
        if (horiz.sqrMagnitude > flatVel.sqrMagnitude)
            flatVel = horiz;

        // 清掉已并入的飞行速度，避免 UpdateFlight 重复积分
        flightVel = Vector3.zero;
    }

    /* ---------- 冲刺（Dash）---------- */

    /// <summary>冲刺冷却计时（剩余秒数）。<= 0 表示可用。</summary>
    float dashCooldown = 0f;

    /// <summary>本帧是否刚用掉冲刺 —— 用于落地/切换时重置冷却计数。</summary>
    bool dashUsedThisAirborne = false;

    /// <summary>
    /// 冲刺：沿视线方向施加**瞬时速度冲量**，地面与空中都能用。
    ///
    /// 【为什么需要它 —— 摆荡的节奏全靠「松手那一瞬的速度」】
    /// 没有冲刺时，玩家只能靠「一直按住方向键」这种笨办法调速度；
    /// 有了冲刺就有了**主动控制节奏**的手段。
    /// 这正是《幽灵行者》冲刺/滑铲/跑墙被当作「动量累积器」的意义。
    ///
    /// 【设计取舍（对标三款标杆游戏）】
    /// · 幽灵行者：空中按住 Dash = 时间减速，松手 = 从新位置冲出。
    ///   本作不做时间减速（那是第一人称视角的设计），
    ///   只保留「瞬时冲量」这一半 —— 因为摆荡游戏的核心是速度，不是时间操控。
    /// · 消逝之光2：钩爪是「真实的绳子」而非传送。
    ///   同理，冲刺**只是加速**而不是「瞬移」——
    ///   它给球一个速度增量，位置仍然靠速度积分推进，保持物理连续性。
    ///
    /// 【关键：不能覆盖已有速度】
    /// 冲刺是**叠加**在当前速度之上的增量，不是设为固定值。
    /// 若直接赋值，就破坏了「动量是核心资产」这条铁律
    /// （本项目在 flatVel 上已经踩过一次这个坑）。
    /// </summary>
    void DoDash()
    {
        if (dashCooldown > 0f) return;

        /* 空中每次滞空只允许一次冲刺（《幽灵行者》的规则）。
         *
         * 【为什么需要这条限制】
         * 没有限制时玩家会在空中连按冲刺，速度无上限地累积，
         * 摆荡的「节奏感」就没了 —— 变成一路加速直到撞墙。
         * 限制成「一次滞空一次」后，冲刺变成**决策**：
         * 「现在冲，还是留着等下一次摆荡？」
         * 这正是幽灵行者冲刺玩法的核心张力。
         * 地面不限制（滑行/地面加速可自由叠加）。*/
        if (!grounded && dashUsedThisAirborne) return;

        // 方向：优先沿视线水平方向；按住 W 时沿当前速度方向
        //（两者一致时冲量收益最大 —— 因为速度是矢量，方向一致才能真正提速）
        Vector3 dir = MoveForward;
        if (flatVel.sqrMagnitude > 0.0001f && Input.GetKey(KeyCode.W))
            dir = new Vector3(flatVel.x, flatVel.y, 0f).normalized;

        // 叠加水平冲量
        Vector3 boost = dir * dashImpulse;
        flatVel += new Vector3(boost.x, boost.y, 0f);

        // 空中冲刺额外给一点上升 —— 让冲刺能接续跳跃，
        // 做出「跳 -> 冲刺 -> 拉高 -> 再摆」的节奏（消逝之光的做法）
        if (!grounded) vertVel += dashLift;

        dashCooldown = dashCooldownTime;
        dashUsedThisAirborne = true;

        if (verboseFireLog)
            Debug.Log("[Dash] 冲刺！水平速度 -> " + flatVel.magnitude.ToString("F0") +
                      "，垂直 -> " + vertVel.ToString("F0"));
    }

    /// <summary>冲刺冷却递减。在 Update 的 Parkour 分支里调用。</summary>
    void TickDashCooldown(float dt)
    {
        if (dashCooldown > 0f) dashCooldown -= dt;
    }

    /// <summary>着地时调用：解除「本次滞空只能用一次冲刺」的限制。</summary>
    void ResetDashOnLanding()
    {
        dashUsedThisAirborne = false;
    }

    /// <summary>切模式 / R 重置 / 松手时清空冲刺状态。
    ///
    /// 【为什么必须清】与 vertVel / grounded 同理 ——
    /// 这些状态若残留，会出现「刚切进游戏world就处于冲刺冷却」
    /// 「落地后仍不能冲刺」等怪现象。状态机的每个进入点都要清全。</summary>
    void ResetDashState()
    {
        dashCooldown = 0f;
        dashUsedThisAirborne = false;
    }

    [Header("摆荡")]
    [Tooltip("进入摆荡时注入动量的比例（1.0 = 完全保留玩家当前速度）。\n"
        + "这是《蜘蛛侠2》「momentum carries between swings」的核心实现 ——\n"
        + "  · 1.0：完全保留，荡到最低点速度最高（最接近标杆）\n"
        + "  · 0.5：保留一半，荡得起来但不会太夸张\n"
        + "  · 0.0：回到旧行为（从静止开始摆），会显得「挂在那晃」\n"
        + "用户反馈「摆荡太慢」，此值是最直接的调节旋钮。")]
    [Range(0f, 1.5f)] public float swingEntryBoost = 1.0f;

    [Header("冲刺")]
    [Tooltip("冲刺的瞬时速度增量（格/秒）。**叠加**在当前速度上，不是设为固定值 —— "
        + "覆盖速度会破坏「动量是核心资产」这条铁律。\n"
        + "★ 与 moveSpeed 同比例下调（50→25 时42→21）：\n"
        + "   冲刺是**相对增幅**，若只降moveSpeed 不降它，\n"
        + "   增幅会从 1.84 倍变成 2.68 倍 —— 冲刺变得过强，\n"
        + "   空中连按两次就能飞出关卡。")]
    public float dashImpulse = 21f;

    [Tooltip("空中冲刺额外附加的上升速度（格/秒）。"
        + "让冲刺能接续跳跃，做出「跳→冲刺→拉高→再摆」的节奏。\n"
        + "注意：额外高度 = v²/(2g) = 14²/100 ≈ **1.96 格** —— "
        + "设 6 时只有 0.36 格，几乎感觉不到，达不到「拉高再松手」的效果")]
    public float dashLift = 14f;

    [Tooltip("冲刺冷却（秒）。0.3 秒左右既不打断节奏，又能防止连按刷速度")]
    public float dashCooldownTime = 0.3f;

    /// <summary>水平移动速度（XZ 平面），已做插值。
    /// 独立于 flightVel（那是空中惯性），两者互不干扰。</summary>
    Vector3 flatVel = Vector3.zero;

    /* ---------- 跳跃 ---------- */

    /// <summary>垂直速度（+Z 为上）。与 flatVel 分开，
    /// 因为跳跃是抛物线运动，水平速度不该受它影响。</summary>
    float vertVel = 0f;

    /// <summary>是否站在地面上（跳跃/落地的判据）。</summary>
    bool grounded = true;

    /// <summary>
    /// ⚠ **当前无调用点**（键位精简后未接线）—— 保留待用。
    ///
    /// 【原职责】空格作为上下文键：地面跳 / 空中抓丝线。
    /// 【为何不再接线】用户精简键位时明确「空格只保留跳跃」，
    /// 空中抓线改由**左键**承担（与发射同一套自动瞄准，语义更统一）。
    ///
    /// 【何时重新启用】若日后想让空中能「抓已有的丝线」而不是发射新线，
    /// 把调用点加回 HandleKeys 即可（前提是再引入一个独立按键）。
    ///
    /// 依赖：<see cref="TryGrab"/>。
    /// </summary>
    void HandleJumpOrGrab()
    {
        if (grounded || vertVel <= 0.01f)
        {
            // 地面（或刚落地）：普通跳跃
            DoJump();
            return;
        }

        /* 空中：先尝试抓**已有的**丝线，抓不到才发射新的。
         *
         * 【为什么要区分】
         * 蜘蛛侠2 里这两种是不同的操作：
         *   ·抓已有丝线 = 空中调整轨迹、重新借力（连贯动作）
         *   · 发射新丝线 = 建立全新连接（重新起摆）
         * 之前只有后者（FireAtAnchor），玩家在空中无法利用已有的线，
         * 只能不断新建 —— 动作会显得「每一下都是重新开始」，
         * 少了连续摆荡的流畅感（幽灵行者说的「无缝衔接」）。
         *
         * TryGrab 此前一直无调用点（selfcheck 7b 扫出来的功能缺口），
         * 现在接在这里：附近有可抓的线就抓，没有才发射。*/
        if (!TryGrab()) FireAtAnchor();
    }

    /// <summary>普通跳跃。给一个向上的初速度，之后由重力接管。
    /// 水平速度**保留** —— 蜘蛛侠式起跳不应该打断跑动节奏。</summary>
    void DoJump()
    {
        vertVel = jumpSpeed;
        grounded = false;
        if (verboseFireLog)
            Debug.Log("[Jump] 起跳，垂直速度 " + jumpSpeed +
                      "（水平速度保留 " + flatVel.magnitude.ToString("F0") + "）");
    }

    [Header("跳跃")]
    [Tooltip("起跳的垂直初速度（格/秒）。重力 50 时最高点 = v²/(2g) = 26²/100 = 6.8 格，"
        + "上升 0.52 秒。\n"
        + "取值权衡：台阶最大落差 4 格，apex 需≥ 5 格才留得住余量；"
        + "而 22 只有 4.8 格（余量 0.8）太紧，跳不过时会让人很挫败。\n"
        + "对照：重力 15 时同样的高度要 1 秒才升得上 —— "
        + "这正是用户反馈「上升下降都太慢、跳跃有延迟」的原因。")]
    public float jumpSpeed = 26f;

    [Tooltip("落地时垂直速度的衰减（1=完全弹停）")]
    [Range(0f, 1f)] public float landBounce = 0f;

    /// <summary>移动的「前方」= 视线方向在水平面上的投影。
    ///
    /// 【为什么用 LookDirFlat 而不是 cam.transform.forward】
    /// 后者依赖 ApplyRotation 已执行，而本组件 Update 与相机组件的
    /// Update 顺序不保证 —— 读到的可能是上一帧朝向，导致「按W 往哪走」
    /// 和「画面朝哪」对不上。LookDirFlat 由 yaw 现算，与顺序无关，
    /// 且与 OrbitAround 同源，保证移动方向与画面方向永远一致。
    ///
    /// 相机被鼠标环绕改变后，W 的方向跟着变，符合第三人称直觉。</summary>
    Vector3 MoveForward
    {
        get
        {
            if (cam == null) return Vector3.forward;
            Vector3 d = cam.LookDirFlat;
            if (d.sqrMagnitude < 0.0001f) return Vector3.forward;
            return d;
        }
    }

    /// <summary>移动的「右方」= 前方在水平面内顺时针转 90°。
    /// 不直接用 cam.transform.right，因为相机的 up 是 +Z，
    /// right 在俯视时会退化。</summary>
    Vector3 MoveRight
    {
        get
        {
            Vector3 f = MoveForward;
            return new Vector3(-f.y, f.x, 0f);
        }
    }

    /// <summary>是否按了移动/摆动键。用来判断玩家是否在主动操作 ——
    /// 没按任何键时，惯性飞行才继续；按了就立刻接管为可控移动。</summary>
    bool AnyDirectionKey()
    {
        return Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.S) ||
               Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.D) ||
               Input.GetKey(KeyCode.E) || Input.GetKey(KeyCode.Q);
    }

    [Header("自由移动")]
    [Tooltip("WASD 移动速度（格/秒）。\n"
        + "历程：35 → 450（错误）→ 40 → 50 → **25**。\n"
        + "· 450 格/秒 ≈ 1620 km/h，比跑车快 10 倍，是错的\n"
        + "· 用户反馈「有点快」-> 50 砍半到 25\n"
        + "· 25×3.6 = 90 km/h，仍属游戏化跑酷的快档位\n"
        + "★ 联动参数：dashImpulse / groundAccel / groundDecel "
        + "都按同比例下调，否则相对强度会失衡（见各自注释）。")]
    public float moveSpeed = 25f;

    /* ---------- 挂荡：球跟随丝线末端 ---------- */

    /// <summary>
    /// 摆荡：球**必须跟随丝线末端**，泵力只用来给末端施加力。
    ///
    /// 【★ 这是一个曾经存在的根本缺陷 —— 修复记录】
    /// 原实现只用 `transform.position += tangential * (pump * accel * dt * dt)`
    /// 靠加速度积分「把球推开」，**球的位置从不跟随丝线末端**。
    /// 结果（用户反馈「莫名其妙把球和锚点连起来又莫名断开」）：
    ///   · 球与丝线末端是两个互不相干的物体 -> 视觉上分离 =看起来「断了」
    ///   · 不按 WASD 时 pump=0 -> 球停着不动，丝线在别处摆 -> 更是「断开」
    ///   · 玩家看到的现象：球「飞向」锚点，然后线就断了
    ///     实际上线没断，只是球不在末端上了。
    ///
    /// 【正确的物理关系】
    /// 丝线末端的位置由 SilkChain 的约束求解决定（摆动中心 + 摆动半径）。
    /// 玩家（球）是挂在末端上的**载荷** —— 它的位置应该**等于**末端位置。
    /// 玩家按键的作用是**对末端施加力**（改变摆动方向/加速摆动），
    /// 而不是直接位移自己。这是蜘蛛侠式摆荡的标准做法：
    ///   按方向键 = 荡向那个方向（身体随之被拉过去）
    ///   松手       = 脱离丝线，末端速度成为自己的初速度
    /// </summary>
    void UpdateSwing(float dt)
    {
        if (grabbed == null) { isFlying = true; return; }

        // 末端速度即本物体的真实速度（由 SilkChain 从位置差反推）
        Vector3 endVel = grabbed.chain != null
            ? grabbed.chain.GetEndVelocity()
            : Vector3.zero;

        Vector3 tangential, lateral;
        if (grabbed.chain != null) grabbed.chain.GetSwingAxes(out tangential, out lateral);
        else { tangential = Vector3.forward; lateral = Vector3.right; }

        float pump = Input.GetAxis("Vertical");
        float side = Input.GetAxis("Horizontal");

        bool hasInput = Mathf.Abs(pump) > 0.01f || Mathf.Abs(side) > 0.01f;

        if (hasInput)
        {
            grabbed.StartSwing();

            /* 泵力作用在**末端**上 —— 让摆动加速/改变方向，
             * 而不是直接把球位移出去。
             * 改用速度冲量（dt 的一次项）而非位移（dt 的二次项）：
             *   位移 = a*dt^2 在 dt=0.016 时极小，泵力几乎无效
             *   （实测 1 秒只累积 1.8 格，是目标值的 14%）
             * 速度冲量则每帧稳定生效，手感才跟得上。*/
            if (Mathf.Abs(pump) > 0.01f)
                grabbed.chain.AddEndVelocity(tangential * (pump * pumpAccel * dt));
            if (Mathf.Abs(side) > 0.01f)
                grabbed.chain.AddEndVelocity(lateral * (side * lateralAccel * dt));
        }

        /* ★ 核心修正：球的位置 = 丝线末端的位置。
         *
         * 必须在施力**之后**取 —— 这样本帧的泵力立刻反映到位置上，
         * 玩家能看到「按了键身体就被拉过去」，反馈才跟得上手。
         * 物理链（SilkChain）是在它自己的 Update 里解的，
         * 故这里读到的是上一帧解出的末端位置 ——
         * 相差一帧（16ms），肉眼无法察觉。*/
        if (grabbed.chain != null)
            transform.position = grabbed.chain.GetEndPosition();

        bodyVelocity = endVel;
    }

    /* ---------- 按键 ---------- */
    void HandleKeys()
    {
        /* ============================================================
         *  跑酷世界按键表（经用户精简，2026-10-07）
         * ============================================================
         *   WASD / QE  移动 / 升降
         *   空格跳跃
         *   鼠标左键   发射丝线（必须在空中）
         *   鼠标右键   松开丝线
         *   左 Shift   冲刺（按住则持续加速）
         *   C          固化当前位置为节点
         *   R          回起点
         *   Tab        切换 FreeFly / Parkour
         *   鼠标移动   改变视角（无需按键）
         *
         *  【已按用户要求移除】
         *   B  斜上发射 —— 与左键功能重叠
         *   V  结网     —— 暂不需要
         *   X  断自发线 —— 断裂不归玩家控制
         *   G  断视线线 —— 同上
         *  上述方法体均**保留**（未删），只是不再有调用点，
         *  日后若要恢复玩法直接加回调用即可。
         * ============================================================ */

        /* 空格：**只保留跳跃**。
         * 原先是上下文键（地面跳 / 空中抓丝线），用户精简时明确只要跳跃。
         * 空中抓线若日后需要，用左键（与发射同一套自动瞄准）。*/
        if (Input.GetKeyDown(KeyCode.Space)) DoJump();

        /* 左 Shift：冲刺（Dash）——瞬时速度冲量，地面/空中都能用。
         *
         * 【为什么用左 Shift】原本左 Shift 是「加速跑」+「松手」两用、
         * 右 Shift 才是冲刺。用户精简后要求「Shift 保留冲刺就够了」，
         * 于是统一到左 Shift，一个键一种语义。
         *
         * 【按住 Shift 时仍会加速跑】那是 moveSpeed × sprintMultiplier 的
         * 持续加速（见 UpdateFreeMove），与按下瞬间的冲刺冲量叠加，
         * 手感连贯：先「弹」出去，再「推」着走。*/
        if (Input.GetKeyDown(KeyCode.LeftShift)) DoDash();

        /* 鼠标右键：松开丝线。
         *
         * 【为什么用右键】参考《消逝的光芒2》——右键松手是动作游戏惯例。
         * 且右键拖拽转视角与滚轮缩放都只在 FreeFly 分支生效，
         * 在跑酷世界本来空着，正好拿来用。*/
        if (Input.GetMouseButtonDown(1)) DoRelease();

        /* 鼠标左键：**自动瞄准**并发射丝线（不再需要玩家点选）。
         * 详见 FireAtAnchor / PickBestAnchor 的注释——
         * 手动点击在100³ 网格 + 高速移动下几乎不可用。
         *
         * 【本作是「丝线」不是钩爪】玩家发射的是蛛丝，
         * 末端连到锚点后可以摆荡。
         *
         * 【必须在空中才能发射 —— 参考《消逝的光芒2》的操作契约】
         * 官方操作说明原文：「**在跳跃过程中**按 L2/LT 释放抓钩，
         * 将其作为绳索摆动」。
         *
         * 为什么这条约束重要：它让每次发射都对应一次**主动的跳跃决策**。
         * 若允许站在平台上手指发射，玩家就会退化成「站在原地按左键」，
         * 摆荡的节奏感（起跳→勾住→摆→松手→再起跳）完全消失——
         * 那正是消光2 与我们当前最大的体感差别。*/
        if (Input.GetMouseButtonDown(0) && RequireAirborne("左键"))
            FireAtAnchor();

        /* C：把**当前所在位置**固化成玩家节点。
         *
         * 语义（与两个世界约定一致）：固化的是「玩家此刻悬停的那个点」，
         * 位置取球的当前位置由 CreateAnchorAt 吸附到网格并做 5 格去重
         * —— 所以不会凭空在墙面上造点，它只会复用附近已存在的格点。
         *
         * 用途：固化后的节点带 PlayerNode 标记与更大的碰撞体，
         * 既能作为后续发射/摆荡的挂点，也能被静态丝线连起来结网。*/
        if (Input.GetKeyDown(KeyCode.C)) PinCurrentNode();

        if (Input.GetKeyDown(KeyCode.R))
        {
            RespawnAtStart("玩家按 R");
        }
    }

    /// <summary>
    /// C 键：把玩家**当前所在位置**固化成可复用的玩家节点。
    ///
    /// 【为什么不凭空造点】CreateAnchorAt 内部会把世界坐标吸附到网格，
    /// 并在 5 格内复用已存在的锚点。所以这里固化的是「玩家此刻悬停的
    /// 那一格」，与「两个世界都不允许凭空创建锚点」的约定一致 ——
    /// 它只是把脚下这格标记成 PlayerNode，让它可被丝线粘住、可作挂点。
    ///
    /// 固化后：标记 PlayerNode（视觉更大、碰撞半径更大、醒目颜色），
    /// 后续发射/摆荡可以勾住它，也能用静态丝线把它连进网里 ——
    /// 这就是用户要的「结网」基础积木。
    /// </summary>
    void PinCurrentNode()
    {
        if (builder == null) return;

        var node = builder.ExecutePinNode(
            new SilkPinNodeSignal(transform.position, selectAsStart: false));

        if (node == null)
        {
            Debug.Log("[Parkour] 固化失败：当前格子附近没有可用的锚点");
            return;
        }

        UpdateVisualColor();   // 让球的颜色反映新状态
        Debug.Log("[Parkour] 已固化节点 " + node.position +
                  "（当前线 " + builder.Lines.Count + " 条）");
    }

    /// <summary>
    /// 回到起点并清空所有速度与状态。
    ///
    /// 【为什么要抽成方法】原先这段逻辑内联在 R 键处理里，
    /// 但「掉落自动重生」也需要完全相同的一整套清理 ——
    /// 若复制一份，将来加新状态时必然漏改其中一处
    /// （本项目已因「重置漏清垂直状态」踩过坑）。
    /// </summary>
    void RespawnAtStart(string reason)
    {
        DoRelease();
        transform.position = startPosition;

        // 四种速度/状态全清 —— 任何一项残留都会导致刚重生就异常：
        //   flightVel  飞行惯性 -> 带着上一段的速度冲出起点
        //   flatVel    地面速度 -> 同上
        //   vertVel    垂直速度 -> 卡在跳跃中或悬空
        //   grounded   接地标志 -> 空中能误跳
        flightVel = Vector3.zero;
        flatVel = Vector3.zero;
        vertVel = 0f;
        grounded = false;
        ResetDashState();       // 冲刺冷却
        isFlying = true;

        if (builder != null) builder.CancelPendingNode();
        FollowCamera();

        if (verboseFireLog) Debug.Log("[Respawn] " + reason);
    }

    /// <summary>掉出关卡时自动重生。
    ///
    /// 【为什么必须有】原先没有任何掉落兜底 —— 玩家从窄道掉下去后
    /// 会无限下坠，**永远回不到关卡**，只能重启程序。
    /// 对基础关卡来说这是致命的：参考视频反复强调
    /// 「失败要快、重生要快，鼓励不断尝试」，
    /// 连幽灵行者的做法都是「死亡瞬间重生、失败不惩罚」。
    ///
    /// 触发线取「内墙底部再往下 20 格」——
    /// 内墙范围是 ±50，平台顶面在 -30 左右，
    /// 掉到 -70 就说明已经彻底离开关卡，此时拉回起点最合理。
    /// </summary>
    void CheckFallRespawn()
    {
        if (transform.position.z > fallRespawnZ) return;

        if (verboseFireLog)
            Debug.Log("[Respawn] 掉出关卡（z=" +
                      transform.position.z.ToString("F1") + " < " +
                      fallRespawnZ.ToString("F0") + "），自动回起点");
        RespawnAtStart("掉落自动重生");
    }

    [Tooltip("掉到比这个高度更低就自动回起点（格）。"
        + "内墙范围 ±50、平台顶面约 -30，取 -70 意味着"
        + "「已彻底离开关卡」，此时重生最合理。"
        + "设为正数可关闭该功能")]
    public float fallRespawnZ = -70f;

    /// <summary>
    /// 要求玩家当前处于「空中」才允许发射钩爪。
    ///
    /// 【依据】《消逝的光芒2》的官方操作说明：
    /// 「**在跳跃过程中**按 L2/LT 释放抓钩，将其作为绳索摆动」。
    ///
    /// 【为什么必须加这条约束】
    /// 允许站在平台上随手发射，会让摆荡退化成「站在原地按左键」——
    /// 起跳→勾住→摆→松手→再起跳 这个节奏循环消失，
    /// 而那正是蜘蛛侠/消光2 移动手感的核心。
    ///
    /// 【为什么地面要给出提示而不是静默忽略】
    /// 静默忽略会让玩家以为按键坏了。明确告知「需要先跳起来」
    /// 才能把规则讲清楚 —— 对应参考视频里强调的引导原则。
    /// </summary>
    bool RequireAirborne(string action)
    {
        if (!grounded && vertVel > 0.01f) return true;

        if (verboseFireLog)
            Debug.Log("[Airborne] " + action + " 需要先跳起来 —— " +
                      "钩爪只能在空中发射（参考消逝之光2 的操作契约）");
        return false;
    }

    /// <summary>
    /// Parkour 的左键：**一键把「自己」和「目标锚点」连起来**。
    ///
    /// 与 FreeFly 的关系（**语义**相同，实现已分离）：
    ///   FreeFly  左键① 选中锚点 A     → 左键② 选中锚点 B   → 建线 A-B
    ///   Parkour  左键  目标锚点 T→ 一步建线 自己-T
    ///
    /// 【实现为何分离】曾试图让Parkour 连调OnAnchorPicked 两次来复用同一套
    /// 状态机，结果造成「一次点击建出两根线」。原因是 OnAnchorPicked 依赖
    /// 共享字段 isFirstSelected / firstAnchor，而切模式时它们不会复位：
    /// FreeFly 下单击过一次就会留下脏状态，导致 call1 被误当成「第二个点」。
    /// 详见 ConnectForParkour 的注释。切模式时已加 ClearSelectionState 兜底。
    /// </summary>
    void FireAtAnchor()
    {
        if (builder == null) return;

        /* 【自动瞄准，不再要求玩家点选】
         *
         * 原来这里是鼠标射线点击，在本作几乎不可用：
         *   · 锚点分布在 100³ 网格、间距 8 格，屏幕上很小
         *   · 玩家高速移动中点击 -> 几乎必错
         *   · 鼠标同时还要控制视角 -> 双重负担
         * 这是设计层面的错误，不是实现 bug。
         *
         * 蜘蛛侠2 就是自动瞄准：玩家给方向意图，游戏自己算最佳落点。
         * 现在照这个思路：按评分挑最优锚点（见 PickBestAnchor）。
         *
         * 键位仍不与 FreeFly 共享：
         *   FreeFly 左键点选（关卡编辑要精确）
         *   Parkour 左键自动瞄准（游戏要顺手）*/
        /* 摆荡中不允许再发射 —— 必须先松手。
         *
         * 【为什么】参考《消逝的光芒2》与蜘蛛侠2：一次只挂一根丝。
         * 若摆荡中还能发射，玩家会不断在锚点间「瞬移」，
         * 既不是摆荡也不是飞行，完全失去「荡出去」的手感 ——
         * 用户反馈的「点击其他锚点会建新线」正是这个问题。
         *
         * 正确节奏是：发射 → 摆荡 → 松手 → 再发射。*/
        if (grabbed != null)
        {
            if (verboseFireLog)
                Debug.Log("[Fire] 已在摆荡中 —— 先按左Shift 松手再发射");
            return;
        }

        var target = PickBestAnchor();
        if (target == null) return;   // PickBestAnchor 内部已说明原因

        // 独立入口建线：自己 → 目标。不走 FreeFly 的两次点击状态机
        var line = builder.ConnectForParkour(selfNode, target);
        if (line == null) return;     // 太近或重复
        if (!firedLines.Contains(line)) firedLines.Add(line);

        // 末端挂到目标点-> 进入摆荡
        /* ★ 关键修正：这里**不能**用 Attach(target)。
         *
         * Attach(AnchorPoint) 会调用 DriveEndTo(anchor) ——
         * 把丝线末端**锁死在锚点上**（endDriven = true）。
         * 后果：丝线根本不会摆动，只是被钉在锚点。
         * 玩家看到的现象正是用户反馈的：
         *   「莫名其妙把球和锚点连起来，莫名其妙断开来」——
         *   线是「连上」了（钉住了），但球与末端位置脱钩，
         *   球靠加速度积分乱飘 -> 看起来球和线分离了。
         *
         * 正确做法：末端**自由**（不Drive），由 SilkChain 的约束求解
         * 决定位置 —— 玩家是挂在末端的载荷，按方向键对末端施力。
         * 这样才有真正的摆荡。*/
        line.AttachSelf(builder, "丝线末端");
        line.StartSwing();
        grabbed = line;
        isFlying = false;

        /* ★★ 动量注入：把玩家当前的运动速度交给丝线末端。
         *
         * 【这是「一钩飞出去」与「挂在那晃」的分水岭】
         * 标杆（蜘蛛侠2 / 消逝之光2）的核心设计原则是
         * 「momentum carries between swings」—— 动量在摆荡之间保留。
         * 玩家跑着跳出去、钩住锚点时，摆荡**从零开始**是错的：
         * 那样玩家会看到球「挂在绳上小幅晃动」，而不是「划出去」。
         *
         * 【注入什么速度】
         * 玩家在空中的真实速度 = 水平(flatVel) + 垂直(vertVel)。
         * 两者都要给：
         *   · 水平速度 -> 决定摆的幅度与「甩出去」的距离
         *   · 垂直速度 -> 若正在下落，进入摆荡时是加速的（自由落体）
         * 若只给水平，落差摆荡的感觉就丢了。
         *
         * 【为什么用 AddEndVelocity 而不是直接赋值】
         * 保持「动量是核心资产」的铁律 —— 丝线末端原有速度
         * （建链时的残余）会被叠加保留，而不是被覆盖清零。*/
        Vector3 carry = flatVel + Vector3.forward * vertVel;
        if (carry.sqrMagnitude > 0.0001f && line.chain != null)
            line.chain.AddEndVelocity(carry * swingEntryBoost);

        if (verboseFireLog)
            Debug.Log("[Fire] 已连接自己 → " + target.position +
                      "（评分 " + lastAnchorScore.ToString("F1") + "）"
                      + "｜注入动量 " + (carry * swingEntryBoost).magnitude.ToString("F0")
                      + " 格/秒（实际速度 " + carry.magnitude.ToString("F0") + " × "
                      + swingEntryBoost.ToString("F2") + "）");
    }

    [Tooltip("输出自动瞄准的选点结果（调自动瞄准手感时临时开启）")]
    public bool verboseFireLog = false;

    float lastAnchorScore;

    /* ---------- 自动瞄准的目标指示器 ----------
     *
     * 【为什么需要它 —— 参考《基于瞬移能力的关卡练习》】
     * 那个视频里传送落点用**白色圆盘**明确标示「该站在哪」，
     * 出发点用黄色 L 形标记，目标点用黄色方块 ——
     * 玩家靠画面就能读懂「我要去哪、会落在哪」。
     *
     * 我们用 PickBestAnchor 自动选点，替玩家做了判断，
     * 但玩家因此**失去了预期** —— 按左键之前不知道会钩到哪，
     * 钩完也不知道球会挂在哪、朝哪摆。
     * 自动化的代价是「信息不对等」，必须用视觉反馈补回来。
     *
     * 所以：选中候选点时高亮它，并在玩家与目标之间画一条指引线。
     * 这样「自动瞄准」从黑箱变成可预期的动作。*/

    LineRenderer aimLine;
    Transform aimMarker;

    /// <summary>当前被指示的目标（每帧刷新）。</summary>
    AnchorPoint indicatedAnchor;

    /// <summary>创建指示器的视觉物件。幂等。</summary>
    void EnsureAimIndicator()
    {
        if (aimLine == null)
        {
            var go = new GameObject("AimLine");
            go.transform.SetParent(transform, false);
            aimLine = go.AddComponent<LineRenderer>();
            Shader sh = Shader.Find("Sprites/Default");
            if (sh == null) sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh != null) aimLine.material = new Material(sh)
            {
                color = new Color(1f, 0.85f, 0.2f, 0.55f)
            };
            aimLine.widthMultiplier = 0.6f;
            aimLine.positionCount = 2;
            aimLine.useWorldSpace = true;
            aimLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            aimLine.receiveShadows = false;
            go.transform.SetParent(builder != null ? builder.transform : transform, false);
        }

        if (aimMarker == null)
        {
            // 用一个压扁的球做「目标环」，比方块更轻、不遮挡视线
            var m = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            m.name = "AimMarker";
            var col = m.GetComponent<Collider>();
            if (col != null) Object.Destroy(col);   // 只作视觉，不参与碰撞
            m.transform.localScale = new Vector3(visualRadius * 0.9f,
                                                 visualRadius * 0.9f,
                                                 visualRadius * 0.9f);
            var mr = m.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                Shader sh2 = Shader.Find("Sprites/Default");
                if (sh2 != null) mr.material = new Material(sh2)
                {
                    color = new Color(1f, 0.85f, 0.2f, 0.35f)
                };
            }
            m.transform.SetParent(builder != null ? builder.transform : transform, false);
            aimMarker = m.transform;
        }
    }

    /// <summary>显示指向某个锚点的指引。传 null 则隐藏。
    /// 每帧调用（由UpdateAimIndicator 驱动）。</summary>
    void ShowAimIndicator(AnchorPoint target)
    {
        indicatedAnchor = target;

        bool show = target != null && target.AnchorAlive && builder != null
                    && visual != null;      // visual 为 null 说明不在 Parkour

        if (aimLine != null) aimLine.enabled = show;
        if (aimMarker != null) aimMarker.gameObject.SetActive(show);
        if (!show) return;

        Vector3 from = transform.position + Vector3.forward * visualRadius;
        Vector3 to = target.WorldPosition;

        aimLine.positionCount = 2;
        aimLine.SetPosition(0, from);
        aimLine.SetPosition(1, to);

        aimMarker.position = to;
    }

    /// <summary>每帧刷新指示器（LateUpdate 里调）。
    /// 只在「未抓着丝线且在地面附近」时显示 —— 摆荡中不需要瞄准提示。</summary>
    void UpdateAimIndicator()
    {
        EnsureAimIndicator();

        bool shouldShow = mode == SilkControlMode.Parkour
                       && grabbed == null          // 摆荡中不显示
                       && !isFlying               // 空中惯性飞行时不显示
                       && !dashUsedThisAirborne   // 刚冲刺过就不显示，避免干扰
                       /* 【新增】只有在「空中」才显示。
                        * 因为钩爪现在必须空中才能发射（RequireAirborne），
                        * 地面显示指示器会误导玩家「按左键就能勾住」。*/
                       && !grounded;

        if (!shouldShow)
        {
            ShowAimIndicator(null);
            return;
        }

        ShowAimIndicator(PickBestAnchor());
    }

    /// <summary>
    /// 自动瞄准：在视野内挑一个「最适合当前状态」的锚点。
    ///
    /// 【评分维度】模拟蜘蛛侠2 的「最佳落点」判断，按重要性排序：
    ///   1. **必须够高**（目标高于自己）—— 摆荡要先获得势能，
    ///      否则荡不起来。这是物理前提，不满足直接排除。
    ///   2. **距离适中** —— 太近够不着摆的幅度；太远会飞过头。
    ///      用「接近理想距离」的程度评分（抛物线型曲线）。
    ///   3. **在前方** —— 顺着视线方向最符合直觉。
    ///   4. **高度收益** —— 越高越能持续摆荡，但权重低于距离。
    ///
    /// 效果就是：新手总在「够高 + 距离合适 + 在前方」的锚点上荡，
    /// 而游戏帮他把这个判断做掉了。
    /// </summary>
    AnchorPoint PickBestAnchor()
    {
        Camera c = camComp != null ? camComp : (cam != null ? cam.GetComponent<Camera>() : null);
        if (c == null) return null;

        var all = FindObjectsOfType<AnchorPoint>();
        Vector3 selfPos = transform.position;
        Vector3 facing = MoveForward;          // 视线水平方向
        if (facing.sqrMagnitude < 0.0001f) facing = Vector3.forward;

        AnchorPoint best = null;
        float bestScore = float.MinValue;
        lastAnchorScore = 0f;
        int candidates = 0;

        foreach (var a in all)
        {
            if (a == null || !a.AnchorAlive) continue;
            if (a.type == AnchorType.SilkNode || a.type == AnchorType.Internal) continue;

            Vector3 wp = a.WorldPosition;
            Vector3 to = wp - selfPos;
            float dist = to.magnitude;
            if (dist < minAnchorDistance || dist > fireSearchRange) continue;

            // 维度1：必须够高。留一点容差，允许平飞（贴天花板横移）
            if (wp.z < selfPos.z + minAnchorHeightGain) continue;

            // 视野内才考虑：用 dot 而非视锥判定，避免近距离时视角退化
            Vector3 dirTo = to.normalized;
            if (Vector3.Dot(c.transform.forward, dirTo) < autoAimCone) continue;

            candidates++;

            float score = 0f;

            // 维度2：距离 —— 越接近理想距离越高（抛物线型）
            float ideal = Mathf.Lerp(minAnchorDistance, fireSearchRange * 0.85f, 0.5f);
            float norm = (dist - ideal) / ideal;
            score += (1f - Mathf.Clamp01(norm * norm)) * 3f;

            // 维度3：前方优先
            score += Vector3.Dot(dirTo, facing) * 1.5f;

            // 维度4：高度收益
            score += Mathf.Clamp01((wp.z - selfPos.z) / Mathf.Max(fireSearchRange, 1f)) * 0.8f;

            if (score > bestScore)
            {
                bestScore = score;
                best = a;
            }
        }

        lastAnchorScore = bestScore;

        if (best == null)
        {
            if (verboseFireLog)
                Debug.Log("[Fire] 无合适锚点（候选 " + candidates +
                          "；需距离 " + minAnchorDistance + "~" + fireSearchRange +
                          "、高出 " + minAnchorHeightGain + "）");
            return null;
        }

        if (verboseFireLog)
            Debug.Log("[Fire] 候选 " + candidates + " 个→ 选中 " + best.position +
                      " 距离 " + Vector3.Distance(selfPos, best.WorldPosition).ToString("F1") +
                      " 评分 " + bestScore.ToString("F2"));
        return best;
    }

    [Header("自动瞄准")]
    [Tooltip("自动搜索锚点的最大距离（格）")]
    public float fireSearchRange = 90f;

    [Tooltip("目标锚点至少要比自己高出多少（格）。"
           + "摆荡要先获得势能荡不起来 —— 这是物理前提，不够高的直接排除")]
    public float minAnchorHeightGain = 3f;

    [Tooltip("目标锚点的最小距离（格）。太近则摆的幅度不足")]
    public float minAnchorDistance = 12f;

    [Tooltip("瞄准锥：锚点方向与视线夹角的余弦下限。"
           + "0.45 ≈ 63 度锥角，越小越要求锚点在正前方")]
    [Range(0.1f, 0.95f)] public float autoAimCone = 0.45f;

    /// <summary>
    /// 自己这个「锚点」。Parkour 世界的丝线起点。
    /// **每次访问都重新按当前位置解析** —— 玩家一直在移动，
    /// 若缓存一个锚点不更新，建线起点就会停在初始位置。
    ///
    /// 【曾发生的 bug】原先是「存在即返回」的缓存写法：
    ///     if (_selfNode != null && _selfNode.AnchorAlive) return _selfNode;
    /// 结果球移动后 selfNode 仍指向最初那个锚点 —— 用户反馈
    /// 「在新旧位置之间构建一条线」正是这个原因：
    /// 线看起来从「上次的位置」连到「新锚点」。
    ///
    /// 【为什么仍要复用而不是每次新建】
    /// CreateAnchorAt 有 5 格去重逻辑，同一位置反复调用会返回
    /// 已有的锚点，不会堆出一堆重合点。所以每次调用它是安全的。
    /// </summary>
    AnchorPoint selfNode
    {
        get
        {
            if (builder == null) return null;
            // 不再缓存 —— 每次都按当前位置解析/新建。
            // CreateAnchorAt 内部有距离去重，同位置会复用同一锚点。
            _selfNode = builder.CreateAnchorAt(transform.position);
            return _selfNode;
        }
    }
    AnchorPoint _selfNode;


    /// <summary>
    /// 蜘蛛侠式发射：沿「运动前方 + 上抬」方向发射，命中后自动勾住并进入摆动。
    ///
    /// 与水平发射的关键差别：方向是斜上方的。Python 实算（爬升 35 度）：
    ///   射程 61 格 -> 水平 50 格 + 上升 35 格，正好跨过 50 格的沟壑。
    /// 所以射程要开大（fireRange 默认 200），才能跳过大沟。
    /// </summary>
    void TryFireAndHook()
    {
        if (builder == null) return;
        if (cam == null) cam = FindObjectOfType<SimpleOrbitCamera>();
        if (cam == null) return;

        Vector3 origin = transform.position;

        // 基准方向：有速度时沿运动方向（真正的「前进」），否则用相机朝向
        Vector3 flat = bodyVelocity.sqrMagnitude > 1f
            ? new Vector3(bodyVelocity.x, bodyVelocity.y, 0f)
            : new Vector3(cam.transform.forward.x, cam.transform.forward.y, 0f);
        if (flat.sqrMagnitude < 0.0001f) flat = Vector3.forward;
        flat = flat.normalized;

        // 按爬升角抬起（Z 为高度轴）
        float e = fireElevation * Mathf.Deg2Rad;
        Vector3 dir = (flat * Mathf.Cos(e) + Vector3.forward * Mathf.Sin(e)).normalized;

        if (!Physics.Raycast(origin, dir, out RaycastHit hit, fireRange, fireMask))
        {
            shotsFired++;
            Debug.Log("[Parkour] 发射未命中（射程 " + fireRange + "），计数 " +
                      shotsFired + "/" + shotsToPin);
            return;
        }
        if (Vector3.Distance(origin, hit.point) < minFireLength) return;

        // 生成丝线
        SilkEventBus.Post(new SilkFireSignal(origin, hit.point, autoAttach: false));
        var line = LastFiredLine();
        if (line == null) return;
        if (!firedLines.Contains(line)) firedLines.Add(line);

        // 把末端挂到命中点 -> 进入摆荡
        var tip = builder.CreateAnchorAt(hit.point);
        if (tip == null) return;
        line.Attach(tip, builder, "命中点");
        if (autoSwingAfterHook) line.StartSwing();
        isFlying = false;
        grabbed = line;
        shotsFired++;   // 计数达到 shotsToPin 后才允许就地建锚点
        Debug.Log("[Parkour] 勾住 " + hit.point + " 距离 " +
                  Vector3.Distance(origin, hit.point).ToString("F1"));
    }

    /// <summary>Parkour 世界的划断：切断视线中心指向的那根丝线。
    /// 与 FreeFly 的「右键单击断最近线」是两套独立实现 ——
    /// 因为两个世界的操作习惯不同，不共享。</summary>
    void CutLineUnderCrosshair()
    {
        if (builder == null) return;
        if (cam == null) cam = FindObjectOfType<SimpleOrbitCamera>();
        if (cam == null) return;

        // 从相机中心发射线，用最近中点判定命中。
        // ScreenPointToRay 在 Camera 组件上，SimpleOrbitCamera 没有这个方法。
        if (camComp == null) camComp = cam.GetComponent<Camera>();
        if (camComp == null) return;
        Ray ray = camComp.ScreenPointToRay(
            new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f));
        SilkLine best = null;
        float bestD = 20f;   // 视线附近20 格内算命中
        foreach (var line in builder.Lines)
        {
            if (line == null || !line.CanBeCut) continue;
            Vector3 mid = (line.rootFrom.WorldPosition + line.rootTo.WorldPosition) * 0.5f;
            Vector3 cp = ray.origin + ray.direction * Vector3.Dot(mid - ray.origin, ray.direction);
            float d = Vector3.Distance(mid, cp);
            if (d < bestD) { bestD = d; best = line; }
        }

        if (best == null)
        {
            Debug.Log("[Cut] 视线内没有可断的线");
            return;
        }
        if (best == grabbed) DoRelease();
        SilkEventBus.Post(new SilkBreakSignal(SilkBreakCause.PlayerRelease)
            .AtNormalized(0.35f).On(best));
        Debug.Log("[Cut] 断开视线所指的丝线");
    }

    /// <summary>取最近一根自己发射的线（刚生成的在列表末尾）。</summary>
    SilkLine LastFiredLine()
    {
        if (builder == null || builder.Lines.Count == 0) return null;
        return builder.Lines[builder.Lines.Count - 1];
    }

    /// <summary>把当前位置固化成可粘附节点。
    /// 之后它可以：被动态丝线粘住 / 作为结网的连接点 / 当墙面挂点使用。</summary>
    void PinHere()
    {
        SilkEventBus.Post(new SilkPinNodeSignal(transform.position));
        Debug.Log("[Parkour] 固化当前位置为节点");
    }

    /// <summary>静态结网：把自己固化过的节点两两连起来。
    /// 先固化几个点再按 V，就能在空中构建出自己的挂点网络。</summary>
    void TrySpanNodes()
    {
        if (builder == null) return;

        var nodes = new List<AnchorPoint>();
        foreach (var a in builder.Lines.Count > 0 ? AllAnchors() : new List<AnchorPoint>())
            if (a != null && a.type == AnchorType.PlayerNode) nodes.Add(a);

        if (nodes.Count < 2)
        {
            Debug.Log("[Parkour] 至少需要 2 个已固化的节点（当前 " + nodes.Count + " 个）。先按 C 固化。");
            return;
        }

        // 依次把相邻的节点连起来，形成网络
        int made = 0;
        for (int i = 0; i + 1 < nodes.Count; i++)
        {
            SilkEventBus.Post(new SilkSpanSignal(nodes[i].WorldPosition,
                                              nodes[i + 1].WorldPosition));
            made++;
        }
        Debug.Log("[Parkour] 结网 " + made + " 条（" + nodes.Count + " 个节点）");
    }

    /// <summary>取场景中所有锚点（供结网筛选 PlayerNode）。</summary>
    System.Collections.Generic.List<AnchorPoint> AllAnchors()
        => new System.Collections.Generic.List<AnchorPoint>(FindObjectsOfType<AnchorPoint>());

    /// <summary>断开自己发射的第一根丝线。系统生成的网不能断。</summary>
    void CutFirstFiredLine()
    {
        while (firedLines.Count > 0)
        {
            var line = firedLines[0];
            // 空引用或状态不允许断裂的（已摆动/已脱手）直接丢弃
            if (line == null || !line.CanBeCut)
            {
                firedLines.RemoveAt(0);
                continue;
            }
            // 若正抓着这根，先松手
            if (line == grabbed) DoRelease();
            RequestCut(line);
            firedLines.RemoveAt(0);
            break;
        }
    }

    void RequestCut(SilkLine line)
    {
        if (line == null) return;
        // 断在靠近玩家的一端，让玩家这一侧脱手
        var signal = new SilkBreakSignal(SilkBreakCause.PlayerRelease)
            .AtNormalized(0.35f)      // 从低处往高处算 0.35
            .On(line);
        SilkEventBus.Post(signal);
    }

    /// <summary>抓最近的 Static 状态丝线。</summary>
    /// <summary>尝试抓住附近**已有**的丝线。返回是否抓成功。
    ///
    /// 【为什么返回 bool】空中按空格要先试抓、失败才发射新线
    /// （见 HandleJumpOrGrab）—— 调用方需要知道结果才能决定下一步。
    /// </summary>
    bool TryGrab()
    {
        if (builder == null) return false;

        SilkLine best = null;
        float bestD = grabRange;
        Vector3 me = transform.position;

        foreach (var line in builder.Lines)
        {
            if (line == null || line.life != SilkLifeState.Static) continue;
            Vector3 mid = (line.rootFrom.WorldPosition + line.rootTo.WorldPosition) * 0.5f;
            float d = Vector3.Distance(me, mid);
            if (d < bestD) { bestD = d; best = line; }
        }

        if (best == null) return false;

        // 通过事件总线发布抓住事件，并**指定目标线** ——
        // 不指定的话 ExecuteGrab 会用它自己的 pickRadius(15) 重新找，
        // 而这里的 grabRange 是 30，两处半径不一致会抓到不同的线，
        // 造成「以为抓住了、实际没挂上」的状态错乱。
        SilkEventBus.Post(new SilkGrabSignal(transform, "玩家", startSwing: true).On(best));
        grabbed = best;
        isFlying = false;
        if (verboseFireLog)
            Debug.Log("[Grab] 抓住已有丝线");
        return true;
    }

    /// <summary>松手：把末端速度交给自身，然后转入脱手飞行。</summary>
    void DoRelease()
    {
        if (grabbed != null)
        {
            // 取丝线末端的真实速度作为飞行初速度 —— 「甩出去」手感的来源
            if (grabbed.chain != null) flightVel = grabbed.chain.GetEndVelocity();
            grabbed.Release();
            grabbed = null;
        }
        isFlying = true;

        /* 垂直状态也要重置。
         *
         * 【为什么要】本函数原先只重置 isFlying，但抓丝线期间
         * grounded/vertVel 可能处于任意状态（起跳后被抓住、
         * 摆荡中垂直速度已被物理改写）。松手后若不清理：
         *   · vertVel 残留 -> 下次落地被误判成「还在上升」
         *   · grounded 残留 true -> 空中按空格会错误地再跳一次
         * 统一收敛到「空中、无垂直速度」的一致状态。*/
        vertVel = 0f;
        grounded = false;
        ResetDashState();

        // 只在真的松过手时才打 —— DoRelease 由空格触发，频率低，
        // 但玩家若连按会刷屏，故加个开关。默认不输出。
        if (verboseReleaseLog)
            Debug.Log("[Parkour] 松手，末端速度 " + flightVel.magnitude.ToString("F1"));
    }

    [Tooltip("输出松手速度诊断（排查「动量是否保留」时临时开启）")]
    public bool verboseReleaseLog = false;

    /// <summary>供 UI 查询：当前是否抓着丝线。</summary>
    public bool IsSwinging => grabbed != null && !isFlying;
}
