# Glimbloom Feel-Pass Research Spec

## 1. Animation timing principles

### What public sources actually support
- `Royal Match` is consistently described as unusually fast, concurrent, and low-friction: Deconstructor of Fun calls out “quick animations and concurrent matching” and says it is “one of the fastest switchers live” (https://www.deconstructoroffun.com/blog/2021/3/21/royal-match-the-new-king-from-turkey). `The Experimentation Group` similarly uses Royal Match as a polish benchmark and says its slowed-down row blasters stay “sharp and satisfying” without interrupting play (https://www.theexperimentation.group/our-work/field-notes). `[High confidence]`
- Public CC/RM sources do **not** publish a reliable frame sheet for tile-fall, pop, score-popup, or hitstop timings. For exact implementation values, use practitioner ranges below and treat them as `[INFERRED]`. `[High confidence]`

### Per-event timing targets for Glimbloom
- Tile drop/fall: use `Ease.Linear` for the main travel so gravity reads as real, then a tiny `Ease.OutQuad` settle on landing; `0.09-0.14s` for 1 cell, add `0.015-0.025s` per extra cell `[INFERRED]`. Basis: RM’s “fast” concurrent feel and the general “don’t cause waiting” guidance in Material motion, which puts frequent mobile transitions around `225-300ms` and warns that `>400ms` feels slow (https://m1.material.io/motion/duration-easing.html).  
- Cascade gravity: prefer true gravity timing over heavily eased “floaty” drops; let columns start together or nearly together, not in a visible queue `[INFERRED]`. If you need readability, stagger columns by only `8-16ms` `[INFERRED]`.
- Tile select highlight: `Ease.OutQuad` in, `Ease.InOutSine` idle pulse; initial response in `0.06-0.10s`, then pulse every `0.55-0.80s` `[INFERRED]`.
- Tile match/explode: anticipation `0.04-0.06s`, impact `0.06-0.10s`, debris/glow tail `0.12-0.20s` `[INFERRED]`.
- Score popup: entry `0.10-0.16s`, hang `0.30-0.45s`, exit `0.18-0.28s` `[INFERRED]`.
- UI panels/buttons: use mobile motion ranges close to Material’s `225ms` enter, `195ms` exit, `300ms` standard transition, `375ms` large/full-screen transition (https://m1.material.io/motion/duration-easing.html). For DOTween, map these to `Ease.OutCubic` / `Ease.OutQuad` for enter and `Ease.InCubic` for exit `[INFERRED]`.

## 2. Squash and stretch / anticipation

### Practical CC/RM-style rules
- Small match hit: pre-squash to `0.92-0.96` Y and `1.04-1.08` X for `30-45ms`, then burst to `1.10-1.16` uniform scale for `45-70ms`, settle to `1.00` over `70-110ms` with `Ease.OutBack` `[INFERRED]`.
- Big special/word payoff: pre-squash `0.84-0.90` Y / `1.08-1.14` X for `35-55ms`, overshoot to `1.18-1.28` for `55-85ms` `[INFERRED]`.
- Keep volume believable: stretch/squash should read as elastic candy/gem compression, not rubber UI. Swink and the “juice” tradition both treat exaggeration as valuable, but only when it preserves readability and control (Steve Swink’s `Game Feel`: https://redpantsgamedesign.com/game-feel, and `Juice It or Lose It`: https://www.gdcvault.com/play/1016487/Juice-It-or-Lose). `[High confidence]`
- Royal Match’s feel reads less “cartoon wobble” than many clones; it favors short anticipation and immediate payoff over long wobbling recovery `[INFERRED]`.

## 3. Layered feedback stack

### Typical layer count
- Small 3-tile match should fire `4-6` layers: tile flash, pop/scale, particles, audio tick, optional micro-haptic, optional micro-screen tint `[INFERRED]`.
- Word completion / special combo should fire `6-8` layers: pre-pulse/glow, scale hit, burst particles, score popup, audio chord or pitch-rise, haptic, micro camera shake, `1-3` frame hitstop `[INFERRED]`.
- Finale moments like `Candy Crush` Sugar Crush and `Royal Match` level-clear celebrations add celebration VO/text, repeated auto-firings, board-wide sparkle, and stronger score/UI emphasis (Sugar Crush explainer: https://candycrush.zendesk.com/hc/en-us/articles/360000750858-What-s-a-Sugar-Crush). `[High confidence]`

### Stack order
- Best order for Glimbloom: `anticipation glow -> impact scale/pop -> particles -> score popup -> audio accent -> haptic -> gravity resume` `[INFERRED]`.
- Avoid firing every layer at identical frame 0; offset by `0-40ms` so the event has contour `[INFERRED]`.

## 4. Hitstop / freeze-frame

### What is documented
- “Juice” and game-feel talks strongly endorse brief freezes/slowdowns as impact amplifiers, but public CC/RM materials do not publish exact frame counts for their board actions (overview sources: https://www.gdcvault.com/play/1016487/Juice-It-or-Lose and https://www.gdcvault.com/play/1022759/Game-Feel-Why-Your-Death). `[High confidence]`

### Glimbloom spec
- Normal tile match: `0-1` frame stop only; often none `[INFERRED]`.
- Primed word trigger / booster collision: `2-3` frames at 60fps (`33-50ms`) `[INFERRED]`.
- Meltdown FX / board-clearing event: `3-5` frames (`50-83ms`) or `0.10s` timescale dip to `0.2-0.35` instead of a hard stop `[INFERRED]`.
- Sugar Crush / level-clear equivalent: use chained micro-pauses, not one long freeze; `1-2` frames per auto-fire with no freeze on every single cascade `[INFERRED]`.
- Royal Match-style clear celebration should privilege tempo; if the player can feel “waiting for celebration,” the hitstop is too long `[INFERRED]`.

## 5. Anticipation and follow-through

### Before payoff
- Pre-explosion pulse/glow: `35-70ms`, usually `1.04-1.10` scale plus emissive or outline ramp `[INFERRED]`.
- Audio swell: short riser or filtered whoosh beginning `30-60ms` before impact `[INFERRED]`.
- Special pieces and high-value word states should advertise danger/reward continuously with idle shimmer every `1.2-2.0s`, but the shimmer must be low-amplitude so it does not compete with hints `[INFERRED]`.

### After payoff
- Follow-through should mostly be particles, debris arcs, and score travel; avoid a long elastic rebound on the board piece itself `[INFERRED]`.
- On touch devices, Apple’s guidance is to match haptic intensity/sharpness to the animation’s intensity/sharpness, not to run long meaningless buzzes (https://developer.apple.com/design/human-interface-guidelines/playing-haptics?changes=_7). `[High confidence]`

## 6. Scoring popup feel

### Spawn / hold / exit
- Spawn with `DOScale` + slight `DOMoveY`: start `0.70-0.85` scale and `8-18px` low, enter with `Ease.OutBack` or `Ease.OutCubic` over `0.10-0.16s` `[INFERRED]`.
- Hold `0.30-0.45s` for small scores, `0.45-0.65s` for word / combo scores `[INFERRED]`.
- Exit with both `fade + 18-32px rise` over `0.18-0.28s`; for big scores, end by flying to the total-score anchor over `0.22-0.36s` `[INFERRED]`.

### Rapid-fire behavior
- When many popups fire, merge by source or by `120-180ms` windows; do not spawn more than `2-3` fully independent score popups simultaneously in the same local area `[INFERRED]`.
- Escalate pitch per merge step by `+0.03 to +0.08` semitone-equivalent ratio per event, cap around `+0.30 to +0.45` total before reset `[INFERRED]`.

## 7. Sources searched

### Primary / near-primary
- Petri Purho + Martin Jonasson, `Juice It or Lose It` GDC Vault: https://www.gdcvault.com/play/1016487/Juice-It-or-Lose
- Steve Swink, `Game Feel`: https://redpantsgamedesign.com/game-feel
- GDC Vault, `Candy Crush Postmortem: Luck in the Right Places`: https://gdcvault.com/play/1019319/Candy-Crush-Postmortem-Luck-in
- GDC Vault, `Controls You Can Feel`: https://www.gdcvault.com/play/1015663/Controls-You-Can-Feel-Putting
- Apple HIG / Core Haptics docs: https://developer.apple.com/design/human-interface-guidelines/playing-haptics?changes=_7 and https://developer.apple.com/documentation/corehaptics

### Secondary but useful
- Naavik, `Royal Match: Dream Games’ Regal Performance`: https://naavik.co/deep-dives/royal-match/
- Deconstructor of Fun, `Royal Match - The New King from Turkey?`: https://www.deconstructoroffun.com/blog/2021/3/21/royal-match-the-new-king-from-turkey
- UserWise, `5 Simple UX Lessons From Royal Match`: https://blog.userwise.io/blog/5-simple-ux-lessons-from-royal-match
- Funovus, `Royal Match Dominates Match-3`: https://www.funovus.com/blogs/royal-match-dominates-match-3-what-can-all-designers-learn/
- The Experimentation Group, `Field Notes`: https://www.theexperimentation.group/our-work/field-notes
- Material motion duration/easing: https://m1.material.io/motion/duration-easing.html
- Candy Crush Sugar Crush help/wiki: https://candycrush.zendesk.com/hc/en-us/articles/360000750858-What-s-a-Sugar-Crush and https://candycrush.fandom.com/wiki/Sugar_Crush

### Search result note
- I searched for exact CC/RM frame timings in GDC talks, King materials, Dream/Royal Match interviews, YouTube teardowns, and public docs. Exact per-animation numbers were not publicly documented in a reliable way, so most implementation values above are tagged `[INFERRED]`. `[High confidence]`

## 8. Concrete tunable recommendations (Unity/DOTween specifics)

- Tile fall: `DOMoveY` with `Ease.Linear`; `0.10s` per cell baseline, clamp total fall to `0.10-0.28s` `[INFERRED]`.
- Land settle: `DOPunchScale(Vector3.one * 0.06, 0.08f, 1, 0.2f)` or `DOScale(1.04, 0.04).SetEase(Ease.OutQuad).From(0.98)` `[INFERRED]`.
- Select: `DOScale(1.06, 0.08).SetEase(Ease.OutQuad)` then yoyo pulse `1.00 <-> 1.04` over `0.65s` with `Ease.InOutSine` `[INFERRED]`.
- Match pop: sequence `0.04s` pre-squash with `Ease.InQuad`, `0.07s` pop with `Ease.OutBack`, `0.10s` fade/shrink tail with `Ease.InQuad` `[INFERRED]`.
- UI button press: `0.85` scale for `0.05-0.07s`, rebound to `1.00` in `0.10-0.14s` with `Ease.OutBack` `[INFERRED]`.
- Panel enter/exit: enter `0.22-0.28s` `Ease.OutCubic`; exit `0.18-0.22s` `Ease.InCubic` `[INFERRED]`, aligned with Material mobile timing ranges (https://m1.material.io/motion/duration-easing.html).
- Haptics: iOS intensity/sharpness are normalized `0-1` in Core Haptics (https://developer.apple.com/documentation/CoreHaptics/updating-continuous-and-transient-haptic-parameters-in-real-time); Android amplitude is `1-255` when amplitude control exists (https://developer.android.com/reference/android/os/VibrationEffect?hl=he). Map Glimbloom tiers to: micro `0.18-0.30` / `45-75`, standard `0.35-0.55` / `90-140`, big `0.60-0.85` / `150-220` `[INFERRED]`.
- Audio pitch: cascade escalation `+0.03 to +0.06` per chain, cap `+0.24 to +0.36`; reset on turn end `[INFERRED]`.
- Camera shake: small `0.06-0.12` units for `60-90ms`, medium `0.12-0.22` for `90-140ms`, big `0.22-0.40` for `120-180ms` `[INFERRED]`.
- Slow-mo/hitstop: none or `1` frame for common matches, `2-3` frames for big words, `3-5` frames for meltdown/board clear `[INFERRED]`.

## 9. Anti-patterns to avoid

- Floaty eased falling that makes gravity feel fake; this is the fastest route to “cheap mobile clone” feel `[INFERRED]`.
- Long celebration locks, especially after the result is obvious; RM’s advantage is that polish rarely blocks the next meaningful state for long (https://www.deconstructoroffun.com/blog/2021/3/21/royal-match-the-new-king-from-turkey). `[High confidence]`
- Uniform easing everywhere. Linear or near-linear travel for falling, asymmetric ease-out for impact/UI, and only sparse `OutBack` accents `[INFERRED]`.
- Too many independent score popups, particles, or haptics at once. Noise kills hierarchy `[INFERRED]`.
- Big haptics on low-value events. Save “thud” for word completion, booster collision, meltdown, and level clear `[INFERRED]`.
- Camera shake on every match. Reserve visible shake for `~top 20%` of payoffs `[INFERRED]`.

## 10. Confidence ranking summary

### High confidence
- Royal Match’s standout feel is speed, concurrent resolution, and low-friction polish rather than ornate animation latency: https://www.deconstructoroffun.com/blog/2021/3/21/royal-match-the-new-king-from-turkey ; https://www.theexperimentation.group/our-work/field-notes
- Public sources do not expose trustworthy exact CC/RM frame data for most board animations. Use tagged inference.
- Mobile UI transitions commonly cluster around `195-375ms` depending on enter/exit/scope: https://m1.material.io/motion/duration-easing.html
- iOS custom haptics use normalized intensity/sharpness `0-1`; Android amplitude is `1-255` when supported: https://developer.apple.com/documentation/CoreHaptics/updating-continuous-and-transient-haptic-parameters-in-real-time ; https://developer.android.com/reference/android/os/VibrationEffect?hl=he

### Inferred
- Tile fall should be mostly linear at `0.09-0.14s` per cell with tiny settle only.
- Small matches should use `4-6` feedback layers; big combos `6-8`.
- Hitstop should be minimal on routine matches and concentrated on word/booster/meltdown peaks.
- Score popups should enter in `0.10-0.16s`, hang `0.30-0.45s`, and merge aggressively during cascades.

### Speculative
- If Glimbloom wants a more “showy” identity than RM, you can push special-word overshoot scale to `1.30-1.36` and slowdown dips to `0.15-0.20` timescale for finales, but this likely trades away some RM-like throughput `[SPECULATIVE]`.
