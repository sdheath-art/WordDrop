using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Named AI rivals with persistent win/loss records.
    /// Each rival has a name, play style description, and maps to an AI profile.
    /// Win/loss records persist via PlayerPrefs.
    /// </summary>
    public class RivalSystem : MonoBehaviour
    {
        public static RivalSystem Instance { get; private set; }

        // ── Rival definitions ────────────────────────────────────────────────────

        public struct Rival
        {
            public string Name;
            public string Title;        // e.g. "The Scorer"
            public string Description;  // play style hint
            public AIAgent.AIProfile Profile;
            public int Difficulty;       // 0=easy, 1=medium, 2=hard
            public Color AccentColor;
        }

        public static readonly Rival[] ALL_RIVALS = new Rival[]
        {
            new Rival
            {
                Name = "Sarah",
                Title = "The Wordsmith",
                Description = "Goes for big words and high scores",
                Profile = AIAgent.AIProfile.Scorer,
                Difficulty = 1,
                AccentColor = new Color(1f, 0.56f, 0.67f, 1f), // pink
            },
            new Rival
            {
                Name = "Max",
                Title = "The Wall",
                Description = "Blocks your best plays and steals your setups",
                Profile = AIAgent.AIProfile.Blocker,
                Difficulty = 1,
                AccentColor = new Color(0.4f, 0.7f, 1f, 1f), // blue
            },
            new Rival
            {
                Name = "Luna",
                Title = "The Chain Hunter",
                Description = "Lives for chain detonations — watch your primed words",
                Profile = AIAgent.AIProfile.TriggerHunter,
                Difficulty = 2,
                AccentColor = new Color(0.8f, 0.5f, 1f, 1f), // purple
            },
        };

        // ── Current rival ────────────────────────────────────────────────────────

        private int _currentRivalIndex = 0;

        public Rival CurrentRival => ALL_RIVALS[_currentRivalIndex];

        // ── Lifecycle ────────────────────────────────────────────────────────────

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            if (Instance == null)
            {
                var go = new GameObject("RivalSystem");
                go.AddComponent<RivalSystem>();
            }
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        // ── Public API ───────────────────────────────────────────────────────────

        /// <summary>Pick a random rival for the next match.</summary>
        public Rival PickRandomRival()
        {
            _currentRivalIndex = Random.Range(0, ALL_RIVALS.Length);
            Rival rival = ALL_RIVALS[_currentRivalIndex];

            // Apply rival's profile and difficulty to AIAgent
            AIAgent.CurrentProfile = rival.Profile;
            AIAgent.Difficulty = rival.Difficulty;

//             Debug.Log($"[RivalSystem] Picked rival: {rival.Name} '{rival.Title}' " +
                      // $"(profile={rival.Profile}, difficulty={rival.Difficulty})");
            return rival;
        }

        /// <summary>Record a win for the player against the current rival.</summary>
        public void RecordPlayerWin()
        {
            string key = $"rival_wins_{CurrentRival.Name}";
            int wins = PlayerPrefs.GetInt(key, 0) + 1;
            PlayerPrefs.SetInt(key, wins);
            PlayerPrefs.Save();
//             Debug.Log($"[RivalSystem] Player beat {CurrentRival.Name}! Record: {wins}W-{GetLosses(CurrentRival.Name)}L");
        }

        /// <summary>Record a loss for the player against the current rival.</summary>
        public void RecordPlayerLoss()
        {
            string key = $"rival_losses_{CurrentRival.Name}";
            int losses = PlayerPrefs.GetInt(key, 0) + 1;
            PlayerPrefs.SetInt(key, losses);
            PlayerPrefs.Save();
//             Debug.Log($"[RivalSystem] {CurrentRival.Name} beat player. Record: {GetWins(CurrentRival.Name)}W-{losses}L");
        }

        /// <summary>Get player's wins against a rival.</summary>
        public int GetWins(string rivalName) => PlayerPrefs.GetInt($"rival_wins_{rivalName}", 0);

        /// <summary>Get player's losses against a rival.</summary>
        public int GetLosses(string rivalName) => PlayerPrefs.GetInt($"rival_losses_{rivalName}", 0);

        /// <summary>Get formatted record string.</summary>
        public string GetRecordString(string rivalName)
        {
            return $"{GetWins(rivalName)}W - {GetLosses(rivalName)}L";
        }

        /// <summary>Get formatted record for current rival.</summary>
        public string GetCurrentRecord() => GetRecordString(CurrentRival.Name);
    }
}
