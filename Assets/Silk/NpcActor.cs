using UnityEngine;

/// <summary>
/// ★★ 白模 NPC 小人 —— 程序拼装身体 + 动作状态机 + 漫游/捡物循环。
///
/// 【需求 —— 用户原话（2026-10-12）】
///   「我除了蜘蛛以外还打算在游戏里面安上小人，就是类似于人类一败涂地那样的
///    可以安装装备，衣服什么的小人，但是只是白模……这些小人将作为游戏的NPC，
///    用于构造真实的世界。」
///   「要求他们能简单表现什么的，比如举起双手奔跑，比如走路，
///    比如拿起来设定好的道具什么的」
///
/// 【为什么是「关节化白模 + 程序动作」而不是骨骼蒙皮模型】
///   · 白模阶段不需要蒙皮模型；关节 = 空物体 + 原语几何，全部代码生成
///   · 所有动作（走/跑/举双手/捡东西）都是**关节角**，纯代码驱动，
///     不依赖任何外部动画资源（用户没有国外账号，动画库是硬约束）
///   · ★ 挂点（手）已经就位 —— 以后升级正式模型时，挂点换成骨骼节点即可，
///     装备/换装逻辑全部平移
///
/// 【身体结构（局部 +Y = 前，+Z = 上，Z-up 项目约定）】
///   root(骨盆) ─┬─ spine ─ chest ─ neck ─ head
///               ├─ shoulder.L → upperArm.L → elbow.L → forearm.L → hand.L(挂点)
///               ├─ shoulder.R（同上镜像）
///               ├─ hip.L → thigh.L → knee.L → shin.L → foot.L
///               └─ hip.R（同上镜像）
///   尺寸按世界比例：人 1.7m = 8.5 格（1m = 5 格），
///   蜘蛛 2.5 格 = 0.5m —— 「蜘蛛视角看人」的比例成立。
///
/// 【★ 为什么每个部件都要 Destroy 掉 collider】
///   蜘蛛的足端射线 surfaceMask = Everything，NPC 的碰撞体挡住射线
///   → 蜘蛛会把 NPC 当地面踩上去。白模 NPC 不参与物理。
/// </summary>
public class NpcActor : MonoBehaviour
{
    // ================================================================
    //  动作枚举
    // ================================================================
    public enum NpcAction { Idle, Walk, Run, RunHandsUp, PickUp, Carry }

    // ================================================================
    //  关节引用（构建时填）
    // ================================================================
    private Transform root;          // 骨盆（整体移动/朝向/蹲起）
    private Transform chest;         // 胸（前倾/后仰）
    private Transform shoulderL, shoulderR;
    private Transform elbowL, elbowR;
    private Transform hipL, hipR;
    private Transform kneeL, kneeR;
    private Transform handSocketL, handSocketR;   // ★ 装备挂点

    // ---- 关节角度缓存（平滑用）----
    private float thighL, thighR;        // 髋摆动（绕 X）
    private float kneeL_, kneeR_;        // 膝弯曲
    private float armL, armR;            // 肩摆动
    private float elbowL_, elbowR_;      // 肘弯曲
    private float crouch;                // 蹲姿量 0~1
    private float chestPitch;            // 躯干前倾

    // ================================================================
    //  行为状态
    // ================================================================
    [Header("移动")]
    public float walkSpeed = 2.0f;       // 格/秒（0.4 m/s 漫步）
    public float runSpeed = 5.5f;        // 格/秒（1.1 m/s 小跑）
    public float wanderRadius = 5f;
    public bool wander = true;

    [Header("体型（格。人 1.7m = 8.5 格，一般不动）")]
    public float thighLen = 2.2f;
    public float shinLen = 2.0f;
    public float upperArmLen = 1.6f;
    public float forearmLen = 1.4f;

    private NpcAction action = NpcAction.Idle;
    private float gaitPhase;             // 步态相位
    private Vector3 wanderTarget;
    private float idleTimer;

    // ---- 捡东西序列 ----
    private GameObject carriedProp;      // 手里的道具
    private GameObject pickupTarget;     // 正要去拿的道具
    private float pickupTimer;           // 序列进度
    private const float PICKUP_REACH_TIME = 0.45f;  // 俯身够物时长
    private const float PICKUP_RISE_TIME = 0.4f;    // 起身时长

    // 世界里所有「设定好的道具」——spawner 填
    public static readonly System.Collections.Generic.List<GameObject> Props
        = new System.Collections.Generic.List<GameObject>();

    // ---- 随机器（静态共用，避免每帧 Random 状态互相干扰）----
    private static System.Random rng = new System.Random();

    // ================================================================
    //  生成
    // ================================================================

    /// <summary>生成一只白模小人。tint 传灰白色系即「白模」。</summary>
    public static NpcActor Spawn(Transform parent, Vector3 groundPos, Color tint)
    {
        var go = new GameObject("Npc_" + (npcCounter++));
        go.transform.SetParent(parent, false);
        go.transform.position = groundPos;

        var npc = go.AddComponent<NpcActor>();
        npc.BuildBody(tint);
        npc.PickWanderTarget();
        return npc;
    }
    private static int npcCounter;

    /// <summary>
    /// ★ 测试场一键生成：npcCount 只小人 + propCount 个道具。
    /// 由 SilkSpiderTestStage.Build 调用（parent 挂测试场根下，重建即清理）。
    /// </summary>
    public static void SpawnTestGroup(Transform parent, Vector2 center, int npcCount, int propCount)
    {
        Props.Clear();
        for (int i = 0; i < propCount; i++)
        {
            var prop = GameObject.CreatePrimitive(PrimitiveType.Cube);
            prop.name = "NpcProp_" + i;
            Object.Destroy(prop.GetComponent<Collider>());   // 不参与物理（见类注释）
            prop.transform.SetParent(parent, false);
            prop.transform.position = new Vector3(
                center.x + Rand(-4f, 4f), center.y + Rand(-3f, 3f), 0.5f);
            prop.transform.localScale = new Vector3(1f, 1f, 1f);
            SetColor(prop, new Color(0.9f, 0.7f, 0.25f));    // 道具用显眼的黄
            Props.Add(prop);
        }

        for (int i = 0; i < npcCount; i++)
        {
            float g = 0.72f + 0.06f * (i % 4);   // 灰白系微差 = 白模
            var tint = new Color(g, g, g * 0.98f);
            var npc = Spawn(parent, new Vector3(
                center.x + Rand(-5f, 5f), center.y + Rand(-3f, 3f), 0f), tint);
            npc.transform.SetParent(parent, true);
        }
        Debug.Log("[Npc] 测试小人生成完毕：" + npcCount + " 只 NPC，" + propCount + " 个道具。");
    }

    // ================================================================
    //  身体构建
    // ================================================================

    /// <summary>空关节（旋转发生在这里，网格挂在它的子级）。</summary>
    private static Transform Joint(Transform parent, string name, Vector3 localPos)
    {
        var j = new GameObject(name);
        j.transform.SetParent(parent, false);
        j.transform.localPosition = localPos;
        return j.transform;
    }

    /// <summary>圆柱肢体段：从关节出发指向局部 -Z，长 len、半径 r。</summary>
    private static Transform Limb(Transform joint, string name, float len, float r)
    {
        var seg = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        seg.name = name;
        Object.Destroy(seg.GetComponent<Collider>());
        seg.transform.SetParent(joint, false);
        // Cylinder 原语轴向是 +Y；Euler(-90,0,0) 把 +Y 转到 -Z（垂下）
        seg.transform.localPosition = new Vector3(0f, 0f, -len * 0.5f);
        seg.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
        seg.transform.localScale = new Vector3(r * 2f, len * 0.5f, r * 2f);
        // ★ Cylinder 原语默认高 2 → 高度 = localScale.y × 2，所以 y = len*0.5
        return seg.transform;
    }

    private static void SetColor(GameObject go, Color c)
    {
        var mr = go.GetComponent<MeshRenderer>();
        if (mr == null) return;
        Shader sh = Shader.Find("Standard");
        if (sh == null) sh = Shader.Find("Sprites/Default");
        if (sh != null) mr.material = new Material(sh) { color = c };
    }

    /// <summary>拼出整只白模小人。高度 ≈ 8.4 格（1.7m）。</summary>
    private void BuildBody(Color tint)
    {
        float hipZ = thighLen + shinLen + 0.2f;   // 骨盆离地高 ≈ 4.4

        // ---- 骨盆（根）----
        root = Joint(transform, "Pelvis", Vector3.zero);
        root.localPosition = new Vector3(0f, 0f, hipZ);
        var pelvisMesh = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Object.Destroy(pelvisMesh.GetComponent<Collider>());
        pelvisMesh.name = "PelvisMesh";
        pelvisMesh.transform.SetParent(root, false);
        pelvisMesh.transform.localScale = new Vector3(1.6f, 1.0f, 0.8f);
        SetColor(pelvisMesh, tint);

        // ---- 躯干 ----
        chest = Joint(root, "Chest", new Vector3(0f, 0f, 0.5f));
        var chestMesh = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Object.Destroy(chestMesh.GetComponent<Collider>());
        chestMesh.name = "ChestMesh";
        chestMesh.transform.SetParent(chest, false);
        chestMesh.transform.localPosition = new Vector3(0f, 0f, 1.3f);
        chestMesh.transform.localScale = new Vector3(2.2f, 1.2f, 2.6f);
        SetColor(chestMesh, tint);

        // ---- 头 ----
        var head = Joint(chest, "Neck", new Vector3(0f, 0f, 2.7f));
        var headMesh = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Object.Destroy(headMesh.GetComponent<Collider>());
        headMesh.name = "HeadMesh";
        headMesh.transform.SetParent(head, false);
        headMesh.transform.localPosition = new Vector3(0f, 0f, 0.6f);
        headMesh.transform.localScale = new Vector3(1.7f, 1.7f, 1.7f);
        SetColor(headMesh, new Color(tint.r * 1.05f, tint.g * 1.05f, tint.b * 1.05f));

        // ---- 手臂（肩在胸两侧顶端）----
        shoulderL = Joint(chest, "Shoulder.L", new Vector3(-1.4f, 0f, 2.3f));
        shoulderR = Joint(chest, "Shoulder.R", new Vector3(1.4f, 0f, 2.3f));
        Limb(shoulderL, "UpperArm.L", upperArmLen, 0.33f);
        Limb(shoulderR, "UpperArm.R", upperArmLen, 0.33f);
        elbowL = Joint(shoulderL, "Elbow.L", new Vector3(0f, 0f, -upperArmLen));
        elbowR = Joint(shoulderR, "Elbow.R", new Vector3(0f, 0f, -upperArmLen));
        Limb(elbowL, "Forearm.L", forearmLen, 0.28f);
        Limb(elbowR, "Forearm.R", forearmLen, 0.28f);
        handSocketL = Joint(elbowL, "Hand.L", new Vector3(0f, 0f, -forearmLen));
        handSocketR = Joint(elbowR, "Hand.R", new Vector3(0f, 0f, -forearmLen));
        var handL = GameObject.CreatePrimitive(PrimitiveType.Sphere);   // 手 = 小球
        Object.Destroy(handL.GetComponent<Collider>());
        handL.name = "HandMesh.L";
        handL.transform.SetParent(handSocketL, false);
        handL.transform.localScale = new Vector3(0.6f, 0.6f, 0.6f);
        SetColor(handL, tint);
        var handR = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Object.Destroy(handR.GetComponent<Collider>());
        handR.name = "HandMesh.R";
        handR.transform.SetParent(handSocketR, false);
        handR.transform.localScale = new Vector3(0.6f, 0.6f, 0.6f);
        SetColor(handR, tint);

        // ---- 腿 ----
        hipL = Joint(root, "Hip.L", new Vector3(-0.55f, 0f, -0.2f));
        hipR = Joint(root, "Hip.R", new Vector3(0.55f, 0f, -0.2f));
        Limb(hipL, "Thigh.L", thighLen, 0.4f);
        Limb(hipR, "Thigh.R", thighLen, 0.4f);
        kneeL = Joint(hipL, "Knee.L", new Vector3(0f, 0f, -thighLen));
        kneeR = Joint(hipR, "Knee.R", new Vector3(0f, 0f, -thighLen));
        Limb(kneeL, "Shin.L", shinLen, 0.32f);
        Limb(kneeR, "Shin.R", shinLen, 0.32f);
        // 脚：前伸的小盒
        var footL = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Object.Destroy(footL.GetComponent<Collider>());
        footL.name = "Foot.L";
        footL.transform.SetParent(kneeL, false);
        footL.transform.localPosition = new Vector3(0f, 0.35f, -shinLen);
        footL.transform.localScale = new Vector3(0.8f, 1.7f, 0.4f);
        SetColor(footL, tint);
        var footR = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Object.Destroy(footR.GetComponent<Collider>());
        footR.name = "Foot.R";
        footR.transform.SetParent(kneeR, false);
        footR.transform.localPosition = new Vector3(0f, 0.35f, -shinLen);
        footR.transform.localScale = new Vector3(0.8f, 1.7f, 0.4f);
        SetColor(footR, tint);
    }

    // ================================================================
    //  每帧：行为 + 程序动画
    // ================================================================

    private void Update()
    {
        float dt = Time.deltaTime;
        UpdateBehavior(dt);
        UpdateAnimation(dt);
    }

    /// <summary>行为层：漫游 + 捡东西循环 + 动作切换。</summary>
    private void UpdateBehavior(float dt)
    {
        // ---- 捡东西序列进行中 → 不做别的 ----
        if (action == NpcAction.PickUp)
        {
            pickupTimer += dt;
            if (pickupTimer >= PICKUP_REACH_TIME && carriedProp == null
                && pickupTarget != null)
            {
                // ★ 到达「抓住」时刻：道具挂到手部挂点
                carriedProp = pickupTarget;
                carriedProp.transform.SetParent(handSocketR, true);
                carriedProp.transform.localPosition = new Vector3(0f, 0f, -0.5f);
                carriedProp.transform.localRotation = Quaternion.identity;
                carriedProp.transform.localScale = Vector3.one;
                Props.Remove(carriedProp);
            }
            if (pickupTimer >= PICKUP_REACH_TIME + PICKUP_RISE_TIME)
            {
                action = NpcAction.Carry;
                PickWanderTarget();
            }
            return;
        }

        // ---- 移动（漫游）----
        Vector3 toTarget = wanderTarget - transform.position;
        toTarget.z = 0f;
        float dist = new Vector2(toTarget.x, toTarget.y).magnitude;

        if (action == NpcAction.Carry && carriedProp != null && dist < 1.0f)
        {
            // ★ 到达投放点：放下道具（脱离挂点、落在地面）
            carriedProp.transform.SetParent(null, true);
            Vector3 p = carriedProp.transform.position;
            carriedProp.transform.position = new Vector3(p.x, p.y, 0.5f);
            carriedProp.transform.rotation = Quaternion.identity;
            Props.Add(carriedProp);
            carriedProp = null;
            action = NpcAction.Idle;
            idleTimer = Rand(1.5f, 4f);
            PickWanderTarget();
            return;
        }

        if (dist < 0.6f)
        {
            // 到点 → 站一会儿 → 决定下一个动作
            if (action != NpcAction.Idle) { action = NpcAction.Idle; idleTimer = Rand(1f, 3.5f); }
            idleTimer -= dt;
            if (idleTimer <= 0f && wander)
            {
                DecideNextAction();
                PickWanderTarget();
            }
            return;
        }

        // ---- 朝目标走/跑 ----
        float speed = action == NpcAction.Run || action == NpcAction.RunHandsUp
            ? runSpeed : walkSpeed;
        Vector3 step = toTarget.normalized * speed * dt;
        transform.position += new Vector3(step.x, step.y, 0f);

        // 朝向：平滑转向移动方向（+Y 为前 → yaw = atan2(-dx, dy)）
        float targetYaw = Mathf.Atan2(-toTarget.x, toTarget.y) * Mathf.Rad2Deg;
        float cur = transform.localEulerAngles.z;
        float diff = Mathf.DeltaAngle(cur, targetYaw);
        transform.localRotation = Quaternion.Euler(0f, 0f,
            cur + diff * Mathf.Clamp01(dt * 5f));

        // 步态相位推进（步频 ∝ 速度）
        gaitPhase += dt * (2.2f + speed * 1.6f);
    }

    /// <summary>决定下一个动作（简单随机行为表 —— 以后这里换成 Utility AI）。</summary>
    private void DecideNextAction()
    {
        // 附近有道具 → 30% 概率去拿
        if (Props.Count > 0 && carriedProp == null && Rand(0f, 1f) < 0.35f)
        {
            GameObject nearest = null;
            float best = float.MaxValue;
            for (int i = 0; i < Props.Count; i++)
            {
                if (Props[i] == null) continue;
                float d = Vector2.Distance(
                    new Vector2(transform.position.x, transform.position.y),
                    new Vector2(Props[i].transform.position.x, Props[i].transform.position.y));
                if (d < best) { best = d; nearest = Props[i]; }
            }
            if (nearest != null)
            {
                pickupTarget = nearest;
                wanderTarget = nearest.transform.position;   // 先走到道具旁边
                action = NpcAction.Walk;
                return;
            }
        }

        float r = Rand(0f, 1f);
        if (r < 0.15f) { action = NpcAction.RunHandsUp; }        // 举双手奔跑（用户点名）
        else if (r < 0.45f) { action = NpcAction.Run; }
        else { action = NpcAction.Walk; }
    }

    /// <summary>拿取流程的入口（外部也可调用）。</summary>
    public void GoPickUp(GameObject prop)
    {
        pickupTarget = prop;
        wanderTarget = prop.transform.position;
        action = NpcAction.Walk;
    }

    private void PickWanderTarget()
    {
        Vector2 c = new Vector2(transform.position.x, transform.position.y);
        Vector2 t = c + new Vector2(Rand(-wanderRadius, wanderRadius),
                                    Rand(-wanderRadius, wanderRadius));
        // 夹在测试场可行走区内（x∈[-11,11] y∈[-6,1]）
        wanderTarget = new Vector3(Mathf.Clamp(t.x, -11f, 11f),
                                    Mathf.Clamp(t.y, -6f, 1f), 0f);

        // 如果正要去拿道具 → 目标 = 道具旁 0.8 格（别踩上去）
        if (action == NpcAction.Walk && pickupTarget != null && carriedProp == null)
        {
            Vector3 pp = pickupTarget.transform.position;
            Vector2 dir = (c - new Vector2(pp.x, pp.y));
            if (dir.sqrMagnitude > 0.01f)
                wanderTarget = new Vector3(pp.x, pp.y, 0f)
                    + new Vector3(dir.normalized.x, dir.normalized.y, 0f) * 0.9f;
        }
    }

    // ================================================================
    //  程序动画：把动作翻译成关节角
    // ================================================================
    private void UpdateAnimation(float dt)
    {
        bool moving = action == NpcAction.Walk || action == NpcAction.Run
            || action == NpcAction.RunHandsUp || action == NpcAction.Carry;
        bool running = action == NpcAction.Run || action == NpcAction.RunHandsUp;

        // ---- 目标关节角 ----
        float tThighL = 0f, tThighR = 0f, tKneeL = 0f, tKneeR = 0f;
        float tArmL = 0f, tArmR = 0f, tElbowL = 0f, tElbowR = 0f;
        float tCrouch = 0f, tChest = 0f;

        if (moving)
        {
            float swing = running ? 42f : 26f;         // 髋摆幅
            float kneeSwing = running ? 65f : 40f;     // 膝摆幅
            float s = Mathf.Sin(gaitPhase);
            float c = Mathf.Sin(gaitPhase + Mathf.PI); // 反相腿

            tThighL = s * swing;
            tThighR = c * swing;
            // 膝：腿后摆→蹬直，前摆→抬起弯曲（相位超前 90°）
            tKneeL = Mathf.Max(0f, Mathf.Sin(gaitPhase + 1.6f)) * kneeSwing;
            tKneeR = Mathf.Max(0f, Mathf.Sin(gaitPhase + 1.6f + Mathf.PI)) * kneeSwing;

            float armSwing = running ? 38f : 20f;
            tArmL = -c * armSwing;   // 臂与同侧腿反相
            tArmR = -s * armSwing;
            tElbowL = running ? 60f : 20f;
            tElbowR = running ? 60f : 20f;

            if (action == NpcAction.RunHandsUp)
            {
                // ★ 举双手奔跑（用户点名动作）：手臂锁死上举，只随跑步微晃
                tArmL = 165f + Mathf.Sin(gaitPhase * 2f) * 6f;
                tArmR = 165f - Mathf.Sin(gaitPhase * 2f) * 6f;
                tElbowL = 20f; tElbowR = 20f;
            }
            if (action == NpcAction.Carry && carriedProp != null)
            {
                // 携带：双臂前伸托住
                tArmL = -55f; tArmR = -55f;
                tElbowL = 45f; tElbowR = 45f;
            }
        }
        else if (action == NpcAction.Idle)
        {
            // 站立呼吸：轻微起伏
            float b = Mathf.Sin(Time.time * 1.8f);
            tArmL = 3f * b; tArmR = -3f * b;
        }
        else if (action == NpcAction.PickUp)
        {
            // 俯身够物：蹲 + 躯干前倾 + 右臂伸向地面
            float t = Mathf.Clamp01(pickupTimer / PICKUP_REACH_TIME);
            bool holding = pickupTimer >= PICKUP_REACH_TIME;
            float back = holding
                ? Mathf.Clamp01((pickupTimer - PICKUP_REACH_TIME) / PICKUP_RISE_TIME)
                : 0f;
            float k = t * (1f - back);   // 0=站着 1=完全俯身

            tCrouch = 0.75f * k;         // 蹲下
            tChest = 55f * k;            // 前倾
            tThighL = -55f * k; tThighR = -55f * k;
            tKneeL = 95f * k; tKneeR = 95f * k;
            tArmR = -110f * k;           // 右臂向下够
            tElbowR = 10f * k;
            tArmL = 25f * k;             // 左臂后展保持平衡
        }

        // ---- 平滑（固定速率趋近，动作切换不跳变）----
        float k2 = Mathf.Clamp01(dt * 10f);
        thighL = Mathf.LerpAngle(thighL, tThighL, k2);
        thighR = Mathf.LerpAngle(thighR, tThighR, k2);
        kneeL_ = Mathf.Lerp(kneeL_, tKneeL, k2);
        kneeR_ = Mathf.Lerp(kneeR_, tKneeR, k2);
        armL = Mathf.LerpAngle(armL, tArmL, k2);
        armR = Mathf.LerpAngle(armR, tArmR, k2);
        elbowL_ = Mathf.Lerp(elbowL_, tElbowL, k2);
        elbowR_ = Mathf.Lerp(elbowR_, tElbowR, k2);
        crouch = Mathf.Lerp(crouch, tCrouch, k2);
        chestPitch = Mathf.Lerp(chestPitch, tChest, k2);

        // ---- 写进关节 ----
        // 髋：绕 X 摆动（前后踢腿）
        hipL.localRotation = Quaternion.Euler(thighL, 0f, 0f);
        hipR.localRotation = Quaternion.Euler(thighR, 0f, 0f);
        // 膝：只往一个方向弯（小腿向后勾）
        kneeL.localRotation = Quaternion.Euler(kneeL_, 0f, 0f);
        kneeR.localRotation = Quaternion.Euler(kneeR_, 0f, 0f);
        // 肩/肘
        shoulderL.localRotation = Quaternion.Euler(armL, 0f, 0f);
        shoulderR.localRotation = Quaternion.Euler(armR, 0f, 0f);
        elbowL.localRotation = Quaternion.Euler(elbowL_, 0f, 0f);
        elbowR.localRotation = Quaternion.Euler(elbowR_, 0f, 0f);
        // 躯干前倾 + 蹲（骨盆下沉）
        chest.localRotation = Quaternion.Euler(-chestPitch, 0f, 0f);
        float standH = thighLen + shinLen + 0.2f;
        root.localPosition = new Vector3(0f, 0f,
            Mathf.Lerp(standH, standH * 0.62f, crouch));

        // 走路时身体轻微起伏（步频两倍）
        if (moving)
        {
            float bob = Mathf.Abs(Mathf.Sin(gaitPhase)) * (running ? 0.22f : 0.10f);
            Vector3 p = root.localPosition;
            root.localPosition = new Vector3(p.x, p.y, p.z + bob - 0.1f);
        }
    }

    // ================================================================
    //  工具
    // ================================================================
    private static float Rand(float min, float max)
    {
        return min + (float)rng.NextDouble() * (max - min);
    }
}
