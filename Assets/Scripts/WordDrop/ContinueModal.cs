using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace WordDrop
{
    /// <summary>
    /// MVP P3 continue offer. Shown after a Survival top-out (and after the
    /// TopOutPanel announcement plays). Two actions:
    ///   - CONTINUE: spend CONTINUE_COIN_COST coins → clear top 3 rows + refill
    ///     resources → resume play (no heart consumed)
    ///   - GIVE UP: heart consumed → finalize game over
    ///
    /// Canvas sortingOrder = 170 (above TopOutPanel @ 165, below GameOverUI @ 10000).
    /// </summary>
    public class ContinueModal : MonoBehaviour
    {
        public static ContinueModal Instance { get; private set; }

        private const int CANVAS_SORT_ORDER = 170;

        private Canvas _canvas;
        private GameObject _panel;
        private TextMeshProUGUI _titleText;
        private TextMeshProUGUI _bodyText;
        private TextMeshProUGUI _continueLabel;
        private TextMeshProUGUI _adLabel;
        private Button _continueButton;
        private Button _adButton;
        private SurvivalManager _survival;
        private int _currentCost;
        private bool _adInFlight;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            BuildUI();
            if (_canvas != null) _canvas.gameObject.SetActive(false);
        }

        private void BuildUI()
        {
            var canvasGO = new GameObject("ContinueModalCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGO.transform.SetParent(transform, false);
            _canvas = canvasGO.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = CANVAS_SORT_ORDER;

            var scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080, 1920);
            scaler.matchWidthOrHeight = 0.5f;

            // Backdrop dim (full screen, tap-blocking)
            var dimGO = new GameObject("Dim", typeof(RectTransform), typeof(Image));
            dimGO.transform.SetParent(canvasGO.transform, false);
            var dimRT = dimGO.GetComponent<RectTransform>();
            dimRT.anchorMin = Vector2.zero;
            dimRT.anchorMax = Vector2.one;
            dimRT.offsetMin = Vector2.zero;
            dimRT.offsetMax = Vector2.zero;
            dimGO.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.55f);

            // Panel card
            var panelGO = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            panelGO.transform.SetParent(canvasGO.transform, false);
            _panel = panelGO;
            var panelRT = panelGO.GetComponent<RectTransform>();
            panelRT.anchorMin = new Vector2(0.5f, 0.5f);
            panelRT.anchorMax = new Vector2(0.5f, 0.5f);
            panelRT.pivot     = new Vector2(0.5f, 0.5f);
            panelRT.sizeDelta = new Vector2(475f, 557f);
            panelGO.GetComponent<Image>().color = new Color(0.12f, 0.08f, 0.22f, 0.98f);

            // Title
            var titleGO = new GameObject("Title", typeof(RectTransform));
            titleGO.transform.SetParent(panelRT, false);
            _titleText = titleGO.AddComponent<TextMeshProUGUI>();
            var displayFont = GameFont.GetDisplayTMP();
            if (displayFont != null) _titleText.font = displayFont;
            _titleText.text = "CONTINUE?";
            _titleText.fontSize = 56;
            _titleText.alignment = TextAlignmentOptions.Center;
            _titleText.color = new Color(1f, 0.84f, 0.42f, 1f);
            var titleRT = titleGO.GetComponent<RectTransform>();
            titleRT.anchorMin = new Vector2(0.05f, 0.78f);
            titleRT.anchorMax = new Vector2(0.95f, 0.95f);
            titleRT.offsetMin = Vector2.zero;
            titleRT.offsetMax = Vector2.zero;

            // Body — describes what continue does
            var bodyGO = new GameObject("Body", typeof(RectTransform));
            bodyGO.transform.SetParent(panelRT, false);
            _bodyText = bodyGO.AddComponent<TextMeshProUGUI>();
            if (displayFont != null) _bodyText.font = displayFont;
            _bodyText.text = "Clear the top rows\nand refill resources";
            _bodyText.fontSize = 28;
            _bodyText.alignment = TextAlignmentOptions.Center;
            _bodyText.color = new Color(0.94f, 0.90f, 0.84f, 1f);
            var bodyRT = bodyGO.GetComponent<RectTransform>();
            bodyRT.anchorMin = new Vector2(0.05f, 0.48f);
            bodyRT.anchorMax = new Vector2(0.95f, 0.72f);
            bodyRT.offsetMin = Vector2.zero;
            bodyRT.offsetMax = Vector2.zero;

            // Continue button (paid path) — primary CTA. Cost is dynamic per
            // SurvivalManager.CurrentContinueCost (50→100 ladder).
            var continueBtnGO = MakeButton(panelRT, "ContinueBtn",
                new Vector2(0.10f, 0.42f), new Vector2(0.90f, 0.56f),
                "CONTINUE  ●  50",
                new Color(0.18f, 0.65f, 0.38f, 1f),
                OnContinueClicked);
            _continueButton = continueBtnGO.GetComponent<Button>();
            _continueLabel = continueBtnGO.GetComponentInChildren<TextMeshProUGUI>();

            // Watch Ad button — Codex rule: visually secondary but clearly available.
            // Muted blue. Same rescue path as paid continue.
            var adBtnGO = MakeButton(panelRT, "AdBtn",
                new Vector2(0.15f, 0.26f), new Vector2(0.85f, 0.39f),
                "WATCH AD",
                new Color(0.25f, 0.42f, 0.62f, 1f),
                OnAdClicked);
            _adButton = adBtnGO.GetComponent<Button>();
            _adLabel = adBtnGO.GetComponentInChildren<TextMeshProUGUI>();
            // Smaller text — secondary button per Codex's "secondary but clear" rule
            if (_adLabel != null) _adLabel.fontSize = 28;

            // Give Up button — small, low visual weight
            var giveUpGO = MakeButton(panelRT, "GiveUpBtn",
                new Vector2(0.25f, 0.08f), new Vector2(0.75f, 0.20f),
                "GIVE UP",
                new Color(0.32f, 0.24f, 0.40f, 1f),
                OnGiveUpClicked);
            var giveUpLabel = giveUpGO.GetComponentInChildren<TextMeshProUGUI>();
            if (giveUpLabel != null) giveUpLabel.fontSize = 24;
        }

        private GameObject MakeButton(RectTransform parent, string name,
            Vector2 anchorMin, Vector2 anchorMax,
            string label, Color bgColor, UnityEngine.Events.UnityAction onClick)
        {
            var btnGO = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            btnGO.transform.SetParent(parent, false);
            var rt = btnGO.GetComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            btnGO.GetComponent<Image>().color = bgColor;
            btnGO.GetComponent<Button>().onClick.AddListener(onClick);

            var labelGO = new GameObject("Label", typeof(RectTransform));
            labelGO.transform.SetParent(btnGO.transform, false);
            var labelText = labelGO.AddComponent<TextMeshProUGUI>();
            var displayFont = GameFont.GetDisplayTMP();
            if (displayFont != null) labelText.font = displayFont;
            labelText.text = label;
            labelText.fontSize = 36;
            labelText.alignment = TextAlignmentOptions.Center;
            labelText.color = Color.white;
            var labelRT = labelGO.GetComponent<RectTransform>();
            labelRT.anchorMin = Vector2.zero;
            labelRT.anchorMax = Vector2.one;
            labelRT.offsetMin = Vector2.zero;
            labelRT.offsetMax = Vector2.zero;

            return btnGO;
        }

        public void Show(SurvivalManager survival)
        {
            _survival = survival;
            _adInFlight = false;

            // Dynamic cost from SurvivalManager (50 → 100 ladder).
            _currentCost = survival != null ? survival.CurrentContinueCost : 50;
            bool canAfford = CoinWallet.Balance >= _currentCost;

            if (_continueLabel != null)
                _continueLabel.text = canAfford
                    ? $"CONTINUE  ●  {_currentCost}"
                    : $"NEED ● {_currentCost - CoinWallet.Balance} MORE";
            if (_continueButton != null)
                _continueButton.interactable = canAfford;

            // Ad button always available (no cooldown per Codex). Disabled only
            // if AdManager is missing or an ad request is in-flight.
            bool adAvailable = AdManager.Instance != null;
            if (_adButton != null) _adButton.interactable = adAvailable;
            if (_adLabel != null) _adLabel.text = adAvailable ? "WATCH AD" : "AD UNAVAILABLE";

            try
            {
                AnalyticsManager.Log("continue_offered",
                    "stage", survival != null ? survival.CurrentStageIndex : -1,
                    "continue_number", survival != null ? survival.ContinuesUsedThisRun + 1 : -1,
                    "cost", _currentCost,
                    "coin_balance", CoinWallet.Balance,
                    "can_afford", canAfford ? 1 : 0,
                    "ad_available", adAvailable ? 1 : 0);
            }
            catch (Exception ex) { Debug.LogError($"[ContinueModal] Analytics threw: {ex.Message}"); }

            _canvas.gameObject.SetActive(true);
            GameAudio.Instance?.PlayUIClick();
        }

        private void Hide()
        {
            _canvas.gameObject.SetActive(false);
        }

        private void OnContinueClicked()
        {
            if (_survival == null || _adInFlight) { Hide(); return; }
            if (!CoinWallet.Spend(_currentCost))
            {
                Debug.LogWarning($"[ContinueModal] Continue tapped without sufficient coins for cost={_currentCost} — should have been gated.");
                return;
            }

            GameAudio.Instance?.PlayUIClick();
            Hide();
            _survival.ApplyContinueRescue();
            _survival.ResumeFromContinue();
        }

        private void OnAdClicked()
        {
            if (_survival == null || _adInFlight) return;
            if (AdManager.Instance == null)
            {
                Debug.LogWarning("[ContinueModal] AdManager missing — ad continue path unavailable.");
                return;
            }

            _adInFlight = true;
            if (_adButton != null) _adButton.interactable = false;
            if (_continueButton != null) _continueButton.interactable = false;

            try
            {
                AnalyticsManager.Log("continue_ad_started",
                    "stage", _survival.CurrentStageIndex,
                    "continue_number", _survival.ContinuesUsedThisRun + 1);
            }
            catch (Exception ex) { Debug.LogError($"[ContinueModal] Analytics threw: {ex.Message}"); }

            // Same rescue path as paid continue. AdManager.ShowRewardedAd fires
            // the callback only on successful ad completion.
            AdManager.Instance.ShowRewardedAd(onRewardGranted: () =>
            {
                try
                {
                    AnalyticsManager.Log("continue_ad_completed",
                        "stage", _survival.CurrentStageIndex,
                        "continue_number", _survival.ContinuesUsedThisRun + 1);
                }
                catch (Exception ex) { Debug.LogError($"[ContinueModal] Analytics threw: {ex.Message}"); }

                Hide();
                _adInFlight = false;
                _survival.ApplyContinueRescue();
                _survival.ResumeFromContinue();
            });
        }

        private void OnGiveUpClicked()
        {
            if (_adInFlight) return;
            GameAudio.Instance?.PlayUIClick();
            Hide();
            if (_survival != null) _survival.DeclineContinue();
        }
    }
}
