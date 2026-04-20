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

        public static void Add(int amount)
        {
            if (amount <= 0) return;
            int next = Balance + amount;
            PlayerPrefs.SetInt(BALANCE_KEY, next);
            PlayerPrefs.Save();
        }

        /// <summary>Returns true if the spend succeeded, false if insufficient funds.</summary>
        public static bool Spend(int amount)
        {
            if (amount <= 0) return true;
            int current = Balance;
            if (current < amount) return false;
            PlayerPrefs.SetInt(BALANCE_KEY, current - amount);
            PlayerPrefs.Save();
            return true;
        }

        public static void ResetAll()
        {
            PlayerPrefs.DeleteKey(BALANCE_KEY);
            PlayerPrefs.Save();
        }
    }
}
