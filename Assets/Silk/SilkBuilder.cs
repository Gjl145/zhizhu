using System.Collections.Generic;
using UnityEngine;

/* ============================================================
 *   SilkWebSystem.cs  ——  正方体蛛网（自然掉落版）
 *   X=左右  Y=前后  Z=高度（重力方向）
 *
 *   左键       连丝
 *   右键单击   断丝（1/4~1/2 随机，自然掉落）
 *   右键拖拽   旋转视角
 *   WASD       飞行移动
 *   QE          升降
 *   Shift       加速
 *   滚轮        微移
 *   R           取消
 *   C           清空
 *   G           织网
 *   X           老化
 * ============================================================ */

public enum SilkState { Intact, Broken, Fading }

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
        chain.gravity = 15f;
        chain.subdivisions = 8;

        /* 参数必须与 SplitSegment 保持一致，否则同一根线在
         * 「抓住」和「断裂」两条路径下形态不同 —— 用户会看到
         * 抓住时线突然绷直、断裂时线保持弧度。
         * 抓住时线还没被破坏，所以沿用当前段的弧度与松弛系数。*/
        var seg0 = segments.Count > 0 ? segments[0] : null;
        chain.catenarySag = seg0 != null ? seg0.sagRatio : 0.07f;
        chain.slackScale = 1.15f;
        chain.maxStrain = 0.25f;

        chain.Build(rootFrom, rootTo, this, 1f);
        // 渲染不再需要「让位」—— SilkChain 只用那一个 SilkSegment 画线，
        // 形状由物理节点决定，没有第二个渲染源。
    }

    /// <summary>把丝线恢复到静态状态：销毁约束链、解除渲染接管、
    /// 交回给 SilkSegment 的悬链线渲染。</summary>
    public void RestoreStatic()
    {
        // 1. 先解除渲染接管，否则约束链销毁后没人写顶点，线会僵住
        foreach (var seg in segments)
            if (seg != null)
            {
                seg.chainDriven = false;
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

        segments.Remove(oldSeg);

        // 上半截：保留为约束链，由 SilkChain 驱动自然摆动
        // 下半截：按需求直接丢弃，不创建任何段
        chainGO = new GameObject("SilkChain_" + rootFrom.position + "_" + rootTo.position);
        chainGO.transform.SetParent(builder.transform);
        chain = chainGO.AddComponent<SilkChain>();
        chain.damping = 0.995f;      // 原 0.985 半衰期仅 0.76s，摆荡 3 秒就没劲
        chain.gravity = 15f;
        chain.subdivisions = 8;      // 原 3 段只有 2 个折点，撑不起绳索的弧线甩动
        // 形态连续性：沿断裂前那条悬链线布点，而不是直线均分。
        // 这样断裂瞬间垂度不会归零，视觉上不会「弹一下」。
        chain.catenarySag = oldSeg.sagRatio;
        chain.slackScale = 1.15f;    // 略松于弧长，重力能把弧线拉直
        chain.Build(high, node, this, oldSeg.tension);

        /* 断裂后：链条接管 oldSeg 的渲染。
         * oldSeg 马上要被销毁，但视觉要连续 ——
         * 让约束链把 nodes 位置写进 oldSeg 的 LineRenderer 再销毁。
         * Build 里取的是 owner.segments[0]，这里 oldSeg 可能正是它，
         * 所以单独指定更可靠。*/
        chain.SetRenderSegment(oldSeg);

        // 必须在 Build() 之后、首帧 Update 之前注入初速度。
        // Build 里 velocities 全部初始化为 0，此时施加冲量才正确；
        // 若改初始位置（污染 prevPositions）会首帧瞬移。
        if (injectVelocity && chain != null)
            chain.ApplyInitialVelocity(initialVelocity, tipBoost);

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
    public float gravity = 15f;        // 重力加速度（格/s²）
    public float damping = 0.985f;     // 每帧速度保留系数，越接近 1 摆得越久
    public float airDrag = 0.02f;      // 空气阻力（速度线性衰减）
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

        /* 渲染责任：只交给原 SilkSegment 一根线。
         *
         * 旧实现在这里建了 8 个 ChainSeg_ 当「画笔」，还强制 noSag=true
         * 把弧度关掉 —— 结果同一根线被 SilkSegment（弧线）和
         * 8 段 ChainSeg（直线折线）同时渲染，视觉上就是「两条线」。
         *
         * 正确做法：约束链的 nodes 已经带重力/阻尼/约束，
         * 位置本身就是物理算出来的弧形。每帧把 nodes 的位置
         * 写进那一个 SilkSegment 的 LineRenderer 顶点即可 ——
         * 一根线，形变由物理决定，不需要第二个渲染源。*/
        if (owner != null && owner.segments.Count > 0)
        {
            renderSeg = owner.segments[0];
            // 交给本组件渲染：设chainDriven 并关掉它自己的 LineRenderer，
            // 否则两个渲染源同时画 -> 视觉上两条线
            renderSeg.chainDriven = true;
            var lr0 = renderSeg.GetComponent<LineRenderer>();
            if (lr0 != null) lr0.enabled = false;
        }

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
        float dragFactor = Mathf.Clamp01(1f - airDrag * dt * 60f);
        for (int i = 1; i <= last; i++)
        {
            bool isDrivenEnd = (i == last && endDriven);
            if (!isDrivenEnd)
                velocities[i] += new Vector3(0, 0, -gravity * dt);
            else if (endDriven)
                velocities[i] = drivenVelocity;      // 完全交给外部（玩家输入）
            velocities[i] *= damping;
            velocities[i] *= dragFactor;
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

                /* 弹性绳索（单向拉伸，类似真实蛛丝）。
                 *
                 * rest = 该段的「自然长度」。允许被拉伸到 rest*(1+maxStrain)：
                 *   len < rest        -> 松弛，不修正（重力让它自然下垂）
                 *   rest <= len <= max -> 正常拉伸，不修正（形变在弹性范围内）
                 *   len > max         -> 拉回到 max（绷直，不再伸长）
                 *
                 * 上一版写成 restNow = len>maxLen ? maxLen : rest，
                 * 导致「压缩时也强行拉回rest」—— 绳索永远绷直，
                 * 因为 rest 已slackScale 放大 15%。
                 * 弹性绳只能被拉长，不能被压短，所以压缩时不修正。*/
                float maxLen = rest * (1f + maxStrain);
                float diff = 0f;
                if (len > maxLen)
                    diff = ((len - maxLen) / len) * stiffness;
                if (diff > 1f) diff = 1f;
                else if (diff < 0f) diff = 0f;
                if (diff == 0f) continue;      // 松弛或正常拉伸，无需修正

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

        // 4. 渲染：把物理算出的节点位置写进那一个 SilkSegment。
        //    一根线，形状完全由上面的重力/阻尼/约束决定。
        ApplyNodesToRender();
    }

    /// <summary>把 nodes 的位置写进 LineRenderer 顶点。
    /// 只有一个渲染源，所以永远不会出现「两条线」。</summary>
    void ApplyNodesToRender()
    {
        if (renderSeg == null) return;
        var lr = renderSeg.GetComponent<LineRenderer>();
        if (lr == null) return;

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
        // 被接管的段不再自己渲染，否则又会出现两个渲染源
        seg.chainDriven = true;
        var lr = seg.GetComponent<LineRenderer>();
        if (lr != null) lr.enabled = false;
    }

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

    [Tooltip("是否生成跑酷测试关卡（3 个平台 + 沟壑）。"
           + "临时功能，删除 SilkTestLevel.cs 后请把这里也移除")]
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
    /// Parkour 世界用：把某个锚点送进「选中-连线」流程。
    /// 与 FreeFly 的OnAnchorPicked 完全同一套逻辑 ——
    /// 区别只是 Parkour 第一个点由「自己」提供，所以只需连点两次。
    /// </summary>
    public void SelectAnchorForPlayer(AnchorPoint a)
    {
        if (a == null) return;
        OnAnchorPicked(a);
    }

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
        // 去重：key 基于体素坐标，与对象生命周期解耦
        if (lineKeys.Contains(GetPairKey(a, b))) return null;
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
        var world = new GameObject("SilkWorld");
        var grid = world.AddComponent<VoxelGrid>();
        grid.size = 100; grid.cellSize = 1f;

        CreateWireCube(world.transform, grid.GetHalfSize());
        CreateInnerWalls(world.transform, grid.GetHalfSize());
        GenerateAnchors(grid, 8);

        /* 跑酷测试关卡（临时）—— 属于**游戏世界**，FreeFly 下不该出现。
         * 所以改为在 HandleModeSwitch 里按模式建/清，不在 Build 里无条件创建。
         * 删除：删掉这两处调用 + SilkTestLevel.cs 整个文件即可。*/

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

    void Start()
    {
        // 从当前朝向反解 yaw/pitch，保证 Start 后画面不跳变
        Vector3 f = transform.forward;
        pitch = Mathf.Asin(Mathf.Clamp(f.z, -1f, 1f)) * Mathf.Rad2Deg;
        yaw = Mathf.Atan2(f.y, f.x) * Mathf.Rad2Deg;
    }

    void Update()
    {
        /* Parkour 模式：相机完全交出控制权。
         * 视角锁死在球上（固定在球后方 + 始终 LookAt 球），
         * 由 SilkParkourController.FollowCamera 负责。此处不响应任何输入。*/
        if (!inputEnabled) return;

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

    /// <summary>按当前 position 重新反解 yaw/pitch 并应用。
    /// 切回 FreeFly 时用 —— 因为 Parkour 期间 FollowCamera 改了相机位置，
    /// 但 yaw/pitch 仍是旧值，不重算会导致朝向与位置不一致。</summary>
    public void ResetOrientation()
    {
        Vector3 f = transform.forward;
        if (f.sqrMagnitude < 0.0001f) return;
        pitch = Mathf.Asin(Mathf.Clamp(f.z, -1f, 1f)) * Mathf.Rad2Deg;
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
        if (lockCursorForLook)
        {
            float mx = Input.GetAxis("Mouse X");
            float my = Input.GetAxis("Mouse Y");
            float signX = dragFollowsMouse ? 1f : -1f;
            float signY = dragFollowsMouse ? 1f : -1f;
            yaw += signX * mx * lookSensitivity;
            pitch += signY * my * lookSensitivity;
            pitch = Mathf.Clamp(pitch, -85f, 85f);
            ApplyRotation();
        }
        else if (Input.GetMouseButton(1))   // 未锁定时的退化方案：右键拖动
        {
            float signX = dragFollowsMouse ? 1f : -1f;
            float signY = dragFollowsMouse ? 1f : -1f;
            yaw += signX * Input.GetAxis("Mouse X") * rotateSpeed;
            pitch += signY * Input.GetAxis("Mouse Y") * rotateSpeed;
            pitch = Mathf.Clamp(pitch, -85f, 85f);
            ApplyRotation();
        }
    }

    [Tooltip("Parkour 模式下锁定光标，让鼠标移动直接控制视角（第一人称手感）")]
    public bool lockCursorForLook = true;

    [Tooltip("光标锁定时的视角灵敏度（度/像素）。右键拖动用rotateSpeed")]
    public float lookSensitivity = 0.22f;

    /// 由 yaw(绕Z) + pitch(仰角) 直接构造朝向，+Z 为上。
    /// 不能用 Quaternion.Euler —— 那是 Y-up 硬编码。
    /// 也不能先 qYaw 再 qPitch 叠加：那样 pitch 的基准轴会随 qYaw 漂移，
    /// 实测朝向误差 1.67（几乎反向），画面会转到看不到立方体的地方。
    void ApplyRotation()
    {
        float p = pitch * Mathf.Deg2Rad;
        Vector3 dir = new Vector3(
            Mathf.Cos(p) * Mathf.Cos(yaw * Mathf.Deg2Rad),
            Mathf.Cos(p) * Mathf.Sin(yaw * Mathf.Deg2Rad),
            Mathf.Sin(p));
        // 第二参数为 up 参考轴：与 dir 接近平行时 LookRotation 会退化，故先夹紧 pitch
        transform.rotation = Quaternion.LookRotation(dir, Vector3.forward);
    }
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

    [Tooltip("泵力加速度（沿切线）。越大越容易「起」起来")]
    public float pumpAccel = 30f;

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
    /// <summary>初始位置：落在测试平台 A 顶面。
    /// 平台 A 中心 (-30,0,-34) 尺寸 30x50x12 -> 顶面 z = -34+6 = -28
    /// 球半径 6 -> 球心 z = -28 + 6 = -22
    /// 之前放在 (0,0,30)，悬在平台上方 58 格，会一直往下掉。</summary>
    public Vector3 startPosition = new Vector3(-30f, 0f, -25f);

    [Header("外观")]
    [Tooltip("自动创建可见球体。没有它就只能从日志判断状态，看不到玩家在哪")]
    public bool autoCreateVisual = true;
    public float visualRadius = 6f;

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
        if (f.sqrMagnitude < 0.5f) f = camLookFlat; // 否则用相机朝向
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
            if (grabbed != null && !isFlying) UpdateSwing(dt);  // 抓丝线：泵力摆荡
            HandleKeys();
            UpdateVisualColor();

            if (isFlying)
            {
                // 松手后短暂保留惯性飞行（含撞墙反弹），玩家一按方向键就接管
                if (flightVel.sqrMagnitude > 1f) UpdateFlight(dt);
                if (!AnyDirectionKey()) UpdateFreeMove(dt);
            }
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
        if (mode == SilkControlMode.Parkour) FollowCamera();
        ScanRenderers();
    }

    float lastScan = -99f;

    /// <summary>扫描场景里所有 SilkSegment，按「两端点」分组，
    /// 找出被重复渲染的点对。每 2 秒一次。</summary>
    void ScanRenderers()
    {
        if (!renderScan) return;
        if (Time.time - lastScan < 2f) return;
        lastScan = Time.time;

        var groups = new Dictionary<string, int>();
        foreach (var seg in FindObjectsOfType<SilkSegment>())
        {
            if (seg == null) continue;
            var lr = seg.GetComponent<LineRenderer>();
            if (lr == null || !lr.enabled) continue;      // 只统计可见的
            if (seg.from == null || seg.to == null) continue;

            // 用体素坐标做 key，与顺序无关
            var pa = seg.from.position; var pb = seg.to.position;
            string key = (pa.x < pb.x || (pa.x == pb.x && pa.y < pb.y))
                ? pa + "|" + pb : pb + "|" + pa;
            if (!groups.ContainsKey(key)) groups[key] = 0;
            groups[key]++;
        }

        int dup = 0;
        foreach (var kv in groups)
            if (kv.Value > 1) { dup++; Debug.Log("[Scan] 重复渲染 " + kv.Value + " 层: " + kv.Key); }
        Debug.Log("[Scan] 可见 SilkSegment 点对数 = " + groups.Count + "，其中重复 " + dup);
    }

    [Tooltip("扫描场景里被重复渲染的丝线（排查重影）")]
    public bool renderScan = true;

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
        if (builder != null) builder.parkourMode = (mode == SilkControlMode.Parkour);

        if (mode == SilkControlMode.Parkour)
        {
            /* 进游戏世界：建球（之前是 Start 里无条件建，
             * 导致 FreeFly 下也能看到球 —— 两个世界被混在一起了）。*/
            transform.position = startPosition;
            flightVel = Vector3.zero;
            isFlying = true;
            CreateVisual();       // 幂等：已存在则直接返回
            UpdateVisualColor();
            logFollowOnce = true; // 每次切模式重打一次诊断
            FollowCamera();       // 立刻摆相机，当帧就能看到球
            // 测试关卡属于游戏世界，FreeFly 下不该存在
            if (builder != null && builder.createTestLevel) SilkTestLevel.Create(TestLevelHalfSize());
        }
        else
        {
            /* 回编辑器世界：销毁球与测试关卡，保持画面干净。
             * FreeFly 属于关卡编辑，不该有玩家角色和跑酷台子。*/
            DestroyVisual();
            SilkTestLevel.Clear();
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
    float TestLevelHalfSize()
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
    /// 第三人称跟随：**只跟随位置，不接管朝向**。
    ///
    /// 之前每帧写 cam.transform.rotation = LookRotation(球-相机)，
    /// 结果把右键环绕（SimpleOrbitCamera 维护的 yaw/pitch）覆盖掉了 ——
    /// 表现为「视角固定，转不动」。
    ///
    /// 现在改为：位置跟随球，朝向交给 SimpleOrbitCamera 自己管。
    /// 因为 Parkour 模式下 inputEnabled=false（关掉键盘），
    /// 但 HandleLookOnly() 仍然响应右键环绕 —— 两者不冲突。
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

        /* 极简版：相机固定在球的 -Y 侧偏上，**始终正对球**。
         * 不响应任何鼠标输入 —— 视角完全由球的位置决定。*/
        cam.transform.position = transform.position
                               + new Vector3(0f, -camDistance, camHeight);
        cam.transform.up = Vector3.forward;

        // 始终 LookAt 球 —— 这就是「视角锁死在球上」
        Vector3 toBall = transform.position - cam.transform.position;
        if (toBall.sqrMagnitude > 0.0001f)
            cam.transform.rotation =
                Quaternion.LookRotation(toBall.normalized, Vector3.forward);

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

    /// <summary>每次切到 Parkour 时重置，让 [Follow] 诊断重新打一次。</summary>
    bool logFollowOnce = true;

    [Tooltip("每帧输出相机跟随诊断（排查「看不到球」时开启）")]
    public bool verboseFollowLog = true;

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
    [Tooltip("相机水平跟随距离。球在 z=30、顶棚在 z=50，只有 20 格余量，"
           + "故 camHeight 不宜超过 15")]
    public float camDistance = 30f;
    [Tooltip("相机高于球的高度。必须 < 20（球到顶棚的距离），否则相机穿出顶棚")]
    public float camHeight = 10f;

    /* ---------- 脱手飞行：纯重力 + 阻尼 + 撞墙反弹 ---------- */
    void UpdateFlight(float dt)
    {
        flightVel += new Vector3(0, 0, -15f * flightGravityScale) * dt;
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

    /* ---------- 自由移动：WASD 控球，相机负责看 ---------- */
    void UpdateFreeMove(float dt)
    {
        // 未抓着丝线时，WASD 直接移动球本身（第三人称常见操作）
        Vector3 move = Vector3.zero;
        if (Input.GetKey(KeyCode.W)) move += camLookFlat;
        if (Input.GetKey(KeyCode.S)) move -= camLookFlat;
        if (Input.GetKey(KeyCode.A)) move -= camLookFlatPerp;
        if (Input.GetKey(KeyCode.D)) move += camLookFlatPerp;
        if (Input.GetKey(KeyCode.E)) move += Vector3.forward;   // 升高
        if (Input.GetKey(KeyCode.Q)) move -= Vector3.forward;   // 降低

        if (move.sqrMagnitude < 0.0001f) return;
        transform.position += move.normalized * moveSpeed * dt;
    }

    /// <summary>是否按了移动/摆动键。用来判断玩家是否在主动操作 ——
    /// 没按任何键时，惯性飞行才继续；按了就立刻接管为可控移动。</summary>
    bool AnyDirectionKey()
    {
        return Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.S) ||
               Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.D) ||
               Input.GetKey(KeyCode.E) || Input.GetKey(KeyCode.Q);
    }

    /// <summary>相机的水平朝向（用于 WASD 移动）</summary>
    Vector3 camLookFlat
    {
        get
        {
            if (cam == null) return Vector3.forward;
            Vector3 d = cam.transform.forward;
            d.z = 0f;
            return d.sqrMagnitude < 0.0001f ? Vector3.right : d.normalized;
        }
    }

    /// <summary>相机朝向的左方向（水平）</summary>
    Vector3 camLookFlatPerp
    {
        get
        {
            Vector3 f = camLookFlat;
            return new Vector3(-f.y, f.x, 0f);
        }
    }

    [Header("自由移动")]
    [Tooltip("WASD 移动球的速度")]
    public float moveSpeed = 35f;

    /* ---------- 挂荡：按输入移动自己，丝线末端跟随 ---------- */
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

        // 有输入时才进入 Swinging 状态 —— 否则丝线会一直停在 Anchored，
        // 状态机里Swinging 这个值等于从未被用过
        if (Mathf.Abs(pump) > 0.01f || Mathf.Abs(side) > 0.01f)
            grabbed.StartSwing();

        /* 位移用加速度积分：displacement = a * dt^2
         * （不是 a*dt —— 那是速度；也不是 a*dt^2/2 *60 * 0.06 这种凑数，
         *   实测 1 秒只累积 1.8 格，是目标值的 14%，泵力几乎无效）
         * Python 实算：accel=30 时1 秒末速度 30 格/s（约 1.1g），符合目标区间。*/
        if (Mathf.Abs(pump) > 0.01f)
            transform.position += tangential * (pump * pumpAccel * dt * dt);
        if (Mathf.Abs(side) > 0.01f)
            transform.position += lateral * (side * lateralAccel * dt * dt);

        bodyVelocity = endVel;
    }

    /* ---------- 按键 ---------- */
    void HandleKeys()
    {
        // 空格：抓住 / 松手
        if (Input.GetKeyDown(KeyCode.Space))
        {
            if (isFlying) TryGrab();
            else DoRelease();
        }

        /* 左键：**以自身当前所在的点为锚点** —— 勾住脚下/身旁的挂点。
         * 不是「鼠标点击建点」—— 两个世界都不允许凭空建点。
         * 玩家能连的只有已经存在的点：墙面锚点、自己固化过的节点、
         * 或者自己此刻悬停的那个点。*/
        if (Input.GetMouseButtonDown(0)) FireAtAnchor();

        // B：蜘蛛侠式发射（斜上勾住并摆荡）—— 走另一条路径
        if (Input.GetKeyDown(KeyCode.B)) TryFireAndHook();

        // X：断开自己发射的第一根丝线
        // X = 断自己发射的线（Parkour 世界专属；
        // FreeFly 世界的 X 是「老化」，两者语义不同，各自独立声明）
        if (Input.GetKeyDown(KeyCode.X)) CutFirstFiredLine();

        // G = 断「视线指向」的丝线（Parkour 世界的划断操作）
        if (Input.GetKeyDown(KeyCode.G)) CutLineUnderCrosshair();

        /* C（固化当前位置为节点）与 V（节点间结网）暂不实现。
         * 用户明确：「固化当前位置为节点这个先不用实现，
         * 之后我再详细说明，和你现在做的不太一样」。
         * 相关方法保留但不接线，等需求明确后再启用。*/

        if (Input.GetKeyDown(KeyCode.R))
        {
            DoRelease();
            transform.position = startPosition;
            flightVel = Vector3.zero;
            isFlying = true;
            if (builder != null) builder.CancelPendingNode();
            FollowCamera();
        }

        // 右键：取消待连线的起点
        if (Input.GetMouseButtonDown(1) && builder != null && builder.HasPendingNode)
            builder.CancelPendingNode();
    }

    /// <summary>
    /// Parkour 的左键：**一键把「自己」和「目标锚点」连起来**。
    ///
    /// 与 FreeFly 的关系（不是两套机制，是同一套）：
    ///   FreeFly  左键① 选中锚点 A     → 左键② 选中锚点 B   → 建线 A-B
    ///   Parkour  左键  已默认选中自己 → 左键 选中目标锚点 → 建线 自己-目标
    ///
    /// 所以 Parkour 只多了「默认选中自己」这一步，
    /// 丝线的建立 / 距离校验 / 去重全部复用 SilkBuilder 既有的 OnAnchorPicked。
    /// </summary>
    void FireAtAnchor()
    {
        if (builder == null) return;

        // 射线找目标锚点（不命中就什么都不做 —— 不凭空建点）
        if (camComp == null) return;
        Ray ray = camComp.ScreenPointToRay(Input.mousePosition);

        AnchorPoint target = null;
        if (Physics.Raycast(ray, out RaycastHit hit, 2000f, builder.anchorLayer))
            target = hit.collider.GetComponent<AnchorPoint>();

        if (target == null)
        {
            Debug.Log("[Fire] 视线里没有可连接的锚点");
            return;
        }

        // 复用 SilkBuilder 的选中-连线流程：自己是起点，目标是终点。
        // 与 FreeFly 完全同一套逻辑，只是第一个点由「自己」提供。
        builder.lastCreatedLine = null;
        builder.SelectAnchorForPlayer(selfNode);
        builder.SelectAnchorForPlayer(target);

        var line = builder.lastCreatedLine;
        if (line == null)
        {
            Debug.Log("[Fire]连线未成立（太近或重复）");
            return;
        }
        if (!firedLines.Contains(line)) firedLines.Add(line);

        // 末端挂到目标点 -> 进入摆荡（后续补发射动画）
        line.Attach(target, builder, "命中点");
        line.StartSwing();
        grabbed = line;
        isFlying = false;
        Debug.Log("[Fire] 已连接自己 → " + target.position);
    }

    /// <summary>
    /// 自己这个「锚点」。Parkour 世界的丝线起点。
    /// 随玩家移动 —— 每次发射时重新取当前位置对应/新建的锚点。
    /// </summary>
    AnchorPoint selfNode
    {
        get
        {
            if (_selfNode != null && _selfNode.AnchorAlive) return _selfNode;
            if (builder == null) return null;
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
    void TryGrab()
    {
        if (builder == null) return;

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

        if (best == null) return;

        // 通过事件总线发布抓住事件，并**指定目标线** ——
        // 不指定的话 ExecuteGrab 会用它自己的 pickRadius(15) 重新找，
        // 而这里的 grabRange 是 30，两处半径不一致会抓到不同的线，
        // 造成「以为抓住了、实际没挂上」的状态错乱。
        SilkEventBus.Post(new SilkGrabSignal(transform, "玩家", startSwing: true).On(best));
        grabbed = best;
        isFlying = false;
        Debug.Log("[Parkour] 抓住丝线");
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
        Debug.Log("[Parkour] 松手，末端速度 " + flightVel.magnitude.ToString("F1"));
    }

    /// <summary>供 UI 查询：当前是否抓着丝线。</summary>
    public bool IsSwinging => grabbed != null && !isFlying;
}
