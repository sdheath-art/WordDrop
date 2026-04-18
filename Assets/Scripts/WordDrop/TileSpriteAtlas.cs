using System.Collections.Generic;
using UnityEngine;
using TMPro;

namespace WordDrop
{
    /// <summary>
    /// Generates pre-baked tile sprites at runtime — each letter+points rendered
    /// into a single texture. Used by the fake 3D system so the shader can affect
    /// the entire tile (background + letter + points) as one image.
    ///
    /// Generated once on first request, then cached for the session.
    /// </summary>
    public static class TileSpriteAtlas
    {
        private static Dictionary<char, Sprite> s_cache;
        private static bool s_built;
        private static float s_cellSize;

        private const int TEX_SIZE = 256;
        private const int BAKE_LAYER = 31;

        /// <summary>
        /// Returns a pre-baked sprite for the given letter (with point value rendered in).
        /// Sprites are generated on first call and cached.
        /// </summary>
        public static Sprite Get(char letter, float cellSize)
        {
            if (!s_built || Mathf.Abs(cellSize - s_cellSize) > 0.01f)
                Build(cellSize);

            char upper = char.ToUpper(letter);
            if (s_cache.TryGetValue(upper, out Sprite sprite))
                return sprite;

            // Fallback: build this specific letter
            return BakeSingleLetter(upper, cellSize);
        }

        private static void Build(float cellSize)
        {
            s_cellSize = cellSize;
            s_cache = new Dictionary<char, Sprite>(26);

            for (char c = 'A'; c <= 'Z'; c++)
                BakeSingleLetter(c, cellSize);

            s_built = true;
//             Debug.Log("[TileSpriteAtlas] Built 26 baked tile sprites.");
        }

        private static Sprite BakeSingleLetter(char letter, float cellSize)
        {
            // Create a temporary tile off-screen
            GameObject tileGO = new GameObject($"BakeTile_{letter}");
            tileGO.transform.position = new Vector3(1000f, 1000f, 0f);

            Tile tile = tileGO.AddComponent<Tile>();
            tile.Initialise(letter, 0, 0, cellSize);

            // Force TMP to generate mesh immediately (otherwise it renders next frame)
            var tmps = tileGO.GetComponentsInChildren<TextMeshPro>();
            foreach (var tmp in tmps)
            {
                tmp.ForceMeshUpdate(true, true);
            }

            // Move all parts to bake layer AFTER TMP mesh generation
            // (TMP creates sub-mesh objects during ForceMeshUpdate)
            SetLayerRecursive(tileGO, BAKE_LAYER);

            // Double-check all renderers are on the bake layer
            foreach (var r in tileGO.GetComponentsInChildren<Renderer>(true))
                r.gameObject.layer = BAKE_LAYER;

            // The tile's visual world size = cellSize * 0.88 * localScale
            // But localScale is set by BuildVisuals: scale = displaySize / nativeSize
            // where displaySize = cellSize * 0.88 and nativeSize = texSize/100
            // So the tile's world bounds = nativeSize * scale = displaySize = cellSize * 0.88
            // Add small padding for the border
            float tileWorldSize = cellSize * 0.88f;
            float worldSize = tileWorldSize * 1.2f; // 20% padding

//             Debug.Log($"[TileSpriteAtlas] Baking '{letter}': cellSize={cellSize:F3} " +
                      // $"tileWorldSize={tileWorldSize:F3} captureArea={worldSize:F3} " +
                      // $"tileScale={tileGO.transform.localScale} " +
                      // $"tilePos={tileGO.transform.position}");

            // Create bake camera centered on the tile
            GameObject camGO = new GameObject("BakeCamera");
            Camera bakeCam = camGO.AddComponent<Camera>();
            bakeCam.orthographic = true;
            bakeCam.orthographicSize = worldSize * 0.5f;
            bakeCam.clearFlags = CameraClearFlags.SolidColor;
            bakeCam.backgroundColor = Color.clear;
            bakeCam.cullingMask = 1 << BAKE_LAYER;
            bakeCam.nearClipPlane = 0.1f;
            bakeCam.farClipPlane = 20f;
            bakeCam.transform.position = new Vector3(1000f, 1000f, -10f);

            // Render to texture
            RenderTexture rt = RenderTexture.GetTemporary(TEX_SIZE, TEX_SIZE, 0, RenderTextureFormat.ARGB32);
            rt.filterMode = FilterMode.Bilinear;
            bakeCam.targetTexture = rt;
            bakeCam.Render();

            // Read pixels
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            Texture2D tex = new Texture2D(TEX_SIZE, TEX_SIZE, TextureFormat.ARGB32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.ReadPixels(new Rect(0, 0, TEX_SIZE, TEX_SIZE), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);

            // PPU matches camera capture: sprite world size = worldSize = what the camera saw
            float ppu = TEX_SIZE / worldSize;
            Sprite sprite = Sprite.Create(tex,
                new Rect(0, 0, TEX_SIZE, TEX_SIZE),
                new Vector2(0.5f, 0.5f), ppu);
            sprite.name = $"BakedTile_{letter}";

            // Cleanup
            Object.Destroy(camGO);
            Object.Destroy(tileGO);

            // Cache
            s_cache[letter] = sprite;
            return sprite;
        }

        private static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            for (int i = 0; i < go.transform.childCount; i++)
                SetLayerRecursive(go.transform.GetChild(i).gameObject, layer);
        }
    }
}
