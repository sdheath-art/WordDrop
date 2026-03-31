using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Stub ad manager. Replace Debug.Log calls with real SDK calls (e.g. AdMob, IronSource).
    /// </summary>
    public class AdManager : MonoBehaviour
    {
        public static AdManager Instance { get; private set; }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            Debug.Log("[AdManager] Initialized (stub mode).");
        }

        /// <summary>
        /// Called between rounds every 2 rounds.
        /// </summary>
        public void ShowInterstitial()
        {
            Debug.Log("[AdManager] ShowInterstitial() — stub. Round interstitial would show here.");
            AnalyticsManager.AdShown("interstitial", completed: true);
        }

        /// <summary>
        /// Called when player taps the Hint button.
        /// </summary>
        public void ShowRewardedAd(System.Action onRewardGranted = null)
        {
            Debug.Log("[AdManager] ShowRewardedAd() — stub. Rewarded ad would show here. Granting reward immediately.");
            AnalyticsManager.AdShown("rewarded", completed: true);
            onRewardGranted?.Invoke();
        }
    }
}
