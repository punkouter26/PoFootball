using System;
using MessagePipe;
using PoFootball.Models;
using PoFootball.Systems;
using UnityEngine;
using VContainer;

namespace PoFootball.Views
{
    /// <summary>
    /// Drives the one quad that is now the entire field.
    ///
    /// SCN_GAME used to draw the pitch as twenty-four stretched copies of a white
    /// pixel — a turf quad, two end zones, nineteen yard lines and two goal lines —
    /// each with its own transform holding a hand-computed Y that had to agree with
    /// Systems_FieldModel and, being scene data, could stop agreeing at any time
    /// without anything failing. PoFootball/Turf generates all of it procedurally,
    /// so this component's real job is to hand the shader the same constants the
    /// simulation uses and then get out of the way.
    ///
    /// It also switches off the legacy marking objects rather than deleting them.
    /// A scene edit is reversible; deleting twenty-four GameObjects out from under
    /// a scene file that is still in review is not, and the hooks in .claude/ block
    /// direct .unity edits for exactly that reason.
    ///
    /// THE WEAR FOLLOWS THE BALL. Every snap moves the scuffed patch to the new
    /// line of scrimmage. It is one material write per play, which is why this
    /// subscribes rather than polling — the LOS changes a handful of times a
    /// quarter, not sixty times a second.
    ///
    /// SO DO THE DOWN MARKERS, IN THE SAME WRITE. The line of scrimmage and the
    /// line to gain are two more floats on the property block the wear already
    /// goes out on, so the two lines every televised game has drawn since 1998
    /// cost this one no draw call and no object. Until they existed nothing on the
    /// field said how far the offense had to go: the distance was a number on the
    /// HUD, and whether a run had made it was something a viewer learned from the
    /// banner afterwards rather than watched happen.
    /// </summary>
    [DefaultExecutionOrder(-200)]
    [RequireComponent(typeof(SpriteRenderer))]
    [DisallowMultipleComponent]
    public sealed class Systems_FieldRenderer : MonoBehaviour, Systems_IInjectableView
    {
        private const string TURF_MATERIAL_RESOURCE = "M_PoFootballTurf";

        /// <summary>Name of the legacy marking parent under the field root.</summary>
        private const string LEGACY_LINES_NODE = "Lines";

        /// <summary>Name prefix of the legacy end zone quads, which are siblings.</summary>
        private const string LEGACY_END_ZONE_PREFIX = "Field_EndZone";

        private static readonly int HalfLengthId = Shader.PropertyToID("_HalfLengthYards");
        private static readonly int HalfWidthId = Shader.PropertyToID("_HalfWidthYards");
        private static readonly int EndZoneId = Shader.PropertyToID("_EndZoneYards");
        private static readonly int WearCenterId = Shader.PropertyToID("_WearCenterY");
        private static readonly int WearAmountId = Shader.PropertyToID("_WearAmount");
        private static readonly int ScrimmageId = Shader.PropertyToID("_ScrimmageY");
        private static readonly int LineToGainId = Shader.PropertyToID("_LineToGainY");

        /// <summary>
        /// Where a down marker goes to not be drawn, in yards from the 50. Matches
        /// the shader's own default for both properties: well off the quad, so the
        /// band test can never reach it.
        /// </summary>
        private const float MARKER_HIDDEN_YARDS = 1000f;

        /// <summary>How chewed the turf is on the opening snap. A groundsman's pitch.</summary>
        private const float WEAR_AT_KICKOFF = 0.12f;

        /// <summary>
        /// The most worn the field is ever allowed to look. Short of the material's
        /// authored 0.45 on purpose — past this the wear band starts competing with
        /// the yard markings for the viewer's attention, and the markings win.
        /// </summary>
        private const float WEAR_CEILING = 0.40f;

        /// <summary>
        /// Plays after which roughly 63% of the total wear has accumulated. A game
        /// runs about seventy scrimmage plays, so most of the change is visible by
        /// the end of the first half.
        /// </summary>
        private const int WEAR_TIME_CONSTANT_PLAYS = 25;

        /// <summary>Snaps taken this game. Drives the wear curve and nothing else.</summary>
        private int _playsRun;

        [Tooltip("Leave empty to load M_PoFootballTurf from Resources.")]
        [SerializeField] private Material _turfMaterial;

        private ISubscriber<Systems_PlaySnappedMessage> _snappedSubscriber;
        private IDisposable _snappedSubscription;

        /// <summary>
        /// Read for the distance to go, and from the PLAY model rather than the game
        /// model on purpose: Systems_GameModel is a Game-mode registration, and the
        /// play model carries the same down and distance in both modes.
        /// </summary>
        private Systems_PlayModel _play;

        private SpriteRenderer _renderer;
        private MaterialPropertyBlock _properties;

        [Inject]
        public void Construct(
            Systems_PlayModel play,
            ISubscriber<Systems_PlaySnappedMessage> snappedSubscriber)
        {
            _play = play;
            _snappedSubscriber = snappedSubscriber;
        }

        private void Awake()
        {
            _renderer = GetComponent<SpriteRenderer>();

            if (_turfMaterial == null)
            {
                _turfMaterial = Resources.Load<Material>(TURF_MATERIAL_RESOURCE);
            }

            if (_turfMaterial == null)
            {
                Debug.LogError(
                    $"{nameof(Systems_FieldRenderer)}: no turf material assigned and none at "
                    + $"Resources/{TURF_MATERIAL_RESOURCE}. The field keeps its flat sprite.");
                return;
            }

            // sharedMaterial, never material — the latter clones per renderer and
            // there would then be two turf materials to keep in step
            // (.claude/rules/performance.md).
            _renderer.sharedMaterial = _turfMaterial;

            // THE SCENE'S TINT GOES, AND IT WAS QUIETLY RECOLOURING THE WHOLE FIELD.
            // SCN_GAME's turf renderer carries (0.11, 0.35, 0.16) — the green the
            // pitch was when it was a flat stretched pixel. PoFootball/Turf
            // multiplies its output by the renderer colour, as every sprite shader
            // does, so that leftover was applied on top of a pattern that already
            // has its own grass: the white yard lines came out green, the red and
            // blue end zones came out near black, and the grass itself rendered at
            // about a third of the brightness the material asks for. Nothing
            // failed; the field simply never looked like its own material.
            //
            // Cleared here rather than in the scene for the reason
            // HideLegacyMarkings disables rather than deletes: the tint is the
            // right colour for the flat sprite this component replaces, so taking
            // the component off still gives back a green field.
            _renderer.color = Color.white;

            FitToField();
            PushFieldMetrics();
            HideLegacyMarkings();
        }

        private void Start()
        {
            if (_snappedSubscriber != null)
            {
                _snappedSubscription = _snappedSubscriber.Subscribe(OnSnapped);
            }
        }

        private void OnDestroy()
        {
            _snappedSubscription?.Dispose();
        }

        /// <summary>
        /// Scales the quad to the full 120 x 53.3 yard footprint including both end
        /// zones, measured off the sprite's own bounds so the result is correct for
        /// any source sprite and any pixels-per-unit setting.
        /// </summary>
        private void FitToField()
        {
            if (_renderer.sprite == null)
            {
                Debug.LogError(
                    $"{nameof(Systems_FieldRenderer)}: the turf renderer has no sprite, so "
                    + "there is nothing to scale. The field will not be drawn.");
                return;
            }

            Vector2 spriteSize = _renderer.sprite.bounds.size;

            if (spriteSize.x <= 0f || spriteSize.y <= 0f)
            {
                return;
            }

            transform.localPosition = new Vector3(0f, 0f, transform.localPosition.z);
            transform.localRotation = Quaternion.identity;
            transform.localScale = new Vector3(
                Systems_FieldModel.FIELD_WIDTH / spriteSize.x,
                Systems_FieldModel.TOTAL_LENGTH / spriteSize.y,
                1f);
        }

        /// <summary>
        /// The shader works in yards from the centre of the field. These four
        /// numbers are the only place the two coordinate systems meet, and they are
        /// all derived from Systems_FieldModel rather than typed into the material.
        /// </summary>
        private void PushFieldMetrics()
        {
            _properties ??= new MaterialPropertyBlock();
            _renderer.GetPropertyBlock(_properties);

            _properties.SetFloat(
                HalfLengthId, Systems_FieldModel.ATTACKING_GOAL_LINE_Y / Systems_FieldModel.YARD);
            _properties.SetFloat(
                HalfWidthId, Systems_FieldModel.HALF_WIDTH / Systems_FieldModel.YARD);
            _properties.SetFloat(
                EndZoneId, Systems_FieldModel.END_ZONE_DEPTH / Systems_FieldModel.YARD);
            _properties.SetFloat(WearCenterId, 0f);

            _renderer.SetPropertyBlock(_properties);
        }

        /// <summary>
        /// Switches off the twenty-four quads the shader has replaced, found by
        /// name among this renderer's siblings rather than wired through the
        /// inspector. A serialized array of twenty-four references is twenty-four
        /// things that can come loose, and the names are already the contract the
        /// scene was authored against.
        ///
        /// Disabled, not deleted. Reverting a graphics pass should be a matter of
        /// removing one component, and .claude/ blocks direct .unity edits partly
        /// so a change like this cannot quietly become permanent.
        /// </summary>
        private void HideLegacyMarkings()
        {
            Transform parent = transform.parent;

            if (parent == null)
            {
                return;
            }

            Transform lines = parent.Find(LEGACY_LINES_NODE);

            if (lines != null)
            {
                lines.gameObject.SetActive(false);
            }

            for (int index = 0; index < parent.childCount; index++)
            {
                Transform child = parent.GetChild(index);

                if (child != transform && child.name.StartsWith(LEGACY_END_ZONE_PREFIX))
                {
                    child.gameObject.SetActive(false);
                }
            }
        }

        private void OnSnapped(Systems_PlaySnappedMessage message)
        {
            if (_properties == null)
            {
                return;
            }

            // THE FIELD GETS WORSE AS THE GAME GOES ON. _WearAmount used to be
            // whatever the material asset said and never moved, so a pristine pitch
            // in the fourth quarter looked exactly like a pristine pitch at kickoff
            // — the shader had a wear term and the game had no wear. Stepping it up
            // one snap at a time turns it into something a viewer can read: a badly
            // chewed field means a long game has been played on it.
            //
            // Asymptotic rather than linear, so it can never saturate into mud
            // however many plays a game runs to, and so most of the visible change
            // lands in the first quarter where a viewer is still learning what the
            // surface looks like.
            _playsRun++;

            float wear = Mathf.Lerp(
                WEAR_AT_KICKOFF,
                WEAR_CEILING,
                1f - Mathf.Exp(-_playsRun / (float)WEAR_TIME_CONSTANT_PLAYS));

            float scrimmageYards = message.LineOfScrimmageY / Systems_FieldModel.YARD;

            _renderer.GetPropertyBlock(_properties);
            _properties.SetFloat(WearCenterId, scrimmageYards);
            _properties.SetFloat(WearAmountId, wear);
            _properties.SetFloat(ScrimmageId, scrimmageYards);
            _properties.SetFloat(LineToGainId, LineToGainYards(message.LineOfScrimmageY));
            _renderer.SetPropertyBlock(_properties);
        }

        /// <summary>
        /// Where the line to gain sits, in yards from the 50, or off the field
        /// entirely when there is no line to draw.
        ///
        /// GOAL TO GO HAS NO LINE TO GAIN. The chains cannot be set past the goal
        /// line — Systems_GameModel clamps its own marker there for the same reason
        /// — so the thing the offense has to reach is the goal line itself, which
        /// is already painted. A second, yellow line laid on top of it would say
        /// "first down here" about a place where the only thing on offer is a
        /// touchdown.
        /// </summary>
        private float LineToGainYards(float lineOfScrimmageY)
        {
            if (_play == null)
            {
                return MARKER_HIDDEN_YARDS;
            }

            float markerY = lineOfScrimmageY + (_play.YardsToGo * Systems_FieldModel.YARD);

            if (markerY >= Systems_FieldModel.ATTACKING_GOAL_LINE_Y)
            {
                return MARKER_HIDDEN_YARDS;
            }

            return markerY / Systems_FieldModel.YARD;
        }
    }
}
