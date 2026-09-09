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

        private SpriteRenderer _renderer;
        private MaterialPropertyBlock _properties;

        [Inject]
        public void Construct(ISubscriber<Systems_PlaySnappedMessage> snappedSubscriber)
        {
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

            _renderer.GetPropertyBlock(_properties);
            _properties.SetFloat(
                WearCenterId, message.LineOfScrimmageY / Systems_FieldModel.YARD);
            _properties.SetFloat(WearAmountId, wear);
            _renderer.SetPropertyBlock(_properties);
        }
    }
}
