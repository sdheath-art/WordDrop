using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Particle effects for WordDrop game events.
    /// Uses Hovl Studio + Kenney textures from Resources/Particles.
    /// Each effect type has its own ParticleSystem tuned for the event.
    /// </summary>
    public class GameParticles : MonoBehaviour
    {
        public static GameParticles Instance { get; private set; }

        private ParticleSystem _sparkleSystem;   // word scored — gold stars
        private ParticleSystem _burstSystem;     // detonation — bright flash burst
        private ParticleSystem _confettiSystem;  // meltdown — colorful confetti
        private ParticleSystem _dustSystem;      // tile land — soft smoke puff
        private ParticleSystem _glowSystem;      // primed — soft radial glow pulse
        private ParticleSystem _chainSystem;     // chain detonation — intense flash + sparks
        private ParticleSystem _shimmerSystem;   // ambient shimmer — gold tiles, primed tiles

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            if (Instance == null)
            {
                var go = new GameObject("GameParticles");
                go.AddComponent<GameParticles>();
            }
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            BuildParticleSystems();
        }

        // ── Materials ───────────────────────────────────────────────────

        private Material _baseMat;

        /// <summary>Clone the pre-configured ParticleUnlit material with a different texture.</summary>
        private Material CloneMat(Texture2D tex)
        {
            if (_baseMat == null)
            {
                _baseMat = Resources.Load<Material>("ParticleUnlit");
                if (_baseMat == null)
                {
                    Debug.LogError("[GameParticles] ParticleUnlit material not found in Resources!");
                    return null;
                }
            }
            if (tex == null) return _baseMat;
            var mat = new Material(_baseMat);
            mat.mainTexture = tex;
            return mat;
        }

        // ── Build Systems ───────────────────────────────────────────────

        private void BuildParticleSystems()
        {
            // Load textures
            var starTex = Resources.Load<Texture2D>("Particles/star_hc");
            var flareTex = Resources.Load<Texture2D>("Particles/flare_hc");
            var flashTex = Resources.Load<Texture2D>("Particles/flashfree2");
            var glowTex = Resources.Load<Texture2D>("Particles/glowfree1");
            var smokeTex = Resources.Load<Texture2D>("Particles/smokefree1");
            var sparkTex = Resources.Load<Texture2D>("Particles/spark");
            var heartTex = Resources.Load<Texture2D>("Particles/heart");
            var snowflakeTex = Resources.Load<Texture2D>("Particles/snowflake");
            var starBasic = Resources.Load<Texture2D>("Particles/star");
            var softTex = Resources.Load<Texture2D>("Particles/soft_circle");

            // Fallback: use soft_circle if specific textures missing
            var fallback = softTex;

            // All materials clone from the pre-configured ParticleUnlit in Resources
            var starMat = CloneMat(starTex ?? fallback);
            var flareMat = CloneMat(flareTex ?? fallback);
            var flashMat = CloneMat(flashTex ?? fallback);
            var glowMat = CloneMat(glowTex ?? fallback);
            var sparkMat = CloneMat(sparkTex ?? fallback);
            var smokeMat = CloneMat(smokeTex ?? fallback);
            var confettiMat = CloneMat(heartTex ?? fallback);

            Debug.Log($"[GameParticles] Loaded textures: star_hc={starTex != null} flare_hc={flareTex != null} " +
                       $"flashfree2={flashTex != null} glowfree1={glowTex != null} smokefree1={smokeTex != null}");

            _sparkleSystem = BuildSparkles(starMat);
            _burstSystem = BuildBurst(flashMat, flareMat);
            _confettiSystem = BuildConfetti(confettiMat);
            _dustSystem = BuildDust(smokeMat);
            _glowSystem = BuildGlow(glowMat);
            _chainSystem = BuildChain(sparkMat, flashMat);
            _shimmerSystem = BuildShimmer(CloneMat(starTex ?? fallback));
        }

        // ── Sparkles: gold stars that twinkle outward ───────────────────

        private ParticleSystem BuildSparkles(Material mat)
        {
            var ps = CreateBase("PS_Sparkles");
            var main = ps.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.4f, 0.7f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.8f, 2.0f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.08f, 0.2f);
            main.startColor = new Color(1f, 0.9f, 0.5f, 1f); // warm gold
            main.gravityModifier = -0.3f; // float upward
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = 0.3f;

            // Size: start small, peak at 30%, fade out
            var sol = ps.sizeOverLifetime;
            sol.enabled = true;
            var sizeCurve = new AnimationCurve(
                new Keyframe(0f, 0.3f), new Keyframe(0.3f, 1f),
                new Keyframe(0.7f, 0.8f), new Keyframe(1f, 0f));
            sol.size = new ParticleSystem.MinMaxCurve(1f, sizeCurve);

            // Color: bright gold → transparent
            SetFadeAlpha(ps, new Color(1f, 0.92f, 0.5f, 1f));

            // Rotation spin
            var rot = ps.rotationOverLifetime;
            rot.enabled = true;
            rot.z = new ParticleSystem.MinMaxCurve(-90f * Mathf.Deg2Rad, 90f * Mathf.Deg2Rad);

            SetRenderer(ps, mat, 50);
            return ps;
        }

        // ── Burst: bright flash for detonations ─────────────────────────

        private ParticleSystem BuildBurst(Material flashMat, Material flareMat)
        {
            var ps = CreateBase("PS_Burst");
            var main = ps.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.15f, 0.35f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(2f, 5f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.3f, 0.7f);
            main.startColor = new Color(1f, 0.7f, 0.3f, 1f); // bright orange
            main.gravityModifier = 0.8f;

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = 0.15f;

            // Quick size peak then fast shrink
            var sol = ps.sizeOverLifetime;
            sol.enabled = true;
            var sizeCurve = new AnimationCurve(
                new Keyframe(0f, 0.5f), new Keyframe(0.15f, 1f), new Keyframe(1f, 0f));
            sol.size = new ParticleSystem.MinMaxCurve(1f, sizeCurve);

            SetFadeAlpha(ps, new Color(1f, 0.75f, 0.35f, 1f));

            SetRenderer(ps, flashMat, 51);
            return ps;
        }

        // ── Chain: intense sparks + flash ring for chain detonations ────

        private ParticleSystem BuildChain(Material sparkMat, Material flashMat)
        {
            var ps = CreateBase("PS_Chain");
            var main = ps.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.2f, 0.5f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(3f, 7f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.1f, 0.3f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1f, 0.5f, 0.1f, 1f),  // deep orange
                new Color(1f, 0.95f, 0.7f, 1f)   // near-white hot
            );
            main.gravityModifier = 0.4f;
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = 0.25f;

            var sol = ps.sizeOverLifetime;
            sol.enabled = true;
            sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0f));

            SetFadeAlpha(ps, new Color(1f, 0.8f, 0.4f, 1f));

            SetRenderer(ps, sparkMat, 52);
            return ps;
        }

        // ── Confetti: colorful shapes for meltdown ──────────────────────

        private ParticleSystem BuildConfetti(Material mat)
        {
            var ps = CreateBase("PS_Confetti");
            var main = ps.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.8f, 1.5f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(3f, 6f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.12f, 0.25f);
            main.gravityModifier = 2f; // heavy — confetti falls
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);

            // Random palette: gold, pink, white, purple
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1f, 0.84f, 0.42f, 1f),  // gold
                new Color(1f, 0.56f, 0.67f, 1f)    // pink
            );

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = 0.4f;

            // Tumble rotation
            var rot = ps.rotationOverLifetime;
            rot.enabled = true;
            rot.z = new ParticleSystem.MinMaxCurve(-270f * Mathf.Deg2Rad, 270f * Mathf.Deg2Rad);

            // Shrink slightly toward end
            var sol = ps.sizeOverLifetime;
            sol.enabled = true;
            sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0.4f));

            SetRenderer(ps, mat, 50);
            return ps;
        }

        // ── Dust: soft smoke puff on tile land ──────────────────────────

        private ParticleSystem BuildDust(Material mat)
        {
            var ps = CreateBase("PS_Dust");
            var main = ps.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.3f, 0.5f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.3f, 0.8f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.15f, 0.3f);
            main.startColor = new Color(0.75f, 0.7f, 0.8f, 0.4f); // light purple-grey
            main.gravityModifier = -0.1f; // slight upward drift

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = 0.2f;

            // Expand and fade
            var sol = ps.sizeOverLifetime;
            sol.enabled = true;
            var sizeCurve = new AnimationCurve(
                new Keyframe(0f, 0.5f), new Keyframe(0.3f, 1f), new Keyframe(1f, 1.3f));
            sol.size = new ParticleSystem.MinMaxCurve(1f, sizeCurve);

            SetFadeAlpha(ps, new Color(0.75f, 0.7f, 0.8f, 0.4f));

            SetRenderer(ps, mat, 45);
            return ps;
        }

        // ── Glow: soft radial pulse for primed tiles ────────────────────

        private ParticleSystem BuildGlow(Material mat)
        {
            var ps = CreateBase("PS_Glow");
            var main = ps.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.5f, 0.8f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.1f, 0.4f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.3f, 0.5f);
            main.startColor = new Color(1f, 0.85f, 0.3f, 0.7f); // warm gold glow
            main.gravityModifier = -0.15f;

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = 0.15f;

            // Pulse: grow then shrink
            var sol = ps.sizeOverLifetime;
            sol.enabled = true;
            var sizeCurve = new AnimationCurve(
                new Keyframe(0f, 0.5f), new Keyframe(0.4f, 1.2f), new Keyframe(1f, 0f));
            sol.size = new ParticleSystem.MinMaxCurve(1f, sizeCurve);

            SetFadeAlpha(ps, new Color(1f, 0.85f, 0.3f, 0.8f));

            SetRenderer(ps, mat, 48);
            return ps;
        }

        // ── Shimmer: tiny twinkling stars that drift upward ─────────────

        private ParticleSystem BuildShimmer(Material mat)
        {
            var ps = CreateBase("PS_Shimmer");
            var main = ps.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.6f, 1.0f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.15f, 0.4f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.04f, 0.10f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1f, 0.95f, 0.7f, 0.9f),   // warm white
                new Color(1f, 0.82f, 0.4f, 0.9f)     // gold
            );
            main.gravityModifier = -0.2f; // gentle float upward
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = 0.12f;

            // Twinkle: fade in, peak, fade out
            var sol = ps.sizeOverLifetime;
            sol.enabled = true;
            var sizeCurve = new AnimationCurve(
                new Keyframe(0f, 0f), new Keyframe(0.2f, 1f),
                new Keyframe(0.5f, 0.6f), new Keyframe(0.8f, 1f),
                new Keyframe(1f, 0f));
            sol.size = new ParticleSystem.MinMaxCurve(1f, sizeCurve);

            // Alpha twinkle
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[] {
                    new GradientColorKey(new Color(1f, 0.95f, 0.75f), 0f),
                    new GradientColorKey(new Color(1f, 0.9f, 0.6f), 1f)
                },
                new[] {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(0.9f, 0.2f),
                    new GradientAlphaKey(0.4f, 0.5f),
                    new GradientAlphaKey(0.8f, 0.75f),
                    new GradientAlphaKey(0f, 1f)
                }
            );
            col.color = grad;

            // Gentle spin
            var rot = ps.rotationOverLifetime;
            rot.enabled = true;
            rot.z = new ParticleSystem.MinMaxCurve(-60f * Mathf.Deg2Rad, 60f * Mathf.Deg2Rad);

            SetRenderer(ps, mat, 49); // above glow (48), below sparkle (50)
            return ps;
        }

        // ── Helpers ─────────────────────────────────────────────────────

        private ParticleSystem CreateBase(string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);

            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.playOnAwake = false;
            main.loop = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 200;

            var emission = ps.emission;
            emission.enabled = false; // manual Emit() only

            return ps;
        }

        private static void SetFadeAlpha(ParticleSystem ps, Color startColor)
        {
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(startColor, 0f), new GradientColorKey(startColor, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.8f, 0.5f), new GradientAlphaKey(0f, 1f) }
            );
            col.color = grad;
        }

        private static void SetRenderer(ParticleSystem ps, Material mat, int sortOrder)
        {
            var rend = ps.GetComponent<ParticleSystemRenderer>();
            rend.sortingOrder = sortOrder;
            if (mat != null) rend.sharedMaterial = mat;
            rend.renderMode = ParticleSystemRenderMode.Billboard;
        }

        private void EmitAt(ParticleSystem ps, Vector3 worldPos, int count)
        {
            if (ps == null) return;
            ps.transform.position = worldPos;
            ps.Emit(count);
        }

        // ── Public API ──────────────────────────────────────────────────

        /// <summary>Golden star sparkles when a word is scored. Count scales with points.</summary>
        public void PlayWordScored(Vector3 worldPos, int points)
        {
            int count = Mathf.Clamp(points * 2, 8, 30);
            EmitAt(_sparkleSystem, worldPos, count);
            // Add a soft glow underneath for bigger scores
            if (points >= 10)
                EmitAt(_glowSystem, worldPos, 3);
        }

        /// <summary>Bright burst when tiles detonate.</summary>
        public void PlayDetonation(Vector3 worldPos, int chainDepth)
        {
            int burstCount = Mathf.Min(12 + chainDepth * 5, 35);
            EmitAt(_burstSystem, worldPos, burstCount);
            // Chain detonations get extra spark shower
            if (chainDepth > 0)
                EmitAt(_chainSystem, worldPos, Mathf.Min(8 + chainDepth * 6, 30));
        }

        /// <summary>Confetti burst for meltdown moments.</summary>
        public void PlayMeltdown(Vector3 worldPos)
        {
            EmitAt(_confettiSystem, worldPos, 60);
            EmitAt(_burstSystem, worldPos, 15);   // flash core
            EmitAt(_sparkleSystem, worldPos, 20);  // extra sparkle
        }

        /// <summary>Small dust puff when tile lands on board.</summary>
        public void PlayTileLand(Vector3 worldPos)
        {
            EmitAt(_dustSystem, worldPos, 5);
        }

        /// <summary>Warm glow pulse when tile becomes primed.</summary>
        public void PlayPrimed(Vector3 worldPos)
        {
            EmitAt(_glowSystem, worldPos, 6);
            EmitAt(_sparkleSystem, worldPos, 4); // a few stars too
        }

        /// <summary>Subtle shimmer on a tile. Call periodically for ambient sparkle.</summary>
        public void PlayShimmer(Vector3 worldPos, int count = 2)
        {
            EmitAt(_shimmerSystem, worldPos, count);
        }

        /// <summary>Burst of shimmer for meltdown build-up — intensifying sparkle.</summary>
        public void PlayShimmerBurst(Vector3 worldPos, int count = 8)
        {
            EmitAt(_shimmerSystem, worldPos, count);
            EmitAt(_glowSystem, worldPos, 2);
        }
    }
}
