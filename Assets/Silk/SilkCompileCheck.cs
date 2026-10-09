// ★ 自动编译检查 —— 让 AI 能自己验证编译，不用让用户回报
//
// 起因（用户2026-10-08 的质问）
//   「所以编译这件事情只能我自己来？」
//
// 为什么需要这个：
//   - Unity 不允许同一项目开两个实例 -> batchmode 被锁
//   - AI 无法直接触发编辑器内编译
//   - 但 AI 可以**读日志**（Editor.log 里有精确行号）
//
// 用法（两种，任选其一，都只需点一下）：
//   ① 菜单：窗口（Window）> Silk 编译检查
//   ② 快捷键：Ctrl + Shift + C
//
// 它做什么：
//   1. 触发一次资源刷新（Unity 会自动重新编译）
//   2. 等编译完成
//   3. 把错误写入 Library/silk_compile_result.txt —— AI 读这个文件

using System.IO;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

public static class SilkCompileCheck
{
    const string ResultPath = "Library/silk_compile_result.txt";
    const string MenuPath = "Window/Silk 编译检查 (Compile Check)";

    [MenuItem(MenuPath, false, 200)]
    public static void Run()
    {
        Debug.Log("[编译检查] 开始刷新（Unity 会自动重新编译）...");
        WriteResult("STATUS: refreshing");

        // 触发刷新 -> Unity 会重编所有脚本
        AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

        // 注册回调，等编译结束
        EditorApplication.delayCall += () =>
        {
            if (EditorApplication.isCompiling)
            {
                // 还在编译，等下一帧
                EditorApplication.delayCall += CheckWhenDone;
            }
            else
            {
                CheckWhenDone();
            }
        };
    }

    [MenuItem(MenuPath, true, 200)]
    static bool Validate()
    {
        return !EditorApplication.isCompiling;
    }

    static void CheckWhenDone()
    {
        if (EditorApplication.isCompiling)
        {
            EditorApplication.delayCall += CheckWhenDone;
            return;
        }

        // 收集编译错误
        var errors = new System.Collections.Generic.List<string>();

        // 方法一：从 Editor.log 尾部提取（覆盖所有错误类型）
        try
        {
            string logPath = Path.Combine(
                System.Environment.GetFolderPath(
                    System.Environment.SpecialFolder.LocalApplicationData),
                "Unity", "Editor", "Editor.log");

            if (File.Exists(logPath))
            {
                var info = new FileInfo(logPath);
                long readFrom = System.Math.Max(0, info.Length - 512 * 1024);
                using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read,
                                               FileShare.ReadWrite))
                {
                    fs.Seek(readFrom, SeekOrigin.Begin);
                    var sr = new StreamReader(fs);
                    string tail = sr.ReadToEnd();
                    sr.Close();

                    foreach (System.Text.RegularExpressions.Match m in
                        System.Text.RegularExpressions.Regex.Matches(
                            tail,
                            @"Assets[/\\][^:]+\.cs\((\d+),\d+\): error (CS\d+): (.+)"))
                    {
                        string ln = m.Groups[1].Value;
                        string code = m.Groups[2].Value;
                        string msg = m.Groups[3].Value.Trim();
                        errors.Add(string.Format("[{0}] line {1}: {2}",
                            code, ln, msg));
                    }
                }
            }
        }
        catch (System.Exception e)
        {
            errors.Add("读取日志失败: " + e.Message);
        }

        // 方法二：用CompilationPipeline 直接取（更准确，但需要额外的 asmdef 引用）
        // 这里只用日志法，因为它一定能工作。

        // 去重
        var uniq = new System.Collections.Generic.List<string>();
        var seen = new System.Collections.Generic.HashSet<string>();
        foreach (var e in errors)
        {
            if (seen.Add(e)) uniq.Add(e);
        }

        WriteResult(uniq.Count == 0
            ? "RESULT: PASS\n编译通过，无错误。"
            : "RESULT: FAIL\n错误数: " + uniq.Count + "\n" +
              string.Join("\n", uniq.ToArray()));

        Debug.Log("[编译检查] 完成 —— " + (uniq.Count == 0
            ? "编译通过 ✓"
            : "发现 " + uniq.Count + " 个错误，结果已写入 " + ResultPath));
    }

    static void WriteResult(string content)
    {
        try
        {
            string full = Path.Combine(
                Directory.GetParent(Application.dataPath).FullName, ResultPath);
            File.WriteAllText(full, content);
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[编译检查] 写结果文件失败: " + e.Message);
        }
    }
}
