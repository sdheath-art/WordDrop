using UnityEngine;
using TMPro;

namespace WordDrop
{
    /// <summary>
    /// Font provider — 3 distinct roles:
    ///   Tile:    Avenir Next — clean, legible on board tiles
    ///   UI:      Clarity (Thrill Fonts) — readable HUD, buttons, labels
    ///   Display: Cartoon (Thrill Fonts) — celebrations, announcements, big moments
    /// </summary>
    public static class GameFont
    {
        private static Font _tileFont;
        private static Font _uiFont;
        private static bool _loadedTile, _loadedUI;

        private static TMP_FontAsset _tileTMP;
        private static TMP_FontAsset _uiTMP;
        private static TMP_FontAsset _displayTMP;
        private static bool _loadedTileTMP, _loadedUITMP, _loadedDisplayTMP;

        // ── TMP ─────────────────────────────────────────────────────────────────

        /// <summary>Tile font: Clarity SDF — trying it on board + hand-card letters (2026-06-03, was Baloo2 SDF). Fallbacks Baloo2 → AvenirNext.</summary>
        public static TMP_FontAsset GetTMP()
        {
            if (!_loadedTileTMP)
            {
                _tileTMP = Resources.Load<TMP_FontAsset>("AvenirNext SDF");
                if (_tileTMP == null) _tileTMP = Resources.Load<TMP_FontAsset>("ClearSans-Bold SDF"); // fallback
                if (_tileTMP == null) _tileTMP = Resources.Load<TMP_FontAsset>("Geometos SDF"); // fallback
                if (_tileTMP == null) _tileTMP = Resources.Load<TMP_FontAsset>("Clarity SDF"); // fallback
                if (_tileTMP == null) _tileTMP = Resources.Load<TMP_FontAsset>("Baloo2 SDF"); // fallback
                if (_tileTMP == null) _tileTMP = Resources.Load<TMP_FontAsset>("AvenirNext SDF"); // fallback
                _loadedTileTMP = true;
                bool gotLuckiest = Resources.Load<TMP_FontAsset>("LuckiestGuy SDF") != null;
                Debug.Log($"[GameFont] Tile font loaded: '{(_tileTMP != null ? _tileTMP.name : "NULL")}' | LuckiestGuy SDF resolvable={gotLuckiest}");
            }
            return _tileTMP;
        }

        // 2026-06-04 Spencer: dedicated font for the WILD "?" symbol (Geometos),
        // so the wild glyph reads distinct/enchanted vs the normal tile letters.
        private static TMP_FontAsset _wildSymbolTMP;
        private static bool _loadedWildSymbolTMP;
        public static TMP_FontAsset GetWildSymbolTMP()
        {
            if (!_loadedWildSymbolTMP)
            {
                _wildSymbolTMP = Resources.Load<TMP_FontAsset>("Geometos SDF");
                _loadedWildSymbolTMP = true;
            }
            return _wildSymbolTMP;
        }

        /// <summary>UI font: Clarity SDF — HUD, buttons, scores, labels, game over body.</summary>
        public static TMP_FontAsset GetUITMP()
        {
            if (!_loadedUITMP)
            {
                _uiTMP = Resources.Load<TMP_FontAsset>("Montserrat-Black SDF");
                if (_uiTMP == null) _uiTMP = Resources.Load<TMP_FontAsset>("Cartoon SDF");
                if (_uiTMP == null) _uiTMP = GetTMP();
                _loadedUITMP = true;
//                 Debug.Log($"[GameFont] UI font loaded: {(_uiTMP != null ? _uiTMP.name : "NULL")}");
            }
            return _uiTMP;
        }

        /// <summary>Display font: Cartoon SDF — celebrations, chain counter, meltdown, DOUBLE!, YOU WIN!</summary>
        public static TMP_FontAsset GetDisplayTMP()
        {
            if (!_loadedDisplayTMP)
            {
                _displayTMP = Resources.Load<TMP_FontAsset>("Cartoon SDF");
                if (_displayTMP == null) _displayTMP = Resources.Load<TMP_FontAsset>("Cartoon SDF");
                if (_displayTMP == null) _displayTMP = GetUITMP();
                _loadedDisplayTMP = true;
//                 Debug.Log($"[GameFont] Display font loaded: {(_displayTMP != null ? _displayTMP.name : "NULL")}");
            }
            return _displayTMP;
        }

        // ── Legacy ──────────────────────────────────────────────────────────────

        public static Font Get()
        {
            if (!_loadedTile)
            {
                _tileFont = Resources.Load<Font>("AvenirNext");
                if (_tileFont == null) _tileFont = Resources.Load<Font>("NunitoSans");
                _loadedTile = true;
            }
            return _tileFont;
        }

        public static Font GetUI()
        {
            if (!_loadedUI)
            {
                _uiFont = Resources.Load<Font>("NunitoSans");
                if (_uiFont == null) _uiFont = Get();
                _loadedUI = true;
            }
            return _uiFont;
        }

        public static Font GetDisplay() => GetUI();
        public static Font GetBody() => GetUI();

        public static void Apply(TextMesh tm)
        {
            if (tm == null) return;
            Font f = Get();
            if (f != null) { tm.font = f; var mr = tm.GetComponent<MeshRenderer>(); if (mr != null && f.material != null) mr.sharedMaterial = f.material; }
        }

        public static void ApplyUI(TextMesh tm)
        {
            if (tm == null) return;
            Font f = GetUI();
            if (f != null) { tm.font = f; var mr = tm.GetComponent<MeshRenderer>(); if (mr != null && f.material != null) mr.sharedMaterial = f.material; }
        }

        public static void ApplyDisplay(TextMesh tm) => ApplyUI(tm);
        public static void ApplyBody(TextMesh tm) => ApplyUI(tm);
    }
}
