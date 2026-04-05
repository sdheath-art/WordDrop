using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using DG.Tweening;

namespace WordDrop
{
    /// <summary>
    /// Simple slide transition between menu and game.
    /// Menu → Game: menu slides off left with ease-in, then cards deal.
    /// Game → Menu: menu slides in from left with OutBack overshoot bounce.
    /// </summary>
    public class ScreenTransition : MonoBehaviour
    {
        public static ScreenTransition Instance { get; private set; }

        private bool _isTransitioning;

        private const float SLIDE_OUT_DUR = 0.35f;
        private const float SLIDE_IN_DUR  = 0.45f;
        private const float OVERSHOOT     = 1.3f;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        public bool IsTransitioning => _isTransitioning;

        public void Play(bool toGame, Action onMidpoint, Action onComplete = null)
        {
            if (_isTransitioning) return;
            StartCoroutine(SlideCoroutine(toGame, onMidpoint, onComplete));
        }

        private IEnumerator SlideCoroutine(bool toGame, Action onMidpoint, Action onComplete)
        {
            _isTransitioning = true;
            GameAudio.Instance?.PlayWhoosh();

            RectTransform menuPanelRT = GetMenuPanelRT();

            if (menuPanelRT == null)
            {
                onMidpoint?.Invoke();
                onComplete?.Invoke();
                _isTransitioning = false;
                yield break;
            }

            if (toGame)
            {
                // Keep menu fully visible and on top during setup
                if (MenuUI.Instance != null)
                    MenuUI.Instance.SetVisible(true);
                menuPanelRT.anchoredPosition = Vector2.zero;

                // Start the match behind the menu
                onMidpoint?.Invoke();

                // Wait a few frames for the match to set up, keeping menu on top
                yield return null;
                yield return null;
                yield return null;

                // Re-assert menu visibility (match setup may have hidden it)
                if (MenuUI.Instance != null)
                    MenuUI.Instance.SetVisible(true);
                menuPanelRT.anchoredPosition = Vector2.zero;

                // Now slide menu off to the left
                menuPanelRT.DOKill();
                menuPanelRT.DOAnchorPosX(-1200f, SLIDE_OUT_DUR)
                    .SetEase(Ease.InCubic)
                    .SetUpdate(true);

                yield return new WaitForSecondsRealtime(SLIDE_OUT_DUR);

                // Hide menu and reset
                if (MenuUI.Instance != null)
                    MenuUI.Instance.SetVisible(false);
                menuPanelRT.anchoredPosition = Vector2.zero;
            }
            else
            {
                // Game → Menu: show menu, slide it in from the left
                if (MenuUI.Instance != null)
                    MenuUI.Instance.SetVisible(true);
                menuPanelRT.DOKill();
                menuPanelRT.anchoredPosition = new Vector2(-1200f, 0f);

                onMidpoint?.Invoke();

                // Keep menu visible and positioned
                if (MenuUI.Instance != null)
                    MenuUI.Instance.SetVisible(true);
                menuPanelRT.anchoredPosition = new Vector2(-1200f, 0f);

                menuPanelRT.DOAnchorPosX(0f, SLIDE_IN_DUR)
                    .SetEase(Ease.OutCubic)
                    .SetUpdate(true);

                yield return new WaitForSecondsRealtime(SLIDE_IN_DUR);
                menuPanelRT.anchoredPosition = Vector2.zero;
            }

            _isTransitioning = false;
            onComplete?.Invoke();
        }

        private RectTransform GetMenuPanelRT()
        {
            if (MenuUI.Instance == null) return null;

            Transform menuT = MenuUI.Instance.transform;
            Canvas canvas = menuT.GetComponentInChildren<Canvas>();
            if (canvas == null) return null;

            Transform panel = canvas.transform.Find("MenuPanel");
            if (panel != null)
                return panel.GetComponent<RectTransform>();

            return canvas.GetComponent<RectTransform>();
        }
    }
}
