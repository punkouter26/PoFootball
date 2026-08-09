# Assets

## Ground rule: nothing here is load-bearing

The game renders from eight generated PNGs in `Assets/Art/Shapes/` (circle,
square, triangle, diamond, pentagon, hexagon, ball, pixel — white, 256 px,
imported at 256 px/unit so **one sprite = one metre**). Position group is encoded
by *shape*, team by *tint*. That is the whole art dependency, and it is
checked in.

Everything below is optional polish. Keep it that way: put third-party
imports under `Assets/ThirdParty/<Vendor>/` and reach them through your own
`Systems_*` wrapper, never from `Agent_*`, `Sensor_*`, or `Reward_*`. If a pack
is deleted, the sim must still train and play — only the presentation should
get plainer.

---

## Unity Registry packages (installed)

These came from the Unity registry via `package_resolve`, so they are in
`Packages/manifest.json` and restore on clone — no account, no manual download.

| Package | Version | Why it earns its place here |
|---|---|---|
| `com.unity.ml-agents` | 4.1.0 | The trainer bridge. Comms API 1.5.0 |
| `com.unity.ai.inference` | 2.6.1 | Runs the promoted `.onnx` at play time (pulled in by ML-Agents) |
| `com.unity.behavior` | 1.0.16 | Behaviour graphs for the scripted baseline opponent you rate the learned brain against |
| `com.unity.splines` | 2.9.0 | Receiver routes and play art as real curves rather than hard-coded waypoints |
| `com.unity.cinemachine` | 3.1.7 | Broadcast-style camera that follows the ball inside the portrait frame |
| `com.unity.burst` | 1.8.30 | Compiles the per-tick hot paths — matters at `time_scale: 20` × 6 envs |
| `com.unity.mathematics` | 1.4.0 | SIMD math for sensor work |
| `com.unity.collections` | 6.5.0 | Native containers so observation building does not allocate every FixedUpdate |
| `com.unity.recorder` | 5.1.7 | Capture a play as video to show what a brain actually learned |
| `com.unity.memoryprofiler` | 1.1.12 | 6–8 headless envs on one box is where leaks show up |
| `com.unity.performance.profile-analyzer` | 1.4.0 | Compare frame cost between two training runs |
| `com.unity.test-framework.performance` | 3.5.0 | Perf regression tests — catches a sensor that got 3× slower |
| `com.unity.nuget.newtonsoft-json` | 3.2.2 | JSON bodies for the HTTP telemetry side of the MLOps rule |
| `com.unity.serialization` | 6.5.0 | Fast persistence for season/roster state |

> Requested versions in `manifest.json` for `collections`, `serialization` and
> `mathematics` were raised by UPM's dependency resolution (2.6.8 → 6.5.0,
> 3.1.5 → 6.5.0, 1.3.3 → 1.4.0). The table shows what is actually resolved —
> `Packages/packages-lock.json` is the source of truth.
| `com.unity.addressables` | 4.0.1 | Load brains and team configs by address instead of hard references |

---

## Asset Store: how downloading actually works

**I could not download these for you, and no tool can.** The Asset Store has no
public API or CLI — not `unity`, not `unity-mcp-cli`. A package only becomes
downloadable after *your account* has claimed it, which is a signed-in click on
the web store. There is also nothing cached locally to import: I checked
`%APPDATA%\Unity\Asset Store-5.x` and it does not exist, so this machine has
never downloaded an Asset Store package.

The three steps, per asset:

1. Open the link below **signed in** and press **Add to My Assets** (free = no payment step).
2. In Unity: **Window ▸ Package Manager ▸ My Assets**.
3. Select it ▸ **Download** ▸ **Import**. Import into `Assets/ThirdParty/<Vendor>/`.

Once step 1 is done for all of them, I can drive steps 2–3 from here.

## Top 10 free picks for *this* game

Chosen for a 2D shape-based football sim with a portrait UI and long headless
training runs — not "popular free assets" in general.

| # | Asset | What it's for here |
|---|---|---|
| 1 | [PrimeTween](https://assetstore.unity.com/packages/tools/animation/primetween-high-performance-animations-and-sequences-252960) | Zero-allocation tweens for score flashes, snap anticipation, UI transitions. Allocation-free matters when the sim runs at `time_scale: 20` |
| 2 | [DOTween (HOTween v2)](https://assetstore.unity.com/packages/tools/animation/dotween-hotween-v2-27676) | The long-established alternative to #1. **Pick one, not both** — they solve the same problem |
| 3 | [In-game Debug Console](https://assetstore.unity.com/packages/tools/gui/in-game-debug-console-68068) | Read exceptions inside a headless/dev env build where there is no Editor console. Directly useful when an env dies mid-run |
| 4 | [Runtime Inspector & Hierarchy](https://assetstore.unity.com/packages/tools/gui/runtime-inspector-hierarchy-111349) | Inspect a live agent's components in a player build — see what the brain is actually doing to the Rigidbody2D |
| 5 | [Free Crowd Cheering Sounds](https://assetstore.unity.com/packages/audio/sound-fx/free-crowd-cheering-sounds-225494) | Stadium ambience and reaction stings. The single biggest "this is a football game" cue for shapes on a green field |
| 6 | [FREE Casual Game SFX Pack](https://assetstore.unity.com/packages/audio/sound-fx/free-casual-game-sfx-pack-54116) | Whistle, snap, tackle, down-marker blips |
| 7 | [Free Sound Effects Pack](https://assetstore.unity.com/packages/audio/sound-fx/free-sound-effects-pack-155776) | General UI clicks and menu sounds |
| 8 | [Particle Pack](https://assetstore.unity.com/packages/vfx/particles/particle-pack-127325) (Unity Technologies) | Impact puffs on tackles, touchdown bursts |
| 9 | [Free 2D Mega Pack](https://assetstore.unity.com/packages/2d/free-2d-mega-pack-177430) (Brackeys) | 2D UI bits and FX sprites to lift the HUD past flat rectangles |
| 10 | [2D Football Pack](https://assetstore.unity.com/packages/2d/characters/2d-football-pack-18679) (looneybits) | On-theme 2D character art if you ever want to swap shapes for figures. **Price not verified** — check the listing before assuming free |

Verify the price on each listing before importing; free/paid status changes
over time and #10 in particular I could not confirm.

### Deliberately not recommended

Big 3D packs, terrain tools, and stadium-crowd *mesh* generators. This is a flat
2D game — they add gigabytes and nothing on screen.

---

## Blender MCP

Not set up: it is for authoring 3D meshes, and this game is 2D shapes rendered
by `SpriteRenderer`. There is no GLB in this pipeline for it to produce. If the
game ever grows a 3D element (a stadium backdrop, a 3D trophy screen), that is
the point to add it — not before.
