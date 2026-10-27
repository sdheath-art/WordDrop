using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using WordDrop;

/// <summary>Renders ArtButtons offscreen at the art's own pixel size, so the live label can be diffed against the
/// Photoshop render (~/Documents/WordDrop_Art/vector_buttons/photoshop/unity_&lt;key&gt;.png). 2026-10-25.
/// Runs from the menu, or once by itself after a compile when Temp/render_art_buttons exists (lets Claude check
/// without a click).</summary>
[InitializeOnLoad]
public static class ArtButtonRender
{
    const string TRIGGER = "Temp/render_art_buttons";
    static readonly string OUT = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Personal),
                                              "Documents/WordDrop_Art/vector_buttons/photoshop");

    static ArtButtonRender()
    {
        EditorApplication.delayCall += () =>
        {
            if (!EditorApplication.isPlayingOrWillChangePlaymode && Resources.Load<Material>(ArtButton.TEMPLATE_PATH) == null) CreateLabelMaterial();
            Poll();
        };
        // also poll (once a second) so a trigger written without a recompile still runs (label calibration loop)
        double next = 0;
        EditorApplication.update += () => { if (EditorApplication.timeSinceStartup < next) return; next = EditorApplication.timeSinceStartup + 1; Poll(); };
    }

    static void Poll()
    {
        {
            if (!File.Exists(TRIGGER) || EditorApplication.isPlayingOrWillChangePlaymode) return;
            string[] lines = File.ReadAllLines(TRIGGER);
            File.Delete(TRIGGER);
            if (Resources.Load("WordSlop Button SDF") == null) EditorApplication.ExecuteMenuItem("WordDrop/Fonts/Generate WordSlop SDF + button presets");
            AssetDatabase.Refresh();
            ArtButton.ClearMaterialCache();
            foreach (var l in lines)
            {
                if (l.Trim().Length == 0) continue;
                var p = l.Trim().Split('|');   // key | label | scale
                Render(p[0], p.Length > 1 && p[1].Length > 0 ? p[1] : null, p.Length > 2 ? float.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture) : 1f);
            }
        }
    }

    /// <summary>Creates Assets/Resources/UI/Buttons/ArtButtonLabel.mat: the look of every ArtButton label, on the FULL
    /// "TextMeshPro/Distance Field" shader (Face, Outline, Underlay + Bevel, Lighting, Glow), starting from Spencer's
    /// dialed-in values (2026-10-25). Edit the asset to restyle every label; the outline colour is the purple button's.</summary>
    [MenuItem("WordDrop/Art/Create Label Material (resets ArtButtonLabel.mat)")]
    static void CreateLabelMaterial()
    {
        var font = Resources.Load<TMPro.TMP_FontAsset>("WordSlop Button SDF") ?? Resources.Load<TMPro.TMP_FontAsset>("WordSlop SDF");
        var sh = Shader.Find("TextMeshPro/Distance Field");
        if (font == null || sh == null) { Debug.LogWarning("[ArtButtonRender] can't create label material: font or shader missing"); return; }
        var m = new Material(font.material) { shader = sh, name = "ArtButtonLabel" };
        m.shaderKeywords = new string[0];
        ArtButton.ApplyDefaults(m);
        m.DisableKeyword("OUTLINE_ON");   // a Mobile-shader switch; the full shader always draws the outline
        TMPro.ShaderUtilities.UpdateShaderRatios(m);
        string path = "Assets/Resources/" + ArtButton.TEMPLATE_PATH + ".mat";
        AssetDatabase.DeleteAsset(path);
        AssetDatabase.CreateAsset(m, path);
        AssetDatabase.SaveAssets();
        Debug.Log($"[ArtButtonRender] created {path} (shader {sh.name})");
    }

    [MenuItem("WordDrop/Art/Render Art Buttons (diff vs Photoshop)")]
    static void RenderAll()
    {
        Render("level_green", null); Render("level_purple", null); Render("rect_play", null); Render("pill_map", null);
        Render("level_green", "Level 7"); Render("level_green", "Level 1234");
    }

    static void Render(string key, string label, float scale = 1f)
    {
        var frame = Resources.Load<Sprite>($"UI/Buttons/{key}_frame");
        if (frame == null) { Debug.LogWarning($"[ArtButtonRender] no art for {key}"); return; }
        int W = Mathf.RoundToInt(frame.rect.width * scale), H = Mathf.RoundToInt(frame.rect.height * scale);

        var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
        var camGO = new GameObject("ArtButtonRenderCam") { hideFlags = HideFlags.HideAndDontSave };
        var cam = camGO.AddComponent<Camera>();
        cam.orthographic = true; cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color32(250, 240, 225, 255);   // the preview cream, same as the Photoshop comparisons
        cam.targetTexture = rt; cam.transform.position = new Vector3(0, 0, -100);

        var canvasGO = new GameObject("ArtButtonRenderCanvas", typeof(Canvas)) { hideFlags = HideFlags.HideAndDontSave };
        var canvas = canvasGO.GetComponent<Canvas>();
        // WORLD space at 1 unit = 1 px: a screen-space canvas sizes itself from the editor's screen, not the RT.
        canvas.renderMode = RenderMode.WorldSpace; canvas.worldCamera = cam;
        ((RectTransform)canvas.transform).sizeDelta = new Vector2(W, H);
        canvasGO.transform.position = Vector3.zero;
        cam.orthographicSize = H * 0.5f; cam.aspect = W / (float)H; cam.nearClipPlane = 0.1f; cam.farClipPlane = 1000f;
        Canvas.ForceUpdateCanvases();

        var ab = ArtButton.Create(canvas.transform, key, label);
        if (ab != null)
        {
            ab.PlaySounds = false;
            var r = (RectTransform)ab.transform;
            // Put the root over the frame's own pixels, so the art lands exactly on the PNG canvas.
            var json = JsonUtility.FromJson<FrameOnly>(Resources.Load<TextAsset>($"UI/Buttons/{key}_label").text);
            var fb = json.frameBox;
            r.sizeDelta = new Vector2(fb[2], fb[3]) * scale;
            r.anchoredPosition = new Vector2((fb[0] + fb[2] * 0.5f) * scale - W * 0.5f, H * 0.5f - (fb[1] + fb[3] * 0.5f) * scale);
            ab.Refit();
            Canvas.ForceUpdateCanvases();
            foreach (var t in ab.GetComponentsInChildren<TMPro.TMP_Text>()) t.ForceMeshUpdate(true, true);
            Canvas.ForceUpdateCanvases();
            cam.Render();

            var prev = RenderTexture.active; RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0); tex.Apply();
            RenderTexture.active = prev;
            string name = $"unity_{key}{(label != null ? "_" + label.Replace(" ", "") : "")}{(scale != 1f ? "_s" + Mathf.RoundToInt(scale * 100) : "")}.png";
            File.WriteAllBytes(Path.Combine(OUT, name), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            Debug.Log($"[ArtButtonRender] wrote {name} ({W}x{H}) label='{ab.Label}'");
        }
        Object.DestroyImmediate(canvasGO); Object.DestroyImmediate(camGO);
        rt.Release(); Object.DestroyImmediate(rt);
    }

    [System.Serializable] class FrameOnly { public int[] frameBox; }
}
