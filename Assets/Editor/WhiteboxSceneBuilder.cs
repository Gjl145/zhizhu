using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

/// <summary>
/// ★ 白盒预览场景生成器（2026-10-12，用户需求：MC 地图试导块需要独立场景验收，
///   不要和 ceshi.unity 的蜘蛛测试混在一起）。
///
/// 菜单：Tools → 生成白盒预览场景
/// 做的事：新建场景 → 相机 + 平行光（Z-up 摆向）→ 实例化 Assets/WhiteboxAkra/region_models
///         → Scale=5（MC 一格 = 世界 5 格 = 1 米）→ 保存 Assets/WhiteboxAkra.unity 并打开。
/// 场景是独立文件，和 ceshi.unity 互不影响；重新点菜单 = 重建（幂等）。
/// </summary>
public static class WhiteboxSceneBuilder
{
    private const string ModelPath = "Assets/WhiteboxAkra/region_models.obj";
    private const string ScenePath = "Assets/WhiteboxAkra.unity";

    [MenuItem("Tools/生成白盒预览场景")]
    public static void Build()
    {
        // ---- 1. 新场景 ----
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,
                                                NewSceneMode.Single);

        // ---- 2. 相机（Z-up：位于 -Y 侧偏上，往 +Y 看）----
        var camGo = new GameObject("Main Camera");
        camGo.tag = "MainCamera";
        var cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.10f, 0.11f, 0.14f);
        cam.farClipPlane = 20000f;   // 模型高度可能上千格，裁剪面必须拉远
        camGo.transform.position = new Vector3(0f, -400f, 200f);
        camGo.transform.rotation = Quaternion.LookRotation(
            (new Vector3(0f, 0f, 100f) - camGo.transform.position).normalized,
            Vector3.forward);

        // ---- 3. 平行光（Z-up 从上往下照，与 SilkSpiderTestStage 同一摆法）----
        var lightGo = new GameObject("Directional Light");
        var light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.2f;
        Vector3 sunDir = new Vector3(0.4f, -0.5f, -0.77f).normalized;
        lightGo.transform.rotation = Quaternion.LookRotation(sunDir, Vector3.right);
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.55f, 0.57f, 0.6f);

        // ---- 4. 实例化白盒模型 ----
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
        if (model == null)
        {
            EditorUtility.DisplayDialog("白盒场景", "找不到 " + ModelPath
                + "，先确认 OBJ 已在工程里。", "好");
            return;
        }
        var instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
        instance.name = "Akra_白盒切片";
        instance.transform.position = Vector3.zero;
        instance.transform.localScale = Vector3.one * 5f;   // MC 格 → 世界格

        // ---- 5. 保存并打开 ----
        EditorSceneManager.SaveScene(scene, ScenePath);
        Debug.Log("[Whitebox] 白盒预览场景已生成并打开：" + ScenePath
            + "\n模型 Scale=5（MC 一格 = 1 米）。相机已对准，滚轮缩放 / 右键旋转环视。"
            + "\n★ 此场景与 ceshi.unity 完全独立，蜘蛛测试不受影响。");
    }
}
