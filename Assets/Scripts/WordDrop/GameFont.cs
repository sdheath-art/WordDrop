using UnityEngine;
using TMPro;

namespace WordDrop
{
    /// <summary>
    /// Font provider — 3 distinct roles:
    ///   Tile:    Avenir Next — clean, legible on board tiles
    ///   UI:      Clarity (Thrill Fonts) — readable HUD, buttons, labels
    ///   Display: Cartoon (Thrill Fonts) — celebrations, announcements, big moments
    /// </summary>
    public static class GameFont
    {
        private static Font _tileFont;
        private static Font _uiFont;
        private static bool _loadedTile, _loadedUI;

        /// <summary>2026-10-22 Spencer: "can you just change all the font to nunito please". UI, display and legacy text all
        /// use Nunito Black (the tile font). false = the old Montserrat (UI) / Cartoon (display + legacy) split.</summary>
        public static bool ALL_NUNITO = true;
        /// <summary>2026-10-22 Spencer: "maybe we should just stick to montserrat for numbers". The styled numbers (HUD moves,
        /// level star, goal/reward badges, score pop-ups) use Montserrat Black; words stay Nunito.</summary>
        public static bool NUMBERS_MONTSERRAT = true;
        /// <summary>2026-10-25: WordSlop — our own face (traced from Spencer's ChatGPT specimen; Editor/WordSlopSetup.cs bakes it).
        /// true = UI + display WORDS use it. Tiles (TILE_FONT) and styled numbers (NUMBERS_MONTSERRAT) are unchanged on purpose —
        /// both were Spencer's explicit picks. false = back to the ALL_NUNITO behaviour.</summary>
        public static bool WORDSLOP = true;
        public const string WORDSLOP_FONT = "WordSlop SDF";
        private static TMP_FontAsset LoadWordSlop() => WORDSLOP ? Resources.Load<TMP_FontAsset>(WORDSLOP_FONT) : null;
        private static TMP_FontAsset _numberTMP; private static bool _loadedNumberTMP;
        public static TMP_FontAsset GetNumberTMP()
        {
            if (!NUMBERS_MONTSERRAT) return GetTMP();
            if (!_loadedNumberTMP) { _numberTMP = Resources.Load<TMP_FontAsset>("Montserrat-Black SDF"); _loadedNumberTMP = true; }
            return _numberTMP != null ? _numberTMP : GetTMP();
        }
        /// <summary>Outline/dilate/underlay values are in units of the atlas SPREAD (padding / point size), which differs per
        /// font: Nunito 9/90 = 0.10, Montserrat 3/67 = 0.045. Multiply a value tuned on Nunito by this to get the same
        /// on-screen thickness in <paramref name="f"/>.</summary>
        public static float SpreadScaleVsNunito(TMP_FontAsset f)
        {
            if (f == null || f.faceInfo.pointSize <= 0 || f.atlasPadding <= 0) return 1f;
            return 0.10f / ((float)f.atlasPadding / f.faceInfo.pointSize);
        }
        private static TMP_FontAsset _tileTMP;
        private static TMP_FontAsset _uiTMP;
        private static TMP_FontAsset _displayTMP;
        private static bool _loadedTileTMP, _loadedUITMP, _loadedDisplayTMP;

        // ── TMP ─────────────────────────────────────────────────────────────────

        /// <summary>Tile font: Clarity SDF — trying it on board + hand-card letters (2026-06-03, was Baloo2 SDF). Fallbacks Baloo2 → AvenirNext.</summary>
        // ── FONT CHOICE (2026-09-11) ───────────────────────────────────────────────
        // The tile face was AvenirNext SDF — a system UI typeface. Every reference game
        // (Royal Match, Toon Blast, Candy Crush, Scrabble GO) uses a CHUNKY DISPLAY face with a
        // heavy dark outline, and [[feedback-mockup-pro-look-levers]] ranks that **lever #1**.
        //
        // NOTE the fallback chain below never ran — AvenirNext was loaded FIRST, so the system
        // font was the deliberate choice, not a fallback. The comment claiming "Clarity SDF"
        // disagreed with the code.
        //
        // WHY LilitaOne AND NOT LuckiestGuy: the SDF assets in Resources fall into two atlas
        // generations. ~580KB (AvenirNext, Montserrat-Black, Fredoka-Bold, **LilitaOne**) are
        // healthy. ~8.6MB (Cartoon, **LuckiestGuy**, WendyOne, Futura) were generated with a
        // huge padding/spread ratio — the comment on GetUITMP records that Cartoon SDF
        // "bloats/melts/overlaps glyphs at UI sizes" and is only usable very large. LuckiestGuy
        // is in that same bad class and also logs resolvable=False. Don't reach for it without
        // regenerating the atlas (Window > TextMeshPro > Font Asset Creator, pointSize ~90,
        // padding ~9).
        //
        // Set TILE_FONT back to "AvenirNext SDF" to revert.
        // 2026-09-11: tried LilitaOne first — WRONG TOOL. It is a CONDENSED HEADLINE face,
        // built to cram words into a banner. On a single letter in a square tile that reads
        // tall, narrow, and leaves dead space at the sides. Spencer: "im not a big fan".
        //
        // The real lesson: the chunky display font belongs on the HUD NUMBERS, not the play
        // pieces. Royal Match's tiles are icons (no letters at all); Scrabble GO's tile letters
        // are a plain heavy SANS. The display face is on the "8" and the "13", never the board.
        //
        // Tile letters want WIDE + HEAVY + friendly so they fill the square. Fredoka Bold:
        // rounded letterforms that echo the rounded tiles, warm, fills the cell properly.
        // Alternatives, both already SDF'd and in the healthy ~580KB atlas class:
        //   "Montserrat-Black SDF" — heaviest/widest, max legibility, less playful
        //   "AvenirNext SDF"       — the original; clean but neutral
        // 2026-09-11 status: Qilka Bold was tried and REJECTED by Spencer. Back to Montserrat
        // ExtraBold, which is the last font he did not object to. Rejected so far:
        //   LilitaOne (too condensed) · Fredoka-Bold (too blobby) · Riffic (over the top) ·
        //   QilkaBold (rejected). Untried and still on the shortlist: Nunito Black, Capitol
        //   (Thrill, licensed), Fredoka SemiBold, FatFrank Heavy — all one menu click away
        //   via WordDrop > Fonts.
        public static string TILE_FONT = "NunitoBlack SDF";

        public static TMP_FontAsset GetTMP()
        {
            if (!_loadedTileTMP)
            {
                _tileTMP = Resources.Load<TMP_FontAsset>(TILE_FONT);
                if (_tileTMP == null) _tileTMP = Resources.Load<TMP_FontAsset>("AvenirNext SDF");
                if (_tileTMP == null) _tileTMP = Resources.Load<TMP_FontAsset>("ClearSans-Bold SDF"); // fallback
                if (_tileTMP == null) _tileTMP = Resources.Load<TMP_FontAsset>("Geometos SDF"); // fallback
                if (_tileTMP == null) _tileTMP = Resources.Load<TMP_FontAsset>("Clarity SDF"); // fallback
                if (_tileTMP == null) _tileTMP = Resources.Load<TMP_FontAsset>("Baloo2 SDF"); // fallback
                if (_tileTMP == null) _tileTMP = Resources.Load<TMP_FontAsset>("AvenirNext SDF"); // fallback
                _loadedTileTMP = true;
                bool gotLuckiest = Resources.Load<TMP_FontAsset>("LuckiestGuy SDF") != null;
                WDLog.Log($"[GameFont] Tile font requested='{TILE_FONT}' loaded='{(_tileTMP != null ? _tileTMP.name : "NULL")}' | LuckiestGuy resolvable={gotLuckiest}");
            }
            return _tileTMP;
        }

        // 2026-06-04 Spencer: dedicated font for the WILD "?" symbol (Geometos),
        // so the wild glyph reads distinct/enchanted vs the normal tile letters.
        private static TMP_FontAsset _wildSymbolTMP;
        private static bool _loadedWildSymbolTMP;
        public static TMP_FontAsset GetWildSymbolTMP()
        {
            if (!_loadedWildSymbolTMP)
            {
                _wildSymbolTMP = Resources.Load<TMP_FontAsset>("Geometos SDF");
                _loadedWildSymbolTMP = true;
            }
            return _wildSymbolTMP;
        }

        /// <summary>UI font: Clarity SDF — HUD, buttons, scores, labels, game over body.</summary>
        public static TMP_FontAsset GetUITMP()
        {
            if (!_loadedUITMP)
            {
                // 2026-06-15 Spencer: UI/HUD uses Montserrat-Black SDF (clean). We TRIED Cartoon SDF for
                // everything, but that asset was generated with padding=20 at pointSize=42 (spread ratio
                // ~0.48 vs the normal ~0.05–0.1) — the huge SDF spread bloats/melts/overlaps glyphs at UI
                // sizes (fine only on the giant title). It's baked into the atlas; no code fix. To go full
                // Cartoon, regenerate "Cartoon SDF" in Window▸TextMeshPro▸Font Asset Creator at pointSize
                // ~90, padding ~9, then switch the first load back to "Cartoon SDF". Display/celebration
                // text (GetDisplayTMP) stays Cartoon — it's always large, where the bad atlas looks fine.
                _uiTMP = LoadWordSlop();
                if (_uiTMP == null && ALL_NUNITO) _uiTMP = GetTMP();
                if (_uiTMP == null) _uiTMP = Resources.Load<TMP_FontAsset>("Montserrat-Black SDF");
                if (_uiTMP == null) _uiTMP = Resources.Load<TMP_FontAsset>("Cartoon SDF");
                if (_uiTMP == null) _uiTMP = GetTMP();
                _loadedUITMP = true;
//                 WDLog.Log($"[GameFont] UI font loaded: {(_uiTMP != null ? _uiTMP.name : "NULL")}");
            }
            return _uiTMP;
        }

        /// <summary>Display font: Cartoon SDF — celebrations, chain counter, meltdown, DOUBLE!, YOU WIN!</summary>
        public static TMP_FontAsset GetDisplayTMP()
        {
            if (!_loadedDisplayTMP)
            {
                _displayTMP = LoadWordSlop();
                if (_displayTMP == null && ALL_NUNITO) _displayTMP = GetTMP();
                if (_displayTMP == null) _displayTMP = Resources.Load<TMP_FontAsset>("Cartoon SDF");
                if (_displayTMP == null) _displayTMP = GetUITMP();
                _loadedDisplayTMP = true;
//                 WDLog.Log($"[GameFont] Display font loaded: {(_displayTMP != null ? _displayTMP.name : "NULL")}");
            }
            return _displayTMP;
        }

        // ── Legacy ──────────────────────────────────────────────────────────────

        public static Font Get()
        {
            if (!_loadedTile)
            {
                _tileFont = Resources.Load<Font>("AvenirNext");
                if (_tileFont == null) _tileFont = Resources.Load<Font>("NunitoSans");
                _loadedTile = true;
            }
            return _tileFont;
        }

        public static Font GetUI()
        {
            if (!_loadedUI)
            {
                _uiFont = ALL_NUNITO ? Resources.Load<Font>("Nunito-Black") : null;   // Resources/Nunito-Black.ttf (copy of Assets/Fonts)
                if (_uiFont == null) _uiFont = Resources.Load<Font>("Cartoon"); // 2026-06-15 Spencer: Cartoon for all UI
                if (_uiFont == null) _uiFont = Resources.Load<Font>("NunitoSans");
                if (_uiFont == null) _uiFont = Get();
                _loadedUI = true;
            }
            return _uiFont;
        }

        public static Font GetDisplay() => GetUI();
        public static Font GetBody() => GetUI();

        public static void Apply(TextMesh tm)
        {
            if (tm == null) return;
            Font f = Get();
            if (f != null) { tm.font = f; var mr = tm.GetComponent<MeshRenderer>(); if (mr != null && f.material != null) mr.sharedMaterial = f.material; }
        }

        public static void ApplyUI(TextMesh tm)
        {
            if (tm == null) return;
            Font f = GetUI();
            if (f != null) { tm.font = f; var mr = tm.GetComponent<MeshRenderer>(); if (mr != null && f.material != null) mr.sharedMaterial = f.material; }
        }

        public static void ApplyDisplay(TextMesh tm) => ApplyUI(tm);
        public static void ApplyBody(TextMesh tm) => ApplyUI(tm);
    }
}
