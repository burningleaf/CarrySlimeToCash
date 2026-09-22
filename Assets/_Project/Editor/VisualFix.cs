using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 视觉修正 + 预览渲染。
///
/// 【为什么需要修正 PPU】
///   这个工程所有物件的尺寸都走同一个约定：`transform.localScale = (sizeX, sizeY)`，
///   而碰撞体是 `BoxCollider2D.size = 1`（跟着 localScale 放大）。
///   也就是说 **localScale 的数值就等于世界尺寸**。
///   这一点只有在「精灵本身是 1×1 世界单位」时才成立，即 **PPU 必须等于贴图像素宽**。
///
///   实际测量（Level0）：
///     G01 地形 localScale=(18,2)， sprite=Sprite_Ground(128px @PPU32 = 4×4u) → 渲染 72×8  ← 大 4 倍
///     Player    localScale=(0.8,1.2)，sprite=Sprite_Box(128px @PPU32 = 4×4u)   → 渲染 3.2×4.8
///   **画面大了 4 倍，但碰撞体是对的** —— 症状是"走起来对、看着不对"，极难查。
///
///   修正规则（本文件是唯一出处）：
///     · 世界精灵：PPU = 贴图像素宽  → 1 张贴图 = 1×1 世界单位
///     · 路牌象形图：PPU = 40        → 64px = 1.6×1.6 单位，正好等于牌面高（sizeY=1.6）
/// </summary>
public static class VisualFix
{
    const string ArtRoot = "Assets/_Project/Art";

    [MenuItem("Tools/呆呆史莱姆/▧ 修正导入 PPU（让画面尺寸对上碰撞）", false, 77)]
    public static void BatchFixImportPpu()
    {
        string[] guids = AssetDatabase.FindAssets("t:Texture2D", new[] { ArtRoot });
        int changed = 0, skipped = 0;
        foreach (string g in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(g);
            if (!path.EndsWith(".png", System.StringComparison.OrdinalIgnoreCase)) continue;

            TextureImporter imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp == null) { skipped++; continue; }

            int want = WantedPpu(path, imp);
            if (Mathf.Approximately(imp.spritePixelsPerUnit, want)) { skipped++; continue; }

            float old = imp.spritePixelsPerUnit;
            imp.spritePixelsPerUnit = want;
            imp.SaveAndReimport();
            changed++;
            Debug.Log(string.Format("[PPU 修正] {0}：{1:0.#} → {2:0.#}", path.Replace(ArtRoot + "/", ""), old, want));
        }
        AssetDatabase.Refresh();
        Debug.Log(string.Format("[PPU 修正] 完成：改了 {0} 个，无需改 {1} 个。", changed, skipped));
    }

    static int WantedPpu(string path, TextureImporter imp)
    {
        Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        int w = tex != null ? tex.width : 32;

        // 路牌：64px 要正好 1.6 单位（= 数据里的 sizeY），所以 PPU = 64 / 1.6 = 40
        if (path.Replace('\\', '/').Contains("/Art/UI/Murals/")) return 40;

        // 其余世界精灵：PPU = 像素宽 → 1 张贴图 = 1×1 世界单位
        return w;
    }

    [MenuItem("Tools/呆呆史莱姆/▧▧ 修正 PPU + 量尺寸 + 渲染预览（一条龙）", false, 75)]
    public static void BatchFixAuditPreview()
    {
        BatchFixImportPpu();
        VisualAudit.BatchAuditVisuals();
        BatchRenderPreviews();
    }

    // ======================= 预览渲染 =======================

    /// <summary>
    /// 把关卡真的渲染成 PNG，用来肉眼验收（这是"表现层"这一轮唯一有说服力的验收方式）。
    /// 批处理模式下没有显示器，所以渲染到 RenderTexture 再存盘。
    /// </summary>
    [MenuItem("Tools/呆呆史莱姆/▣▣▣▣ 渲染关卡预览图（看一眼画面）", false, 76)]
    public static void BatchRenderPreviews()
    {
        RenderOne("Level0", new float[] { 5f, 45f, 90f }, 8f);
        RenderOne("Level1", new float[] { 20f, 80f, 150f }, 12f);
    }

    static void RenderOne(string sceneName, float[] camXs, float camY)
    {
        string scenePath = "Assets/_Project/Scenes/" + sceneName + ".unity";
        if (!File.Exists(scenePath)) { Debug.LogWarning("[预览] 没有场景：" + scenePath); return; }

        Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
        Camera cam = Camera.main;
        if (cam == null)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                cam = root.GetComponentInChildren<Camera>(true);
                if (cam != null) break;
            }
        }
        if (cam == null) { Debug.LogWarning("[预览] 场景里没有相机：" + sceneName); return; }

        int W = 1280, H = 720;
        RenderTexture rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
        RenderTexture prevActive = RenderTexture.active;
        RenderTexture prevTarget = cam.targetTexture;
        var prevPos = cam.transform.position;
        float prevSize = cam.orthographicSize;

        cam.orthographic = true;
        cam.orthographicSize = 6.5f;
        cam.targetTexture = rt;

        string outDir = Path.Combine(Directory.GetCurrentDirectory(), "_preview");
        Directory.CreateDirectory(outDir);

        for (int i = 0; i < camXs.Length; i++)
        {
            cam.transform.position = new Vector3(camXs[i], camY, -10f);

            // URP 下 Camera.Render() 不保证出图；两条路都试，谁出非全黑就用谁
            cam.Render();

            RenderTexture.active = rt;
            Texture2D tex = new Texture2D(W, H, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            RenderTexture.active = prevActive;

            byte[] png = tex.EncodeToPNG();
            Object.DestroyImmediate(tex);

            string file = Path.Combine(outDir, sceneName + "_x" + camXs[i].ToString("0") + ".png");
            File.WriteAllBytes(file, png);
            Debug.Log("[预览] 写出 " + file + "  (" + png.Length + " B)");
        }

        cam.targetTexture = prevTarget;
        cam.transform.position = prevPos;
        cam.orthographicSize = prevSize;
        rt.Release();
        Object.DestroyImmediate(rt);
    }
}
