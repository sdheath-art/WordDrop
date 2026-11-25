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

## 2026-11-24 (Spencer)
- **Flying Tome (airplane power-up) holds the letters from falling until its flight animation is done.** Spencer: "these are things that need to get done at some point ... even if its risky". Same family as the owl hang fixed today (visual waits on an animation before gravity runs). Not investigated yet.
- **Axe hitting a power-up: the power-up only fires after the chop AND its fall have finished** (Spencer 11-24: "the axe does not trigger boosters until the animation is done"). Log 10:29 run: `[Axe] Chopped column 3: 7 tiles` at frame 1925 (the power-up at (3,5) is left out of the cleared list), the cascade completes at 1961, and only then `[Booster] hit a AIRPLANE at (3,0) -> fires` at 1962 - i.e. it falls to the bottom first and goes off from there ~0.6 s late. Wanted: it fires the moment the blade reaches it, from where it was. Same family as the tome hold.

## 2026-11-24 — vase piece (PARKED 11-24: art not good enough yet; code dormant, PNGs in ~/Documents/WordDrop_Art/vase_piece/parked_from_resources)
- [ ] VASE LOOK: Spencer "something about it is bothering me and im not sure what it is but i guess its fine for now". Revisit with fresh eyes. Candidates: darker than every other piece (raw colour kept at his choice), seen slightly from above while other pieces are front-on, faint shadow, stretched art (x1.10 / x0.88), an urn in a library.
- [ ] VASE SOUND: still the wooden crate break sound.
- [ ] MISSING TILE FACE (13:42 screenshot, L22): a letter tile drew only its shadow + letter after a rocket+bomb combo. Not diagnosed; screenshot dump now logs the cause ([NOTE-NOFACE]).
