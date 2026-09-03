using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Renders 7 downward-pointing arrow indicators (▼) above each grid column.
    /// Arrows are visible when a letter is selected and hidden otherwise.
    /// Also highlights the column the mouse/finger is hovering over.
    ///
    /// Arrow sizing: fontSize=24, characterSize=0.035f gives a compact
    /// downward triangle that sits cleanly above each column header.
    /// Built in Awake() so arrows are ready before SceneBootstrap.Start() runs.
    /// </summary>
    public class ColumnArrowManager : MonoBehaviour
    {
        public static ColumnArrowManager Instance { get; private set; }

        private static readonly Color ARROW_COLOR_NORMAL    = new Color(0.85f, 0.85f, 0.90f, 0.80f);
        private static readonly Color ARROW_COLOR_HIGHLIGHT = new Color(0.20f, 0.85f, 0.40f, 1.00f);

        private GameObject[] _arrowObjects = new GameObject[GridManager.COLS];
        private TextMesh[]   _arrowTexts   = new TextMesh[GridManager.COLS];

        private Camera _cam;
        private bool   _arrowsVisible = false;
        private int    _hoveredCol    = -1;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;

            _cam = Camera.main;
            BuildArrows();
            ShowArrows(false);
//             Debug.Log("[ColumnArrowManager] Awake — arrows built and hidden");
        }

        private void BuildArrows()
        {
            GridManager grid = GridManager.Instance;
            if (grid == null)
            {
                Debug.LogWarning("[ColumnArrowManager] GridManager not found — cannot build arrows.");
                return;
            }

            // Place arrows above the grid top with clearance
            float arrowY = grid.GridTop + grid.CellSize * 0.65f;

            for (int col = 0; col < GridManager.COLS; col++)
            {
                float x = grid.GetColumnCenterX(col);

                GameObject arrowGO = new GameObject($"ColArrow_{col}");
                arrowGO.transform.SetParent(transform, false);
                arrowGO.transform.position = new Vector3(x, arrowY, -1f);

                TextMesh tm      = arrowGO.AddComponent<TextMesh>();
                tm.text          = "▼";
                tm.anchor        = TextAnchor.MiddleCenter;
                tm.alignment     = TextAlignment.Center;

                // Small compact arrow: fontSize=24, characterSize=0.035f
                // This gives a world-space size of approximately 0.84 units tall,
                // well within the cell size and visually distinct from tiles.
                tm.fontSize      = 24;
                tm.characterSize = 0.035f;
                tm.fontStyle     = FontStyle.Normal;
                tm.color         = ARROW_COLOR_NORMAL;

                arrowGO.transform.localScale = Vector3.one;

                MeshRenderer mr = arrowGO.GetComponent<MeshRenderer>();
                if (mr != null) mr.sortingOrder = 50; // 2026-09-03: above the per-row tile bands (was 15)

                _arrowObjects[col] = arrowGO;
                _arrowTexts[col]   = tm;
            }

//             Debug.Log($"[ColumnArrowManager] Built {GridManager.COLS} column arrows " +
                      // $"at Y={arrowY:F2} (fontSize=24, characterSize=0.035)");
        }

        public void ShowArrows(bool visible)
        {
            _arrowsVisible = visible;

            // Reposition arrows to current GridTop each time they're shown
            // (grid layout changes between modes / rebuilds)
            if (visible)
            {
                GridManager grid = GridManager.Instance;
                if (grid != null)
                {
                    float arrowY = grid.GridTop + grid.CellSize * 0.65f;
                    for (int col = 0; col < GridManager.COLS; col++)
                    {
                        if (_arrowObjects[col] != null)
                        {
                            float x = grid.GetColumnCenterX(col);
                            _arrowObjects[col].transform.position = new Vector3(x, arrowY, -1f);
                        }
                    }
                }
            }

            for (int col = 0; col < GridManager.COLS; col++)
            {
                if (_arrowObjects[col] != null)
                    _arrowObjects[col].SetActive(visible);
            }
            if (!visible)
                _hoveredCol = -1;
        }

        private void Update()
        {
            if (!_arrowsVisible) return;

            GridManager grid = GridManager.Instance;
            if (grid == null || _cam == null) return;

            // Get pointer position (works for both mouse and touch)
            Vector3 screenPos = Input.mousePosition;
            if (Input.touchCount > 0)
                screenPos = Input.GetTouch(0).position;

            screenPos.z = Mathf.Abs(_cam.transform.position.z);
            Vector3 worldPos = _cam.ScreenToWorldPoint(screenPos);

            int newHoveredCol = -1;
            if (grid.IsYInTapZone(worldPos.y))
                newHoveredCol = grid.WorldXToColumn(worldPos.x);

            if (newHoveredCol != _hoveredCol)
            {
                UpdateArrowColors(newHoveredCol, grid);
                _hoveredCol = newHoveredCol;
            }
        }

        private void UpdateArrowColors(int hoveredCol, GridManager grid)
        {
            for (int col = 0; col < GridManager.COLS; col++)
            {
                bool isHovered = (col == hoveredCol) && grid.IsColumnAvailable(col);
                if (_arrowTexts[col] != null)
                    _arrowTexts[col].color = isHovered ? ARROW_COLOR_HIGHLIGHT : ARROW_COLOR_NORMAL;
            }
        }
    }
}
