using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

/// <summary>Generate a TMP SDF font asset from a .ttf/.otf in the project, straight into
/// Assets/Resources so GameFont.TILE_FONT can load it by name.
///
/// Written 2026-09-11. WordDrop has 100+ font files in Assets/Fonts but only ~15 exist as SDF
/// assets, so every font trial was blocked on a manual trip through Window > TextMeshPro >
/// Font Asset Creator. This does it in one click and, importantly, uses SANE ATLAS SETTINGS.
///
/// WHY THE SETTINGS MATTER: several existing assets in Resources (Cartoon, LuckiestGuy, WendyOne,
/// Futura — all ~8.6MB) were generated with a huge padding-to-pointSize ratio. GameFont's own
/// comment records the result: they "bloat/melt/overlap glyphs at UI sizes" and are only usable
/// very large. The healthy assets (AvenirNext, Montserrat-Black, Fredoka-Bold, LilitaOne) are
/// ~580KB. Point size 90 with padding 9 gives the ~0.1 ratio that behaves.</summary>
public static class FontSdfGenerator
{
    private const int POINT_SIZE = 90;   // spread ratio ~0.1 — see the note above
    private const int PADDING    = 9;
    private const int ATLAS      = 1024;

    // Characters baked up front. Dynamic mode would add them on demand, but pre-populating
    // means no first-use hitch and no surprises if a glyph is missing from the face.
    private const string CHARSET =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789" +
        " .,:;!?'\"()[]{}+-*/=%&#@_<>|~^$×✓✕⚙";

    [MenuItem("WordDrop/Fonts/Generate SDF from selected font")]
    public static void GenerateFromSelection()
    {
        var font = Selection.activeObject as Font;
        if (font == null)
        {
            EditorUtility.DisplayDialog("Font SDF Generator",
                "Select a .ttf or .otf in the Project window first.", "OK");
            return;
        }
        Generate(AssetDatabase.GetAssetPath(font), font.name + " SDF");
    }

    [MenuItem("WordDrop/Fonts/Generate Montserrat ExtraBold SDF")]
    public static void GenMontserratXB() => Generate("Assets/Fonts/Montserrat-ExtraBold.ttf", "MontserratExtraBold SDF");

    [MenuItem("WordDrop/Fonts/Generate Qilka Bold SDF")]
    public static void GenQilka() => Generate("Assets/Fonts/Qilka-Bold copy.otf", "QilkaBold SDF");

    [MenuItem("WordDrop/Fonts/Generate Capitol SDF")]
    public static void GenCapitol() => Generate("Assets/Thrill Fonts/TTF Fonts/Capitol.ttf", "Capitol SDF");

    [MenuItem("WordDrop/Fonts/Generate Riffic SDF")]
    public static void GenRiffic() => Generate("Assets/Fonts/RifficFree-Bold.ttf", "Riffic SDF");

    [MenuItem("WordDrop/Fonts/Generate Fredoka SemiBold SDF")]
    public static void GenFredokaSemi() => Generate("Assets/Fonts/Fredoka-SemiBold.ttf", "FredokaSemiBold SDF");

    [MenuItem("WordDrop/Fonts/Generate M PLUS Rounded Black SDF")]
    public static void GenMPlusRoundedBlack() => Generate("Assets/Fonts/MPLUSRounded1c-Black.ttf", "MPlusRoundedBlack SDF");

    /// <summary>2026-11-01 (Spencer picked M PLUS Rounded Black for the tile letters off the full-screen mocks): bake its SDF
    /// automatically the first time the editor loads without it, so nobody has to find the menu item.</summary>
    [InitializeOnLoadMethod]
    private static void AutoBakeTileFont()
    {
        if (File.Exists("Assets/Resources/MPlusRoundedBlack SDF.asset") || !File.Exists("Assets/Fonts/MPLUSRounded1c-Black.ttf")) return;
        EditorApplication.delayCall += () =>
        {
            if (File.Exists("Assets/Resources/MPlusRoundedBlack SDF.asset")) return;
            Debug.Log("[FontSDF] baking MPlusRoundedBlack SDF (tile letters, first load)");
            GenMPlusRoundedBlack();
        };
    }

    [MenuItem("WordDrop/Fonts/Generate Nunito Black SDF")]
    public static void GenNunitoBlack() => Generate("Assets/Fonts/Nunito-Black.ttf", "NunitoBlack SDF");

    public static void Generate(string ttfPath, string outName)
    {
        Font src = AssetDatabase.LoadAssetAtPath<Font>(ttfPath);
        if (src == null)
        {
            Debug.LogError($"[FontSDF] could not load a Font at {ttfPath}");
            return;
        }

        Directory.CreateDirectory("Assets/Resources");
        string outPath = $"Assets/Resources/{outName}.asset";

        TMP_FontAsset fa = TMP_FontAsset.CreateFontAsset(
            src, POINT_SIZE, PADDING, GlyphRenderMode.SDFAA,
            ATLAS, ATLAS, AtlasPopulationMode.Dynamic, true);

        if (fa == null)
        {
            Debug.LogError($"[FontSDF] CreateFontAsset returned null for {ttfPath}");
            return;
        }

        fa.name = outName;

        // Bake the charset now so nothing is rasterised mid-game. This TMP version's
        // TryAddCharacters takes uint code points, not a string.
        var codes = new System.Collections.Generic.List<uint>(CHARSET.Length);
        foreach (char ch in CHARSET) codes.Add(ch);
        bool all = fa.TryAddCharacters(codes.ToArray());
        if (!all)
            Debug.LogWarning($"[FontSDF] {outName}: some characters are not in the face — fine if they are symbols.");

        ImportKerning(fa, outName, ttfPath);

        AssetDatabase.DeleteAsset(outPath);          // regenerate cleanly
        AssetDatabase.CreateAsset(fa, outPath);

        // Atlas texture and material must live inside the asset or the sprite renders blank.
        if (fa.atlasTextures != null)
        {
            for (int i = 0; i < fa.atlasTextures.Length; i++)
            {
                if (fa.atlasTextures[i] == null) continue;
                fa.atlasTextures[i].name = $"{outName} Atlas {i}";
                AssetDatabase.AddObjectToAsset(fa.atlasTextures[i], fa);
            }
        }
        if (fa.material != null)
        {
            fa.material.name = $"{outName} Material";
            AssetDatabase.AddObjectToAsset(fa.material, fa);
        }

        EditorUtility.SetDirty(fa);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        var info = new FileInfo(outPath);
        Debug.Log($"[FontSDF] created '{outName}' from {Path.GetFileName(ttfPath)} " +
                  $"(pointSize {POINT_SIZE}, padding {PADDING}) -> {outPath} " +
                  $"[{(info.Exists ? info.Length / 1024 : 0)} KB]. " +
                  $"Load it with GameFont.TILE_FONT = \"{outName}\";");
    }

    /// <summary>2026-10-25: TMP's dynamic import left m_GlyphPairAdjustmentRecords EMPTY for WordSlop even though the
    /// .ttf carries GPOS pair kerning (the +20 anti-collision pairs: 7-1, TT, TY, VY…), so "71" etc. set tighter in
    /// Unity than in Photoshop. Ask the font engine for the pairs between the baked glyphs and add them by hand.</summary>
    static void ImportKerning(TMP_FontAsset fa, string outName, string ttfPath)
    {
        // Pairs exported from the .ttf's GPOS by Python (FontEngine's pair-table call is internal in this Unity).
        string kp = Path.ChangeExtension(ttfPath, null) + ".kern.json";
        if (!File.Exists(kp)) return;
        var data = JsonUtility.FromJson<KernFile>("{\"unitsPerEm\":" + ExtractUpm(File.ReadAllText(kp)) + ",\"rows\":" + ToRows(File.ReadAllText(kp)) + "}");
        var list = fa.fontFeatureTable.glyphPairAdjustmentRecords;
        float k = fa.faceInfo.pointSize / data.unitsPerEm;
        int added = 0;
        foreach (var row in data.rows)
        {
            if (!fa.characterLookupTable.TryGetValue((uint)row.a, out var ca) || !fa.characterLookupTable.TryGetValue((uint)row.b, out var cb)) continue;
            list.Add(new UnityEngine.TextCore.LowLevel.GlyphPairAdjustmentRecord(
                new UnityEngine.TextCore.LowLevel.GlyphAdjustmentRecord(ca.glyphIndex, new UnityEngine.TextCore.LowLevel.GlyphValueRecord(0, 0, row.v * k, 0)),
                new UnityEngine.TextCore.LowLevel.GlyphAdjustmentRecord(cb.glyphIndex, new UnityEngine.TextCore.LowLevel.GlyphValueRecord(0, 0, 0, 0))));
            added++;
        }
        fa.ReadFontAssetDefinition();
        Debug.Log($"[FontSDF] {outName}: {added} kerning pairs imported from {Path.GetFileName(kp)}");
    }
    [System.Serializable] class KernRow { public int a, b; public float v; }
    [System.Serializable] class KernFile { public float unitsPerEm; public KernRow[] rows; }
    static string ExtractUpm(string j) { var m = System.Text.RegularExpressions.Regex.Match(j, "\"unitsPerEm\":\\s*([0-9.]+)"); return m.Success ? m.Groups[1].Value : "1000"; }
    static string ToRows(string j)
    {
        // [[a,b,v],...] -> [{"a":..,"b":..,"v":..},...]  (JsonUtility can't read nested arrays)
        var sb = new System.Text.StringBuilder("[");
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(j, "\\[\\s*(-?\\d+)\\s*,\\s*(-?\\d+)\\s*,\\s*(-?[0-9.]+)\\s*\\]"))
            sb.Append(sb.Length > 1 ? "," : "").Append($"{{\"a\":{m.Groups[1].Value},\"b\":{m.Groups[2].Value},\"v\":{m.Groups[3].Value}}}");
        return sb.Append("]").ToString();
    }
}
