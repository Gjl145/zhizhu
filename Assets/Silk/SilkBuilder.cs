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
public enum SilkColor { White, Yellow, Red }
public enum AnchorType { Wall, Internal, SilkNode }
public enum BreakMode { Middle, Quarter, ThreeQuarter, SpiderShot }

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
        gameObject.AddComponent<SphereCollider>().radius = 0.3f;
        transform.localScale = Vector3.one * 0.2f;
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

    LineRenderer lr;
    float fadeTimer;

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

        // 真实蛛丝：中间粗两头细
        lr.widthCurve = new AnimationCurve(
            new Keyframe(0f, 0.015f),
            new Keyframe(0.5f, 0.08f),
            new Keyframe(1f, 0.015f)
        );
        lr.widthMultiplier = 1.0f;
        lr.positionCount = 2;
        lr.useWorldSpace = true;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        lr.generateLightingData = true;

        // 乳白色丝线
        lr.material.color = new Color(0.95f, 0.93f, 0.9f, 0.85f);
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

        Vector3 mid = (from.WorldPosition + to.WorldPosition) * 0.5f;
        if (state == SilkState.Intact && !isFreeEnd && !noSag)
            mid += new Vector3(0, 0, -(1f - tension) * 0.7f);   // Z 为高度，向下弯

        lr.positionCount = 3;
        lr.SetPosition(0, from.WorldPosition);
        lr.SetPosition(1, mid);
        lr.SetPosition(2, to.WorldPosition);

        if (parentLine != null)
        {
            float t = Mathf.Clamp01(tension);
            lr.material.color = Color.Lerp(new Color(0.7f, 0.7f, 0.72f), BaseColor[(int)parentLine.color], t * 0.3f);
            // 保持乳白色为主
            lr.material.color = new Color(0.95f, 0.93f, 0.9f, 0.85f);
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
    public bool hasBroken = false;
    public List<SilkSegment> segments = new();
    public Vector3 breakPoint;
    public SilkChain chain;        // 断裂后接管上半截运动的约束链
    public GameObject chainGO;     // 约束链的根 GameObject

    public SilkLine(AnchorPoint a, AnchorPoint b, SilkColor c, BreakMode mode = BreakMode.Middle)
    {
        rootFrom = a; rootTo = b; color = c; breakMode = mode;
        ComputeBreakPoint();
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
    {
        if (hasBroken) return false;

        SilkSegment target = null;
        float bestD = float.MaxValue;
        foreach (var seg in segments)
        {
            if (seg == null) continue;
            float d = PointToSegment(breakPoint, seg.from.WorldPosition, seg.to.WorldPosition);
            if (d < bestD) { bestD = d; target = seg; }
        }
        if (target == null) return false;

        SplitSegment(target, breakPoint, builder);
        hasBroken = true;
        return true;
    }

    void SplitSegment(SilkSegment oldSeg, Vector3 bp, SilkBuilder builder)
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
        chain.damping = 0.985f;
        chain.gravity = 15f;
        chain.subdivisions = 3;
        chain.Build(high, node, this, oldSeg.tension);

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

    readonly List<AnchorPoint> nodes = new();
    readonly List<SilkSegment> renderSegs = new();
    readonly List<float> restLengths = new();
    readonly List<Vector3> velocities = new();   // 显式速度（格/秒）

    AnchorPoint root;   // 固定端（墙上）
    bool initialized = false;

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
        float segLen = totalLen / n;

        // 断点本身作为末端节点保留
        nodes.Add(highAnchor);
        for (int i = 1; i < n; i++)
        {
            Vector3 p = a + total * (i / (float)n);
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

        for (int i = 0; i < nodes.Count - 1; i++)
            restLengths.Add(segLen);

        // 显式速度初始化为 0 —— 当前是「无初速度」的自然摆动
        for (int i = 0; i < nodes.Count; i++)
            velocities.Add(Vector3.zero);

        // 建立渲染段（位置每帧由本组件写入，isFreeEnd 保持 false）
        for (int i = 0; i < nodes.Count - 1; i++)
        {
            var go = new GameObject("ChainSeg_" + i);
            go.transform.SetParent(transform);
            var seg = go.AddComponent<SilkSegment>();
            seg.from = nodes[i];
            seg.to = nodes[i + 1];
            seg.parentLine = owner;
            seg.tension = tension;
            seg.isFreeEnd = false;   // 关键：不由 SilkSegment 自行摆动，避免双重积分
            seg.noSag = true;        // 弧度交给约束链，不叠加中点下垂
            renderSegs.Add(seg);
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
        float dragFactor = Mathf.Clamp01(1f - airDrag * dt * 60f);
        for (int i = 1; i < nodes.Count; i++)
        {
            velocities[i] += new Vector3(0, 0, -gravity * dt);
            velocities[i] *= damping;
            velocities[i] *= dragFactor;
            nodes[i].transform.position += velocities[i] * dt;
        }

        // 2. 多轮距离约束：从根到端依次修正，形成自然弧线
        for (int it = 0; it < solverIterations; it++)
        {
            for (int i = 0; i < nodes.Count - 1; i++)
            {
                Vector3 a = nodes[i].WorldPosition;
                Vector3 b = nodes[i + 1].WorldPosition;
                Vector3 d = b - a;
                float len = d.magnitude;
                if (len < 0.0001f) continue;

                float rest = restLengths[i];
                float diff = ((len - rest) / len) * stiffness;
                if (diff > 1f) diff = 1f;
                else if (diff < -1f) diff = -1f;

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
    }

    void OnDestroy()
    {
        foreach (var seg in renderSegs)
            if (seg) Destroy(seg.gameObject);
        renderSegs.Clear();
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

    readonly List<AnchorPoint> anchors = new();
    readonly List<SilkLine> silkLines = new();
    SilkSpatialHash spatialHash;

    AnchorPoint firstAnchor;
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

        if (Input.GetMouseButtonDown(0)) TryPickOrCreateAnchor();

        if (isFirstSelected && firstAnchor != null)
        {
            previewGO.SetActive(true);
            previewLine.SetPosition(0, firstAnchor.WorldPosition);
            previewLine.SetPosition(1, MouseWorldPoint());
        }
        else previewGO.SetActive(false);

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

        if (Input.GetKeyDown(KeyCode.R)) ResetSelection();
        if (Input.GetKeyDown(KeyCode.C)) ClearAll();
        if (Input.GetKeyDown(KeyCode.G)) GenerateWeb();
        if (Input.GetKeyDown(KeyCode.X)) AgeWeb(0.15f);
    }

    void TryPickOrCreateAnchor()
    {
        Ray ray = mainCam.ScreenPointToRay(Input.mousePosition);
        if (Physics.Raycast(ray, out RaycastHit hit, 2000f, anchorLayer))
        {
            var a = hit.collider.GetComponent<AnchorPoint>();
            if (a != null) { OnAnchorPicked(a); return; }
        }
        if (Physics.Raycast(ray, out hit, 2000f, wallLayer))
        {
            var a = CreateAnchorAt(hit.point);
            OnAnchorPicked(a);
        }
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
                    CreateSilkLine(firstAnchor, a, defaultColor, BreakMode.Middle);
                else
                    Debug.Log("[SilkBuilder] 锚点太近（<5格），拒绝连线");
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

    /* ============ 右键断丝：找最近丝线 → 用自身 breakMode 断 ============ */
    void TryCutNearest(Ray ray)
    {
        Debug.Log("===== [Cut] 右键触发 =====");

        if (silkLines.Count == 0)
        {
            Debug.Log("[Cut] 没有丝线");
            return;
        }

        SilkLine bestLine = null;
        float bestDist = pickRadius;

        foreach (var line in silkLines)
        {
            if (line.hasBroken) continue;

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

        Debug.Log("[Cut] 选中丝线: " + bestLine.rootFrom.position + " → " + bestLine.rootTo.position
                  + " | 断裂点=" + bestLine.breakPoint
                  + " | from.z=" + bestLine.rootFrom.WorldPosition.z
                  + " to.z=" + bestLine.rootTo.WorldPosition.z);

        if (bestLine.BreakAtPresetPoint(this))
        {
            RebuildSpatialHash();
            Debug.Log("[Cut] >>> 断裂成功！自由端自然掉落");
        }
        else
        {
            Debug.Log("[Cut] >>> 断裂失败");
        }
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
        // 去重
        string pairKey = GetPairKey(a, b);
        foreach (var existing in silkLines)
        {
            if (GetPairKey(existing.rootFrom, existing.rootTo) == pairKey)
            {
                return existing;
            }
        }

        var line = new SilkLine(a, b, color, mode);
        silkLines.Add(line);
        var go = new GameObject("Seg_" + a.position + "_" + b.position);
        go.transform.SetParent(transform);
        var seg = go.AddComponent<SilkSegment>();
        seg.from = a; seg.to = b; seg.parentLine = line;
        seg.tension = Random.Range(0.5f, 1f);
        line.segments.Add(seg);
        spatialHash.Insert(seg);
        return line;
    }

    static string GetPairKey(AnchorPoint a, AnchorPoint b)
    {
        int ha = a.GetHashCode();
        int hb = b.GetHashCode();
        return ha < hb ? ha + "_" + hb : hb + "_" + ha;
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
        }
        silkLines.Clear();
        spatialHash.Clear();
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
        if (bottomAnchors.Count > 0 && highAnchors.Count > 0)
        foreach (var bot in bottomAnchors)
        {
            // 每个底面锚点连向 1 个高区锚点，靠去重自然收敛
            var target = highAnchors[Random.Range(0, highAnchors.Count)];
            if (target != bot) CreateSilkLine(bot, target, SilkColor.White, BreakMode.Middle);
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
        foreach (var line in silkLines) if (!line.hasBroken) candidates.Add(line);
        int count = 0;
        foreach (var line in candidates)
        {
            if (Random.value < breakChance)
            {
                if (line.BreakAtPresetPoint(this)) count++;
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
        }
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

    float yaw, pitch;   // yaw 绕 Z，pitch 仰角

    void Start()
    {
        // 从当前朝向反解 yaw/pitch，保证 Start 后画面不跳变
        Vector3 f = transform.forward;
        pitch = Mathf.Asin(Mathf.Clamp(f.z, -1f, 1f)) * Mathf.Rad2Deg;
        yaw = Mathf.Atan2(f.y, f.x) * Mathf.Rad2Deg;
    }

    void Update()
    {
        if (Input.GetKeyDown(flipKey))
        {
            dragFollowsMouse = !dragFollowsMouse;
            Debug.Log("[Camera] 拖拽跟手 = " + (dragFollowsMouse ? "开（物体跟手）" : "关（同 Unity 默认 Y-up）"));
        }

        if (Input.GetMouseButton(1))
        {
            // Z-up 下水平与垂直旋转的屏幕表现不同，需分别取符号（Python 实测）：
            //   水平：绕 +Z 正向 → 物体反向移动，跟手要取反
            //   垂直：绕水平轴     → 物体同向移动，跟手要保持
            float signX = dragFollowsMouse ? -1f : 1f;
            float signY = dragFollowsMouse ? 1f : -1f;
            yaw += signX * Input.GetAxis("Mouse X") * rotateSpeed;
            pitch += signY * Input.GetAxis("Mouse Y") * rotateSpeed;
            // 夹到 ±85 而非 ±89：LookRotation(dir, +Z) 在 dir 接近垂直于 +Z 时
            // 参考轴退化，Python 全角度扫描确认 ±89 那一行有 49 组退化
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