using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Persistent coin currency, PlayerPrefs-backed.
    /// Wired into level completion rewards in Phase 7 and booster/IAP spending in Phase 7 + Phase 11.
    /// </summary>
    public static class CoinWallet
    {
        private const string BALANCE_KEY = "wd_coins_balance";

        public static int Balance => PlayerPrefs.GetInt(BALANCE_KEY, 0);

        /// <summary>Fires after balance changes (Add or Spend). Payload = new balance.
        /// HUD/UI subscribers update display without polling.</summary>
        public static event System.Action<int> OnBalanceChanged;

        public static void Add(int amount)
        {
            if (amount <= 0) return;
            int next = Balance + amount;
            PlayerPrefs.SetInt(BALANCE_KEY, next);
            PlayerPrefs.Save();
            try { OnBalanceChanged?.Invoke(next); }
            catch (System.Exception ex) { Debug.LogError($"[CoinWallet] OnBalanceChanged subscriber threw: {ex.Message}"); }
        }

        /// <summary>Returns true if the spend succeeded, false if insufficient funds.</summary>
        public static bool Spend(int amount)
        {
            if (amount <= 0) return true;
            int current = Balance;
            if (current < amount) return false;
            int next = current - amount;
            PlayerPrefs.SetInt(BALANCE_KEY, next);
            PlayerPrefs.Save();
            try { OnBalanceChanged?.Invoke(next); }
            catch (System.Exception ex) { Debug.LogError($"[CoinWallet] OnBalanceChanged subscriber threw: {ex.Message}"); }
            return true;
        }

        public static void ResetAll()
        {
            PlayerPrefs.DeleteKey(BALANCE_KEY);
            PlayerPrefs.Save();
            try { OnBalanceChanged?.Invoke(0); }
            catch (System.Exception ex) { Debug.LogError($"[CoinWallet] OnBalanceChanged subscriber threw: {ex.Message}"); }
        }
    }
}
