# WordDrop — Setup Notes

## Zero Manual Setup Required
Open the project in Unity 2021.3+ (LTS recommended), let scripts compile, then press **Play**.
The harness (`SceneAutoSetup.cs`) automatically:
1. Creates a new scene
2. Attaches `SceneBootstrap` to a GameObject
3. Enters Play Mode

## What Job 1 Provides
- `WordDrop.asmdef` — assembly definition so `UnityEngine.UI` resolves correctly
- `SceneBootstrap.cs` — creates Camera (orthographic size 10, portrait), EventSystem, GameManager
- `GameManager.cs` — state machine with `Menu / Playing / RoundOver / GameOver` states

## Verify Job 1
Console should show:
[SceneBootstrap] Awake begin
[SceneBootstrap] Camera bounds — halfH=10.00, halfW=X.XX
[SceneBootstrap] EventSystem created
[GameManager] Awake — state: Menu
[GameManager] Menu → Menu
[GameManager] Entered Menu
[SceneBootstrap] Awake complete — GameManager state: Menu
No errors or warnings should appear.

## Subsequent Jobs
Each job builds on this scaffold. Do not modify the scene manually — all setup is code-driven.
