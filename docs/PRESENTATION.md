# The presentation layer

Everything in `PoFootball.Views` that exists to be looked at or listened to, and
the reasons each piece is shaped the way it is. Written after the graphics and
audio pass of 2026-09-08.

The one rule that governs all of it: **`Systems_PresentationBudget` gates
construction, not playback.** Every view below asks it before it builds anything,
and returns early — not "builds it and stays quiet". The cost being avoided is the
setup, because a training sweep runs 4–8 copies of this scene at `time_scale` 20
and none of them have a display or an audio device.

---

## Lighting and shadows — `Systems_StadiumRigView`

`SCN_GAME` shipped with a single `Global Light 2D` at intensity 1 in white, and
zero `ShadowCaster2D` anywhere in any of the three scenes. Both shaders had been
written for a rig that did not exist: `PoFootball/Turf` and `PoFootball/Player`
each declare the full URP 2D three-pass set and resolve through
`CombinedShapeLightShared`, and the turf shader's header describes itself as "the
surface every 2D shadow lands on" — which had never once happened.

The rig is a dimmed cool global plus four warm point banks outside the sidelines,
of which **two cast** and two are fill.

**The ambient-to-bank ratio is the whole tuning, and it is not intuitive.** The
first attempt used ambient 0.88, which produced a perfectly legible field with no
visible shadows at all. A 2D shadow can only subtract the light a *bank*
contributed; against a bright ambient that contribution is a small fraction of the
total, so the shadow has nothing to remove. Ambient has to sit **below** the
casting banks. Current values were measured against play-mode captures, not
reasoned about:

| | value |
|---|---|
| ambient (global) | 0.62 |
| casting banks | 1.25 |
| fill banks | 0.65 |
| shadow intensity | 0.82 |

**Shadow shape is the sprite's bounding box, not its silhouette.**
`ShadowCaster2D.shadowCastingSource` and its shape provider are both `internal` to
the URP assembly, and the auto-detection in `ShadowCaster2D.Awake` is wrapped in
`#if UNITY_EDITOR` — so a **player build** always falls through to `ShapeEditor`
and builds a quad from `Renderer.bounds`. At a camera forty metres up the
difference is sub-pixel. Do not reflect into the private field to "fix" this.

---

## Post-processing — `Systems_PostProcessView`

Every ingredient existed and nothing turned it on: `Renderer2D.asset` carried a
`PostProcessData` reference, `UniversalRP.asset` had `SupportsHDR`, and
`DefaultVolumeProfile.asset` was on disk. No scene had a `Volume`.

Built at runtime rather than authored, because an asset-based volume in the scene
cannot be gated by the presentation budget. ACES tonemapping, bloom, vignette and
a light grade. `UniversalRP.colorGradingMode` was moved to `HighDynamicRange` to
match the tonemapper.

**Bloom threshold is 1.35, and 1.05 was wrong.** The design intent is that bloom is
a channel for information the simulation already computes — the carrier rim and the
impact flash that `PoFootball/Player` authors above 1.0. At 1.05 the saturated team
colours crossed the threshold on their own once a casting bank was over them, so
all twenty-two players carried a halo and the carrier stopped being
distinguishable. Verified against captures at both values.

Depth of field and motion blur are deliberately absent: the 2D renderer produces
neither a depth buffer nor motion vectors, so they would be silently inert.

---

## Diagnostics — `Systems_PerformanceOverlayView`

There was no runtime performance readout of any kind. Graphy is in the manifest but
is UGUI, which CLAUDE.md §3 forbids.

Reports **percentiles, not an average** — a mean hides the allocation spike on a
play boundary, which is the failure this project is most likely to have. p99 is the
number to read. Also reports mono heap delta per second, draw calls / SetPass /
tris (Editor only — `UnityEditor.UnityStats` has no runtime equivalent), and the
fixed timestep, which turns red if it is ever not 0.02.

Starts collapsed as a frame-time pill, bottom-left; tap to expand. The per-frame
path writes one float into a pre-allocated ring — no allocation, no string work.

---

## Audio — `Systems_AudioView`, `Systems_BroadcastMicView`, `Systems_AudioSettings`

**The listener was the bug, not spatialisation.** `Systems_AudioView` used to build
every voice at `spatialBlend = 0` with a manual stereo pan, and its comment
explained that real 3D was pointless because "the listener is a camera forty metres
up". Correct diagnosis, wrong conclusion — Unity puts an `AudioListener` on the
main camera by default and nothing moved it, so the microphone sat at a point
roughly equidistant from all twenty-two players at all times.

`Systems_BroadcastMicView` takes over the listener and puts it **nine metres above
the ball**, tracking it with damping. Distance now means something. The height is
the tuning: at zero the mic sits in the pile and a nearby tackle is ~10× a distant
one, which reads as a mixing fault; the height puts a floor under every distance
and brings the ratio across the field closer to 3:1.

Field sounds (the hit, the whistle) are 3D with logarithmic rolloff. Result cues
are 2D — a touchdown fanfare is a broadcast stinger and does not happen anywhere.
The crowd bed ducks under every cue, scaled by that cue's own volume.

**There is no `AudioMixer` asset, and that is a limitation, not a choice.**
`AudioMixer` cannot be created by script — there is no public constructor and no
`AssetDatabase`-friendly API, only the Assets/Create menu. The bus routing and duck
envelope are therefore in code. If a mixer is ever created by hand, the levels in
`Systems_AudioSettings` are what should drive its exposed parameters.

The two serialized volume floats are gone. They lived in `Systems_AudioView` **and**
in `SCN_GAME`, and the note above them recorded the consequence: changing one
without the other "would have looked right in the diff and changed nothing you can
hear". They are now `Systems_AudioSettings`, PlayerPrefs-backed, defaults preserved
exactly so the 80% cut of 2026-08-22 survives.

---

## UI — `Systems_UiTheme`, `Systems_ScreenView`, `Systems_HudView`

**Two fonts shipped with the project and neither was used.**
`BebasNeue-Regular.ttf` and `Oswald-Variable.ttf` were in `Assets/Art/Fonts`,
`PanelSettings.textSettings` was unset, and every label rendered in the UI Toolkit
default face. They now live in `Assets/Resources/Fonts` (moved with their `.meta`
files, so GUIDs are intact) and are applied through the theme's factories, so every
existing call site picked them up with no edit. Bebas is the display face
(numerals, headlines, buttons, wordmark); Oswald carries everything read rather
than glanced at.

**Elevation is borders, not shadows.** The runtime panel implements no box-shadow,
and this project ships no USS. `ApplyElevation` puts a light hairline on the top
edge and a dark line on the bottom — on a near-black palette that is actually the
stronger cue, because a black shadow on a black surface is invisible.

**The clock has a fixed box.** Its text steps between four and five glyphs
("9:58" → "10:02") and the digits are not the same width, so the row re-flowed
every second and the pill beside it twitched. UI Toolkit exposes no tabular-figure
feature, so the label reserves the widest case instead.

**No two-minute clock colour, deliberately.** It was written and removed: amber is
`Accent`, which the theme reserves for the chains "so it always means the line",
and the palette note is explicit that every other hue is spoken for. There is no
free colour for urgency, and spending the chains' one is how the accent came to
mean four things last time.

UI tap sounds are wired in `Systems_ScreenView`, not in the theme's `Button`
factory — the theme is static with no scene presence, so a click handler there
would need a singleton or a service locator, both banned by
`.claude/rules/architecture.md`. The base class already owns a GameObject, so it
owns one `AudioSource` and listens for `ClickEvent` on the way **down**
(`TrickleDown`), because a button that calls `StopPropagation` would otherwise
silence itself.

---

## Turf VFX — `Systems_TurfScuffView`, `Systems_FieldRenderer`

Turf sprays from the ball carrier's cuts, sized by the component of his
acceleration **perpendicular to his own velocity**. Accelerating in a straight line
is not a cut; including it would make the effect fire hardest at the snap.

**It watches one player, not twenty-two.** `Systems_AudioView`'s scope note records
that its deleted cleat scuffs "walked over the registry every frame" and were "the
only audio cost the simulation could actually feel". This resolves the carrier only
when possession changes, then does one vector subtraction per frame.

`_WearAmount` now accumulates over a game (asymptotic, ~25-play time constant). The
turf shader always had a wear term; the game never moved it, so a fourth-quarter
pitch looked exactly like a kickoff pitch.

---

## Sprite atlases — why this project has none

`.claude/rules/performance.md` requires atlases for all 2D sprites, and
`Tools > PoFootball > Audit Sprite Usage` explains why none exist here:

- **404 of 413 textures are referenced by nothing** — the entire Kenney SportsPack
  and UIPack. Atlasing dead art does not save a draw call; it grows the `.aab`.
- **The eight sprites the game does draw must not be atlased.**
  `PoFootball_PlayerBody.hlsl` builds its outline by sampling `_MainTex` at offset
  UVs and treating anything outside the 0–1 rect as empty — which that file
  documents as load-bearing for the lineman's square, whose source texture fills to
  the border. In an atlas `uv` is the *atlas* rect, so the test can never fail and
  the offset taps land on neighbouring sprites. Atlasing would delete the square's
  outline and bleed other shapes into every rim.
- There is no draw call to win anyway: `Systems_PlayerAppearanceView` documents
  that the 22 players are 22 draw calls **by design** (per-instance
  `MaterialPropertyBlock`), the field is one procedural quad, and the UI is UI
  Toolkit with no sprites.

`Tools > PoFootball > Build UI Sprite Atlas` is left as working infrastructure for
the day real UI art arrives. It refuses `Assets/Art/Shapes` rather than trusting
whoever runs it to remember the above.

The audit **exempts `Assets/Resources`**, because `Resources.Load` takes a path and
a resource's GUID appears in no asset file — a reference scan reports everything
there as dead. Its first run listed the two UI typefaces as unused.

---

## Measured cost

Play-mode captures of `SCN_GAME` with the full rig, 22 shadow casters, the post
stack, the diagnostic overlay and 3D audio: **16.5–16.8 ms/frame**, i.e. the 60 FPS
cap of CLAUDE.md §3 with headroom. Training is unaffected — none of it is
constructed when `Systems_PresentationBudget.EffectsEnabled` is false, and
`SCN_TRAIN_FOOTBALL` has no UI document at all.
