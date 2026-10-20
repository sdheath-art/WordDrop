# WordDrop — what we're building and why

Companion to `HANDOFF_2026_10_20.md`. That file is the technical state and the traps.
This one is the intent: what the game is, where it's going, and what the current work is for.

---

## The game

A mobile word game, built in Unity, aimed squarely at the Royal Match / Candy Crush shelf —
candy-bright, chunky, juicy. Spencer is an artist-developer; the art direction is his and it
is held to a high standard. It is being built to ship.

**The core loop, as of the September pivot:**

> Swap two touching tiles — or swipe across a word you spot — and that word **stays lit on
> the board** instead of vanishing. Lit words that touch form a connected **web**. Tap a lit
> word to **detonate** the whole web. Each word in the chain is worth more than the last.
> Rows push up from the bottom as you play; reach the top and the run ends.

Two things make that loop work, and both are recent decisions worth understanding:

**Words stay armed rather than clearing.** This is the whole pivot. A normal word game
resolves a word the instant you make it, so there is no decision left to make. Here, forming
a word is a *commitment* — it sits on the board taking up space, it can be destroyed by a
rising row, and it's worth more the bigger the web gets. The question "detonate now or arm
one more?" is the game.

**Points come only from detonating** (changed 2026-10-20). Until today, forming a word also
paid. That meant an armed word was already banked, so a rise eating your web cost you nothing
you *held* — only something you might have had. Foregone gain is a much weaker pull than
loss, which is why the web never felt at risk. Payment now happens exclusively on detonation.
The multiplier was retuned (1.5 → 2.0) so a single word fired immediately pays exactly what
it used to; multi-word webs pay more, because the part that used to be flat now rides the
chain multiplier. Playing well got better; spamming three-letter words did not.

---

## The shape of the product

**Procedural escalating runs, not an authored campaign.** Locked in June, boss-directed.
A small team cannot sustain a level treadmill — ship 30, players clear them in a week, and
you are stuck forever. So: a run is a sequence of *generated* levels with an escalating
difficulty profile, the player replays from level 1, and every run varies.

- Levels clear on **score**, not objectives (`ScoreRunMode`). Targets start at 1,200 and
  grow 1.15× per level.
- `LevelRules.cs` holds a 15-row table of per-level rule changes — ice counts, a 4-letter
  minimum, branch priming, burning fuses, gold tiles, rise cadence. One new mechanic at a
  time, with relief levels between the hard ones.
- Replay hooks are **high score** and **furthest level reached**, plus a **daily seed**
  (one shared puzzle a day, Balatro's retention trick). Daily runs are fair — same seed,
  fixed loadout, no purchases or ad advantages.

**No level map.** This is the June lock, and it was drifted away from. A node map promises
hand-authored levels and a finite journey, and this game has neither — it's UI that lies
about what the product is. Royal Match can afford a map because the renovation meta *is*
their game; ours is the score. The home screen is a background, a big **Level N** button,
hearts, coins and settings. Press the button, get the level card, play.

---

## Where we are right now

The core loop works. What's being rebuilt is everything *around* it — the shell was built
for the old drop game and has been getting replaced piece by piece. That's why things keep
breaking: new core, old scaffolding, and the seams are where the bugs live.

**Done today:**
- Scoring moved to detonation-only (above).
- Four board/animation bugs found in the live log and fixed — including the long-standing
  "a board was already there and another animated over the top", which turned out to be two
  different systems seeding the board one frame apart.
- The "ice disappears on a row rise" bug root-caused properly after two earlier patches at
  the wrong layer. Three chained defects; the deciding one was that the reconcile pass
  compared *flags* when the fault was in *pixels*.
- The level card stopped showing an icon and the level's internal name ("Warm Up") and now
  shows the points target plus the rules that actually differ on this level — with the core
  loop explained on levels 1–3 for someone who has never played.
- The level map was replaced with the home screen, and its big green Play button was built
  against a reference Spencer supplied, with the geometry solved numerically rather than
  eyeballed.

**The immediate next thing** is a **pending/armed score readout on the HUD**. With scoring
now detonation-only, the opening of every level scores nothing until the first blast, which
reads as broken. A number that climbs as you arm and slams into the score when you detonate
is what makes the mechanic legible — and `RulesEngine.PreviewGroupPayout` already computes
it, so this is wiring, not design.

---

## The meta-game question (live, half-decided)

Spencer raised this twice and was right both times: an endless scoring game needs a reason
for runs to accumulate. The decision reached:

**Build a systems meta, not a content meta.**

- A *content* meta — Royal Match's renovation, Candy Crush's map — rewards you with new
  authored content. It needs someone shipping rooms forever. That is the same treadmill that
  killed the 100-level campaign, so rejecting it here is the same decision for the same
  reason.
- A *systems* meta — Balatro's jokers, Slay the Spire's unlocks — rewards you with a new
  **rule** that changes how future runs play. Written once, generates its own variety.

The good news: most of it already exists. `RunBuffs` is a working roguelite perk system —
six stacking perks (RichVeins, DeepChain, BiggerBlast, ShortFuse, SlowTide, GoldenTouch) with
titles, descriptions and a pick-one modal, granted on stage clear. One line keeps it from
being a meta:

```
SurvivalManager.cs:1579   RunBuffs.ResetRun();   // buffs are RUN-scoped — never carry between runs
```

The plan is to spend coins between runs to unlock buffs permanently. It compounds with the
score rather than competing with it — every unlock makes a higher score reachable, so the
high score stays the thing you chase and the meta is how you get there. **Not scoped yet.**

---

## Open questions waiting on Spencer

1. **Should a gold cluster PAY gold, or just look gold?** A connected web now renders gold if
   any word in it is gold. That is visual only. Making it pay 2× across the whole web would
   roughly double a four-word web off a single J or Z — strongest play in the game, and the
   level targets would need rechecking. Unanswered.
2. **Scope the meta-game** — what persists, what it costs, what the unlock screen looks like.
3. **The name.** "Lexora" is taken by multiple word games. Worth settling before more art is
   commissioned against it.

---

## How Spencer works, and what he expects

- **Greyscale-first art.** He builds value, then applies colour by gradient map per material.
  Measure references and give him value numbers, never adjectives. Never suggest skipping the
  greyscale stage.
- **Measure, don't eyeball.** This applies to code as much as art. When he says something
  "looks weird", the answer is usually a number that was guessed.
- **He will tell you when a decision is made.** If he repeats or reaffirms something after
  you have raised a concern, that is his call — say so once and build it.
- **Think like a game designer.** His words: *"some of these things you should just naturally
  know how to structure it so that i dont need to tell you to fix these things after."*
  Scoreboards resetting between levels, input locking after a win — do not wait to be asked.
- **Don't delete things.** *"you will end up deleting things we are going to lose im sure."*
  The level map still exists, switched off, for exactly this reason.
- **Polish is shipping work**, and quality beats speed (locked 2026-08-04). Multi-pass
  iteration is legitimate; do not nudge toward shipping.
