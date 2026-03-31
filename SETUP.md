# WordDrop — Setup Notes

## Auto-Setup
The game is fully self-assembling. Open the project in Unity and press Play. `SceneBootstrap` creates everything at runtime.

## Job 5: Null-Safe RulesEngine Step Methods

### Changes Made
`GameVisualBridge.cs` — `RunStepByStepResolutionInner` method updated with null-safety for all phase handlers:

- **WordsDetected**: `step.NewWords` null-check — treats null as empty, logs "none", continues to NextStep without error
- **WordsScored**: `step.ScoredWords` null-check — skips the entire animation loop if null or empty, logs reason, no wait
- **TriggersFound**: `step.Triggers` null/empty check — skips flash animation and wait entirely if no triggers
- **Exploding**: `step.ExplodedCells` null/empty check — skips shrink animation but **still calls `grid.RemoveTiles([])`** (safe no-op); logs clearly
- **GravityApplied**: `grid.ApplyGravity()` is **always yielded** — even if an exception occurs mid-coroutine, the yield already happened; `RebuildFromRulesEngine` follows
- **Complete**: `rules.FinalizeDrop()` is **always called** even when `step.TotalScore == 0` — explicit comment documents this intent
- **default**: New case logs the unexpected phase name, its int value, and current `rules.CurrentPhase`, then sets `resolving = false` to exit cleanly

### Verification
- Drop a letter that doesn't form a word: resolution reaches `Complete` phase cleanly (TotalScore=0, FinalizeDrop called)
- Drop letters that form a word: `WordsScored` animates correctly, `Complete` fires with correct score
- No phase silently skips — every path has a `Debug.Log`
- Unexpected phases stop the loop with a clear warning instead of spinning forever
