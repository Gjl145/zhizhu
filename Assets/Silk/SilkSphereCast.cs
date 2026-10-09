using UnityEngine;

/// <summary>
/// 相对父Transform 的位置 —— 让射线的起点与方向随物体旋转自动跟随。
///
/// 【移植自开源项目Unity-Procedural-IK-Wall-Walking-Spider 的 PositionRelative】
/// ★ 为什么要这个类而不是直接存一个世界坐标：
///   蜘蛛会绕表面法线旋转。如果射线端点存在**世界坐标**里，
///   蜘蛛一转，射线就指向别处了。
///   原项目的解法是：把起点/终点都存成**父物体的局部坐标**，
///   于是父物体旋转时射线自动跟着转，**一辈子只需初始化一次**。
/// </summary>
public class SilkPositionRelative
{
    private Vector3 position;
    private Transform parent;

    public SilkPositionRelative(Vector3 worldPos)
    {
        position = worldPos;
        parent = null;
    }

    public SilkPositionRelative(Vector3 worldPos, Transform m_parent)
    {
        parent = m_parent;
        SetWorldPosition(worldPos);
    }

    public Vector3 GetWorldPosition()
        => parent == null ? position : parent.TransformPoint(position);

    public Vector3 GetLocalPosition() => position;

    public void SetWorldPosition(Vector3 worldPos)
        => position = parent == null ? worldPos : parent.InverseTransformPoint(worldPos);

    public void SetLocalPosition(Vector3 localPos) => position = localPos;

    public void SetParent(Transform m_parent)
    {
        Vector3 world = GetWorldPosition();
        parent = m_parent;
        SetWorldPosition(world);
    }
}

/// <summary>
/// 球形射线 —— 起点与终点都绑定在父Transform 上，随父物体旋转自动跟随。
///
/// 【移植说明】
///   原项目有两个类：RayCast 与 SphereCast，放在 namespace Raycasting。
///   ★ 我们**只抄 SphereCast** —— 贴面探测用不到细射线，
///   球形射线的好处是「有体积」，贴墙时不容易漏检（细射线会从墙角缝穿过去）。
/// </summary>
public class SilkSphereCast
{
    private SilkPositionRelative origin;
    private SilkPositionRelative end;
    private float radius;

    /// <summary>★ 关键：射线**必须排除蜘蛛自己**，否则它会打到自己的碰撞体。</summary>
    private Collider ignoreCollider;

    /// <summary>
    /// ★ 要忽略的**根 Transform**（整个蜘蛛）。
    ///
    /// 【为什么不用摘 Layer —— 这是一个会连坐方案】
    ///   直觉做法是「把自身所在的层从 mask 里去掉」。
    ///   ★ 但本项目所有几何体都在 **Default 层**（MEMORY 第二节：
    ///   ProBuilder 生成的 Cube 没有自定义层），
    ///   摘掉 Default 等于**把整个场景一起排除**，射线永远打空。
    ///   → 必须用「忽略具体物体」的方式，见 <see cref="CastAll"/>。
    ///
    /// 【原项目怎么解决的】
    ///   原项目的 SphereCast 构造函数把蜘蛛的 Transform 传进去当作
    ///   「射线端点的父级」（那是 PositionRelative 的用途），
    ///   而射线**本身**用 `Physics.SphereCast` 时并没有额外排除自己——
    ///   因为它的蜘蛛 Collider 与腿射线起点是**错开**的（腿在身体外侧）。
    ///   而我们把射线起点放在球心，会**必然打到自己**。
    ///   → 所以必须显式排除。这是本移植**必须新增**的逻辑。
    /// </summary>
    private Transform ignoreRoot;

    public SilkSphereCast(Vector3 worldOrigin, Vector3 worldEnd, float m_radius)
    {
        origin = new SilkPositionRelative(worldOrigin);
        end = new SilkPositionRelative(worldEnd);
        radius = m_radius;
    }

    /// <summary>★ 关键：射线**必须排除蜘蛛自己**，否则它会打到自己的碰撞体。</summary>
    public SilkSphereCast(Vector3 worldOrigin, Vector3 worldEnd, float m_radius,
                          Transform parentForEndpoints)
    {
        origin = new SilkPositionRelative(worldOrigin, parentForEndpoints);
        end = new SilkPositionRelative(worldEnd, parentForEndpoints);
        radius = m_radius;
        ignoreRoot = parentForEndpoints;
    }

    /// <summary>
    /// 设置要忽略的碰撞体。
    ///
    /// 【为什么必须排除 ★踩过的坑的同类】
    ///   Unity 的 SphereCast 若**起点已在碰撞体内**，会返回 distance = 0
    ///   或直接不命中 —— 这与 MEMORY 第六节「相机穿墙」是同一个坑。
    ///   我们的射线起点在球心，而球上（子物体 Body）有一个 isTrigger 的
    ///   SphereCollider，不排除它的话射线永远打到自己 →
    ///   SurfaceNormal 取到错误法线 → 球在原地抖动。
    /// </summary>
    public void SetIgnore(Collider c) => ignoreCollider = c;

    /// <summary>
    /// ★ 设置要忽略的整个根物体（它及其所有子级碰撞体）。
    ///
    /// 【为什么需要 —— 独立化后本体上有多个遮挡碰撞体】
    ///   SetIgnore 只能记**一个** Collider。蜘蛛本体上现在有：
    ///     ① Body 的 groundProbe（旧球遗留）
    ///     ② SpiderProxy（新建的独立碰撞代理）
    ///   逐个 SetIgnore 会互相覆盖，漏掉的那个照样挡住足端射线
    ///   → 表面法线取错→ 蜘蛛原地抖动。
    ///   按根排除一次解决，且与 CastAll 里的既有判据一致。
    /// </summary>
    public void SetIgnoreRoot(Transform root) => ignoreRoot = root;

    public Vector3 GetOrigin() => origin.GetWorldPosition();

    public Vector3 GetDirection() => end.GetWorldPosition() - origin.GetWorldPosition();

    public Vector3 GetEnd() => end.GetWorldPosition();

    public float GetRadius() => radius;

    public void SetRadius(float m_radius) => radius = m_radius;

    /// <summary>
    /// 执行射线。
    ///
    /// 【★ 必须排除自身碰撞体】
    ///   Unity 的 SphereCast 起点若已在碰撞体内，会返回 distance = 0 或直接不命中
    ///   （与 MEMORY 第六节「相机穿墙」同一个坑）。
    ///   本组件挂在球上，而球有 SphereCollider，
    ///   若不排除自身，射线永远打到自己 → SurfaceNormal 变成球心处的法线 → 抖动。
    /// </summary>
    public bool Cast(out RaycastHit hitInfo, LayerMask mask)
    {
        Vector3 dir = GetDirection();
        float dist = dir.magnitude;
        if (dist < 0.0001f)
        {
            hitInfo = default(RaycastHit);
            return false;
        }

        Vector3 castDir = dir / dist;
        Vector3 castOrigin = GetOrigin();

        /* 优先走「精确过滤」路径：SphereCastAll + 排除自身。
         *
         * 【为什么不能靠摘 Layer —— 会连坐】
         *   本项目所有几何体都在 Default 层，摘掉等于把场景也排除了。
         *   见 <see cref="ignoreRoot"/> 的说明。
         *
         * 【为什么不能靠把起点挪出碰撞体 —— 会漏检】
         *   挪远一点确实能躲开自己，但那样贴墙时射线起点已在墙的另一侧，
         *   反而打不到近处的面。
         *   → 只能靠过滤。
         */
        // ★ 门槛必须同时看两个字段：SetIgnoreRoot 也要触发这条精确过滤路径。
        //   否则只设了 root 没设 collider 时会掉到下面的粗过滤分支，
        //   两条分支排除逻辑不同 → 行为不一致（表现为偶发抖动）。
        if (ignoreCollider != null || ignoreRoot != null)
        {
            RaycastHit[] all = Physics.SphereCastAll(
                castOrigin, radius, castDir, dist, mask,
                QueryTriggerInteraction.Ignore);

            float best = float.PositiveInfinity;
            RaycastHit bestHit = default(RaycastHit);
            bool found = false;

            for (int i = 0; i < all.Length; i++)
            {
                // ★ 排除自身所在的根物体（整个蜘蛛，含所有子物体）
                if (all[i].collider == null) continue;
                if (all[i].collider.transform.root == ignoreRoot) continue;
                if (all[i].collider == ignoreCollider) continue;

                if (all[i].distance < best)
                {
                    best = all[i].distance;
                    bestHit = all[i];
                    found = true;
                }
            }

            hitInfo = bestHit;
            return found;
        }

        return Physics.SphereCast(castOrigin, radius, castDir,
                                 out hitInfo, dist, mask,
                                 QueryTriggerInteraction.Ignore);
    }

    /// <summary>
    /// 调试画线。showDebug = true 时由组件调用。
    ///
    /// 【只用 Debug.DrawLine】
    ///   不用 Debug.DrawWireSphere / DrawSphere ——
    ///   它们在不同 Unity 版本里重载签名有差异（duration 参数是后加的），
    ///   而 DrawLine 是最稳定的那个。调试辅助不值得为它冒编译风险。
    /// </summary>
    public void Draw(Color col)
    {
        Vector3 o = GetOrigin();
        Vector3 e = GetEnd();

        Debug.DrawLine(o, e, col);

        // 端点画三个正交短线段，表示「这是个球形射线的端点」
        Vector3 rx = Vector3.right * radius;
        Vector3 ry = Vector3.up * radius;
        Vector3 rz = Vector3.forward * radius;

        Debug.DrawLine(o - rx, o + rx, col);
        Debug.DrawLine(o - ry, o + ry, col);
        Debug.DrawLine(o - rz, o + rz, col);

        Debug.DrawLine(e - rx, e + rx, col);
        Debug.DrawLine(e - ry, e + ry, col);
        Debug.DrawLine(e - rz, e + rz, col);
    }

    /// <summary>
    /// 从任意起点打到任意终点 —— 不受父Transform 绑定限制。
    ///
    /// 【为什么足端射线不能用绑父的版本】
    ///   SilkSpiderSurfaceMove 的两条射线端点绑在transform 上，
    ///   因为它们的方向**恒定**（沿表面法线 / 沿 transform.forward）。
    ///   但足端射线的起点是「骨盆上方与脚上方的插值」——
    ///   **每帧都不一样**，8 条腿各不相同。
    ///   → 只能每帧显式给起点终点，所以需要这个重载。
    ///
    /// 【为什么腿要各自一条射线而不是共用】
    ///   共用一条会互相覆盖端点。这里每次调用只是传参，不改状态，
    ///   所以「一条腿一条」其实只为语义清晰 —— 真正共享的是 ignore 配置。
    /// </summary>
    public bool CastBetween(Vector3 worldStart, Vector3 worldEnd,
                            LayerMask mask, out RaycastHit hitInfo)
    {
        Vector3 dir = worldEnd - worldStart;
        float dist = dir.magnitude;
        if (dist < 0.0001f)
        {
            hitInfo = default(RaycastHit);
            return false;
        }

        Vector3 castDir = dir / dist;

        // 与 Cast 同一套排除逻辑（见上面说明：不能摘 Layer，会连坐整个场景）
        if (ignoreCollider != null || ignoreRoot != null)
        {
            RaycastHit[] all = Physics.SphereCastAll(
                worldStart, radius, castDir, dist, mask,
                QueryTriggerInteraction.Ignore);

            float best = float.PositiveInfinity;
            RaycastHit bestHit = default(RaycastHit);
            bool found = false;

            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].collider == null) continue;
                if (ignoreRoot != null && all[i].collider.transform.root == ignoreRoot) continue;
                if (all[i].collider == ignoreCollider) continue;

                if (all[i].distance < best)
                {
                    best = all[i].distance;
                    bestHit = all[i];
                    found = true;
                }
            }

            hitInfo = bestHit;
            return found;
        }

        return Physics.SphereCast(worldStart, radius, castDir,
                                 out hitInfo, dist, mask,
                                 QueryTriggerInteraction.Ignore);
    }
}