using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// MVP P5 booster — "Starseed Charm". Targeted single-tile transformation.
    /// Player taps a tile; that tile becomes a Wild (matches any letter in a
    /// scored word). Reuses RulesEngine.DebugSetWild — same code path used by
    /// dev/test wild placement, with the same edge-case handling (stones
    /// refuse, wild persists until consumed).
    /// </summary>
    public class StarseedCharm : Booster
    {
        public override string Id               => "starseed_charm";
        public override string DisplayName      => "Starseed Charm";
        // L1 = 1 wild, L2 = tap + closest neighbor → 2 wilds, L3 = + 4 neighbors → up to 5 wilds
        public override string ShortDescription
        {
            get
            {
                switch (Level)
                {
                    case 2:  return "Tap a tile — it + 1 neighbor become wild.";
                    case 3:  return "Tap a tile — it + 4 neighbors become wild.";
                    default: return "Tap a tile — it blooms into a wild.";
                }
            }
        }
        public override bool NeedsTarget => true;

        public override void ResolveWithTarget(int col, int row, System.Action onComplete)
        {
            var rules = RulesEngine.Instance;
            if (rules == null) { onComplete?.Invoke(); return; }

            int wildifiedCount = 0;
            void TryWildify(int c, int r)
            {
                if (c < 0 || c >= RulesEngine.COLS) return;
                if (r < 0 || r >= RulesEngine.ROWS) return;
                if (rules.GetCell(c, r) == null) return;
                rules.DebugSetWild(c, r);
                wildifiedCount++;
            }

            TryWildify(col, row);
            if (Level >= 2)
            {
                int[][] dirs = { new[]{0,1}, new[]{0,-1}, new[]{-1,0}, new[]{1,0} };
                for (int i = 0; i < dirs.Length; i++)
                {
                    int c = col + dirs[i][0], r = row + dirs[i][1];
                    if (c >= 0 && c < RulesEngine.COLS && r >= 0 && r < RulesEngine.ROWS
                        && rules.GetCell(c, r) != null)
                    {
                        TryWildify(c, r);
                        break;
                    }
                }
            }
            if (Level >= 3)
            {
                TryWildify(col, row + 1);
                TryWildify(col, row - 1);
                TryWildify(col - 1, row);
                TryWildify(col + 1, row);
            }

            Debug.Log($"[StarseedCharm L{Level}] Wildified {wildifiedCount} tile(s) centered at ({col},{row})");
            GameAudio.Instance?.PlayUIClick();
            onComplete?.Invoke();
        }
    }
}
