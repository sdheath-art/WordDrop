using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;

/// <summary>WordSlop — WordDrop's own typeface (2026-10-25): every glyph traced from Spencer's ChatGPT specimen, spacing
/// fitted to its word lines (source + build scripts: ~/Documents/WordDrop_Art/typography/). This bakes "WordSlop SDF" into
/// Resources with FontSdfGenerator's sane atlas settings (pt 90 / pad 9 = the same 0.1 spread as Nunito, so every outline /
/// underlay value tuned on Nunito carries over 1:1), then makes the BUTTON TEXT presets measured on the button sheet:
/// crisp dark ring in the button's own hue + a short hard drop below (+ a face gradient for the faint shading inside the
/// letter bottoms). Runs once by itself after compile if the asset is missing; re-run from the menu after replacing the .ttf.</summary>
[InitializeOnLoad]
public static class WordSlopSetup
{
    const string TTF = "Assets/Fonts/WordSlop-Bold.ttf";
    const string FONT = "WordSlop SDF";
    // 2026-10-25: button labels use a slightly lighter/narrower cut (Spencer picked "A": 2% narrower, strokes -12 units),
    // because the reference button art draws the lettering lighter than the specimen the main font was traced from.
    const string BTN_TTF = "Assets/Fonts/WordSlop-Button.ttf";
    const string BTN_FONT = "WordSlop Button SDF";
    const string DIR = "Assets/Resources";
    // 2026-10-29: button labels switched to Lilita One (Spencer). Baked with the SAME settings as WordSlop so the label
    // material's outline/underlay values carry over; kerning from Assets/Fonts/LilitaOne-Regular.kern.json.
    const string LILITA_TTF = "Assets/Fonts/LilitaOne-Regular.ttf";
    const string LILITA_FONT = "LilitaOne Button SDF";
    // 2026-11-06: ArtButton labels (Play / Level / OK) switched to WordSlop Title Medium (Spencer: "more distinct, helps
    // marketing"). Same bake settings, so the label material's outline/underlay values hold; kerning = the Button cut's file
    // (same GPOS kernfix lookup).
    const string TITLE_MED_TTF = "Assets/Fonts/WordSlop-Title-Medium.ttf";
    const string TITLE_MED_FONT = "WordSlop Title Medium SDF";
    // 2026-11-06: the BOLD Title cut for the level card's "Level N" heading (Spencer). Same bake + kerning file.
    const string TITLE_TTF = "Assets/Fonts/WordSlop-Title.ttf";
    const string TITLE_FONT = "WordSlop Title SDF";

    // (preset, ring colour, drop colour, top tint) — measured on the rebuilt buttons (vector_buttons/photoshop/*_scene_cfg.jsx).
    // Recipe (2026-10-25, measured at the sheet's 22px-cap resolution): PURE WHITE face + a faint tint of the button colour
    // inside the TOP edge (bottom stays white), ONE crisp ~1px dark ring hugging the letter, almost no drop shadow.
    static readonly (string name, string ring, string drop, string tint)[] PRESETS =
    {
        ("Button Green",  "#077007", "#045204", "#D6EDD2"),   // Play
        ("Button Purple", "#2808C6", "#1E069A", "#E2D8F6"),   // Continue
        ("Button Blue",   "#0533A9", "#06288C", "#D6E5F8"),   // Map
    };

    static WordSlopSetup()
    {
        EditorApplication.delayCall += () =>
        {
            if (File.Exists(TTF) && (!File.Exists($"{DIR}/{FONT}.asset") || !File.Exists($"{DIR}/{BTN_FONT}.asset"))) Build();
            if (File.Exists(LILITA_TTF) && !File.Exists($"{DIR}/{LILITA_FONT}.asset")) FontSdfGenerator.Generate(LILITA_TTF, LILITA_FONT);
            if (File.Exists(TITLE_MED_TTF) && !File.Exists($"{DIR}/{TITLE_MED_FONT}.asset")) FontSdfGenerator.Generate(TITLE_MED_TTF, TITLE_MED_FONT);
            if (File.Exists(TITLE_TTF) && !File.Exists($"{DIR}/{TITLE_FONT}.asset")) FontSdfGenerator.Generate(TITLE_TTF, TITLE_FONT);
        };
    }

    [MenuItem("WordDrop/Fonts/Generate WordSlop SDF + button presets")]
    public static void Build()
    {
        FontSdfGenerator.Generate(TTF, FONT);
        if (File.Exists(BTN_TTF)) FontSdfGenerator.Generate(BTN_TTF, BTN_FONT);
        if (File.Exists(LILITA_TTF)) FontSdfGenerator.Generate(LILITA_TTF, LILITA_FONT);
        if (File.Exists(TITLE_MED_TTF)) FontSdfGenerator.Generate(TITLE_MED_TTF, TITLE_MED_FONT);
        if (File.Exists(TITLE_TTF)) FontSdfGenerator.Generate(TITLE_TTF, TITLE_FONT);
        // presets are built on the BUTTON cut when it exists (they are button-label materials)
        var fa = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>($"{DIR}/{(File.Exists(BTN_TTF) ? BTN_FONT : FONT)}.asset");
        if (fa == null || fa.material == null) { Debug.LogError("[WordSlop] font asset missing after generate"); return; }

        foreach (var p in PRESETS)
        {
            string path = $"{DIR}/{fa.name} - {p.name}.mat";   // "<font> - <x>" so it shows in TMP's Material Preset dropdown
            var m = new Material(fa.material) { name = $"{fa.name} - {p.name}" };
            // ~1px crisp ring at a ~22px cap (em ~36px; spread 0.1 em): TMP outlines straddle the edge, so dilate the face a
            // little to keep the white weight and let the ring sit mostly outside.
            m.SetFloat("_FaceDilate", 0.12f);
            m.SetFloat("_OutlineWidth", 0.33f);   // 1.3 px at a ~22px cap: total ring darkness matched the sheet (1.0 px read "a bit thin")
            m.SetColor("_OutlineColor", Hex(p.ring));
            m.EnableKeyword("OUTLINE_ON");
            m.EnableKeyword("UNDERLAY_ON");                 // drop BELOW the ring: the sheet's dark edge is heaviest below > sides > top
            var d = Hex(p.drop); d.a = 0.75f;
            m.SetColor("_UnderlayColor", d);
            m.SetFloat("_UnderlayOffsetX", 0f);
            m.SetFloat("_UnderlayOffsetY", -0.5f);
            m.SetFloat("_UnderlayDilate", 0.25f);
            m.SetFloat("_UnderlaySoftness", 0.05f);
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(m, path);

            // the TOP-edge tint as a vertex gradient (TMP has no inner shadow): tint at the top -> pure white below.
            // Assign to a TMP_Text's colorGradientPreset with enableVertexGradient = true.
            string gpath = $"{DIR}/{fa.name} Face {p.name.Replace("Button ", "")}.asset";
            var grad = ScriptableObject.CreateInstance<TMP_ColorGradient>();
            grad.colorMode = ColorMode.VerticalGradient;
            grad.topLeft = grad.topRight = Color.Lerp(Hex(p.tint), Color.white, 0.4f);
            grad.bottomLeft = grad.bottomRight = Color.white;
            AssetDatabase.DeleteAsset(gpath);
            AssetDatabase.CreateAsset(grad, gpath);
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"[WordSlop] baked '{FONT}' + {PRESETS.Length} button presets + face gradients into {DIR}");
    }

    static Color Hex(string h) { ColorUtility.TryParseHtmlString(h, out var c); return c; }
}
