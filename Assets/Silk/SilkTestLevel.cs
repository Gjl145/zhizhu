/* ============================================================================
 *  跑酷测试关卡（临时） —— 验证跑酷功能，**后续会整体删除**
 * ============================================================================
 *
 *  【为什么单独成文件】
 *  这是一份「跑酷世界」专用的验证场景。删掉整个文件 +
 *  在 SilkWorldBootstrap 里删掉一行BuildTestLevel() 调用即可，
 *  不影响任何正式逻辑。
 *
 *  【它验证什么】
 *   1. 撞墙反弹   —— 平台四周是立方体内墙，飞过去应被弹回
 *   2. 跨沟壑     —— 平台 A 与 B 之间留30 格空隙，射程需 36.6 格
 *   3. 勾住摆荡   —— 高塔顶面 z=40 接近顶棚，适合高处挂点
 *   4. 自建锚点   —— 平台 B 上方是空白，可就地固化
 *   5. 视觉参照   —— 不同颜色区分「起点 / 落点 / 高处挂点」
 *
 *  【布局】(X 水平 / Y 前后 / Z 高度，立方体内墙在 ±50)
 *   平台 A   x -45..-15   y -25..25   z -40..-28   起点（灰）
 *   沟壑      x -15.. 15   宽 30 格
 *   平台 B   x  15.. 45   y -25..25   z -40..-28   落点（蓝）
 *   高塔      x  25.. 45   y  25.. 45   z -40.. 40   高处挂点（橙）
 *
 *  【删除方法】
 *   1. 删除本文件
 *   2. SilkWorldBootstrap.Build() 里删掉 `CreateTestLevel(grid.GetHalfSize());`
 */

using UnityEngine;

public static class SilkTestLevel
{
    /// <summary>创建测试关卡。传入立方体半高用于自适应缩放。</summary>
    public static void Create(float h)
    {
        // 按半高缩放：小场景也能用同一套布局
        float k = h / 50f;

        CreatePlatform("TestPlat_A_起点", new Vector3(-30f, 0f, -34f), new Vector3(30f, 50f, 12f) * k,
                       new Color(0.55f, 0.55f, 0.6f));
        CreatePlatform("TestPlat_B_落点", new Vector3(30f, 0f, -34f), new Vector3(30f, 50f, 12f) * k,
                       new Color(0.3f, 0.55f, 0.85f));
        CreatePlatform("TestTower_高处挂点", new Vector3(35f, 35f, 0f), new Vector3(20f, 20f, 80f) * k,
                       new Color(0.9f, 0.6f, 0.25f));

        // 沟壑底部的提示板（让玩家知道下面是空的）
        CreateTip(new Vector3(0f, 0f, -48f) * k, "沟壑 30 格 —— 需射程 37 格");

        Debug.Log("[TestLevel] 跑酷测试关卡已创建（3 平台 + 提示板），删除 SilkTestLevel.cs 即可移除");
    }

    /// <summary>移除所有测试关卡物件（运行时兜底，便于动态清理）。</summary>
    public static void Clear()
    {
        var all = Object.FindObjectsOfType<Transform>();
        int n = 0;
        foreach (var t in all)
        {
            if (t != null && t.name.StartsWith("Test"))
            {
                Object.Destroy(t.gameObject);
                n++;
            }
        }
        Debug.Log("[TestLevel] 清理 " + n + " 个测试物件");
    }

    static void CreatePlatform(string name, Vector3 center, Vector3 size, Color color)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = "Test_" + name;
        go.transform.position = center;
        go.transform.localScale = size;
        go.transform.rotation = Quaternion.identity;

        // CreatePrimitive(Cube) 自带 BoxCollider —— 平台可以直接站上去、被撞到
        var mr = go.GetComponent<MeshRenderer>();
        if (mr != null)
        {
            Shader sh = Shader.Find("Universal Render Pipeline/Lit");
            if (sh == null) sh = Shader.Find("Standard");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            if (sh != null) mr.material = new Material(sh) { color = color };
        }
    }

    static void CreateTip(Vector3 pos, string text)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        go.name = "Test_Tip";
        go.transform.position = pos;
        go.transform.rotation = Quaternion.LookRotation(Vector3.forward);
        var col = go.GetComponent<Collider>();
        if (col != null) Object.Destroy(col);
        var mr = go.GetComponent<MeshRenderer>();
        if (mr != null)
        {
            Shader sh = Shader.Find("Sprites/Default");
            if (sh == null) sh = Shader.Find("Unlit/Transparent");
            if (sh != null)
                mr.material = new Material(sh)
                { color = new Color(0.5f, 0.5f, 0.5f, 0.4f) };
        }
    }
}
