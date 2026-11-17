using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using DG.Tweening;
using TMPro;

namespace WordDrop
{
    /// <summary>
    /// Floating bonus popup — pops in, holds, floats up, fades out.
    /// Used to communicate every source of points to the player:
    /// word scores, detonation re-scores, heat bonuses, final push, comeback.
    /// </summary>
    public class BonusPopup : MonoBehaviour
    {
        public static BonusPopup Instance { get; private set; }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        // ── Score Accumulator — merges overlapping scores into one popup ────

        private int _playerAccum = 0;
        private int _aiAccum = 0;
        private Vector3 _playerAccumPos;
        private Vector3 _aiAccumPos;
        private Coroutine _playerFlushCo;
        private Coroutine _aiFlushCo;
        private const float ACCUM_WINDOW = 0.15f; // merge scores within this window

        // ── WORD INSTEAD OF SCORE (2026-10-22 Spencer: "instead of having the numbers of the score fly up, can we try having the
        // word that was made instead? i just want to see what it looks like"). The pop-up flies the WORD(S) that went off, in
        // the same Royal-Match number style; the points still land on the HUD exactly as before. false = "+N". ──
        public static bool WORD_INSTEAD_OF_SCORE = true;
        public static int  WORD_POPUP_MAX_WORDS = 2;   // more than this in one burst -> "FIRST +N MORE"
        private readonly List<string> _playerAccumWords = new List<string>();
        private void AccumulateScore(int points, Vector3 worldPos, bool isPlayer, string word)
        {
            if (isPlayer && !string.IsNullOrEmpty(word))
                foreach (var w in word.Split(' ')) if (w.Length > 1 && !_playerAccumWords.Contains(w)) _playerAccumWords.Add(w);
            AccumulateScore(points, worldPos, isPlayer);
        }
        private string AccumWordsText()
        {
            if (_playerAccumWords.Count == 0) return null;
            string t = _playerAccumWords.Count <= WORD_POPUP_MAX_WORDS
                ? string.Join("  ", _playerAccumWords)
                : $"{_playerAccumWords[0]}  +{_playerAccumWords.Count - 1} MORE";
            _playerAccumWords.Clear();
            return t;
        }

        private void AccumulateScore(int points, Vector3 worldPos, bool isPlayer)
        {
            if (isPlayer)
            {
                _playerAccum += points;
                _playerAccumPos = (_playerAccum == points) ? worldPos : Vector3.Lerp(_playerAccumPos, worldPos, 0.5f);
                if (_playerFlushCo != null) StopCoroutine(_playerFlushCo);
                _playerFlushCo = StartCoroutine(FlushAccum(true));
            }
            else
            {
                _aiAccum += points;
                _aiAccumPos = (_aiAccum == points) ? worldPos : Vector3.Lerp(_aiAccumPos, worldPos, 0.5f);
                if (_aiFlushCo != null) StopCoroutine(_aiFlushCo);
                _aiFlushCo = StartCoroutine(FlushAccum(false));
            }
        }

        private IEnumerator FlushAccum(bool isPlayer)
        {
            yield return new WaitForSeconds(ACCUM_WINDOW);

            int total = isPlayer ? _playerAccum : _aiAccum;
            Vector3 pos = isPlayer ? _playerAccumPos : _aiAccumPos;

            if (isPlayer) { _playerAccum = 0; _playerFlushCo = null; }
            else { _aiAccum = 0; _aiFlushCo = null; }

            if (total <= 0) yield break;

            // Color and scale based on total points
            Color color;
            float fontSize;
            if (total >= 25)
            {
                color = new Color(1f, 0.3f, 0.65f, 1f); // hot pink/magenta — massive
                fontSize = 9f;
            }
            else if (total >= 16)
            {
                color = new Color(1f, 0.65f, 0.1f, 1f); // orange — big
                fontSize = 8f;
            }
            else if (total >= 8)
            {
                color = new Color(1f, 0.85f, 0.2f, 1f); // gold — solid
                fontSize = 7f;
            }
            else
            {
                color = Color.white; // standard
                fontSize = 6f;
            }

            string words = isPlayer ? AccumWordsText() : null;
            WDLog.Log($"[WordPopup] flush total={total} words='{words}' on={WORD_INSTEAD_OF_SCORE}");   // TEMP diag 2026-10-22
            if (WORD_INSTEAD_OF_SCORE && !string.IsNullOrEmpty(words))
                StartCoroutine(FlyScoreToHUD(words, color, pos, isPlayer, total, Mathf.Max(6f, fontSize - (words.Length > 8 ? 2f : words.Length > 5 ? 1f : 0f))));
            else
                StartCoroutine(FlyScoreToHUD($"+{total}", color, pos, isPlayer, total, fontSize));
        }

        // ── Public API ──────────────────────────────────────────────────────

        /// <summary>Show a score popup at a world position.</summary>
        public void Show(string text, Color color, Vector3 worldPos, float scale = 1f)
        {
            StartCoroutine(AnimatePopup(text, color, worldPos, scale));
        }

        /// <summary>Show a word score popup — accumulated and merged.</summary>
        // ── SCORE POPUPS OFF (2026-09-08 Spencer) ───────────────────────────────────
        // "we dont even display the score at this point so if its firing anything it
        // shouldn't be." Correct — the endless level's HUD shows an OBJECTIVE (a fake
        // LongWord goal of 999999) and a move count. There is no score readout anywhere,
        // so every popup was announcing a number the player can never see.
        //
        // Gated HERE rather than at the ten separate call sites, so there is one switch
        // and no chance of a stray path still firing. The swap-pivot direction IS
        // score-based endless, so when the score display is built this comes back on —
        // and at that point it is worth deciding whether the number belongs at the moment
        // a word is FORMED (where it fires now) or when it DETONATES (where the payoff is).
        public static bool WORD_SCORE_POPUPS = false;

        public void ShowWordScore(string word, int points, Vector3 worldPos, bool isPlayer = true)
        {
            if (HandManager.BonusBlastRunning) return;   // 2026-10-28 Spencer: no score pop-ups during the end-of-level bonus
            if (!WORD_SCORE_POPUPS) return;
            AccumulateScore(points, worldPos, isPlayer);
        }

        /// <summary>Show detonation re-score — accumulated.</summary>
        public void ShowDetonation(string word, int points, Vector3 worldPos, int chainDepth = 0, bool isPlayer = true)
        {
            if (HandManager.BonusBlastRunning) return;   // 2026-10-28 Spencer: no score pop-ups during the end-of-level bonus
            AccumulateScore(points, worldPos, isPlayer, word);
        }

        /// <summary>Show heat fuse bonus — accumulated.</summary>
        public void ShowHeatBonus(int points, Vector3 worldPos, bool isPlayer = true)
        {
            if (HandManager.BonusBlastRunning) return;   // 2026-10-28 Spencer: no score pop-ups during the end-of-level bonus
            if (points <= 0) return;
            AccumulateScore(points, worldPos, isPlayer);
        }

        /// <summary>Show final push bonus — flies directly (special label).</summary>
        public void ShowFinalPush(int points, Vector3 worldPos, bool isPlayer = true)
        {
            if (points <= 0) return;
            Color c = new Color(1f, 0.95f, 0.3f, 1f);
            StartCoroutine(FlyScoreToHUD($"FINAL +{points}", c, worldPos + Vector3.up * 0.5f, isPlayer, points));
        }

        /// <summary>Show comeback bonus — flies directly (special label).</summary>
        public void ShowComeback(int points, Vector3 worldPos, bool isPlayer = true)
        {
            if (points <= 0) return;
            Color c = new Color(0.3f, 0.9f, 0.4f, 1f);
            StartCoroutine(FlyScoreToHUD($"COMEBACK +{points}", c, worldPos + Vector3.up * 0.5f, isPlayer, points));
        }

        /// <summary>Show rewrite refund.</summary>
        public void ShowRefund(Vector3 worldPos)
        {
            GameAudio.Instance?.PlayChargeBack();
            var cfg = UIConfig.Instance;
            Color c = cfg != null ? cfg.popupRefundColor : new Color(0.3f, 0.85f, 0.9f, 1f);
            float s = cfg != null ? cfg.popupRefundScale : 0.9f;
            Show("CHARGE BACK!", c, worldPos + Vector3.up * 0.3f, s);
        }

        /// <summary>
        /// Phase 11d — "EDIT FREE!" celebration fired when a Survival edit builds
        /// a 5+ letter word. Gold/green, louder pop than ShowRefund. When capped
        /// (player already at max edits), shows "EDIT FREE (CAP)" at a smaller
        /// scale so the acknowledgment is there without implying a charge landed.
        /// </summary>
        public void ShowEditFree(Vector3 worldPos, bool capped = false)
        {
            GameAudio.Instance?.PlayChargeBack();
            Color c = capped
                ? new Color(0.75f, 0.85f, 0.55f, 1f)  // muted olive when capped
                : new Color(0.40f, 1.00f, 0.55f, 1f); // vivid green on real refund
            float s = capped ? 0.9f : 1.15f;
            string text = capped ? "EDIT FREE (CAP)" : "EDIT FREE!";
            Show(text, c, worldPos + Vector3.up * 0.4f, s);
        }

        // ── Flying Score Animation ──────────────────────────────────────────

        // 2026-05-15: temporarily disabled per Spencer's request.
        //
        // FlyingScoreEnabled: the small "+points" particles that fly from tile
        //   locations to the main score readout.
        // BonusTextEnabled: all word popups like "DOUBLE", "EDIT FREE!",
        //   "CHARGE BACK!", "CLEAR!", "COMEBACK", "FINAL", etc.
        //
        // Both default OFF. Flip to true to re-enable that category.
        public static bool FlyingScoreEnabled = false;
        public static bool BonusTextEnabled   = false;

        // ── ROYAL MATCH SCORE STYLE (2026-10-22 Spencer: "make a nunito black version of their text treatment for the score
        // pop ups"). Measured off his RM screenshot "+50": cream / butter-yellow fill (~#FFF5C0), thick dark navy-teal stroke
        // (~#0F4064, ~12-15% of cap height), a lighter rim inside the stroke. Here: Nunito Black, vertical cream->butter
        // gradient, navy outline, and a darker navy underlay offset DOWN as a chunky bottom lip. One shared material. ──
        public static bool RM_SCORE_STYLE = true;
        // 2026-10-22: OUR palette (Spencer's mockup "2469"), not Royal Match's navy/butter — see HUDManager RM_NUM_*.
        public static Color RM_SCORE_TOP     = new Color32(0xFF, 0xFD, 0xE3, 0xFF);   // warm cream
        public static Color RM_SCORE_BOTTOM  = new Color32(0xEC, 0xDF, 0xDE, 0xFF);   // lavender-cream
        public static Color RM_SCORE_OUTLINE = new Color32(0x22, 0x03, 0x4F, 0xFF);   // HUD ink purple
        public static Color RM_SCORE_LIP     = new Color32(0x22, 0x03, 0x4F, 0xFF);   // same ink, the thicker bottom edge
        private static Material s_rmScoreMat;
        public static bool ApplyRMScoreStylePublic(TextMeshPro tmp) => ApplyRMScoreStyle(tmp);

        // ── WORD POP STYLE (2026-10-23 Spencer: approximate the Royal Match "+10" — his crop + royalmatchfullplaythrough.mp4
        // @1:22 — in Nunito Black). Measured: fill pale cream #FBFAD0 at the top to butter #F7EFA5 at the bottom, a THIN dark
        // slate-navy stroke #27394B, a soft light halo outside it. SIZE (the part that looked "crazy"): RM's digits are ~25% of a
        // tile tall (24px on a ~96px cell), the "+10" ~2/3 of a tile wide — small, sitting on the cleared tile. WORD_POP_CAP_CELLS
        // is that final letter height in cells; SizeWordPop measures the laid-out text and fits it. ──
        public static Color WORD_POP_TOP     = new Color32(0xFB, 0xFA, 0xD0, 0xFF);
        public static Color WORD_POP_BOTTOM  = new Color32(0xF7, 0xEF, 0xA5, 0xFF);
        public static Color WORD_POP_STROKE  = new Color32(0x27, 0x39, 0x4B, 0xFF);
        public static Color WORD_POP_HALO    = new Color(1f, 1f, 0.94f, 0.55f);
        public static float WORD_POP_CAP_CELLS = 1.10f;   // 2026-10-28 Spencer (v3 lettering): "bigger" (0.80 -> 1.10)   // 2026-10-23 Spencer: "double its size" (0.40 still too small)   // 2026-10-23 Spencer: "too small" at 0.25 (RM's measured digit height). RM's +10 sits
                                                          // on ONE tile with no letters around it; ours sits on a 4-6 tile word among half-tile
                                                          // letters, so the same absolute size read tiny. 0.40 = ~80% of a tile letter.
        public static float WORD_POP_STROKE_W  = 0.16f;   // Nunito units (atlas padding 9 @ 90pt) — ~5-6% of letter height: thin, like RM
        private static Material s_wordPopMat;
        // V3 WORD POP (2026-10-28 Spencer: "add back letter pop ups but make them match the aesthetic"). The same lettering he
        // already approved in game: WordSlop Button (the HUD numbers + nav labels), cream gradient (#FFFAE0 -> #FCEEC4), a thin
        // dark-purple #301050 outline and a small straight-down drop in the same purple (the HUD heart count, scaled down twice
        // by Spencer: outline ~8% of the cap, drop ~2%). WORD_POP_V3 = false -> the 10-23 Nunito "RM +10" look.
        public static bool  WORD_POP_V3 = true;
        // 11-02 Spencer: SKY BLUE face (D of typography/tile_fonts_1101/word_pop_ivory.png) - the cream face (FFFAE0 -> FCEEC4) sank
        // into the board-C ivory tiles. Gold goal-word pops unchanged.
        public static Color WORD_POP_V3_TOP = new Color32(0xE6, 0xF7, 0xFF, 0xFF), WORD_POP_V3_BOT = new Color32(0x7F, 0xCB, 0xFF, 0xFF);
        public static Color WORD_POP_V3_INK = new Color32(0x30, 0x10, 0x50, 0xFF);
        public static float WORD_POP_V3_TRACKING = 9f;   // TMP em/100 units: + = looser
        private static Material s_wordPopV3Mat;
        private static bool ApplyWordPopStyleV3(TextMeshPro tmp)
        {
            var font = Resources.Load<TMP_FontAsset>("WordSlop Button SDF");
            if (tmp == null || font == null) return false;
            tmp.font = font;
            if (s_wordPopV3Mat == null)
            {
                s_wordPopV3Mat = new Material(font.material) { name = "WordPopV3" };
                s_wordPopV3Mat.EnableKeyword("OUTLINE_ON");
                s_wordPopV3Mat.SetColor("_OutlineColor", WORD_POP_V3_INK);
                // 10-31 TEXT RECIPE D (Spencer: "also make the change for the word pop ups"): the HUD count values - stroke ~5% of
                // cap, hard drop ~7% - instead of the 10-28 outline 0.22 / dilate 0.18 / drop -0.3. Plus a soft shadow copy
                // (AddWordPopSoftShadow) so the cream word lifts off the white tiles.
                s_wordPopV3Mat.SetFloat("_OutlineWidth", HUDManager.D_STROKE);
                s_wordPopV3Mat.SetFloat("_FaceDilate", HUDManager.D_STROKE * 0.5f);
                s_wordPopV3Mat.EnableKeyword("UNDERLAY_ON");
                s_wordPopV3Mat.SetColor("_UnderlayColor", WORD_POP_V3_INK);
                s_wordPopV3Mat.SetFloat("_UnderlayOffsetX", 0f);
                s_wordPopV3Mat.SetFloat("_UnderlayOffsetY", HUDManager.D_DROP);
                s_wordPopV3Mat.SetFloat("_UnderlayDilate", HUDManager.D_STROKE);
                s_wordPopV3Mat.SetFloat("_UnderlaySoftness", 0f);
                ShaderUtilities.UpdateShaderRatios(s_wordPopV3Mat);
            }
            tmp.fontSharedMaterial = s_wordPopV3Mat;
            tmp.enableVertexGradient = true;
            tmp.colorGradient = new VertexGradient(WORD_POP_V3_TOP, WORD_POP_V3_TOP, WORD_POP_V3_BOT, WORD_POP_V3_BOT);
            tmp.color = Color.white;
            tmp.fontStyle = FontStyles.Normal;
            tmp.characterSpacing = WORD_POP_V3_TRACKING;   // 2026-10-28 Spencer: "the kerning is so tight" - the face dilate + outline close the gaps
            tmp.UpdateMeshPadding();
            return true;
        }

        /// <summary>SOFT SHADOW behind a word pop (10-31, same as the home +coins pop): a child copy with an invisible face and a
        /// soft, grown, slightly dropped underlay in the plate dark. Returns it so the caller can fade it with the word.</summary>
        public static bool WORD_POP_SOFT_SHADOW = true;
        private static Material s_wordPopShadowMat;
        public static TextMeshPro AddWordPopSoftShadow(TextMeshPro main)
        {
            if (!WORD_POP_SOFT_SHADOW || main == null || s_wordPopV3Mat == null || (main.fontSharedMaterial != s_wordPopV3Mat && main.fontSharedMaterial != s_wordPopGoldMat)) return null;
            if (s_wordPopShadowMat == null)
            {
                s_wordPopShadowMat = new Material(s_wordPopV3Mat) { name = "WordPopSoftShadow" };
                s_wordPopShadowMat.DisableKeyword("OUTLINE_ON"); s_wordPopShadowMat.SetFloat("_OutlineWidth", 0f);
                s_wordPopShadowMat.SetColor("_FaceColor", new Color(1f, 1f, 1f, 0f));
                s_wordPopShadowMat.SetColor("_UnderlayColor", LevelMapPanel.COIN_POP_SHADOW);
                s_wordPopShadowMat.SetFloat("_UnderlayOffsetY", LevelMapPanel.COIN_POP_SHADOW_Y);
                s_wordPopShadowMat.SetFloat("_UnderlayDilate", LevelMapPanel.COIN_POP_SHADOW_DILATE);
                s_wordPopShadowMat.SetFloat("_UnderlaySoftness", LevelMapPanel.COIN_POP_SHADOW_SOFT);
                ShaderUtilities.UpdateShaderRatios(s_wordPopShadowMat);
            }
            var go = new GameObject("WordPopSoftShadow");
            go.transform.SetParent(main.transform, false);
            var sh = go.AddComponent<TextMeshPro>();
            sh.font = main.font; sh.fontSharedMaterial = s_wordPopShadowMat;
            sh.text = main.text; sh.fontSize = main.fontSize; sh.fontStyle = main.fontStyle; sh.characterSpacing = main.characterSpacing;
            sh.alignment = main.alignment; sh.enableWordWrapping = false; sh.overflowMode = main.overflowMode;
            sh.rectTransform.sizeDelta = main.rectTransform.sizeDelta;
            sh.enableVertexGradient = false; sh.color = Color.white; sh.alpha = main.alpha;
            sh.sortingOrder = main.sortingOrder - 1;
            sh.UpdateMeshPadding();
            return sh;
        }

        // GOAL WORD = GOLD + LONG WORD = BIGGER (10-31): HandManager sets these just before the pop is built; ClusterOutline reads
        // and clears them. Gold: gold face (#FFF2A0 -> #F5D147), orange rim (outline #C9600C), dark lip (underlay #6E2A05) - the TMP
        // approximation of the mocked tier-1 gold (composition/library_home/goal_word_pop.png).
        public static bool  GOAL_WORD_GOLD = true;
        public static bool  NextPopGold; public static int NextPopLength;
        public static float GOLD_POP_SCALE = 1.15f, LONG_POP_STEP = 0.10f;   // +10% size per letter past 4 (cap 3 steps)
        public static Color GOLD_TOP = new Color32(0xFF, 0xF2, 0xA0, 0xFF), GOLD_BOT = new Color32(0xF5, 0xD1, 0x47, 0xFF);
        public static Color GOLD_RIM = new Color32(0xC9, 0x60, 0x0C, 0xFF), GOLD_LIP = new Color32(0x6E, 0x2A, 0x05, 0xFF);
        private static Material s_wordPopGoldMat;
        public static void ApplyWordPopGold(TextMeshPro tmp)
        {
            if (tmp == null || s_wordPopV3Mat == null || tmp.fontSharedMaterial != s_wordPopV3Mat) return;
            if (s_wordPopGoldMat == null)
            {
                s_wordPopGoldMat = new Material(s_wordPopV3Mat) { name = "WordPopGold" };
                s_wordPopGoldMat.SetColor("_OutlineColor", GOLD_RIM);
                s_wordPopGoldMat.SetFloat("_OutlineWidth", HUDManager.D_STROKE * 1.4f);
                s_wordPopGoldMat.SetFloat("_FaceDilate", HUDManager.D_STROKE * 0.7f);
                s_wordPopGoldMat.SetColor("_UnderlayColor", GOLD_LIP);
                s_wordPopGoldMat.SetFloat("_UnderlayDilate", HUDManager.D_STROKE * 1.4f);
                ShaderUtilities.UpdateShaderRatios(s_wordPopGoldMat);
            }
            tmp.fontSharedMaterial = s_wordPopGoldMat;
            tmp.enableVertexGradient = true;
            tmp.colorGradient = new VertexGradient(GOLD_TOP, GOLD_TOP, GOLD_BOT, GOLD_BOT);
            tmp.UpdateMeshPadding();
        }
        /// <summary>Size multiplier for the next pop: gold words a touch bigger, long words bigger per letter.</summary>
        public static float NextPopScale()
        {
            float k = 1f + LONG_POP_STEP * Mathf.Clamp(NextPopLength - 4, 0, 3);
            if (NextPopGold) k *= GOLD_POP_SCALE;
            return k;
        }

        public static bool ApplyWordPopStyle(TextMeshPro tmp)
        {
            if (WORD_POP_V3 && ApplyWordPopStyleV3(tmp)) return true;
            var font = GameFont.GetTMP();   // NunitoBlack SDF
            if (tmp == null || font == null) return false;
            tmp.font = font;
            if (s_wordPopMat == null)
            {
                s_wordPopMat = new Material(font.material) { name = "WordPopNunito" };
                s_wordPopMat.EnableKeyword("OUTLINE_ON");   // TMP_SDF-Mobile: no keyword = no outline
                s_wordPopMat.SetFloat("_OutlineWidth", WORD_POP_STROKE_W);
                s_wordPopMat.SetColor("_OutlineColor", WORD_POP_STROKE);
                s_wordPopMat.SetFloat("_FaceDilate", 0.06f);
                s_wordPopMat.EnableKeyword("UNDERLAY_ON");    // the soft light halo around the stroke
                s_wordPopMat.SetColor("_UnderlayColor", WORD_POP_HALO);
                s_wordPopMat.SetFloat("_UnderlayOffsetX", 0f);
                s_wordPopMat.SetFloat("_UnderlayOffsetY", 0f);
                s_wordPopMat.SetFloat("_UnderlayDilate", 0.55f);
                s_wordPopMat.SetFloat("_UnderlaySoftness", 0.6f);
            }
            tmp.fontSharedMaterial = s_wordPopMat;
            tmp.enableVertexGradient = true;
            tmp.colorGradient = new VertexGradient(WORD_POP_TOP, WORD_POP_TOP, WORD_POP_BOTTOM, WORD_POP_BOTTOM);
            tmp.color = Color.white;
            tmp.fontStyle = FontStyles.Normal;
            tmp.characterSpacing = 0f;
            tmp.UpdateMeshPadding();
            return true;
        }
        /// <summary>Fit a word pop so its letters end up WORD_POP_CAP_CELLS of a tile tall AFTER the pop's own swell (grow).
        /// Measures the laid-out caps rather than trusting a point-size guess.</summary>
        public static void SizeWordPop(TextMeshPro tmp, float grow = 1f)
        {
            if (tmp == null) return;
            float cell = GridManager.Instance != null ? GridManager.Instance.CellSize : 0.95f;
            float target = WORD_POP_CAP_CELLS * cell / Mathf.Max(0.01f, grow);
            tmp.fontSize = 10f; tmp.ForceMeshUpdate();
            float h = tmp.textBounds.size.y;
            if (h > 0.0001f) tmp.fontSize = 10f * target / h;
            tmp.ForceMeshUpdate();
        }
        /// <summary>The score pop-up's size ladder (FlushAccum) — shared so the board's word pop matches it exactly.</summary>
        public static float ScorePopupFontSize(int total) => total >= 25 ? 9f : total >= 16 ? 8f : total >= 8 ? 7f : 6f;
        private static bool ApplyRMScoreStyle(TextMeshPro tmp)
        {
            var font = GameFont.GetNumberTMP();   // 2026-10-22: Montserrat Black for numbers (GameFont.NUMBERS_MONTSERRAT)
            if (font == null) return false;
            float k = GameFont.SpreadScaleVsNunito(font);   // the values below were tuned on Nunito
            tmp.font = font;
            if (s_rmScoreMat == null)
            {
                s_rmScoreMat = new Material(font.material) { name = "RMScorePopup" };
                s_rmScoreMat.EnableKeyword("OUTLINE_ON");   // TMP_SDF-Mobile: _OutlineWidth is ignored without it (2026-10-22 find)
                s_rmScoreMat.SetFloat("_OutlineWidth", Mathf.Min(0.75f, 0.28f * k));
                s_rmScoreMat.SetColor("_OutlineColor", RM_SCORE_OUTLINE);
                s_rmScoreMat.SetFloat("_FaceDilate", Mathf.Min(0.2f, 0.08f * k));   // pushes the face out so the thick stroke doesn't thin the numerals
                s_rmScoreMat.EnableKeyword("UNDERLAY_ON");
                s_rmScoreMat.SetColor("_UnderlayColor", RM_SCORE_LIP);
                s_rmScoreMat.SetFloat("_UnderlayOffsetX", 0f);
                s_rmScoreMat.SetFloat("_UnderlayOffsetY", Mathf.Max(-1f, -0.45f * k));
                s_rmScoreMat.SetFloat("_UnderlayDilate", Mathf.Min(1f, 0.36f * k));
                s_rmScoreMat.SetFloat("_UnderlaySoftness", 0.05f);
            }
            tmp.fontSharedMaterial = s_rmScoreMat;
            tmp.enableVertexGradient = true;
            tmp.colorGradient = new VertexGradient(RM_SCORE_TOP, RM_SCORE_TOP, RM_SCORE_BOTTOM, RM_SCORE_BOTTOM);
            tmp.color = Color.white;
            tmp.UpdateMeshPadding();
            return true;
        }

        private IEnumerator FlyScoreToHUD(string text, Color color, Vector3 startPos, bool isPlayer, int points = 0, float fontSize = 6f)
        {
            if (!FlyingScoreEnabled) yield break;

            GameAudio.Instance?.PlayBonusPopup();

            GameObject go = new GameObject("FlyingScore");
            go.transform.position = startPos;

            TextMeshPro tmp = go.AddComponent<TextMeshPro>();
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.fontStyle = FontStyles.Normal;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.sortingOrder = 100;
            tmp.rectTransform.sizeDelta = new Vector2(5f, 2f);
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Overflow;
            bool isWords = !string.IsNullOrEmpty(text) && !text.StartsWith("+") && !text.StartsWith("FINAL") && !text.StartsWith("COMEBACK");
            if (isWords && ApplyWordPopStyle(tmp)) { color = Color.white; SizeWordPop(tmp, 1.3f); }   // word pops: RM look + RM size (2026-10-23)
            else if (RM_SCORE_STYLE && ApplyRMScoreStyle(tmp)) color = Color.white;   // the gradient carries the colour; tint stays white
            else
            {
                TMPro.TMP_FontAsset font = GameFont.GetDisplayTMP();
                if (font != null) tmp.font = font;
                tmp.color = color;
                tmp.outlineWidth = 0.2f;
                tmp.outlineColor = new Color32(0, 0, 0, 200);
                TMPHelper.ApplyEffects(tmp, color);
            }

            Transform t = go.transform;

            // Pop in with overshoot
            t.localScale = Vector3.zero;
            t.DOScale(1.3f, 0.12f).SetEase(Ease.OutBack);
            yield return new WaitForSeconds(0.12f);
            t.DOScale(1f, 0.08f).SetEase(Ease.OutQuad);
            yield return new WaitForSeconds(0.15f); // brief hold so player reads it

            // Get target position (HUD score counter)
            // HUD is Screen Space Overlay — position is in screen pixels. Convert to world.
            Camera cam = Camera.main;
            Vector3 targetWorld;
            if (cam != null && HUDManager.Instance != null)
            {
                Vector3 screenPos = isPlayer
                    ? HUDManager.Instance.GetPlayerScoreWorldPos()
                    : HUDManager.Instance.GetAIScoreWorldPos();
                // Overlay canvas position IS screen position — convert directly
                float camDist = Mathf.Abs(cam.transform.position.z);
                targetWorld = cam.ScreenToWorldPoint(new Vector3(screenPos.x, screenPos.y, camDist));
            }
            else
            {
                targetWorld = startPos + Vector3.up * 3f;
            }

            // Fly to HUD with arc + shimmer trail
            float flyDur = 0.4f;
            float elapsed = 0f;
            Vector3 flyStart = t.position;
            float arcHeight = 1.5f;
            float trailTimer = 0f;

            while (elapsed < flyDur && t != null)
            {
                elapsed += Time.deltaTime;
                float progress = Mathf.Clamp01(elapsed / flyDur);
                float eased = 1f - (1f - progress) * (1f - progress); // ease out quad

                // Parabolic arc
                Vector3 pos = Vector3.Lerp(flyStart, targetWorld, eased);
                float arc = arcHeight * 4f * progress * (1f - progress);
                pos.y += arc;
                t.position = pos;

                // Shrink as it approaches
                float scale = Mathf.Lerp(1f, 0.4f, eased);
                t.localScale = Vector3.one * scale;

                // Fade slightly
                float alpha = Mathf.Lerp(1f, 0.6f, eased);
                tmp.color = new Color(color.r, color.g, color.b, alpha);

                // Gold shimmer trail — emit particles along the arc
                trailTimer += Time.deltaTime;
                if (trailTimer >= 0.03f)
                {
                    trailTimer = 0f;
                    GameParticles.Instance?.PlayShimmer(pos, 2);
                }

                yield return null;
            }

            // Arrived — punch the HUD counter, tick up score, play impact
            GameAudio.Instance?.PlayScoreImpact(points);
            if (isPlayer)
                HUDManager.Instance?.PunchPlayerScore(points);
            else
                HUDManager.Instance?.PunchAIScore(points);

            Destroy(go);
        }

        // ── Standard Popup Animation ────────────────────────────────────────

        private IEnumerator AnimatePopup(string text, Color color, Vector3 worldPos, float scale)
        {
            if (!BonusTextEnabled) yield break;

            GameAudio.Instance?.PlayBonusPopup();
            var cfg = UIConfig.Instance;

            // ── Create main text object ──
            GameObject go = new GameObject("BonusPopup");
            go.transform.position = worldPos;

            TextMeshPro tmp = go.AddComponent<TextMeshPro>();
            TMPro.TMP_FontAsset popupFont = (cfg != null && cfg.popupFont != null) ? cfg.popupFont : GameFont.GetDisplayTMP();
            if (popupFont != null) tmp.font = popupFont;

            tmp.text = text;
            float baseFontSize = cfg != null ? cfg.popupBaseFontSize : 7f;
            tmp.fontSize = baseFontSize * scale;
            tmp.fontStyle = FontStyles.Normal;
            tmp.color = Color.white; // Start white — will flash to final color
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.sortingOrder = 60;  // 11-06: 47 -> 60, ABOVE the board FX glows (<=57): the L1-20 recording showed the label unreadable for ~0.2 s under the power-up birth flash. 2026-10-21: 46->47, above the refill lid (44/45) // was 30; re-based above the row bands (tiles reach 37) 2026-10-20
            tmp.rectTransform.sizeDelta = new Vector2(14f, 2f);
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Overflow;

            tmp.outlineWidth = cfg != null ? cfg.popupOutlineWidth : 0.2f;
            Color32 outlineCol = cfg != null ? (Color32)cfg.popupOutlineColor : new Color32(0, 0, 0, 220);
            tmp.outlineColor = outlineCol;
            TMPHelper.ApplyEffects(tmp, color);

            Transform t = go.transform;

            // ── Ghost/echo effect — duplicate that scales up and fades out behind main text ──
            GameObject ghostGO = null;
            TextMeshPro ghostTmp = null;
            if (scale >= 1.0f) // only for bigger popups (announcements, not tiny scores)
            {
                ghostGO = new GameObject("PopupGhost");
                ghostGO.transform.position = worldPos;
                ghostTmp = ghostGO.AddComponent<TextMeshPro>();
                if (popupFont != null) ghostTmp.font = popupFont;
                ghostTmp.text = text;
                ghostTmp.fontSize = baseFontSize * scale;
                ghostTmp.fontStyle = FontStyles.Normal;
                ghostTmp.color = new Color(color.r, color.g, color.b, 0.4f);
                ghostTmp.alignment = TextAlignmentOptions.Center;
                ghostTmp.sortingOrder = 59; // 11-06: 46 -> 59 (stays just behind the main text). behind main text (45 tied the refill lid)  // was 29; re-based above the row bands (tiles reach 37) 2026-10-20
                ghostTmp.rectTransform.sizeDelta = new Vector2(14f, 2f);
                ghostTmp.enableWordWrapping = false;
                ghostTmp.overflowMode = TextOverflowModes.Overflow;
                ghostTmp.outlineWidth = 0f;

                // Ghost scales up to 180% and fades out over 0.35s
                ghostGO.transform.localScale = Vector3.one * scale;
                ghostGO.transform.DOScale(Vector3.one * scale * 1.8f, 0.35f)
                    .SetEase(Ease.OutQuad);
                DOTween.To(() => ghostTmp.color, c => ghostTmp.color = c,
                    new Color(color.r, color.g, color.b, 0f), 0.35f)
                    .SetEase(Ease.OutQuad);
            }

            // ── Phase 1: Pop in with white flash ──
            float popInDur = cfg != null ? cfg.popupPopInDuration : 0.12f;
            t.localScale = Vector3.zero;
            t.DOScale(Vector3.one * scale, popInDur).SetEase(Ease.OutBack);
            yield return new WaitForSeconds(popInDur);

            // ── Color flash: white → final color over 0.1s ──
            float flashDur = 0.1f;
            float flashElapsed = 0f;
            while (flashElapsed < flashDur)
            {
                flashElapsed += Time.deltaTime;
                float fp = Mathf.Clamp01(flashElapsed / flashDur);
                tmp.color = Color.Lerp(Color.white, color, fp);
                yield return null;
            }
            tmp.color = color;

            // ── Phase 2: Hold + gentle float up ──
            // Bigger popups (stage-up, announcements) hold longer so player can read them
            float holdDur = cfg != null ? cfg.popupHoldDuration : 0.6f;
            if (scale >= 1.3f) holdDur = Mathf.Max(holdDur, 1.5f);
            float elapsed = 0f;
            Vector3 startPos = t.position;
            while (elapsed < holdDur)
            {
                elapsed += Time.deltaTime;
                float progress = elapsed / holdDur;
                float floatDist = cfg != null ? cfg.popupFloatDistance : 0.4f;
                t.position = startPos + Vector3.up * (progress * floatDist);
                yield return null;
            }

            // ── Phase 3: Fade out while continuing to float ──
            float fadeDur = cfg != null ? cfg.popupFadeDuration : 0.25f;
            elapsed = 0f;
            Color startColor = tmp.color;
            Color32 startOutline = tmp.outlineColor;
            while (elapsed < fadeDur)
            {
                elapsed += Time.deltaTime;
                float progress = elapsed / fadeDur;
                float floatSpd = cfg != null ? cfg.popupFloatSpeed : 0.8f;
                t.position += Vector3.up * Time.deltaTime * floatSpd;

                float alpha = 1f - progress;
                tmp.color = new Color(startColor.r, startColor.g, startColor.b, alpha);
                tmp.outlineColor = new Color32(startOutline.r, startOutline.g, startOutline.b,
                    (byte)(startOutline.a * alpha));

                yield return null;
            }

            Destroy(go);
            if (ghostGO != null) Destroy(ghostGO);
        }
    }
}
