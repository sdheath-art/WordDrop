using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace WordDrop
{
    /// <summary>
    /// Modal shown when a player taps a level with 0 hearts.
    ///
    /// Candy-Crush-style life gate: player sees the countdown to the next heart
    /// and can either wait, spend <see cref="REFILL_COIN_COST"/> coins to refill
    /// to MAX, or watch a rewarded-ad stub (3s delay) to add a single heart.
    ///
    /// Canvas sortingOrder = 150 (same as the other level modals). Only one modal
    /// is visible at a time — LevelSelectScreen is the only path that invokes this
    /// and it hides itself before calling SetVisible(true).
    /// </summary>
    public class HeartWaitModal : MonoBehaviour
    {
        public static HeartWaitModal Instance { get; private set; }

        /// <summary>
        /// Where CLOSE should send the player after dismissing. Default is
        /// LevelSelect (pre-Phase-8 behavior). Daily-flow entry from the main
        /// menu must NOT land the player on LevelSelect — daily is a main-menu
        /// feature.
        /// </summary>
        public enum ReturnContext { LevelSelect, MainMenu, DailyFlow }

        private ReturnContext _returnContext = ReturnContext.LevelSelect;

        /// <summary>Cost of the "refill to full hearts" shortcut.</summary>
        public const int REFILL_COIN_COST = 50;

        /// <summary>Simulated rewarded-ad duration used for the "+1 heart" path.</summary>
        private const float REWARDED_AD_STUB_SECONDS = 3f;

        private Canvas _canvas;
        private GameObject _card;
        private Text _heartsText;
        private Text _countdownText;
        private Text _coinsText;
        private TextMeshProUGUI _refillLabel;
        private TextMeshProUGUI _adLabel;
        private bool _adBusy;
        private Coroutine _tickCoroutine;
        private Coroutine _adCoroutine;

        /// <summary>
        /// One-shot flag — set after a successful ad grant, cleared only when the
        /// regen anchor advances (i.e. a full 30-min interval has passed since the
        /// last grant). Prevents spamming AD·+1♥ to bypass the regen clock entirely.
        /// </summary>
        private const string KEY_LAST_AD_HEART_TICKS = "wd_hearts_last_ad_grant_ticks";

        private static readonly Color PANEL_BG = new Color(0.10f, 0.08f, 0.20f, 0.95f);
        private static readonly Color CARD_BG  = new Color(0.18f, 0.12f, 0.22f, 1f);
        private static readonly Color TITLE    = new Color(1.00f, 0.56f, 0.56f, 1f);

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;

            BuildUI();
            // Initial hide must bypass PopOut — canvas is active on creation so
            // animated SetVisible(false) flashes the modal on cold boot.
            if (_canvas != null) _canvas.gameObject.SetActive(false);
        }

        private void BuildUI()
        {
            GameObject canvasGO = new GameObject("HeartWaitCanvas");
            canvasGO.transform.SetParent(transform, false);
            _canvas = canvasGO.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 150;

            CanvasScaler scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(540f, 960f);
            scaler.matchWidthOrHeight = 0.5f;

            canvasGO.AddComponent<GraphicRaycaster>();

            GameObject overlay = new GameObject("Overlay");
            overlay.transform.SetParent(canvasGO.transform, false);
            RectTransform oRT = overlay.AddComponent<RectTransform>();
            oRT.anchorMin = Vector2.zero;
            oRT.anchorMax = Vector2.one;
            oRT.offsetMin = Vector2.zero;
            oRT.offsetMax = Vector2.zero;
            Image oImg = overlay.AddComponent<Image>();
            oImg.color = PANEL_BG;
            oImg.raycastTarget = true;

            _card = new GameObject("Card");
            _card.transform.SetParent(canvasGO.transform, false);
            RectTransform cRT = _card.AddComponent<RectTransform>();
            cRT.anchorMin = new Vector2(0.10f, 0.28f);
            cRT.anchorMax = new Vector2(0.90f, 0.72f);
            cRT.offsetMin = Vector2.zero;
            cRT.offsetMax = Vector2.zero;
            Image cImg = _card.AddComponent<Image>();
            cImg.color = CARD_BG;
            GameObject card = _card;

            var title = CreateLabel(card.transform, "Title",
                new Vector2(0.05f, 0.80f), new Vector2(0.95f, 0.95f),
                "OUT OF LIVES", 36, TITLE);
            title.fontStyle = FontStyle.Bold;

            _heartsText = CreateLabel(card.transform, "HeartsValue",
                new Vector2(0.05f, 0.62f), new Vector2(0.95f, 0.78f),
                "♥ 0 / 5", 52, new Color(1.00f, 0.42f, 0.52f, 1f));
            _heartsText.fontStyle = FontStyle.Bold;

            CreateLabel(card.transform, "CountdownLabel",
                new Vector2(0.05f, 0.52f), new Vector2(0.95f, 0.60f),
                "Next life in", 18, new Color(0.85f, 0.82f, 0.92f, 1f));

            _countdownText = CreateLabel(card.transform, "CountdownValue",
                new Vector2(0.05f, 0.40f), new Vector2(0.95f, 0.52f),
                "--:--", 40, Color.white);
            _countdownText.fontStyle = FontStyle.Bold;

            _coinsText = CreateLabel(card.transform, "CoinsLine",
                new Vector2(0.05f, 0.32f), new Vector2(0.95f, 0.38f),
                "", 16, UILayout.Gold);

            // Buttons row — Refill (coins) / Watch Ad / Close
            float btnY0 = 0.10f;
            float btnY1 = 0.24f;

            int childBefore = card.transform.childCount;
            MenuUI.CreateButton(card.transform, "BtnRefill",
                new Vector2(0.06f, btnY0), new Vector2(0.36f, btnY1),
                "REFILL", new Color(0.96f, 0.77f, 0.26f, 1f), Color.black, 20,
                OnRefillClicked);
            if (card.transform.childCount > childBefore)
                _refillLabel = card.transform.GetChild(card.transform.childCount - 1)
                    .GetComponentInChildren<TextMeshProUGUI>();

            childBefore = card.transform.childCount;
            MenuUI.CreateButton(card.transform, "BtnWatchAd",
                new Vector2(0.39f, btnY0), new Vector2(0.64f, btnY1),
                "WATCH AD", new Color(0.30f, 0.60f, 0.85f, 1f), Color.white, 18,
                OnWatchAdClicked);
            if (card.transform.childCount > childBefore)
                _adLabel = card.transform.GetChild(card.transform.childCount - 1)
                    .GetComponentInChildren<TextMeshProUGUI>();

            MenuUI.CreateButton(card.transform, "BtnClose",
                new Vector2(0.67f, btnY0), new Vector2(0.94f, btnY1),
                "CLOSE", new Color(0.35f, 0.35f, 0.45f, 1f), Color.white, 20,
                OnCloseClicked);
        }

        // ── Visibility ──────────────────────────────────────────────────────────

        /// <summary>
        /// Default-context entry. Preserves the pre-Phase-8 callers (LevelSelect)
        /// that didn't specify a return context.
        /// </summary>
        public void SetVisible(bool visible)
        {
            SetVisible(visible, _returnContext);
        }

        /// <summary>
        /// Context-aware entry. Caller specifies where CLOSE should return the
        /// player. Phase 8 added MainMenu/DailyFlow routes so the daily 0-heart
        /// detour returns to the main menu instead of Level Select.
        /// </summary>
        public void SetVisible(bool visible, ReturnContext returnTo)
        {
            SetVisible(visible, returnTo, null);
        }

        /// <summary>Animated show/hide with optional onHidden callback.</summary>
        public void SetVisible(bool visible, ReturnContext returnTo, System.Action onHidden)
        {
            if (visible) _returnContext = returnTo;
            if (_canvas == null) { onHidden?.Invoke(); return; }

            if (visible)
            {
                _canvas.gameObject.SetActive(true);
                var raycaster = _canvas.GetComponent<GraphicRaycaster>();
                if (raycaster != null) raycaster.enabled = true;
                _adBusy = false;
                RefreshStatus();
                if (_card != null) UIAnimations.PopIn(_card.transform);
                if (_tickCoroutine == null && gameObject.activeInHierarchy)
                    _tickCoroutine = StartCoroutine(TickCountdown());
                return;
            }

            if (_tickCoroutine != null) { StopCoroutine(_tickCoroutine); _tickCoroutine = null; }
            // Cancel any pending ad-stub so closing + reopening the modal can't
            // queue a second +1♥ grant from a still-running coroutine.
            if (_adCoroutine != null) { StopCoroutine(_adCoroutine); _adCoroutine = null; }
            _adBusy = false;

            if (!_canvas.gameObject.activeSelf) { onHidden?.Invoke(); return; }

            var rc = _canvas.GetComponent<GraphicRaycaster>();
            if (rc != null) rc.enabled = false;
            if (_card == null)
            {
                _canvas.gameObject.SetActive(false);
                onHidden?.Invoke();
                return;
            }
            UIAnimations.PopOut(_card.transform, onComplete: () =>
            {
                if (_canvas != null) _canvas.gameObject.SetActive(false);
                onHidden?.Invoke();
            });
        }

        private IEnumerator TickCountdown()
        {
            var wait = new WaitForSecondsRealtime(1f);
            while (_canvas != null && _canvas.gameObject.activeSelf)
            {
                RefreshStatus();
                yield return wait;
            }
            _tickCoroutine = null;
        }

        private void RefreshStatus()
        {
            int hearts = HeartsManager.Current;
            bool adAvailable = IsAdGrantAvailable();
            if (_heartsText != null) _heartsText.text = $"♥ {hearts} / {HeartsManager.MAX_HEARTS}";
            if (_countdownText != null)
            {
                if (hearts >= HeartsManager.MAX_HEARTS)
                    _countdownText.text = "FULL";
                else
                {
                    TimeSpan t = HeartsManager.TimeUntilNextHeart;
                    _countdownText.text = $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
                }
            }
            if (_coinsText != null)
            {
                if (!adAvailable && hearts < HeartsManager.MAX_HEARTS)
                    _coinsText.text = $"Coins: {CoinWallet.Balance}  ·  Ad used — wait for next heart";
                else
                    _coinsText.text = $"Coins: {CoinWallet.Balance}  ·  Refill costs {REFILL_COIN_COST}";
            }

            if (_refillLabel != null)
                _refillLabel.text = $"REFILL · {REFILL_COIN_COST}c";
            if (_adLabel != null)
            {
                if (_adBusy) _adLabel.text = "WATCHING…";
                else if (!adAvailable) _adLabel.text = "AD USED";
                else _adLabel.text = "AD · +1♥";
            }
        }

        /// <summary>
        /// Rewarded-ad reward is rate-limited to once per heart regen interval.
        /// Prevents spamming the stub to mint free hearts at 3s/each and bypassing
        /// the Candy-Crush-style life gate entirely. In a real build the SDK's own
        /// daily cap usually enforces this; we mirror the regen clock so the user
        /// can't just wait N×3s for N hearts.
        /// </summary>
        private bool IsAdGrantAvailable()
        {
            if (HeartsManager.Current >= HeartsManager.MAX_HEARTS) return false;
            string s = PlayerPrefs.GetString(KEY_LAST_AD_HEART_TICKS, "");
            if (string.IsNullOrEmpty(s) || !long.TryParse(s, out long ticks)) return true;
            DateTime last = new DateTime(ticks, DateTimeKind.Utc);
            TimeSpan elapsed = DateTime.UtcNow - last;
            return elapsed.TotalSeconds >= HeartsManager.REGEN_SECONDS_PER_HEART;
        }

        // ── Button actions ──────────────────────────────────────────────────────

        private void OnRefillClicked()
        {
            if (HeartsManager.Current >= HeartsManager.MAX_HEARTS) { RefreshStatus(); return; }
            if (!CoinWallet.Spend(REFILL_COIN_COST))
            {
                // Not enough coins — nudge the player toward the ad path.
                if (_coinsText != null)
                    _coinsText.text = $"Need {REFILL_COIN_COST} coins to refill. Watch an ad instead?";
                return;
            }
            AnalyticsManager.Log(LevelAnalyticsEvents.COINS_SPENT,
                "amount", REFILL_COIN_COST,
                "sink", "hearts_refill");
            HeartsManager.GrantFull();
            RefreshStatus();
        }

        private void OnWatchAdClicked()
        {
            if (_adBusy) return;
            if (_adCoroutine != null) return; // coroutine already queued; belt + suspenders
            if (HeartsManager.Current >= HeartsManager.MAX_HEARTS) { RefreshStatus(); return; }
            if (!IsAdGrantAvailable()) { RefreshStatus(); return; }
            _adCoroutine = StartCoroutine(WatchAdCoroutine());
        }

        private IEnumerator WatchAdCoroutine()
        {
            _adBusy = true;
            RefreshStatus();
            // TODO(Phase 11): swap this delay for a real rewarded-ad SDK callback.
            yield return new WaitForSecondsRealtime(REWARDED_AD_STUB_SECONDS);
            _adBusy = false;
            _adCoroutine = null;

            HeartsManager.GrantOne();

            // Stamp the grant moment so IsAdGrantAvailable gates subsequent taps
            // until a full regen interval has elapsed.
            PlayerPrefs.SetString(KEY_LAST_AD_HEART_TICKS, DateTime.UtcNow.Ticks.ToString());
            PlayerPrefs.Save();

            AnalyticsManager.Log(LevelAnalyticsEvents.BOOSTER_USED,
                "booster", "heart_plus_one",
                "source", "rewarded_ad");
            RefreshStatus();
        }

        private void OnCloseClicked()
        {
            var context = _returnContext;
            SetVisible(false, context, onHidden: () =>
            {
                switch (context)
                {
                    case ReturnContext.MainMenu:
                    case ReturnContext.DailyFlow:
                        // MainMenu + DailyFlow both land on MenuUI today. DailyFlow is
                        // carved out so Phase 10's dedicated daily screen (if added)
                        // can route differently without breaking MainMenu callers.
                        if (MenuUI.Instance != null) MenuUI.Instance.SetVisible(true);
                        break;
                    case ReturnContext.LevelSelect:
                    default:
                        if (LevelSelectScreen.Instance != null)
                            LevelSelectScreen.Instance.SetVisible(true);
                        break;
                }
            });
        }

        // ── UI plumbing ─────────────────────────────────────────────────────────

        private static Text CreateLabel(Transform parent, string name,
            Vector2 anchorMin, Vector2 anchorMax, string text, int fontSize, Color color)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            Text t = go.AddComponent<Text>();
            t.font = MenuUI.GetFont();
            t.text = text;
            t.fontSize = fontSize;
            t.color = color;
            t.alignment = TextAnchor.MiddleCenter;
            return t;
        }
    }
}
