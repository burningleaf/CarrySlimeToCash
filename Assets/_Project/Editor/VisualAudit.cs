using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 视觉尺寸体检（诊断用）。
///
/// 为什么需要它：
///   这个工程的物件尺寸有两套来源 —— 关卡数据里的 `sizeX/sizeY`（设计值），
///   和精灵自己的世界尺寸（= 像素尺寸 ÷ PixelsPerUnit）。
///   `BuildXxx` 是拿 `localScale = size` 去缩放的，所以**渲染出来的世界尺寸**
///   = 精灵世界尺寸 × size。这两者一旦对不上，画面就会整体偏大或偏小，
///   而**碰撞体永远是对的**（它用的是 `box.size = 1` × localScale），
///   于是症状是"看着不对但走起来对"，极难查。
///
/// 所以这里把真实数字打出来，别再靠推算。
/// </summary>
public static class VisualAudit
{
    [MenuItem("Tools/呆呆史莱姆/▣▣▣ 接线路牌 + 量世界尺寸（一条龙）", false, 79)]
    public static void BatchWireMuralsAndAudit()
    {
        MuralWiring.BatchWireMurals();
        Audit();
    }

    [MenuItem("Tools/呆呆史莱姆/◇ 量视觉世界尺寸（诊断）", false, 78)]
    public static void BatchAuditVisuals()
    {
        if (EditorSceneManager.GetActiveScene().name != "Level0")
            EditorSceneManager.OpenScene("Assets/_Project/Scenes/Level0.unity", OpenSceneMode.Single);
        Audit();
    }

    static void Audit()
    {
        Scene scene = EditorSceneManager.GetActiveScene();
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("========== 视觉世界尺寸体检：" + scene.name + " ==========");
        sb.AppendLine("列含义：对象 | 精灵(像素@PPU=世界尺寸) | localScale | 实际渲染世界尺寸");

        string[] names =
        {
            "Ground_A", "Ground_D", "G01", "G03",
            "Mural_H1", "Mural_H1/Board", "Mural_H1/Icon", "Mural_H1/Label", "Mural_H1/Post",
            "C_Start", "C1_1", "goal_0", "Plat_1", "Tunnel_1",
        };
        foreach (string n in names) Line(sb, scene, n);

        // 玩家 / 史莱姆：它们挂在预制体实例上，名字固定
        Line(sb, scene, "Player");
        Line(sb, scene, "Player/Sprite");
        Line(sb, scene, "Slime");
        Line(sb, scene, "Slime/Sprite");

        sb.AppendLine("==========================================");
        Debug.Log(sb.ToString());
    }

    static void Line(StringBuilder sb, Scene scene, string path)
    {
        GameObject go = FindByPath(scene, path);
        if (go == null) { sb.AppendLine(string.Format("  {0,-22} （场景里没有）", path)); return; }

        SpriteRenderer sr = go.GetComponent<SpriteRenderer>();
        Transform t = go.transform;
        string spriteInfo = "（无 SpriteRenderer）";
        Vector3 world = Vector3.zero;
        bool hasBounds = false;

        if (sr != null)
        {
            Renderer r = sr;
            world = r.bounds.size;
            hasBounds = true;
            if (sr.sprite == null) spriteInfo = "（sprite 为空！）";
            else
            {
                Texture2D tex = sr.sprite.texture;
                spriteInfo = string.Format("{0} ({1}x{2}px @PPU{3:0.#} = {4:0.##}x{5:0.##}u)",
                    sr.sprite.name,
                    tex != null ? tex.width : 0, tex != null ? tex.height : 0,
                    sr.sprite.pixelsPerUnit,
                    sr.sprite.bounds.size.x, sr.sprite.bounds.size.y);
            }
        }

        sb.AppendLine(string.Format("  {0,-22} {1,-46} scale={2,-20} 世界={3}",
            path, spriteInfo,
            string.Format("({0:0.##},{1:0.##})", t.localScale.x, t.localScale.y),
            hasBounds ? string.Format("{0:0.##}x{1:0.##}", world.x, world.y) : "——"));
    }

    static GameObject FindByPath(Scene scene, string path)
    {
        string[] parts = path.Split('/');
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            foreach (Transform tr in all)
            {
                if (tr.name != parts[0]) continue;
                Transform cur = tr;
                bool ok = true;
                for (int i = 1; i < parts.Length; i++)
                {
                    cur = cur.Find(parts[i]);
                    if (cur == null) { ok = false; break; }
                }
                if (ok) return cur.gameObject;
            }
        }
        return null;
    }
}
