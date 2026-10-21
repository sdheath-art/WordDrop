using System.Collections.Generic;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Tiered gameplay dictionary system.
    ///
    /// Three tiers loaded from TextAssets in Resources/:
    ///   Core     (~12k words) — common, recognizable, fair for competitive PvP
    ///   Extended (~51k words) — broader but still structurally normal English
    ///   Full     (~52k words) — original enable1 Scrabble dictionary (debug/reference)
    ///
    /// Default tier: Core (best for shipped gameplay).
    /// Switch via WordDictionary.SetTier(DictionaryTier.Extended) etc.
    ///
    /// Word lists are plain text files (one UPPERCASE word per line) in Resources/.
    /// To regenerate tiers, run filter_dictionary_v3.py and copy outputs to Resources/.
    ///
    /// Tuning:
    ///   - To force-include words in Core: add to dict_force_include.txt, rerun filter
    ///   - To force-exclude words: add to dict_force_exclude.txt, rerun filter
    ///   - Review removals: see dict_removed_from_core.txt / dict_removed_from_extended.txt
    /// </summary>
    public static class WordDictionary
    {
        // ── Tier enum ────────────────────────────────────────────────────────────

        public enum DictionaryTier
        {
            Core,       // ~12k — tight, fair, recognizable words only
            Extended,   // ~51k — broader but reasonable
            Full        // ~52k — original enable1 (Scrabble dictionary)
        }

        // ── State ────────────────────────────────────────────────────────────────

        private static DictionaryTier _currentTier = DictionaryTier.Core;
        private static HashSet<string> _wordSet;
        private static int _wordCount;


        // ── Public API ───────────────────────────────────────────────────────────

        /// <summary>Current active dictionary tier.</summary>
        public static DictionaryTier CurrentTier => _currentTier;

        /// <summary>Number of words in the current dictionary.</summary>
        public static int WordCount
        {
            get
            {
                EnsureLoaded();
                return _wordCount;
            }
        }

        /// <summary>
        /// Switches to a different dictionary tier.
        /// Forces a reload on next IsValidWord() call.
        /// </summary>
        public static void SetTier(DictionaryTier tier)
        {
            if (tier == _currentTier && _wordSet != null) return;
            _currentTier = tier;
            _wordSet = null; // force reload
//             WDLog.Log($"[WordDictionary] Tier set to {tier} — will reload on next lookup.");
        }

        /// <summary>
        /// Returns true if the word is valid in the current dictionary tier.
        /// Thread-safe after initial load.
        /// </summary>
        public static bool IsValidWord(string word)
        {
            if (string.IsNullOrEmpty(word)) return false;
            EnsureLoaded();
            return _wordSet.Contains(word.ToUpper().Trim());
        }

        // ── INTENTIONAL WORDS (2026-10-21 Spencer: "isnt LOIN a word" / HONK) ─────────────────────────────────────────
        // dict_core was cut at a word-frequency line (zipf 3.0) and real words just under it fell off (LOIN 2.75, HONK 2.95).
        // Resources/dict_intentional.txt = dict_extended words at zipf >= 2.5 that are standard dictionary words (or their
        // plurals/-ed/-ing), minus names and crude/offensive words (~3,150). They count ONLY when the player makes the word
        // on purpose — a swipe, or a swap whose word runs through the swapped tile — never when falling letters spell them.
        private static HashSet<string> _intentionalSet;
        public static bool IsIntentionalWord(string word)
        {
            if (IsValidWord(word)) return true;
            if (_intentionalSet == null)
            {
                _intentionalSet = new HashSet<string>();
                var ta = Resources.Load<TextAsset>("dict_intentional");
                if (ta != null) foreach (var raw in ta.text.Split('\n')) { var t = raw.Trim().ToUpper(); if (t.Length >= 3) _intentionalSet.Add(t); }
            }
            return _intentionalSet.Contains(word.ToUpper().Trim());
        }

        /// <summary>
        /// Returns a summary string for debug/HUD display.
        /// </summary>
        public static string GetTierInfo()
        {
            EnsureLoaded();
            return $"{_currentTier} ({_wordCount:N0} words)";
        }

        // ── Loading ──────────────────────────────────────────────────────────────

        private static void EnsureLoaded()
        {
            if (_wordSet != null) return;

            string resourceName = GetResourceName(_currentTier);
            TextAsset textAsset = Resources.Load<TextAsset>(resourceName);

            if (textAsset == null)
            {
                Debug.LogError($"[WordDictionary] Could not load Resources/{resourceName}.txt — " +
                               "falling back to empty dictionary!");
                _wordSet = new HashSet<string>();
                _wordCount = 0;
                return;
            }

            _wordSet = new HashSet<string>();
            string[] lines = textAsset.text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string w = lines[i].Trim().ToUpper();
                if (w.Length >= 3 && w.Length <= 9)   // was <= 7 — silently dropped every longer word (BIRTHDAY, SATURDAY...) 2026-10-21
                    _wordSet.Add(w);
            }

            // LONG WORDS (2026-10-21 Spencer: "isnt creation a word"). The dictionaries were built at 7 letters max.
            // Resources/dict_long.txt = common 8-9 letter words (word-frequency zipf >= 3.0, standard dictionary, no names,
            // offensive words removed) — ~5,400. Count everywhere, like core.
            var longTa = Resources.Load<TextAsset>("dict_long");
            if (longTa != null)
                foreach (var raw in longTa.text.Split('\n')) { var w = raw.Trim().ToUpper(); if (w.Length >= 8 && w.Length <= 9) _wordSet.Add(w); }

            _wordCount = _wordSet.Count;
//             WDLog.Log($"[WordDictionary] Loaded {_currentTier} tier: {_wordCount:N0} words " +
                      // $"from Resources/{resourceName}.txt");

            Resources.UnloadAsset(textAsset);
        }

        private static string GetResourceName(DictionaryTier tier)
        {
            switch (tier)
            {
                case DictionaryTier.Core:     return "dict_core";
                case DictionaryTier.Extended: return "dict_extended";
                case DictionaryTier.Full:     return "dict_full";
                default:                      return "dict_core";
            }
        }
    }
}
