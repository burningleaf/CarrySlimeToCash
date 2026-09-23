using UnityEditor;
using UnityEngine;

/// <summary>
/// 流5（美术与音频）的一键入口：把三个生成器 + 接线按正确顺序跑一遍。
///
/// 为什么要有这个：
///   这三步（生成美术 → 生成音效 → 接线）有**严格的先后顺序** ——
///   接线脚本要靠 `AssetDatabase.LoadAssetAtPath` 找到已经生成并导入的 wav，
///   先接线后生成的话，接线那一刻文件还不存在，只会得到一堆"缺文件"。
///   而 Unity 的 `-executeMethod` 一次只能跑一个方法，所以这里做个包装，
///   让命令行一条顶三条，也让人不容易把顺序搞错。
///
/// 用法：
///   菜单 Tools → 呆呆史莱姆 → ♪♪ 一键生成全部素材（美术 + 音效 + 接线）   ← 人点，带确认框
///   Unity.exe -batchmode -quit -projectPath ... -executeMethod Flow5Tools.BatchGenerateAllAssets   ← 机器用，无弹窗
///
/// 现在美术那一步（PixelArtGenerator）会在写盘末尾自动跑一次 VisualFix.BatchFixImportPpu()，
/// 所以「一键生成全部素材」之后 PPU 一定是对的，不用再手动点「▧ 修正导入 PPU」。
/// </summary>
public static class Flow5Tools
{
    /// <summary>
    /// 菜单入口（人点）：带确认框，说清会覆盖什么，然后转调批处理实现。
    /// ⚠ 确认框只在这里 —— 下面 BatchGenerateAllAssets 会被 -executeMethod 调用，绝不能弹窗。
    ///   确认框自身也被 `!Application.isBatchMode` 包住（对齐 LevelSolver.cs 里既有的写法）：
    ///   批处理模式下不弹窗、默认放行，绝不会把 `-executeMethod` 卡住。
    /// </summary>
    [MenuItem("Tools/呆呆史莱姆/♪♪ 一键生成全部素材（美术 + 音效 + 接线）", false, 93)]
    public static void GenerateAllAssetsMenu()
    {
        if (!Application.isBatchMode)
        {
            bool go = EditorUtility.DisplayDialog(
                "一键生成全部素材？",
                "会【重写】这些资源（同名同路径覆盖，不可撤销）：\n\n" +
                "  · 美术 ── Assets/_Project/Art/** 下的 PNG（重新画 + 重写导入设置）\n" +
                "      ⚠ PPU 会在生成末尾由 VisualFix.WantedPpu 自动修正（世界精灵 = 贴图像素宽、路牌 = 40），\n" +
                "        所以不会再出现 2026-09-23 那种「地图像素放大 4 倍」的事故。\n" +
                "  · 音效 ── Assets/_Project/Audio/SFX/** 下的 WAV\n" +
                "  · 路牌 ── Assets/_Project/Art/UI/Murals/** 下的 Mural PNG\n" +
                "  · 接线 ── 把上面生成好的资源填回各场景 / 预制体的 public 字段\n\n" +
                "脚本、关卡 JSON、场景结构不动。要继续吗？",
                "生成", "取消");
            if (!go) return;
        }

        BatchGenerateAllAssets();
        if (!Application.isBatchMode)
            EditorUtility.DisplayDialog("生成完毕",
                "美术 + 音效 + 路牌 + 接线已全部跑完。\n细节看 Console 窗口（Window → General → Console）。", "好");
    }

    /// <summary>
    /// 批处理入口：Unity.exe -batchmode -quit -projectPath ... -executeMethod Flow5Tools.BatchGenerateAllAssets
    /// ⚠ 没有任何对话框（批处理模式下弹窗会卡住流程）—— 确认框在菜单包装 GenerateAllAssetsMenu 里。
    /// </summary>
    public static void BatchGenerateAllAssets()
    {
        Debug.Log("[流5] ========== 开始生成全部素材 ==========");

        // ① 美术（会写 PNG 并设置 TextureImporter）
        Debug.Log("[流5] ① 生成像素美术……");
        PixelArtGenerator.BatchGeneratePixelArt();

        // ② 音效（会写 WAV 并设置 AudioImporter）
        Debug.Log("[流5] ② 生成音效……");
        AudioGenerator.BatchGenerateAudio();

        // ③ 象形图路牌（无文字教学要用；独立于上面两步）
        Debug.Log("[流5] ③ 生成象形图路牌……");
        MuralGenerator.BatchGenerateMurals();

        // ④ 接线（按路径加载上面几步生成的资源，填进各脚本的 public 字段）
        Debug.Log("[流5] ④ 接线到场景与预制体……");
        AssetWiring.BatchWireAssets();

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[流5] ========== 全部生成完毕 ==========");
    }
}
