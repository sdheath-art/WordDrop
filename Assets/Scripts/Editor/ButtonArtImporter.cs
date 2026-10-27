using UnityEditor;

/// <summary>Import settings for the Photoshop button art in Resources/UI/Buttons (frame + blank face per button,
/// built by ~/Documents/WordDrop_Art/vector_buttons/photoshop/build_all.sh). 2026-10-25.
/// nPOTScale MUST be None: Unity's default rounds a non-power-of-two PNG to a power of two and silently squashes it
/// (cost hours on 2026-09-11). Frame and face are drawn on the same canvas and stacked 1:1, so any rescale or
/// compression difference between the two shows as a seam between the gold and the face.</summary>
public class ButtonArtImporter : AssetPostprocessor
{
    void OnPreprocessTexture()
    {
        bool nav = assetPath.StartsWith("Assets/Resources/UI/NavBar/")    // 2026-10-27: the bottom nav bar (2160 wide, 2x)
                || assetPath.StartsWith("Assets/Resources/UI/HudBar/");   //             and the top HUD bar (2358 wide, 2x)
        if (!assetPath.StartsWith("Assets/Resources/UI/Buttons/") && !nav) return;
        var ti = (TextureImporter)assetImporter;
        ti.textureType         = TextureImporterType.Sprite;
        ti.spriteImportMode    = SpriteImportMode.Single;
        ti.npotScale           = TextureImporterNPOTScale.None;
        ti.alphaIsTransparency = true;
        ti.mipmapEnabled       = false;
        ti.sRGBTexture         = true;
        ti.textureCompression  = TextureImporterCompression.CompressedHQ;
        ti.maxTextureSize      = nav ? 4096 : 1024;
    }
}
