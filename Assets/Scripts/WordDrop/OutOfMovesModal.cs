using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace WordDrop
{
    /// <summary>
    /// Modal shown when LevelController fires OnLevelFail (moves exhausted, target missed).
    ///
    /// Displays: "Out of Moves" banner, score vs target, shortfall, hearts line,
    /// coin balance, and +5 MOVES / RETRY / MENU buttons. +5 MOVES spends
    /// BOOSTER_COIN_COST coins when the wallet can cover it, otherwise falls back
    /// to a rewarded-ad stub (REWARDED_AD_STUB_SECONDS sleep). On either path it
    /// calls LevelController.GrantExtraMoves(5) to resume the same attempt.
    ///
    /// Canvas sortingOrder = 150 (same as LevelCompletedModal). Only one modal can
    /// show at a time — LevelController's single-fire guards ensure this.
    /// </summary>
    public class OutOfMovesModal : MonoBehaviour
    {
        public static OutOfMovesModal Instance { get; private set; }

        private Canvas _canvas;
        private GameObject _card;
        private Text _scoreValueText;
        private Text _targetValueText;
        private Text _shortfallText;
        private Text _heartsText;
        private Text _coinBalanceText;
        private GameObject _btnBooster;
        private TextMeshProUGUI _btnBoosterLabel;
        private LevelData _levelData;
        private bool _boosterBusy;
        private Coroutine _adCoroutine;

        /// <summary>
        /// Captured at fail-modal show time. The booster coroutine verifies this
        /// against the current LevelController.RunToken before granting, so a
        /// MENU/RETRY tap during the 3s rewarded-ad stub can't leak moves into a
        /// different attempt.
        /// </summary>
        private int _pendingRunToken;

        /// <summary>Whether the current fail is eligible for a +5-moves recovery.</summary>
        private bool _boosterAvailable;

        /// <summary>+5 Moves price when paying with coins.</summary>
        public const int BOOSTER_COIN_COST = 10;

        /// <summary>Simulated rewarded-ad duration used when coins are insufficient.</summary>
        private const float REWARDED_AD_STUB_SECONDS = 3f;

        private static readonly Color PANEL_BG = new Color(0.10f, 0.08f, 0.20f, 0.95f);
        private static readonly Color CARD_BG  = new Color(0.20f, 0.14f, 0.18f, 1f);
        private static readonly Color TITLE    = new Color(1.00f, 0.56f, 0.56f, 1f);

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;

            BuildUI();
            SetVisible(false);

            if (LevelController.Instance != null)
                LevelController.Instance.OnLevelFail += HandleLevelFail;
        }

        private void OnDestroy()
        {
            if (LevelController.Instance != null)
                LevelController.Instance.OnLevelFail -= HandleLevelFail;
        }

        private void BuildUI()
        {
            GameObject canvasGO = new GameObject("OutOfMovesCanvas");
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
            cRT.anchorMin = new Vector2(0.08f, 0.24f);
            cRT.anchorMax = new Vector2(0.92f, 0.76f);
            cRT.offsetMin = Vector2.zero;
            cRT.offsetMax = Vector2.zero;
            Image cImg = _card.AddComponent<Image>();
            cImg.color = CARD_BG;
            GameObject card = _card;

            var title = CreateLabel(card.transform, "Title",
                new Vector2(0.05f, 0.80f), new Vector2(0.95f, 0.95f),
                "OUT OF MOVES", 40, TITLE);
            title.fontStyle = FontStyle.Bold;

            CreateLabel(card.transform, "SubHint",
                new Vector2(0.05f, 0.68f), new Vector2(0.95f, 0.78f),
                "So close — don't give up.", 20, new Color(0.90f, 0.85f, 0.80f, 1f));

            CreateLabel(card.transform, "ScoreLabel",
                new Vector2(0.08f, 0.56f), new Vector2(0.48f, 0.64f),
                "Score", 18, new Color(0.75f, 0.72f, 0.82f, 1f));
            CreateLabel(card.transform, "TargetLabel",
                new Vector2(0.52f, 0.56f), new Vector2(0.92f, 0.64f),
                "Target", 18, new Color(0.75f, 0.72f, 0.82f, 1f));

            _scoreValueText = CreateLabel(card.transform, "ScoreValue",
                new Vector2(0.08f, 0.46f), new Vector2(0.48f, 0.58f),
                "0", 36, Color.white);
            _scoreValueText.fontStyle = FontStyle.Bold;

            var targetValue = CreateLabel(card.transform, "TargetValue",
                new Vector2(0.52f, 0.46f), new Vector2(0.92f, 0.58f),
                "0", 36, new Color(1f, 0.84f, 0.25f, 1f));
            targetValue.fontStyle = FontStyle.Bold;

            _shortfallText = CreateLabel(card.transform, "Shortfall",
                new Vector2(0.08f, 0.36f), new Vector2(0.92f, 0.44f),
                "", 18, new Color(1f, 0.70f, 0.70f, 1f));

            _heartsText = CreateLabel(card.transform, "Hearts",
                new Vector2(0.08f, 0.30f), new Vector2(0.92f, 0.36f),
                "", 16, new Color(0.90f, 0.85f, 0.80f, 1f));

            _coinBalanceText = CreateLabel(card.transform, "CoinBalance",
                new Vector2(0.08f, 0.24f), new Vector2(0.92f, 0.30f),
                "", 16, UILayout.Gold);

            // Buttons
            float btnY0 = 0.08f;
            float btnY1 = 0.22f;
            MenuUI.CreateButton(card.transform, "BtnMenu",
                new Vector2(0.06f, btnY0), new Vector2(0.32f, btnY1),
                "MENU", new Color(0.35f, 0.35f, 0.45f, 1f), Color.white, 22,
                OnMenuClicked);

            int childBefore = card.transform.childCount;
            MenuUI.CreateButton(card.transform, "BtnBooster",
                new Vector2(0.37f, btnY0), new Vector2(0.63f, btnY1),
                "+5 MOVES", new Color(0.55f, 0.45f, 0.80f, 1f), Color.white, 20,
                OnBoosterClicked);
            if (card.transform.childCount > childBefore)
            {
                _btnBooster = card.transform.GetChild(card.transform.childCount - 1).gameObject;
                _btnBoosterLabel = _btnBooster.GetComponentInChildren<TextMeshProUGUI>();
            }

            MenuUI.CreateButton(card.transform, "BtnRetry",
                new Vector2(0.68f, btnY0), new Vector2(0.94f, btnY1),
                "RETRY", new Color(0.85f, 0.45f, 0.35f, 1f), Color.white, 22,
                OnRetryClicked);

            _targetValueText = targetValue;
        }

        // ── Event handling ──────────────────────────────────────────────────────

        private void HandleLevelFail(int score, int shortfall)
        {
            _levelData = LevelController.Instance?.CurrentLevel;
            if (_levelData != null)
                LevelProgressManager.IncrementAttempts(_levelData.levelId);

            _boosterBusy = false;
            _pendingRunToken = LevelController.Instance != null
                ? LevelController.Instance.RunToken : 0;
            // Non-recoverable fails (board-full, rising-row overflow) — adding
            // moves alone wouldn't unstick the grid, so hide the booster entirely
            // and tell the player to just RETRY.
            _boosterAvailable = LevelController.Instance != null
                && LevelController.Instance.LastFailIsRecoverable;

            _scoreValueText.text = score.ToString();
            if (_targetValueText != null && _levelData != null)
                _targetValueText.text = _levelData.target.ToString();
            _shortfallText.text = _boosterAvailable
                ? $"You needed {shortfall} more to clear the target."
                : "Board is jammed — tap RETRY to start fresh.";
            _heartsText.text = $"Hearts: {HeartsManager.Current}/{HeartsManager.MAX_HEARTS} — Retry spends 1.";
            if (_btnBooster != null) _btnBooster.SetActive(_boosterAvailable);
            RefreshBoosterLabel();

            SetVisible(true);
        }

        /// <summary>
        /// Keeps the booster button label + helper line in sync with the coin balance.
        /// The button label stays short ("+5 MOVES" / "WATCHING AD…") so it fits the
        /// cramped three-button row; the cost path ("Spends 10 coins" vs. "Watch an ad")
        /// is spelled out in the gold helper line below.
        /// </summary>
        private void RefreshBoosterLabel()
        {
            int balance = CoinWallet.Balance;
            if (_btnBoosterLabel != null)
                _btnBoosterLabel.text = _boosterBusy ? "WATCHING AD…" : "+5 MOVES";
            if (_coinBalanceText != null)
            {
                if (!_boosterAvailable)
                    _coinBalanceText.text = $"Coins: {balance}";
                else if (_boosterBusy)
                    _coinBalanceText.text = "…rewarding +5 moves…";
                else if (balance >= BOOSTER_COIN_COST)
                    _coinBalanceText.text = $"Coins: {balance}  →  spends {BOOSTER_COIN_COST}";
                else
                    _coinBalanceText.text = $"Coins: {balance}  →  watch a short ad";
            }
        }

        // ── Button actions ──────────────────────────────────────────────────────

        private void OnRetryClicked()
        {
            if (_levelData == null) { OnMenuClicked(); return; }

            if (!HeartsManager.Consume())
            {
                _heartsText.text = "No hearts left! Wait for regen or tap MENU.";
                return;
            }

            // Daily retry stays in daily mode so the next completion still fires
            // DailyDropManager.MarkPlayedToday. Only flip the flag off for non-daily
            // retries. Capture before dismiss so the 0.15s-later onHidden callback
            // sees the correct value.
            bool wasDaily = DailyDropManager.IsDailyMode;
            var data = _levelData;
            SetVisible(false, onHidden: () => DoRetry(data, wasDaily));
        }

        private void DoRetry(LevelData data, bool wasDaily)
        {
            SurvivalManager.IsSurvivalMode = false;
            BlitzManager.IsBlitzMode = false;
            DailyDropManager.IsDailyMode = wasDaily;
            GameManager.CurrentMode = GameMode.Level;

            LevelController.Instance.StartLevel(data);
            AnalyticsManager.Log(LevelAnalyticsEvents.LEVEL_RETRY,
                "level_id", data.levelId);

            // TransitionTo(Playing) handles Playing→Playing as a restart and
            // re-enables HandManager input inside OnStateEntered.
            if (GameManager.Instance != null)
                GameManager.Instance.TransitionTo(GameState.Playing);
        }

        private void OnBoosterClicked()
        {
            if (_boosterBusy) return;
            if (!_boosterAvailable) return;
            if (_levelData == null) return;

            // Coin path first — if the wallet covers it, spend and grant immediately.
            if (CoinWallet.Spend(BOOSTER_COIN_COST))
            {
                if (!TryGrantBoosterMoves(source: "coins"))
                {
                    // Refund if the run is no longer valid (shouldn't happen on the
                    // synchronous path, but preserves invariants).
                    CoinWallet.Add(BOOSTER_COIN_COST);
                    return;
                }
                AnalyticsManager.Log(LevelAnalyticsEvents.COINS_SPENT,
                    "level_id", _levelData.levelId,
                    "amount", BOOSTER_COIN_COST,
                    "sink", "booster_extra_moves");
                return;
            }

            // Rewarded-ad fallback — 3s stub stands in for a real SDK integration.
            // TODO(Phase 11): replace with AdMob/Unity Ads rewarded-ad SDK.
            _adCoroutine = StartCoroutine(RewardedAdStubCoroutine(_pendingRunToken));
        }

        private IEnumerator RewardedAdStubCoroutine(int runToken)
        {
            _boosterBusy = true;
            RefreshBoosterLabel();
            yield return new WaitForSecondsRealtime(REWARDED_AD_STUB_SECONDS);
            _boosterBusy = false;
            _adCoroutine = null;

            // Snapshot token check — SetVisible(false) cancels the coroutine, but
            // guard here too in case anyone else stops and restarts the modal
            // while the stub is sleeping.
            if (LevelController.Instance == null || LevelController.Instance.RunToken != runToken)
            {
                Debug.Log("[OutOfMovesModal] Ad-stub callback ignored — run changed during wait.");
                yield break;
            }
            TryGrantBoosterMoves(source: "rewarded_ad");
        }

        /// <summary>
        /// Routes the grant through LevelController with the captured run-token.
        /// Returns false if LevelController rejects (stale token, non-recoverable
        /// fail, already completed). Callers must back out any payment on false.
        /// </summary>
        private bool TryGrantBoosterMoves(string source)
        {
            if (_levelData == null) return false;
            if (LevelController.Instance == null) return false;
            bool granted = LevelController.Instance.GrantExtraMoves(5, _pendingRunToken);
            if (!granted) return false;

            AnalyticsManager.Log(LevelAnalyticsEvents.BOOSTER_USED,
                "level_id", _levelData.levelId,
                "booster", "extra_moves_5",
                "source", source);

            SetVisible(false);
            return true;
        }

        private void OnMenuClicked()
        {
            // Capture daily state now — onHidden runs 0.15s later and
            // IsDailyMode will have been cleared by then.
            bool daily = DailyDropManager.IsDailyMode;
            SetVisible(false, onHidden: () => NavigateToMenu(daily));
        }

        private void NavigateToMenu(bool wasDaily)
        {
            if (LevelController.Instance != null)
                LevelController.Instance.AbortLevel();

            // Daily attempts return to the main menu, not Level Select — daily is
            // a main-menu feature and the player can't pick another level from
            // inside a daily attempt. Standard level attempts return to Level Select.
            DailyDropManager.IsDailyMode = false;
            GameManager.CurrentMode = GameMode.Survival;
            if (GameManager.Instance != null)
                GameManager.Instance.TransitionTo(GameState.Menu);
            if (!wasDaily && LevelSelectScreen.Instance != null)
                LevelSelectScreen.Instance.SetVisible(true);
        }

        // ── UI plumbing ─────────────────────────────────────────────────────────

        public void SetVisible(bool visible)
        {
            SetVisible(visible, null);
        }

        /// <summary>
        /// Animated show/hide. Hide path: disables input, runs PopOut, then
        /// SetActive(false) + fires <paramref name="onHidden"/>. Callers pass
        /// navigation there so the next transition happens AFTER the card has
        /// visibly left.
        /// </summary>
        public void SetVisible(bool visible, Action onHidden)
        {
            if (_canvas == null) { onHidden?.Invoke(); return; }
            if (visible)
            {
                _canvas.gameObject.SetActive(true);
                var raycaster = _canvas.GetComponent<GraphicRaycaster>();
                if (raycaster != null) raycaster.enabled = true;
                if (_card != null) UIAnimations.PopIn(_card.transform);
                return;
            }

            // Hiding invalidates any in-flight ad-stub. The modal's _pendingRunToken
            // snapshot is the belt; stopping the coroutine is the suspenders. Leaving
            // it running means a 3s-delayed grant could land on an unrelated retry.
            if (_adCoroutine != null) { StopCoroutine(_adCoroutine); _adCoroutine = null; }
            _boosterBusy = false;

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
