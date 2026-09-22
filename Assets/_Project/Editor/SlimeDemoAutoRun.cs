// ---------------------------------------------------------------------------
// SlimeDemoAutoRun.cs —— 首次自动搭建钩子
//
// 作用：Unity 编译完这个脚本后，自动执行一次"补齐工程设置 + 连接场景引用"，
//       不需要你点任何菜单。
//
//   · 只会自动跑一次（标记文件写在 Library/ 里，不进 Assets，不会被打包）
//   · 跑之前会先把当前场景存盘，不会丢东西
//   · 之后想手动再跑：菜单 Tools → 呆呆史莱姆 → ②
//   · 想让它下次编译再自动跑一次：菜单 Tools → 呆呆史莱姆 → ⑦
// ---------------------------------------------------------------------------

using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[InitializeOnLoad]
public static class SlimeDemoAutoRun
{
    const string TestScenePath = "Assets/_Project/Scenes/Test.unity";
    static bool _rescheduled;

    static SlimeDemoAutoRun()
    {
        // 延迟到编辑器空闲时再跑，避免在编译/导入过程中操作资源
        EditorApplication.delayCall += TryRun;
    }

    static string MarkerPath
    {
        get { return Path.GetFullPath(Path.Combine(Application.dataPath, "../Library/SlimeDemoAutoRun.done")); }
    }

    [MenuItem("Tools/呆呆史莱姆/⑦ 清除自动搭建标记（下次编译重跑）", false, 107)]
    public static void ClearMarker()
    {
        try
        {
            if (File.Exists(MarkerPath)) File.Delete(MarkerPath);
            Debug.Log("[呆呆史莱姆] 已清除自动搭建标记：下次脚本重新编译时会再自动跑一次。");
        }
        catch (Exception e)
        {
            Debug.LogWarning("[呆呆史莱姆] 清除标记失败：" + e.Message);
        }
    }

    static void TryRun()
    {
        if (Application.isBatchMode) return;

        // 播放中 / 正在编译 / 正在导入资源 → 等一会儿再试
        if (EditorApplication.isPlayingOrWillChangePlaymode ||
            EditorApplication.isCompiling ||
            EditorApplication.isUpdating)
        {
            Reschedule();
            return;
        }

        if (File.Exists(MarkerPath)) return;

        try
        {
            Debug.Log("[呆呆史莱姆] ===== 首次自动搭建开始（Unity 自己完成，你不用操作）=====");

            // 0) 先把现有场景存盘，绝不丢东西
            EditorSceneManager.SaveOpenScenes();

            // 1) 工程设置：Layer / Tag / Physics2D 碰撞矩阵（幂等，可重复执行）
            SlimeDemoSetup.InitProjectSettings();

            // 2) 当前场景里没有玩家 → 自动切到 Test 场景
            if (!SlimeDemoSetup.ActiveSceneHasPlayer() && File.Exists(TestScenePath))
            {
                Debug.Log("[呆呆史莱姆] 当前场景里没有玩家，自动切换到 " + TestScenePath);
                EditorSceneManager.OpenScene(TestScenePath, OpenSceneMode.Single);
            }

            // 3) 把场景里所有空的引用连上（只填空的，不动你已经拖好的）
            SlimeDemoSetup.AutoWireMenu();

            // 4) 占位美术 + 路径点预制体（引导石要用）
            SlimeDemoSetup.CreateWaypointPrefabAndAssign();

            // 5) 数值对齐：史莱姆重力 = 玩家重力（跳高一致），跟随速度对齐到玩家移速
            SlimeDemoSetup.AlignSlimeNumbers();

            // 6) 往场景里补关卡元素（尖刺 / 回血球 / 金币 / 收购站）
            SlimeDemoSetup.AddLevelElements();

            // 7) 搭 HUD 与物品栏（会自动导入 TMP Essentials 和中文字体）
            SlimeDemoSetup.BuildHud();

            // 8) 搭暂停菜单与结算面板 + EventSystem
            SlimeDemoSetup.BuildPanels();

            // 9) 存盘
            EditorSceneManager.SaveOpenScenes();

            File.WriteAllText(MarkerPath, DateTime.Now.ToString("u"));
            Debug.Log("[呆呆史莱姆] ===== ✅ 自动搭建完成，直接按 ▶ Play 就能测试 =====");
        }
        catch (Exception e)
        {
            Debug.LogError("[呆呆史莱姆] 自动搭建出错（不影响你自己手动点菜单）：\n" + e);
        }
    }

    static void Reschedule()
    {
        if (_rescheduled) return;
        _rescheduled = true;
        EditorApplication.delayCall += () =>
        {
            _rescheduled = false;
            TryRun();
        };
    }
}
