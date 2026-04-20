using UnityEngine;
using DG.Tweening;

namespace WordDrop
{
    /// <summary>
    /// Shows "N turns left" countdown when 10 or fewer turns remain.
    /// Writes directly into HUDManager's turn counter label (screen-space UI).
    /// </summary>
    public class TurnCountdown : MonoBehaviour
    {
        public static TurnCountdown Instance { get; private set; }

        private const int SHOW_AT_TURNS_LEFT = 10;

        private static readonly Color COLOR_CALM     = new Color(0.94f, 0.90f, 0.84f, 1f); // warm white
        private static readonly Color COLOR_WARM     = new Color(1.00f, 0.84f, 0.42f, 1f); // gold
        private static readonly Color COLOR_HOT      = new Color(1.00f, 0.56f, 0.67f, 1f); // pink
        private static readonly Color COLOR_CRITICAL  = new Color(1.00f, 0.40f, 0.40f, 1f); // soft red

        private int _lastShownTurns = -1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            if (Instance == null)
            {
                var go = new GameObject("TurnCountdown");
                go.AddComponent<TurnCountdown>();
            }
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        public void UpdateTurnsLeft(int turnsRemaining, int maxTotal)
        {
            if (HUDManager.Instance == null) return;

            // In blitz mode, BlitzManager manages the timer display.
            // In Survival and Level modes, the label is owned by MatchController's
            // StartMatch HUD setup ("SURVIVAL" / "LEVEL N") — don't overwrite it
            // with a "turns left" count because EffectiveMaxTurns is int.MaxValue.
            if (BlitzManager.IsBlitzMode) return;
            if (SurvivalManager.IsSurvivalMode) return;
            if (GameManager.IsLevelMode) return;

            int playerTurnsLeft;
            if (MatchController.Instance != null)
            {
                int humanUsed = MatchController.Instance.GetPlayerTurns(MatchController.PLAYER_HUMAN);
                playerTurnsLeft = MatchController.Instance.EffectiveMaxTurns - humanUsed;
            }
            else
            {
                playerTurnsLeft = turnsRemaining / 2;
            }

            // Sudden death override
            if (MatchController.Instance != null && MatchController.Instance.IsSuddenDeath)
            {
                if (_lastShownTurns != -99) // only set once
                {
                    _lastShownTurns = -99;
                    HUDManager.Instance.SetTurnCountdownText("SUDDEN DEATH", COLOR_CRITICAL);
                }
                return;
            }

            if (playerTurnsLeft <= 0)
            {
                HUDManager.Instance.SetTurnCountdownText("", COLOR_CALM);
                _lastShownTurns = -1;
                return;
            }

            if (playerTurnsLeft == _lastShownTurns) return;
            _lastShownTurns = playerTurnsLeft;

            string text = playerTurnsLeft == 1 ? "LAST TURN!" : $"{playerTurnsLeft} left";

            // Color escalation in final turns
            Color color;
            if (playerTurnsLeft > SHOW_AT_TURNS_LEFT)
                color = COLOR_CALM;
            else if (playerTurnsLeft >= 6)
                color = COLOR_CALM;
            else if (playerTurnsLeft >= 4)
                color = COLOR_WARM;
            else if (playerTurnsLeft >= 2)
                color = COLOR_HOT;
            else
                color = COLOR_CRITICAL;

            HUDManager.Instance.SetTurnCountdownText(text, color);
        }

        public void Hide()
        {
            _lastShownTurns = -1;
            if (HUDManager.Instance != null)
                HUDManager.Instance.SetTurnCountdownText("", COLOR_CALM);
        }
    }
}
