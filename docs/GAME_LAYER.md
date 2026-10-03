# The game layer

Everything here sits *on top of* the simulation. It turns the endless stream of
single plays the trainer needs into a game a person can watch: downs, a
scoreboard, a clock, a box score, and a front end to start it from.

The whole layer is opt-in. `Systems_GameLifetimeScope` carries a `Systems_SimMode`
field, and in `Training` none of it is in the container.

---

## Running it

| I want to… | Do this |
|---|---|
| Play/watch a game | Open `Assets/Scenes/SCN_MENU.unity`, press Play, tap **PLAY** |
| Jump straight to a game | Open `Assets/Scenes/SCN_GAME.unity`, press Play |
| Train | Open `Assets/Scenes/SCN_TRAIN_FOOTBALL.unity` — unchanged, still `Training` |

Build order is `SCN_MENU` (0), `SCN_GAME` (1), `SCN_TRAIN_FOOTBALL` (2), so a
player build opens on the front end.

---

## The one rule this layer must not break

**A brain trained before the game layer existed must see identical dynamics
after it.**

`Systems_EpisodeDirector` used to draw the line of scrimmage inline from a
seeded `Unity.Mathematics.Random`. That draw now sits behind
`Systems_ISpotProvider`:

- `Systems_RandomSpotProvider` — training. Same seed source, same generator, same
  single `NextFloat` per episode against the same bounds.
- `Systems_GameFlowSystem` — a game. Returns wherever the chains left the ball.

`Systems_SpotProviderTests.RandomProvider_ReproducesTheOriginalInlineDraw`
reconstructs the original expression and asserts 250 consecutive draws still
match. If someone "improves" the sampling, that test fails — which is the point,
because the alternative failure mode is a silently different distribution of
starting states and an `.onnx` that no longer means what its MANIFEST says.

Nothing else in the simulation was touched. No constant in
`Systems_SimConstants`, no physics, no observation, no reward.

---

## How two teams share one offense

There are 22 bodies: eleven offensive, eleven defensive, with fixed roles. Every
policy was fitted on **"the offense attacks +Y"**.

A real game needs both teams to drive. Rather than teach the defense to play
offense — which would put every drive outside the distribution the brains were
trained on — the world stays fixed and **field position is mirrored through
`y → -y` on every change of possession**.

> Tackled on your own 29 → the ball is at Y = −19.2 m. The other team takes over
> at the same physical spot, which in *its* attacking frame is its own 71 — the
> opponent's 29. Exactly right, and no policy sees anything unfamiliar.

So `Systems_TeamId` (Home/Away) is a property of a *possession*, while
`Systems_TeamSide` (Offense/Defense) is a property of a *body*. They are
different axes and both are needed.

The consequence worth knowing: team totals in the box score split by jersey, but
**player stat lines do not** — slot 8's line is that quarterback's production
across the whole game, for both teams. It is a self-play scrimmage scored as a
game.

`Systems_GameFlowTests.Mirroring_IsItsOwnInverse` pins the reflection.

---

## Rules implemented

| Rule | Behaviour |
|---|---|
| Downs | 4 downs, 10 yards, chains tracked against a first-down marker |
| Touchdown | 7 — six plus an **awarded** try (there is no kicking model, and a random one would put noise in the score no policy can influence) |
| Field goal | **Distance-weighted odds in a game, a hard cliff at 55 yards in training** — see `Systems_IKickModel`. The seam is the one `Systems_ISpotProvider` already established: training keeps the pure function every policy was fitted against, a played game gets the version worth watching |
| Punt | Net yards drawn around 40 in a game, flat 40 in training — same seam, same reason |
| Safety | 2 to the defense, recognised at the rules layer purely from where the ball stopped. The physics layer still has no concept of one. Takes precedence over a strip: a ball loose in your own end zone is a rule this sim has no model for |
| Interception / turnover on downs | Possession flips, spot mirrored |
| Incompletion | Ball returns to the previous spot, clock stops |
| Clock | Ticks live during a play; a **12 s** huddle is charged at the whistle only when the clock kept running. Was 25 s, which capped a quarter at eleven or twelve snaps and made a whole game 45 plays against an NFL game's ~130 |
| Quarter end | The down finishes first — expiry is remembered and acted on at the whistle |
| Halftime | Ball to whoever did not receive the opening kickoff, own 35 (`KICKOFF_TOUCHBACK_YARD_LINE`). `Systems_GamePhase.Halftime` is a real state: the clock does not run across the interval, and the first snap of the second half clears it |
| Overtime | **Sudden death, one period** (`OVERTIME_SECONDS`, 200 s — regulation's 300 scaled the way a quarter is). The first score of any kind wins; a period expiring still level is a tie, as in the NFL regular season. The real possession-owed rule is not modelled |
| Kickoff | Resolved at the rules layer like a punt, not simulated. Mostly a touchback at the 35; the rest is a return drawn around it. **Onside** when the scoring team is still more than one score down inside the last minute of the fourth — recovery `ONSIDE_RECOVERY_CHANCE`, and a miss hands over the ball at the kicking team's own 45 |
| Fumble | A tackle can strip the ball — `Systems_IFumbleModel`, same training/game seam as the kick model. Only LOST fumbles are modelled: one the offense recovers is indistinguishable from a tackle here. Chance scales with the hit, the number of tacklers and the carrier's role |
| Forward pass | Must be thrown from behind the line of scrimmage. Past it the quarterback has tucked it and is a runner — there is no penalty system, so the throw is simply unavailable |
| Catching | A pass may be caught **behind** the line, so screens and checkdowns exist. Both lines remain ineligible, which is what the old blanket line-of-scrimmage gate was really guarding |

Clock and scoring constants live in `Systems_GameRules`, deliberately separate
from `Systems_SimConstants`. Nothing in `Systems_GameRules` can change what an
`.onnx` was fitted against, so it is safe to tune between builds.

---

## Is it actually football?

`Systems_GameFlowSystem` logs a verdict at every final whistle:

```
[PoFootball] REALISM  yards/play 6.24 | 4th downs faced 10 | TD/drive 0.36 | scrimmage plays 71
```

Real football runs about **5.5 yards a play**, faces a fourth down on roughly **one
series in three**, and scores a touchdown on about **one drive in five**.

This layer was measured against those numbers and the simulation underneath it was
changed until it met them. Before that work (contract revision 7) a complete game
read:

```
FINAL 21-28 after 45 plays, 9 drives. Punts 0, FG 0/0, safeties 0, turnovers on downs 0.
REALISM  yards/play 11.20 | 4th downs faced 1 | TD/drive 0.82
```

Eleven yards a play and one fourth down in an entire game — which meant every
kicking rule in this document was unreachable code. Every branch of `Resolve` now
executes in an ordinary game, and finals read like 19-35, 14-24, 23-21.

### The tackling work, and where it got to

An audit re-measured the shipped build at **9.31 yards a play** across four games
(7.71 / 8.24 / 10.00 / 11.30) with a touchdown on 0.47 of drives — so the claim
above had drifted, and the single game it rested on was inside the noise.

Two rules were the cause, both in `Systems_Referee.ReportSustainedContact`:

- **A second tackler counted for nothing.** Every defender in contact called in on
  the same tick and the tick guard collapsed them into one, so three men wrapping a
  back up was worth exactly as much as one.
- **Contact had to be strictly consecutive.** Two discs colliding push each other
  apart, so a defender who landed a hit bounced off, missed a tick, and the count
  reset — the carrier shrugged off a tackle that had been made.

Both are fixed. The structural results are unambiguous: plays ending on the tick
cap fell from ~42% of scrimmage plays to ~13%, drive counts came back to the high
teens, and punts, safeties, fumbles and field goals all now occur in an ordinary
game where the previous build produced **zero punts and zero safeties** across a
whole one.

**The yardage did not converge, and this is an open problem.** Single games at
`SUSTAINED_TACKLE_TICKS` of 8, 10 and 12 measured 7.10, 4.44 and 8.74 — a
NON-MONOTONIC ordering, which means game-to-game variance is larger than the
effect being tuned. The constant is left at 10 because that setting produced the
soundest structure, not because its yardage was verified. Anyone picking this up
should measure **means over several games per setting**, never single games; the
spread on one fixed config has been observed from 4.44 to 11.30.

The changes were in the simulation, not here: `Agent_ActionContract` revision 8
lists them. Nothing in `Systems_GameRules` can change what an `.onnx` was fitted
against, which is why `HUDDLE_SECONDS` could be halved without touching the stamp.

---

## Known limitations

- **Out-of-distribution field position.** Training draws the line of scrimmage
  from the own 10 to the opponent's 40. A game can snap from anywhere, so goal-line
  and backed-up situations are outside what the current brains ever saw. Expect
  weaker play there until a curriculum covers it.
- **The sim keeps running after the final whistle.** `Systems_GameFlowSystem`
  ignores further plays once the game is `Final`, and the HUD covers the field
  with the final-score overlay. Freezing the simulation would mean a view reaching
  into the episode director, which the dependency direction forbids.
- **No jersey numbers.** Position is encoded by shape and team by tint. At the
  zoom a 53-yard-wide field needs in portrait, a number would be about four pixels
  tall — unreadable, and 22 more draw calls.
- **Audio is synthesised, not recorded.** See below.

---

## Audio without assets

`docs/ASSETS.md` is explicit that the only art dependency is the generated PNGs,
and that Asset Store audio cannot be fetched without a signed-in human. Shipping
silence until someone does that is worse than making our own noise, so
`Systems_ToneBank` builds every clip from raw samples at load: a two-tone whistle
with a warble, filtered noise for pad pops, triangle-wave cues, and a looping
crowd bed that swells with a running "excitement" value.

If real recordings are imported later, `Systems_AudioView` takes serialized
overrides per clip and the synthesised versions become the fallback.

---

## Files

```
Model/   Systems_GameModel, Systems_GameRules, Systems_GamePhase, Systems_TeamId,
         Systems_DownResult, Systems_SimMode,
         Systems_BoxScore, Systems_TeamStatLine

Systems/ Systems_GameFlowSystem      downs, chains, score, clock  (+ ISpotProvider)
         Systems_ISpotProvider       the training/game seam (+ HasNextPlay)
         Systems_RandomSpotProvider  the original seeded draw
         Systems_StatsSystem         team totals
         Systems_MenuLifetimeScope   front-end composition root

View/    Systems_ScreenView          UIDocument + PanelSettings plumbing
         Systems_HudView             scoreboard, banner, final overlay + team totals
         Systems_MenuView            front end
         Systems_UiTheme             all styling, in C# — no .uss, no .uxml
         Systems_DisplayText         "3rd & 7", "OWN 34", "4:37"
         Systems_SceneRouter         menu ↔ game
         Systems_RoleShapeApplier    shape per position, tint per side
         Systems_RoleShapeSet        the shape/colour asset
         Systems_AudioView           crowd bed, whistle, impacts, result cues
         Systems_ToneBank            procedural clips
```

---

## The loop closes

`SCN_MENU` → PLAY → `SCN_GAME` → the fourth quarter expires →
`Systems_GameFlowSystem` sets `Systems_GamePhase.Final`, publishes
`Systems_GameOverMessage`, and answers `HasNextPlay` with false from then on.

That last part is what actually stops the game. `Systems_EpisodeDirector` asks
before every snap; a false answer means it stops re-forming the teams and never
ticks again, so the bodies stay where the final whistle left them. Without it the
simulation kept playing downs forever behind the FINAL overlay.

From the overlay, REMATCH reloads `SCN_GAME` and MENU returns to `SCN_MENU` —
both through `Systems_SceneRouter`, so a second game is built by exactly the same
path as the first rather than by resetting each model by hand.

**Nothing is persisted between games.** A career record used to be written to
`Application.persistentDataPath/career.json`; it was removed along with the rest
of the save layer, because a lifetime record for a game with no season, no roster
and no progression was read by one label on the menu and nothing else.
