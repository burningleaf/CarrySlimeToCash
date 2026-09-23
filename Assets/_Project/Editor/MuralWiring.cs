// ---------------------------------------------------------------------------
// MuralWiring.cs —— 象形图路牌接线（Level0，只放在 Editor 文件夹里，不进游戏包）
//
// 背景：Level0 有 15 块路牌，占位阶段牌面上写的是「以后该画什么」的文字
//       （LevelBuilder.showMuralLabels = true）。美术出图后要把
//       Assets/_Project/Art/UI/Murals/Mural_<muralId>.png 接到路牌上。
//
// 接线要同时落三个地方，缺一个都不算接上：
//   1) Level0.json  的 murals[k].sprIndex = k（k = 数组顺序，从 0 开始）
//   2) 场景 Level0.unity 里 LevelBuilder.muralSprites[k] = 第 k 块牌的精灵
//   3) LevelBuilder.showMuralLabels = false
//   最后按 JSON 重建一次场景，让 BuildMural 用新素材重新生成路牌
//   （只挂 Icon 不够：不重建的话场景里还是老的 Label 文字）。
//
// 【为什么 sprIndex 能原样往返】（这是接线的安全前提）
//   导出器 LevelDataWindow.ClassifyMural（LevelDataWindow.cs:551-563）是这么反推的：
//     牌的子物体 Icon → SpriteRenderer.sprite → 在 builder.muralSprites 里**按引用**
//     线性查找，取**第一个**相等的下标写进 m.sprIndex；找不到就是 -1（初始值）。
//   比较时用的行是 LevelData.cs:424 的 "U <muralId>|<qx>,<qy>|<sprIndex>"，
//   而且这组行会先排序（LevelData.cs:425），所以 JSON 里 murals 的顺序不影响判定，
//   只有 (muralId, qx, qy, sprIndex) 四元组必须一致。
//   → 本工具写进去的 sprIndex 一定能导出回来，前提是：
//       (a) 场景里的 muralSprites 与 murals 数组【同序】，且非空；
//       (b) 同一张 Sprite 在 muralSprites 里只出现一次（muralId 不重复，否则反查
//           会命中较小下标，另一块的 sprIndex 就变了）。
//     两条在 Level0 上都成立（15 个 muralId 互不相同）。
//   ⚠ 反过来说：只改 JSON 不写 muralSprites 是【丢数据】的写法 ——
//     场景里没有 Icon → 下次导出把 sprIndex 全写成 -1（历史上就这么丢过数据）。
//
// 特性：无删除、无目录操作、幂等（重跑一遍结果相同；sprIndex 每次都按数组顺序重算）。
// ---------------------------------------------------------------------------

using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class MuralWiring
{
    /// <summary>要接线的关卡：场景名 = JSON 名 = Level0</summary>
    const string LevelName = "Level0";

    /// <summary>象形图所在目录，文件名固定为 Mural_&lt;muralId&gt;.png</summary>
    const string MuralDir = "Assets/_Project/Art/UI/Murals";

    // ======================= 菜单 =======================

    [MenuItem("Tools/呆呆史莱姆/▣▣ 接线象形图路牌（Level0）", false, 24)]
    public static void BatchWireMurals()
    {
        string jsonPath = LevelDataWindow.LevelsDir + "/" + LevelName + ".json";
        string scenePath = LevelDataWindow.SceneDir + "/" + LevelName + ".unity";

        if (!File.Exists(jsonPath)) { Debug.LogError("[路牌接线] 找不到关卡数据：" + jsonPath); return; }
        if (!File.Exists(scenePath)) { Debug.LogError("[路牌接线] 找不到场景：" + scenePath); return; }

        // 0) 美术流可能刚写完 PNG，.meta/导入还没落地，先刷一次资源库
        AssetDatabase.Refresh();

        // ---------- 1) 读 JSON（唯一真相源） ----------
        string jsonBackup = File.ReadAllText(jsonPath, Encoding.UTF8);
        LevelData data = LevelData.FromJson(jsonBackup);
        if (data == null) { Debug.LogError("[路牌接线] JSON 解析失败：" + jsonPath); return; }
        if (data.murals == null || data.murals.Length == 0)
        {
            Debug.LogError("[路牌接线] " + jsonPath + " 里一块路牌都没有，什么都没做。");
            return;
        }

        // ---------- 2) 按数组顺序逐块加载精灵 ----------
        int n = data.murals.Length;
        Sprite[] sprites = new Sprite[n];
        List<string> missing = new List<string>();
        List<string> repeatIds = new List<string>();
        HashSet<string> seenIds = new HashSet<string>();
        int wired = 0;

        for (int k = 0; k < n; k++)
        {
            Mural m = data.murals[k];
            if (m == null)
            {
                sprites[k] = null;
                missing.Add("#" + k + " 数据为 null");
                continue;
            }

            string id = m.muralId == null ? "" : m.muralId.Trim();
            if (!seenIds.Add(id)) repeatIds.Add(string.IsNullOrEmpty(id) ? "(空 muralId)" : id);

            Sprite sp = null;
            if (!string.IsNullOrEmpty(id))
                sp = AssetDatabase.LoadAssetAtPath<Sprite>(MuralDir + "/Mural_" + id + ".png");

            if (sp == null)
            {
                // 缺图不中断：这块留空（-1），其余照接
                m.sprIndex = -1;
                sprites[k] = null;      // ⚠ 位置必须留着，否则后面所有块的 sprIndex 都会错位
                missing.Add("#" + k + " " + (string.IsNullOrEmpty(id) ? "(空 muralId)" : id) +
                            " → " + MuralDir + "/Mural_" + id + ".png");
            }
            else
            {
                m.sprIndex = k;         // 按数组顺序：第 k 块 = 下标 k
                sprites[k] = sp;
                wired++;
            }
        }

        // ---------- 3) 写回 JSON ----------
        File.WriteAllText(jsonPath, data.ToJson(), new UTF8Encoding(false));
        Debug.Log("[路牌接线] 已写出 " + jsonPath + "（" + new FileInfo(jsonPath).Length + " 字节）");

        // ---------- 4) 打开场景，把 muralSprites / showMuralLabels 写进 LevelBuilder ----------
        Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
        if (!scene.IsValid() || !scene.isLoaded)
        {
            File.WriteAllText(jsonPath, jsonBackup, new UTF8Encoding(false));
            Debug.LogError("[路牌接线] 场景打不开，已把 JSON 回滚：" + scenePath);
            return;
        }

        LevelBuilder builder = FindBuilder(scene);
        if (builder == null)
        {
            File.WriteAllText(jsonPath, jsonBackup, new UTF8Encoding(false));
            Debug.LogError("[路牌接线] 场景里找不到 LevelBuilder（根物体名 " + LevelDataWindow.BuilderName +
                           "），已把 JSON 回滚。场景未改动。");
            return;
        }

        builder.muralSprites = sprites;
        builder.showMuralLabels = false;
        EditorUtility.SetDirty(builder);
        EditorSceneManager.MarkSceneDirty(scene);
        bool savedWire = EditorSceneManager.SaveScene(scene, scenePath);
        AssetDatabase.SaveAssets();

        // ---------- 5) 按 JSON 重建场景内容（路牌才会用新素材重新生成） ----------
        string rebuildMsg;
        bool rebuilt = LevelDataWindow.RebuildScene(scene, out rebuildMsg);
        if (!rebuilt)
        {
            Debug.LogError("[路牌接线] 接线已写进场景，但重建失败，场景里可能还是旧的 Label 文字：\n" + rebuildMsg +
                           "\n（JSON 保持已接线状态。修好重建问题后重跑本命令即可，本命令幂等。）");
            return;
        }

        EditorSceneManager.MarkSceneDirty(scene);
        bool savedRebuild = EditorSceneManager.SaveScene(scene, scenePath);
        AssetDatabase.SaveAssets();

        // ---------- 6) 重建后自检 + 报告 ----------
        // 自检走的是导出器同一条路径：Mural_<代号> / Icon / 精灵引用。
        int iconOk = 0, iconBad = 0, labelLeft = 0;
        List<string> bad = new List<string>();
        Transform rootT = builder.Root != null ? builder.Root.transform : null;

        if (rootT == null)
        {
            bad.Add("重建后 builder.Root 为空，无法自检");
            iconBad++;
        }
        else
        {
            for (int k = 0; k < n; k++)
            {
                Mural m = data.murals[k];
                if (m == null) continue;

                string code = string.IsNullOrEmpty(m.muralId) ? ("M" + k) : m.muralId;
                Transform t = rootT.Find("Mural_" + code);
                if (t == null)
                {
                    iconBad++;
                    bad.Add("#" + k + " " + code + "：重建后找不到 Mural_" + code);
                    continue;
                }
                if (t.Find("Label") != null) labelLeft++;

                Transform icon = t.Find("Icon");
                if (sprites[k] == null)
                {
                    if (icon == null) iconOk++;     // 预期：缺图 → 不生成 Icon
                    else { iconBad++; bad.Add("#" + k + " " + code + "：缺图却生成了 Icon"); }
                }
                else
                {
                    SpriteRenderer isr = icon != null ? icon.GetComponent<SpriteRenderer>() : null;
                    if (isr != null && isr.sprite == sprites[k]) iconOk++;
                    else
                    {
                        iconBad++;
                        bad.Add("#" + k + " " + code + "：Icon 上的精灵不对（实际 " +
                                (isr == null || isr.sprite == null ? "空" : isr.sprite.name) + "）");
                    }
                }
            }
        }

        StringBuilder sb = new StringBuilder();
        sb.Append("[路牌接线] ").Append(LevelName).Append(" 完成：接上 ").Append(wired)
          .Append(" 块 / 缺失 ").Append(missing.Count).Append(" 块（共 ").Append(n).Append(" 块）\n");
        sb.Append("  JSON：").Append(jsonPath).Append("（sprIndex 已写入）\n");
        sb.Append("  场景：").Append(scenePath).Append('\n');
        sb.Append("    LevelBuilder.muralSprites.Length = ").Append(builder.muralSprites == null ? 0 : builder.muralSprites.Length)
          .Append("（其中 null ").Append(missing.Count).Append(" 个，占位保持与 murals 同序）\n");
        sb.Append("    LevelBuilder.showMuralLabels = ").Append(builder.showMuralLabels).Append('\n');
        sb.Append("    第一次保存场景 = ").Append(savedWire).Append("；重建 = 成功；重建后再保存 = ").Append(savedRebuild).Append('\n');
        sb.Append("    重建：").Append(rebuildMsg).Append('\n');
        sb.Append("    自检（重建后场景里的 Icon）：正确 ").Append(iconOk).Append(" 块 / 异常 ").Append(iconBad)
          .Append(" 块；残留 Label 文字 ").Append(labelLeft).Append(" 个\n");
        sb.Append("  逐块（数组顺序 = sprIndex）：\n");

        for (int k = 0; k < n; k++)
        {
            Mural m = data.murals[k];
            if (m == null) { sb.Append("    [").Append(k.ToString("00")).Append("] (数据为 null)\n"); continue; }
            Sprite spk = sprites[k];
            sb.Append("    [").Append(k.ToString("00")).Append("] ")
              .Append(string.IsNullOrEmpty(m.muralId) ? "(空) " : m.muralId.PadRight(5))
              .Append(" qx=").Append(m.qx).Append(" qy=").Append(m.qy)
              .Append(" size=").Append(m.sizeX.ToString("0.###")).Append("x").Append(m.sizeY.ToString("0.###"))
              .Append(" sprIndex=").Append(m.sprIndex)
              .Append(" label=\"").Append(m.label == null ? "" : m.label).Append("\"")
              .Append(" sprite=").Append(spk == null ? "(缺图)" : spk.name)
              .Append('\n');
        }

        if (bad.Count > 0) sb.Append("  自检异常明细：\n    ").Append(string.Join("\n    ", bad.ToArray())).Append('\n');

        Debug.Log(sb.ToString());

        if (missing.Count > 0)
        {
            Debug.LogWarning("[路牌接线] 有 " + missing.Count + " 块没找到象形图（sprIndex 保持 -1，牌面会是空板）：\n  " +
                             string.Join("\n  ", missing.ToArray()));
        }

        if (repeatIds.Count > 0)
        {
            HashSet<string> uniq = new HashSet<string>(repeatIds);
            Debug.LogWarning("[路牌接线] muralId 有重复：" + string.Join("、", new List<string>(uniq).ToArray()) +
                             "\n  重复的 muralId 会让导出器的 Icon→muralSprites 反查只命中较小下标，" +
                             "另一块的 sprIndex 往返会不一致（⓪-3 检查会报错）。先保证 muralId 唯一。");
        }

        if (labelLeft > 0)
            Debug.LogWarning("[路牌接线] 重建后仍有 " + labelLeft + " 个 Label 文字物体（预期 0）。" +
                             "检查菜单 ⓪-2 重建后 showMuralLabels 是否仍为 false。");
    }

    // ======================= 小工具 =======================

    /// <summary>在场景里找 LevelBuilder（遍历根物体；编辑器工具里不用 FindObjectOfType）。</summary>
    static LevelBuilder FindBuilder(Scene scene)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            LevelBuilder found = root.GetComponentInChildren<LevelBuilder>(true);
            if (found != null) return found;
        }
        return null;
    }
}
