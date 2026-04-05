using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace WordDrop
{
    /// <summary>
    /// Sets up 2D URP lighting for the WordDrop scene.
    /// Creates a Global Light for ambient illumination and provides
    /// methods to spawn dynamic Point Lights for game events.
    /// Auto-creates on scene load.
    /// </summary>
    public class LightingSetup : MonoBehaviour
    {
        public static LightingSetup Instance { get; private set; }

        private Light2D _globalLight;
        private GameObject _bloomVolume;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            if (Instance == null)
            {
                var go = new GameObject("LightingSetup");
                go.AddComponent<LightingSetup>();
            }
        }

        private Material _spriteLitMat;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;

            // Load the lit sprite material from Resources
            _spriteLitMat = Resources.Load<Material>("SpriteLit2D");
            if (_spriteLitMat != null)
                Debug.Log("[LightingSetup] Loaded SpriteLit2D material from Resources");
            else
                Debug.LogWarning("[LightingSetup] SpriteLit2D material not found in Resources!");

            // 2D lights disabled — causes black sprites with current material setup
            // SetupGlobalLight();
            SetupBloom();

            // Material conversion disabled — sprites use default material
            // The Global Light2D illuminates all sprites regardless of material
            // StartCoroutine(ConvertSpriteMaterials());
        }

        private System.Collections.IEnumerator ConvertSpriteMaterials()
        {
            // Wait several frames for all game objects to be created
            for (int i = 0; i < 5; i++) yield return null;
            ConvertAllSprites();
            // Keep converting periodically for dynamically created sprites
            while (true)
            {
                yield return new WaitForSeconds(0.5f);
                ConvertAllSprites();
            }
        }

        private void ConvertAllSprites()
        {
            if (_spriteLitMat == null) return;
            foreach (var sr in FindObjectsByType<SpriteRenderer>(FindObjectsSortMode.None))
            {
                if (!ShouldUseLitMaterial(sr))
                    continue;

                if (sr.sharedMaterial != _spriteLitMat)
                    sr.sharedMaterial = _spriteLitMat;
            }
        }

        /// <summary>Call this on newly created SpriteRenderers so they respond to 2D lights.</summary>
        public void ApplyLitMaterial(SpriteRenderer sr)
        {
            if (sr != null && _spriteLitMat != null && ShouldUseLitMaterial(sr))
                sr.sharedMaterial = _spriteLitMat;
        }

        private static bool ShouldUseLitMaterial(SpriteRenderer sr)
        {
            if (sr == null)
                return false;

            if (sr.GetComponent<Tile>() != null)
                return true;

            string objectName = sr.gameObject.name;
            return objectName.StartsWith("HandCard_") || objectName == "NextTilePreview";
        }

        private void SetupGlobalLight()
        {
            // Global 2D light — warm ambient illumination
            GameObject lightGO = new GameObject("Global2DLight");
            lightGO.transform.SetParent(transform, false);

            _globalLight = lightGO.AddComponent<Light2D>();
            _globalLight.lightType = Light2D.LightType.Global;
            _globalLight.color = new Color(1f, 1f, 1f, 1f);
            _globalLight.intensity = 1f;
            _globalLight.blendStyleIndex = 0;

            // Target ALL sorting layers
            var layers = SortingLayer.layers;
            Debug.Log($"[LightingSetup] Sorting layers count: {layers.Length}");
            for (int i = 0; i < layers.Length; i++)
                Debug.Log($"[LightingSetup]   Layer '{layers[i].name}' id={layers[i].id} value={layers[i].value}");

            int[] ids = new int[layers.Length];
            for (int i = 0; i < layers.Length; i++) ids[i] = layers[i].id;
            _globalLight.targetSortingLayers = ids;
            Debug.Log($"[LightingSetup] Global light targeting {ids.Length} layers, first id={ids[0]}");
        }

        private void SetupBloom()
        {
            // Post-processing volume for bloom
            _bloomVolume = new GameObject("BloomVolume");
            _bloomVolume.transform.SetParent(transform, false);

            var volume = _bloomVolume.AddComponent<UnityEngine.Rendering.Volume>();
            volume.isGlobal = true;
            volume.priority = 1;

            var profile = ScriptableObject.CreateInstance<UnityEngine.Rendering.VolumeProfile>();
            volume.profile = profile;

            // Bloom — tight, punchy video game glow (not realistic light bleed)
            var bloom = profile.Add<UnityEngine.Rendering.Universal.Bloom>(true);
            bloom.threshold.value = 1.0f;  // only HDR values bloom (primed=1.8, gold=2.0)
            bloom.intensity.value = 0.8f;  // strong pop on what does bloom
            bloom.scatter.value = 0.3f;    // tight glow hugs the source — Balatro/neon style

            // Add subtle vignette for focus
            var vignette = profile.Add<UnityEngine.Rendering.Universal.Vignette>(true);
            vignette.intensity.value = 0.25f;
            vignette.smoothness.value = 0.4f;
            vignette.color.value = new Color(0.1f, 0.05f, 0.15f, 1f); // dark purple edge

            // Color Adjustments — boost saturation and contrast, preserve purple hue
            var colorAdj = profile.Add<UnityEngine.Rendering.Universal.ColorAdjustments>(true);
            colorAdj.postExposure.value = 0.05f;           // subtle brightness lift
            colorAdj.contrast.value = 10f;                 // punch up contrast
            colorAdj.saturation.value = 15f;               // richer colors
            colorAdj.hueShift.value = 3f;                  // nudge blues back toward purple
            colorAdj.colorFilter.value = new Color(1f, 0.98f, 0.97f, 1f); // near-neutral, tiny warmth

            // Neutral Tonemapping — no hue shift on purples (ACES pushes purple→blue)
            var tonemap = profile.Add<UnityEngine.Rendering.Universal.Tonemapping>(true);
            tonemap.mode.value = UnityEngine.Rendering.Universal.TonemappingMode.Neutral;

            // Lift/Gamma/Gain — keep purples true, push golds brighter
            var curves = profile.Add<UnityEngine.Rendering.Universal.LiftGammaGain>(true);
            curves.lift.value = new Vector4(0.02f, -0.03f, 0.03f, 0f);   // shadows: red+blue = purple
            curves.gamma.value = new Vector4(0.01f, -0.01f, 0.01f, 0f);  // mids: slight purple warmth
            curves.gain.value = new Vector4(0.04f, 0.02f, -0.02f, 0f);   // highlights: gold push

            Debug.Log("[LightingSetup] Post-processing: bloom + vignette + color grading + ACES tonemapping");
        }

        /// <summary>
        /// Spawn a temporary point light at a world position — used for explosions, scoring, etc.
        /// Light fades out and self-destructs.
        /// </summary>
        public void SpawnFlashLight(Vector3 worldPos, Color color, float intensity = 2f, float radius = 3f, float duration = 0.3f)
        {
            // Disabled — 2D lights cause black sprites until material pipeline is fixed
            // StartCoroutine(FlashLightCoroutine(worldPos, color, intensity, radius, duration));
        }

        private System.Collections.IEnumerator FlashLightCoroutine(Vector3 pos, Color color, float intensity, float radius, float duration)
        {
            GameObject go = new GameObject("FlashLight2D");
            go.transform.position = new Vector3(pos.x, pos.y, 0f);

            Light2D light = go.AddComponent<Light2D>();
            light.lightType = Light2D.LightType.Point;
            light.color = color;
            light.intensity = intensity;
            light.pointLightOuterRadius = radius;
            light.pointLightInnerRadius = radius * 0.3f;
            light.blendStyleIndex = 0;
            light.targetSortingLayers = new int[] { 0 };

            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                // Quick peak then fade
                float curve = t < 0.2f ? (t / 0.2f) : (1f - ((t - 0.2f) / 0.8f));
                light.intensity = intensity * curve;
                light.pointLightOuterRadius = radius * (0.5f + 0.5f * curve);
                yield return null;
            }

            Destroy(go);
        }

        /// <summary>Warm golden flash for word scoring.</summary>
        public void FlashWordScored(Vector3 pos)
        {
            SpawnFlashLight(pos, new Color(1f, 0.85f, 0.4f, 1f), 1.5f, 2.5f, 0.4f);
        }

        /// <summary>Bright white flash for detonation.</summary>
        public void FlashDetonation(Vector3 pos, int chainDepth = 0)
        {
            float intensity = 2.5f + chainDepth * 0.5f;
            float radius = 3f + chainDepth * 0.5f;
            SpawnFlashLight(pos, new Color(1f, 0.7f, 0.3f, 1f), intensity, radius, 0.35f);
        }

        /// <summary>Big dramatic flash for meltdown.</summary>
        public void FlashMeltdown(Vector3 pos)
        {
            SpawnFlashLight(pos, new Color(1f, 0.95f, 0.7f, 1f), 4f, 6f, 0.6f);
        }

        /// <summary>Subtle glow for primed tile.</summary>
        public void FlashPrimed(Vector3 pos)
        {
            SpawnFlashLight(pos, new Color(1f, 0.8f, 0.3f, 1f), 1f, 1.5f, 0.5f);
        }

        private static void ApplyAllSortingLayers(Light2D light)
        {
            if (light == null)
                return;

            SortingLayer[] sortingLayers = SortingLayer.layers;
            int[] sortingLayerIds = new int[sortingLayers.Length];

            for (int i = 0; i < sortingLayers.Length; i++)
                sortingLayerIds[i] = sortingLayers[i].id;

            light.targetSortingLayers = sortingLayerIds;
        }
    }
}
