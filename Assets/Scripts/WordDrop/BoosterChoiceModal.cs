using System;
using System.Collections;
using System.Collections.Generic;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace WordDrop
{
    /// <summary>
    /// MVP P5 — Stage 1 booster choice modal. Pops after the player clears
    /// Stage 1; presents 3 random booster cards from the pool. Player picks one,
    /// it gets granted to BoosterManager for the rest of the run.
    ///
    /// Cards drawn via SurvivalRng so Daily Survival players today all see the
    /// same 3 options (P3.5 determinism). Player's PICK is their strategy.
    ///
    /// Canvas sortingOrder = 160 (above StageClearModal at 150, below TopOutPanel
    /// at 165 and ContinueModal at 170). Pops AFTER StageClearModal dismisses
    /// so the stage celebration plays out first.
    /// </summary>
    public class BoosterChoiceModal : MonoBehaviour
    {
        public static BoosterChoiceModal Instance { get; private set; }

        private const int CANVAS_SORT_ORDER = 160;
        private const int CARD_COUNT = 3;

        private Canvas _canvas;
        private GameObject _panel;
        private TextMeshProUGUI _titleText;
        private GameObject[] _cardSlots = new GameObject[CARD_COUNT];
        private TextMeshProUGUI[] _cardLabels = new TextMeshProUGUI[CARD_COUNT];
        private TextMeshProUGUI[] _cardDescs = new TextMeshProUGUI[CARD_COUNT];
        private Booster[] _offered = new Booster[CARD_COUNT];

        // 2026-05-28: Roguelite pick-1-of-3 layer stripped per Path A pivot
        // (project_wordrop_level_pivot_2026_05_28.md). Modal infrastructure kept
        // intact in case v1.5 "Endless" mode wants to re-enable it. To re-enable,
        // flip this flag to true.
        public static bool RoguelitePickingEnabled = false;

        private bool _subscribed;
        private bool _isShown;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            BuildUI();
            if (_canvas != null) _canvas.gameObject.SetActive(false);
        }

        private void OnEnable()  { TrySubscribe(); }
        private void OnDisable() { Unsubscribe(); }
        private void Start()     { TrySubscribe(); }

        private void TrySubscribe()
        {
            if (_subscribed) return;
            var sm = SurvivalManager.Instance;
            if (sm == null) return;
            sm.OnStageCleared -= HandleStageCleared;
            sm.OnStageCleared += HandleStageCleared;
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            var sm = SurvivalManager.Instance;
            if (sm != null) sm.OnStageCleared -= HandleStageCleared;
            _subscribed = false;
        }

        private int _lastClearedStage;

        private void HandleStageCleared(SurvivalManager.StageClearContext ctx)
        {
            // 2026-05-28: roguelite picking disabled (Path A). Bail early before
            // any choice logic runs. Boosters are now always-available inventory
            // managed by BoosterManager — no per-stage drafting.
            if (!RoguelitePickingEnabled) return;

            // MVP P5: picks fire at Stage 1 + every 5 stages (5, 10, 15...).
            if (!SurvivalManager.IsChoiceStage(ctx.ClearedStage)) return;
            // Max-tier ride-it: at launch, LAUNCH_MAX_TIER = 2. No more picks once
            // committed to the max tier. Post-launch the cap moves to 3.
            var bm = BoosterManager.Instance;
            if (bm != null && bm.ActiveBooster != null && bm.ActiveBooster.Level >= LAUNCH_MAX_TIER) return;
            _lastClearedStage = ctx.ClearedStage;
            ShowDeferred();
        }

        /// <summary>Modal fires AFTER StageClearModal dismisses. Wait until next
        /// frame so the stage-clear celebration plays first.</summary>
        private void ShowDeferred()
        {
            StartCoroutine(WaitAndShow());
        }

        private IEnumerator WaitAndShow()
        {
            // Wait for the StageClearModal (or any overlay) to dismiss before showing
            // the choice modal — prevents modal-on-top-of-modal stacking.
            // IsOverlayPaused is set true by StageClearModal.Show and cleared on its
            // Continue button. We poll until it clears.
            yield return new WaitForSeconds(0.2f);   // initial settle so we don't catch a brief gap
            float safety = 0f;
            while (SurvivalManager.Instance != null
                   && SurvivalManager.Instance.IsOverlayPaused
                   && safety < 30f)
            {
                safety += Time.unscaledDeltaTime;
                yield return null;
            }
            yield return new WaitForSecondsRealtime(0.25f);  // gentle gap after stage-clear dismiss
            Show();
        }

        public void Show()
        {
            if (_canvas == null || _isShown) return;
            _isShown = true;

            DrawOfferings();
            SurvivalManager.Instance?.SetOverlayPaused(true);

            bool isBoss = SurvivalManager.IsBossStage(_lastClearedStage);
            if (_titleText != null)
                _titleText.text = isBoss ? "🎁  BOSS REWARD  🎁" : "PICK YOUR BLOOM";

            for (int i = 0; i < CARD_COUNT; i++)
            {
                var b = _offered[i];
                string tier = b.TierLabel;
                if (_cardLabels[i] != null)
                    _cardLabels[i].text = string.IsNullOrEmpty(tier) ? b.DisplayName : $"{b.DisplayName}  {tier}";
                if (_cardDescs[i] != null)
                    _cardDescs[i].text = b.ShortDescription;
                if (_cardSlots[i] != null)
                {
                    var img = _cardSlots[i].GetComponent<UnityEngine.UI.Image>();
                    if (img != null)
                    {
                        switch (b.Level)
                        {
                            case 2:  img.color = new Color(0.85f, 0.65f, 0.20f, 1f); break;  // Rare gold
                            case 3:  img.color = new Color(0.65f, 0.30f, 0.85f, 1f); break;  // Epic violet
                            default: img.color = new Color(0.30f, 0.55f, 0.85f, 1f); break;  // Common silver/blue
                        }
                    }
                }
            }

            try { AnalyticsManager.Log("booster_choice_offered",
                "stage", _lastClearedStage,
                "is_boss", isBoss ? 1 : 0,
                "card_0", $"{_offered[0].Id}_L{_offered[0].Level}",
                "card_1", $"{_offered[1].Id}_L{_offered[1].Level}",
                "card_2", $"{_offered[2].Id}_L{_offered[2].Level}"); }
            catch (Exception ex) { Debug.LogError($"[BoosterChoiceModal] Analytics threw: {ex.Message}"); }

            _canvas.gameObject.SetActive(true);
            GameAudio.Instance?.PlayUIClick();
            StartCoroutine(AnimateChestOpen(isBoss));
        }

        /// <summary>MVP P5 chest-opening placeholder. Boss stages get a fuller
        /// ritual: title punch + delay + staggered card pop-in. Stage 1 intro
        /// pick is faster (less ceremony).</summary>
        private IEnumerator AnimateChestOpen(bool isBoss)
        {
            // Kill any leftover tweens, reset card scales.
            for (int i = 0; i < CARD_COUNT; i++)
            {
                if (_cardSlots[i] != null)
                {
                    _cardSlots[i].transform.DOKill();
                    _cardSlots[i].transform.localScale = Vector3.zero;
                }
            }
            if (_titleText != null)
            {
                _titleText.transform.DOKill();
                _titleText.transform.localScale = Vector3.one;
            }

            // Title punch — bigger for boss.
            if (_titleText != null)
            {
                float punchAmount = isBoss ? 0.35f : 0.18f;
                float punchDur    = isBoss ? 0.55f : 0.35f;
                _titleText.transform.DOPunchScale(Vector3.one * punchAmount, punchDur, 1, 0.5f).SetUpdate(true);
            }

            // Pre-card delay (chest "opens").
            float startDelay = isBoss ? 0.45f : 0.10f;
            float stagger    = isBoss ? 0.20f : 0.10f;
            yield return new WaitForSecondsRealtime(startDelay);

            // Cards pop in with stagger + slight overshoot.
            for (int i = 0; i < CARD_COUNT; i++)
            {
                if (_cardSlots[i] != null)
                {
                    _cardSlots[i].transform
                        .DOScale(1f, 0.32f)
                        .SetEase(Ease.OutBack, 1.7f)
                        .SetUpdate(true);
                }
                yield return new WaitForSecondsRealtime(stagger);
            }
        }

        private static Booster MakeBooster(string id, int level)
        {
            switch (id)
            {
                case "bloomburst":     return new Bloomburst()         { Level = level };
                case "lantern_trace":  return new LanternTrace()       { Level = level };
                case "bramble_sweep":  return new BrambleSweep()       { Level = level };
                case "wispwhirl_row":  return new WispwhirlSingleRow() { Level = level };
                case "motepluck":      return new Motepluck()          { Level = level };
                case "starseed_charm": return new StarseedCharm()      { Level = level };
                case "glade_stillness":return new GladeStillness()     { Level = level };
                case "mooncall":       return new Mooncall()           { Level = level };
                default:               return new Bloomburst()         { Level = level };
            }
        }

        // MVP launch surfaces a curated subset of the full 8-booster code pool.
        // Motepluck (overlaps with Bloomburst) and Mooncall (complex primed-word
        // mechanic, harder for new players to grok) are kept in code but excluded
        // from launch draws. Add them back as content drops post-launch for free
        // retention. Tier cap also gated below to L2 for launch — L3 drops in v1.1.
        private static readonly string[] LAUNCH_BOOSTER_IDS =
        {
            "bloomburst", "lantern_trace", "bramble_sweep", "wispwhirl_row",
            "starseed_charm", "glade_stillness",
        };
        // Highest tier offered at launch. Bumped to 3 in v1.1 content drop.
        private const int LAUNCH_MAX_TIER = 2;
        // (kept for legacy refs if any — same list as LAUNCH_BOOSTER_IDS during curated launch)
        private static readonly string[] ALL_BOOSTER_IDS = LAUNCH_BOOSTER_IDS;

        private void DrawOfferings()
        {
            // MVP P5 commitment progression (Spencer's design + Codex's rescue rule):
            //
            //  - Stage 1 (no current booster): 3 random L1 boosters.
            //  - Stage 5+/L<3 (have a current booster):
            //      Slot 0 = UPGRADE card (current booster, next level)
            //      Slot 1 = L1 of another random booster
            //      Slot 2 = L1 of another random booster (different from slot 1)
            //  - SWITCH RESCUE: first time per run that the player switches
            //    (picks a non-upgrade card), the new booster starts at L2 instead
            //    of L1. Consumed after first switch.
            //  - L3 max: caller (HandleStageCleared) suppresses the modal entirely.

            var bm = BoosterManager.Instance;
            bool hasCurrent = bm != null && bm.ActiveBooster != null;
            int currentLevel = hasCurrent ? bm.ActiveBooster.Level : 0;
            string currentId = hasCurrent ? bm.ActiveBooster.Id : null;

            // Pool of non-current booster IDs to draw L1 alternatives from.
            var altIds = new List<string>();
            foreach (var id in ALL_BOOSTER_IDS)
                if (id != currentId) altIds.Add(id);

            // Shuffle altIds via SurvivalRng so daily players get the same options.
            for (int i = altIds.Count - 1; i > 0; i--)
            {
                int j = SurvivalRng.Range(0, i + 1);
                var tmp = altIds[i]; altIds[i] = altIds[j]; altIds[j] = tmp;
            }

            if (!hasCurrent)
            {
                // Stage 1 intro pick: 3 random L1 boosters from the curated launch pool.
                for (int i = 0; i < CARD_COUNT && i < altIds.Count; i++)
                    _offered[i] = MakeBooster(altIds[i], 1);
            }
            else
            {
                // Slot 0 = upgrade current to next level (Level + 1, capped at LAUNCH_MAX_TIER).
                int upgradeLevel = Mathf.Min(currentLevel + 1, LAUNCH_MAX_TIER);
                _offered[0] = MakeBooster(currentId, upgradeLevel);
                // Slots 1, 2 = random L1 alternatives.
                for (int i = 1; i < CARD_COUNT && (i - 1) < altIds.Count; i++)
                    _offered[i] = MakeBooster(altIds[i - 1], 1);
            }
        }

        private void Hide()
        {
            if (_canvas != null) _canvas.gameObject.SetActive(false);
            _isShown = false;
            SurvivalManager.Instance?.SetOverlayPaused(false);
        }

        private void OnCardClicked(int idx)
        {
            if (!_isShown || idx < 0 || idx >= CARD_COUNT) return;
            var picked = _offered[idx];
            if (picked == null) return;

            // Codex L2 rescue rule: the first SWITCH (non-upgrade pick) per run
            // promotes the new booster to L2 automatically. Subsequent switches
            // start at L1 normally. Detect a switch as "slot index != 0 OR no
            // current booster" → wait, more precise: switch = picked id differs
            // from current booster id.
            var bm = BoosterManager.Instance;
            bool hadCurrent = bm != null && bm.ActiveBooster != null;
            bool isSwitch = hadCurrent && bm.ActiveBooster.Id != picked.Id;

            if (isSwitch && !_switchRescueUsed && picked.Level == 1)
            {
                picked.Level = 2;
                _switchRescueUsed = true;
                Debug.Log($"[BoosterChoiceModal] Switch rescue applied — {picked.DisplayName} promoted L1 → L2");
            }

            bm?.GrantBooster(picked);

            try { AnalyticsManager.Log("booster_choice_picked",
                "booster", $"{picked.Id}_L{picked.Level}",
                "slot", idx,
                "switch", isSwitch ? 1 : 0,
                "rescue_consumed", (isSwitch && _switchRescueUsed && picked.Level == 2) ? 1 : 0); }
            catch (Exception ex) { Debug.LogError($"[BoosterChoiceModal] Analytics threw: {ex.Message}"); }

            GameAudio.Instance?.PlayUIClick();
            Hide();
        }

        private bool _switchRescueUsed;

        public void ResetForNewRun()
        {
            _switchRescueUsed = false;
            _isShown = false;
        }

        // ── UI construction ─────────────────────────────────────────────────────

        private void BuildUI()
        {
            var canvasGO = new GameObject("BoosterChoiceModalCanvas",
                typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGO.transform.SetParent(transform, false);
            _canvas = canvasGO.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = CANVAS_SORT_ORDER;

            var scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080, 1920);
            scaler.matchWidthOrHeight = 0.5f;

            // Dim backdrop
            var dimGO = new GameObject("Dim", typeof(RectTransform), typeof(Image));
            dimGO.transform.SetParent(canvasGO.transform, false);
            var dimRT = dimGO.GetComponent<RectTransform>();
            dimRT.anchorMin = Vector2.zero; dimRT.anchorMax = Vector2.one;
            dimRT.offsetMin = Vector2.zero; dimRT.offsetMax = Vector2.zero;
            dimGO.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.6f);

            // Panel
            var panelGO = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            panelGO.transform.SetParent(canvasGO.transform, false);
            _panel = panelGO;
            var panelRT = panelGO.GetComponent<RectTransform>();
            panelRT.anchorMin = new Vector2(0.5f, 0.5f);
            panelRT.anchorMax = new Vector2(0.5f, 0.5f);
            panelRT.pivot     = new Vector2(0.5f, 0.5f);
            panelRT.sizeDelta = new Vector2(540f, 720f);
            panelGO.GetComponent<Image>().color = new Color(0.12f, 0.08f, 0.22f, 0.98f);

            // Title
            var titleGO = new GameObject("Title", typeof(RectTransform));
            titleGO.transform.SetParent(panelRT, false);
            _titleText = titleGO.AddComponent<TextMeshProUGUI>();
            var displayFont = GameFont.GetDisplayTMP();
            if (displayFont != null) _titleText.font = displayFont;
            _titleText.text = "PICK YOUR BLOOM";
            _titleText.fontSize = 48;
            _titleText.alignment = TextAlignmentOptions.Center;
            _titleText.color = new Color(1f, 0.84f, 0.42f, 1f);
            var titleRT = titleGO.GetComponent<RectTransform>();
            titleRT.anchorMin = new Vector2(0.05f, 0.86f);
            titleRT.anchorMax = new Vector2(0.95f, 0.97f);
            titleRT.offsetMin = Vector2.zero; titleRT.offsetMax = Vector2.zero;

            // 3 cards stacked vertically
            float[] cardYTop = { 0.78f, 0.54f, 0.30f };
            float[] cardYBot = { 0.58f, 0.34f, 0.10f };

            var cardColors = new Color[]
            {
                new Color(0.30f, 0.55f, 0.85f, 1f), // common = silver/blue
                new Color(0.85f, 0.65f, 0.20f, 1f), // rare = gold
                new Color(0.65f, 0.30f, 0.85f, 1f), // epic = violet
            };

            for (int i = 0; i < CARD_COUNT; i++)
            {
                int captured = i;
                var card = new GameObject($"Card_{i}", typeof(RectTransform), typeof(Image), typeof(Button));
                card.transform.SetParent(panelRT, false);
                var cardRT = card.GetComponent<RectTransform>();
                cardRT.anchorMin = new Vector2(0.06f, cardYBot[i]);
                cardRT.anchorMax = new Vector2(0.94f, cardYTop[i]);
                cardRT.offsetMin = Vector2.zero; cardRT.offsetMax = Vector2.zero;
                card.GetComponent<Image>().color = cardColors[i];
                card.GetComponent<Button>().onClick.AddListener(() => OnCardClicked(captured));
                _cardSlots[i] = card;

                // Card title
                var titleSub = new GameObject("Name", typeof(RectTransform));
                titleSub.transform.SetParent(card.transform, false);
                var label = titleSub.AddComponent<TextMeshProUGUI>();
                if (displayFont != null) label.font = displayFont;
                label.text = "—";
                label.fontSize = 36;
                label.alignment = TextAlignmentOptions.Center;
                label.color = Color.white;
                var labelRT = titleSub.GetComponent<RectTransform>();
                labelRT.anchorMin = new Vector2(0.05f, 0.55f);
                labelRT.anchorMax = new Vector2(0.95f, 0.95f);
                labelRT.offsetMin = Vector2.zero; labelRT.offsetMax = Vector2.zero;
                _cardLabels[i] = label;

                // Card description
                var descSub = new GameObject("Desc", typeof(RectTransform));
                descSub.transform.SetParent(card.transform, false);
                var desc = descSub.AddComponent<TextMeshProUGUI>();
                if (displayFont != null) desc.font = displayFont;
                desc.text = "—";
                desc.fontSize = 22;
                desc.alignment = TextAlignmentOptions.Center;
                desc.color = new Color(1f, 1f, 1f, 0.85f);
                var descRT = descSub.GetComponent<RectTransform>();
                descRT.anchorMin = new Vector2(0.05f, 0.05f);
                descRT.anchorMax = new Vector2(0.95f, 0.55f);
                descRT.offsetMin = Vector2.zero; descRT.offsetMax = Vector2.zero;
                _cardDescs[i] = desc;
            }
        }
    }
}
