using UnityEngine;

/// <summary>
/// 一条程序化假腿 —— 纯代码生成，零动画片段，零 Collider。
///
/// 【为什么是假腿而不是 IK 骨骼】
///   用户明确要求「先做后者」= 先用代码搭假骨骼，不是买商店模型。
///   IK 需要真实的关节层级 + 角度限制 + CCD 迭代，是第二阶段的事。
///   本阶段目标只有一个：**让腿在动**，能看出迈步节奏。
///   → 所以用「沿弧线摆一串线段」代替「解 IK」。
///
/// 【弧线构造 = 二次贝塞尔】
///   控制点抬高 2×peakLift 时，曲线中点恰好抬升 peakLift：
///     B(0.5) = 0.25a + 0.5·ctrl + 0.25b = mid + up·peakLift
///   peakLift = 0 时曲线退化成直线（不迈步时腿是直的）。
///
/// 【为什么用 LineRenderer 而不是圆柱】
///   1. 零 Collider —— 腿不会打到自己的射线（每帧十几次探测，自身命中很烦）
///   2. 与本项目丝线视觉一致（分段折线，见 90 秒视频 u15 帧）
///   3. widthCurve 能做「根粗尖细」，比等粗圆柱像腿得多
///
/// 【坐标系】Z-up。腿的局部系由 SilkSpiderSurfaceMove 摆正：
///   local Y = 表面法线（「上」）、local Z = 行进方向、local X = 左右
///   → **贴到墙上时这套局部系自动变成「沿墙上下 + 沿墙左右」，零特判。**
/// </summary>
public class SilkSpiderLeg
{
    /// <summary>每条腿的线段数（点位再多一个足端）。</summary>
    public const int SegmentCount = 4;

    /// <summary>腿序号 0..7（0~3 左侧由前到后，4~7 右侧由前到后）。</summary>
    public int Index;

    /// <summary>-1 = 左侧，+1 = 右侧。</summary>
    public float SideSign;

    /// <summary>前后位置系数，-1 = 最后，+1 = 最前。</summary>
    public float ForwardT;

    /// <summary>相位偏移 i/8 —— 步态错开的来源。
    ///
    /// ★ ★ 注意：它**不参与运行时计算**，只是把 i/8 这个概念存档。
    ///   真正让腿错开的是 <see cref="Timing"/> 在 BuildLegs 里被直接初始化成
    ///   (i/8)*cycleTime（视频作者就是把 i/8 写进计时器数组的）。
    ///   留这个字段是为了调试时能看出「第几条腿」。
    /// </summary>
    public float Phase;

    /// <summary>当前锁定的落点（世界空间，贴在表面上）。</summary>
    public Vector3 LockedTarget;

    /// <summary>下一个落点（世界空间）。</summary>
    public Vector3 NextTarget;

    /// <summary>迈步计时器（秒）。超过 cycleTime 或超出可达范围就换目标。</summary>
    public float Timing;

    /// <summary>本腿当前解算出的足端世界坐标（给距离判据与调试用）。</summary>
    public Vector3 FootWorld;

    private LineRenderer line;
    private Vector3[] pts = new Vector3[SegmentCount + 1];

    /// <summary>抬腿曲线：首尾 0、中间 1。切线取自动型→ 起身快、落脚慢。
    ///（视频里作者把曲线点右键选 Auto，理由是「更像真的、不那么机械」）</summary>
    private static readonly AnimationCurve liftCurve = new AnimationCurve(
        new Keyframe(0f, 0f, 0f, 3.2f),
        new Keyframe(0.5f, 1f, 0f, 0f),
        new Keyframe(1f, 0f, -3.2f, 0f));

    /// <summary>创建渲染用的 LineRenderer（明确不带 Collider）。</summary>
    public void BuildVisual(Transform parent, string name, float thickness, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);

        line = go.AddComponent<LineRenderer>();
        line.useWorldSpace = false;      // 用 local，点位自己换算
        line.positionCount = SegmentCount + 1;
        line.numCapVertices = 2;
        line.numCornerVertices = 2;
        line.alignment = LineAlignment.View;
        line.textureMode = LineTextureMode.Stretch;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;

        line.widthCurve = new AnimationCurve(
            new Keyframe(0f, thickness),
            new Keyframe(1f, thickness * 0.3f));

        line.startColor = color;
        line.endColor = color;

        /*★★ 必须显式给材质，否则可能整条腿不可见。
         *
         * 【为什么】
         *   LineRenderer 不设 material 时，Unity 会走内部默认材质。
         *   在 Built-in 管线 + 已手动替换过渲染管线的项目里，
         *   这个默认材质经常解析失败 → **腿画不出来**，而且不报任何错。
         *
         *   LineRenderer 的官方默认是 Sprites/Default（不受光照影响的纯色），
         *   这里就用它，并留 Diffuse 兜底。*/
        Shader lineShader = Shader.Find("Sprites/Default");
        if (lineShader == null) lineShader = Shader.Find("Unlit/Color");
        if (lineShader == null) lineShader = Shader.Find("Diffuse");

        if (lineShader != null)
        {
            line.material = new Material(lineShader);
            line.startColor = color;
            line.endColor = color;
        }
        else
        {
            Debug.LogWarning("[SpiderLeg] " + name
                           + " 找不到可用着色器，腿可能不可见。");
        }

        Collider[] cs = go.GetComponents<Collider>();
        for (int i = 0; i < cs.Length; i++)
            if (cs[i] != null) Object.Destroy(cs[i]);
    }

    /// <summary>解算并写入线段点位。</summary>
    /// <param name="rootWorld">腿根世界坐标（身体上的挂点）。</param>
    /// <param name="footWorld">足端目标世界坐标（已含抬腿高度）。</param>
    /// <param name="upWorld">表面法线（「往上」的方向）。</param>
    /// <param name="peakLift">抬腿峰值高度（格）。0 = 腿伸直不抬。</param>
    /// <param name="space">写入坐标的参考系（通常是带位移的身体节点）。</param>
    public void Solve(Vector3 rootWorld, Vector3 footWorld,
                      Vector3 upWorld, float peakLift, Transform space)
    {
        FootWorld = footWorld;

        pts[0] = rootWorld;
        for (int i = 1; i <= SegmentCount; i++)
        {
            float t = (float)i / SegmentCount;
            pts[i] = ArcPoint(rootWorld, footWorld, upWorld,
                              peakLift, liftCurve.Evaluate(t));
        }

        for (int i = 0; i < pts.Length; i++)
            line.SetPosition(i, space.InverseTransformPoint(pts[i]));
    }

    /// <summary>二次贝塞尔取点。h = 控制点抬升量，中点实际抬升 h/2。</summary>
    private static Vector3 ArcPoint(Vector3 a, Vector3 b, Vector3 up, float h, float t)
    {
        if (h <= 0.0001f) return Vector3.Lerp(a, b, t);

        Vector3 ctrl = (a + b) * 0.5f + up * (h * 2f);
        float it = 1f - t;
        return it * it * a + 2f * it * t * ctrl + t * t * b;
    }

    /// <summary>抬腿曲线（供调试绘制查询）。</summary>
    public static float LiftCurveAt(float t) => liftCurve.Evaluate(Mathf.Clamp01(t));

    public LineRenderer Line => line;
}