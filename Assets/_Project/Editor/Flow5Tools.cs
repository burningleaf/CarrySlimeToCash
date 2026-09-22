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
///   菜单 Tools → 呆呆史莱姆 → ♪♪ 一键生成全部素材（美术 + 音效 + 接线）
///   Unity.exe -batchmode -quit -projectPath ... -executeMethod Flow5Tools.BatchGenerateAllAssets
/// </summary>
public static class Flow5Tools
{
    [MenuItem("Tools/呆呆史莱姆/♪♪ 一键生成全部素材（美术 + 音效 + 接线）", false, 81)]
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
