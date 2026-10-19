using UnityEditor;
using UnityEngine;

/// <summary>Force a texture back through the importer.
///
/// 2026-09-22: the board frame's .meta was edited on disk (nPOTScale 1 -> 0) while Unity was
/// open. A refresh normally catches that, but "normally" is not "certainly" — and an import
/// setting that did not take looks exactly like a broken asset. ForceUpdate removes the doubt.</summary>
public static class ArtMenu
{
    private const string FRAME = "Assets/Resources/Tiles/board_frame@2x.png";

    [MenuItem("WordDrop/Art/Force Reimport Board Frame")]
    public static void ReimportFrame()
    {
        AssetDatabase.ImportAsset(FRAME, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
        AssetDatabase.ImportAsset("Assets/Resources/Tiles/inner_board@2x.png", ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
        AssetDatabase.ImportAsset("Assets/Resources/Tiles/cell_socket@2x.png", ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
        AssetDatabase.ImportAsset("Assets/Resources/Tiles/frame_shadow@2x.png", ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(FRAME);
        var imp = AssetImporter.GetAtPath(FRAME) as TextureImporter;
        Debug.Log($"[Art] reimported {FRAME} -> {(tex == null ? "NULL" : $"{tex.width}x{tex.height}")} " +
                  $"npotScale={(imp == null ? "?" : imp.npotScale.ToString())} " +
                  $"border={(imp == null ? "?" : imp.spriteBorder.ToString())}");
    }

    /// <summary>The digit sprites live in a folder created outside Unity, so the editor does
    /// not know about it until something forces an import. 2026-10-09.</summary>
    [MenuItem("WordDrop/Art/Force Reimport Payout Digits")]
    public static void ReimportDigits()
    {
        AssetDatabase.ImportAsset("Assets/Resources/Digits",
            ImportAssetOptions.ForceUpdate | ImportAssetOptions.ImportRecursive
          | ImportAssetOptions.ForceSynchronousImport);
        int n = 0;
        foreach (char c in "0123456789")
            if (Resources.Load<Sprite>("Digits/digit_" + c) != null) n++;
        bool comma = Resources.Load<Sprite>("Digits/digit_comma") != null;
        bool plus  = Resources.Load<Sprite>("Digits/digit_plus")  != null;
        Debug.Log($"[Art] payout digits: {n}/10 loaded, comma={comma}, plus={plus}");
    }

    [MenuItem("WordDrop/Art/Force Reimport All Tiles")]
    public static void ReimportTiles()
    {
        AssetDatabase.ImportAsset("Assets/Resources/Tiles",
            ImportAssetOptions.ForceUpdate | ImportAssetOptions.ImportRecursive);
        Debug.Log("[Art] reimported Assets/Resources/Tiles");
    }
}
