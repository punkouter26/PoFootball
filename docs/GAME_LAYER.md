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

- `Systems_RandomSpotProvider` — training. Same seed source, same generator; five
  draws per episode since revision 14, which also chose the KIND of snap — about
  one in eleven a kickoff, one in sixteen a try.
- `Systems_GameFlowSystem` — a game. Returns wherever the chains left the ball.

`Systems_SpotProviderTests.RandomProvider_ReproducesTheOriginalInlineDraw`
reconstructs the original expression and asserts 250 consecutive draws still
match. If someone "improves" the sampling, that test fails — which is the point,
because the alternative failure mode is a silently different distribution of
starting states and an `.onnx` that no longer means what its MANIFEST says.

Nothing else in the simulation was touched. No constant in
`Systems_SimConstants`, no physics, no observation, no reward. (Revision 14
changed the draw itself, deliberately, with a new contract revision and a new
config — the only way it may change.)

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

Revision 14 (2026-10-15) made every snap of a game a played snap. The kicking
game, the try, returns and fumbles used to be resolved here, between snaps, with
dice; they are now plays the referee whistles, and this layer only puts the ball
where the play left it. Most of the rules below live in `Systems_Referee`,
`Systems_BallSystem` and `Systems_PenaltySystem` — the same in training and in a
game — and the game layer adds the chains, the score, the clock and the
enforcement.

| Rule | Behaviour |
|---|---|
| Downs | 4 downs, 10 yards, chains tracked against a first-down marker |
| Opening kickoff | A played kickoff by Away to Home. Halftime: whoever received to open the game kicks |
| Kickoff | **Played** (`Systems_PlayKind.Kickoff`). The kicking team is the offense for the snap and kicks from its own 35; the receivers field it and return toward −Y. Into the end zone, or rolled in untouched, is a touchback at the 35; out of bounds untouched, their 40. **Onside** when the scoring team is more than one score down inside the last minute: a short kick the kicking team may recover once it has gone ten yards |
| Touchdown | 6, then a **played try**: a kick from the 15 (`ExtraPoint`) or a snap from the 2 (`TwoPoint`). The late-game two-point chart picks which. A try is untimed; a defense that returns it scores 2 |
| Field goal | **Played**. The quarterback calls it before the snap on fourth down; the unit lines up, the ball is held 7 yards back, and it is good if it crosses the end line between the uprights and over the bar — distance and accuracy come from `Systems_ISkillModel.DrawKick`. A miss goes over at the spot of the kick, or the 20 if further out. It can be blocked |
| Punt | **Played**, called before the snap on fourth down. Punter 14 yards deep, gunners split wide, a returner deep. Fielded, fair-caught (automatic with a cover man within 5 m), muffed, blocked, or left to roll until the cover team downs it; into the end zone is a touchback at the 20 |
| Safety | 2 to the scoring side, and the conceding team free-kicks from its own 20. Either side can concede one — a returner downed in his own end zone after bringing the ball back in |
| Interception | **Returned**: the defender who catches it runs it back toward −Y, and can score. Caught in the end zone and downed there, a touchback |
| Fumble | The tackle can strip the ball (`Systems_IFumbleModel`); the ball is **loose** and either side may fall on it and run. Out of bounds it stays with the side that had it; out the back of an end zone it is a touchback or a safety by who put it there |
| Pre-snap | `PRESNAP_TICKS` with the bodies set. The quarterback may call a fourth-down kick; the middle linebacker calls the defense's front, which lines up the tick it is called (revision 15). A false start or an offside is a drawn flinch — weighted by each lineman's discipline trait — refereed like any other foul |
| Players | Every player has his own speed, mass, turn rate and discipline (`Systems_Roster`): fixed for a played game, redrawn every play in training, observed by the brains |
| Lateral | Any carrier may pitch to a team-mate level with or behind him. Dropped, it is a live ball |
| Forward pass | One, from behind the line of scrimmage — the mask makes an illegal one impossible. A pass aimed at nobody is a **throwaway**: legal from outside the tackle box if it reaches the line, otherwise **intentional grounding** |
| Sack | A quarterback brought down behind the line on a pass he never threw is a `Sack` outcome |
| Forward progress | A carrier in contact who stops gaining ground is down where his progress stopped. One standing still in a crowd is down; one standing still in space has given himself up (`Downed`) |
| Stopped in a crowd | A carrier with an opponent within 3 m who gains under 0.3 m in `STALL_TICKS` is down at the furthest point he reached, touched or not — the scrum driven back through his own blockers (`Systems_Referee.TrackCrowd`, 2026-10-16) |
| Penalties | Detected from the bodies and called with an official's chance (`Systems_PenaltySystem`): false start, offside and delay of game before the snap; holding both ways, pass interference both ways, roughing the passer, facemask, block in the back, kick-catch interference, intentional grounding. The offended team takes the better of the play and the penalty by expected points; half-the-distance applies; fouls on both sides offset. In training a flag costs reward and moves nothing |
| Clock | Live during a play; a **2.5 s** huddle is charged at the whistle only when the clock kept running, against a **120 s** quarter — about seventy-five snaps a game. The dead ball between plays is 1.2 s (`DEAD_BALL_TICKS` 60). **Out of bounds stops it only inside the last 2:00 of the first half and the last 5:00 of the second**, scaled by `CLOCK_SCALE` (120/900) to 16 s and 40 s. A **two-minute warning** at 16 s of the second and fourth quarters. A flag stops it |
| Clock plays | The team ahead late **kneels** when its downs can run out the clock the other side cannot stop; a team without timeouts and with the clock running late **spikes** it |
| Timeouts | 3 a half, 2 in overtime, spent inside the last minute by whichever side the clock is hurting |
| Quarter end | The down finishes first; a touchdown at 0:00 still gets its try |
| Overtime | **The NFL rule** (`OVERTIME_SECONDS`, 80 s — the NFL's ten minutes on this clock). Both teams are owed a possession, a first-possession touchdown included; a score by the side without the ball ends it; once both have had it the next score wins; level at the end is a tie |
| Catching | A pass may be caught **behind** the line, so screens and checkdowns exist. Both lines remain ineligible |

Clock and spotting constants live in `Systems_GameRules`. Everything that decides
how a kick flies, how often a ball is dropped or a flag is thrown, lives in
`Systems_SimConstants`: it is part of the dynamics a brain is fitted against.

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
- **The coach is scripted.** Kneel-downs, spikes, onside kicks, two-point tries and
  timeouts are rules in `Systems_GameFlowSystem`, not policies: training has no
  clock or score for a brain to learn them against.
- **Before the snap the bodies are frozen.** A false start or an offside is a
  drawn flinch (`Systems_IDisciplineModel`), refereed physically — never a policy's
  doing. See Systems_SimConstants.PRESNAP_TICKS for why.

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
