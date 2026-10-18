using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

/// <summary>
/// ★ 白盒预览场景生成器（2026-10-12，用户需求：MC 地图试导块需要独立场景验收，
///   不要和 ceshi.unity 的蜘蛛测试混在一起）。
///
/// 菜单：Tools → 生成白盒预览场景
/// 做的事：新建场景 → 相机 + 平行光（Z-up 摆向）→ 实例化 Assets/WhiteboxAkra/region_models
///         → Scale=5（MC 一格 = 世界 5 格 = 1 米）
///         → ★ 相机按模型实际包围盒自动取景（v2：修复「看不到」——
///           v1 固定机位可能框在模型边缘外）→ 保存 Assets/WhiteboxAkra.unity 并打开。
/// 场景是独立文件，和 ceshi.unity 互不影响；重新点菜单 = 重建（幂等）。
/// </summary>
public static class WhiteboxSceneBuilder
{
    private const string ModelPath = "Assets/WhiteboxAkra/region_models.obj";
    private const string ScenePath = "Assets/WhiteboxAkra.unity";

    [MenuItem("Tools/生成白盒预览场景")]
    public static void Build()
    {
        // ---- 0. 前置检查：模型在不在 ----
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
        if (model == null)
        {
            EditorUtility.DisplayDialog("白盒场景", "找不到 " + ModelPath
                + "，先确认 OBJ 已在工程里。", "好");
            return;
        }

        // ---- 1. 新场景（注意：当前场景若有未保存修改，Unity 会弹保存提示）----
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,
                                                NewSceneMode.Single);

        // ---- 2. 实例化白盒模型（先摆好，再算包围盒放相机）----
        var instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
        instance.name = "Akra_白盒切片";
        instance.transform.position = Vector3.zero;
        instance.transform.localScale = Vector3.one * 5f;   // MC 格 → 世界格

        // ★ 计算模型真实包围盒（世界空间，Scale=5 之后）
        var bounds = new Bounds(instance.transform.position, Vector3.zero);
        var renderers = instance.GetComponentsInChildren<Renderer>();
        foreach (var r in renderers)
            if (r != null) bounds.Encapsulate(r.bounds);
        if (renderers.Length == 0 || bounds.size.sqrMagnitude < 0.001f)
            bounds = new Bounds(Vector3.zero, new Vector3(640f, 2500f, 640f));

        // ---- 3. 相机：包围盒中心外推 1.8 倍对角线，保证全貌入画 ----
        var camGo = new GameObject("Main Camera");
        camGo.tag = "MainCamera";
        var cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.10f, 0.11f, 0.14f);
        float dist = bounds.size.magnitude * 1.8f;
        cam.farClipPlane = dist * 4f + 5000f;
        // 机位：从 -Y（南）偏上俯瞰，同时抬到模型一半高度
        Vector3 center = bounds.center;
        camGo.transform.position = center + new Vector3(
            -bounds.size.x * 0.6f,
            -dist * 0.45f,
             dist * 0.75f);
        camGo.transform.LookAt(center, Vector3.forward);

        // ---- 4. 平行光（Z-up 从上往下照，与 SilkSpiderTestStage 同一摆法）----
        var lightGo = new GameObject("Directional Light");
        var light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.2f;
        Vector3 sunDir = new Vector3(0.4f, -0.5f, -0.77f).normalized;
        lightGo.transform.rotation = Quaternion.LookRotation(sunDir, Vector3.right);
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.55f, 0.57f, 0.6f);

        // ---- 5. 保存并打开 ----
        EditorSceneManager.SaveScene(scene, ScenePath);
        Debug.Log("[Whitebox] 白盒预览场景已重建并打开：" + ScenePath
            + "\n模型 " + instance.name + " Scale=5，渲染器 " + renderers.Length + " 个"
            + "\n包围盒中心 " + bounds.center + "  尺寸 " + bounds.size
            + "\n相机位于 " + camGo.transform.position + "，LookAt " + center
            + "\n★ Scene 视图若仍是黑的：选中 Hierarchy 里的 Akra_白盒切片，按 F 聚焦。");
    }
}
