# WordDrop — running fix list
Started 2026-10-21 during Spencer's playtest. Add as we go; tick off when Spencer confirms.

## From Spencer
- [ ] **Specials SFX** — rocket (fireworks-07, 0-1.8s) + bomb/TNT/combos (explosion.mp3) DONE 2026-10-22, awaiting confirm; disco ball still needs a sound.
- [ ] **Specials VFX pass** — rebuild rocket/bomb/disco effects using the VFX packs already in the project (Hovl, Particle VFX, Hyper Casual, Confetti). Audition first, then pick.

- [ ] Bonus explosion looks wrong in full-board mode ("letters aren't exploding correctly") — need an F9 capture; suspect refill falling in during the rocket ripple.
- [ ] Popup texts over the board (GOLDEN LETTERS!, LETTER SHOWER!, word scores) may be hard to read on a full board.
- [ ] Specials rare in full-board mode (5+ letters needed; mostly 4-letter words) — consider a 4-letter reward.

## Already known (carried over)
- [ ] Commit the uncommitted work (dead-code cleanup + everything since ad0a450) once the checklist passes.
- [ ] Strip the temporary entry-slide perf logging (`HUDManager.ENTRY_PERF_LOG`) once the entry slide is approved.
- [ ] Coins analytics should include the bonus-blast coins.
- [ ] Letter shower has no watchdog if it ever stalls.
- [ ] Fresh-install run (tutorial reset) has never been played on the new game.
- [ ] Tuning round 2 → level order / difficulty curve (after this playtest).

## Waiting on Spencer
- [ ] Add HONK / HONKS / HONKED / HONKING to the dictionary? (needs per-word OK)
- [ ] MOVES number looking smaller — always, or only after a gold letter lands?
- [ ] Keep or delete the old Level/Daily screens and the walking level map?

## Done
- [x] BONUS! text readable — dark plate + shadow (2026-10-21) — awaiting confirm.
- [x] Bonus paid 0 coins on full board (2026-10-21) — awaiting confirm.
- [x] Missing MOVES count in full-board mode (2026-10-21) — awaiting confirm.
- [x] Tiles stuck drawing on top after swaps (2026-10-21) — awaiting confirm.
- [x] Bonus "ray gun" SFX stutter — power-up sound rate-limited (2026-10-21) — awaiting Spencer confirm.
- [x] Row rise firing on level entry before any move (2026-10-21) — awaiting Spencer confirm.
- [x] Rain no longer replaces row rises (2026-10-21) — rain only as the "LETTER SHOWER!" reward.
- [ ] Booster becomes available: the claim SFX plays but nothing draws the eye to WHAT unlocked (2026-11-18, Spencer on phone). Needs a visual cue on the booster itself. Ask on the look before building (art/feel).
