using UnityEngine;
using TMPro;

namespace WordDrop
{
    /// <summary>
    /// Font provider — Nunito Sans everywhere (UI + display).
    /// Avenir Next for tile letters.
    /// No bold styling — fonts carry their own weight.
    /// </summary>
    public static class GameFont
    {
        private static Font _tileFont;
        private static Font _uiFont;
        private static bool _loadedTile, _loadedUI;

        private static TMP_FontAsset _tileTMP;
        private static TMP_FontAsset _uiTMP;
        private static bool _loadedTileTMP, _loadedUITMP;

        // ── TMP ─────────────────────────────────────────────────────────────────

        /// <summary>Tile font: Avenir Next SDF.</summary>
        public static TMP_FontAsset GetTMP()
        {
            if (!_loadedTileTMP)
            {
                _tileTMP = Resources.Load<TMP_FontAsset>("AvenirNext SDF");
                _loadedTileTMP = true;
            }
            return _tileTMP;
        }

        /// <summary>UI font: Nunito Sans SDF — HUD, buttons, game over, shuffle, scoring.</summary>
        public static TMP_FontAsset GetUITMP()
        {
            if (!_loadedUITMP)
            {
                _uiTMP = Resources.Load<TMP_FontAsset>("NunitoExtraBold SDF");
                if (_uiTMP == null) _uiTMP = GetTMP();
                _loadedUITMP = true;
            }
            return _uiTMP;
        }

        /// <summary>Display font: same as UI (Nunito Sans).</summary>
        public static TMP_FontAsset GetDisplayTMP() => GetUITMP();

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
            if (f != null) { tm.font = f; var mr = tm.GetComponent<MeshRenderer>(); if (mr != null && f.material != null) mr.material = f.material; }
        }

        public static void ApplyUI(TextMesh tm)
        {
            if (tm == null) return;
            Font f = GetUI();
            if (f != null) { tm.font = f; var mr = tm.GetComponent<MeshRenderer>(); if (mr != null && f.material != null) mr.material = f.material; }
        }

        public static void ApplyDisplay(TextMesh tm) => ApplyUI(tm);
        public static void ApplyBody(TextMesh tm) => ApplyUI(tm);
    }
}
