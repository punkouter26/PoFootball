using PoFootball.Models;
using PoFootball.Systems;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace PoFootball.Views
{
    /// <summary>
    /// Twenty-two players, a ball and its shadow that only exist to be drawn: the
    /// bodies the instant replay and the post-game highlights move, so that the
    /// real ones never are.
    ///
    /// WHY NOT REWIND THE REAL BODIES. They are Rigidbody2Ds with colliders, owned
    /// by the agents and by Systems_EpisodeDirector. Teleporting them back along
    /// the tape would fire OnCollisionEnter2D between bodies that are passing
    /// through each other's recorded positions — and a collision with the carrier
    /// is how a tackle is reported, so a replay could end the next play before it
    /// was snapped. It would also leave the bodies wherever the replay stopped
    /// rather than where the whistle left them. A ghost has a SpriteRenderer and
    /// nothing else the physics engine can see.
    ///
    /// A GHOST IS A COPY, NOT A LOOKALIKE. Sprite, shared material, colour,
    /// sorting and the whole MaterialPropertyBlock are read off the real renderer
    /// the moment the replay starts, so the bevel, the fatigue tint and the lean
    /// Systems_PlayerAppearanceView painted are on the ghost too. Only two
    /// properties are then written here: _Carrier, because the ball moves between
    /// hands during the replay and the real block only knows who has it now; and
    /// _Impact, zeroed, because the real block is captured a tick after the hit
    /// and would otherwise show the flash for the whole replay. Those two names
    /// mirror Systems_PlayerAppearanceView's and must move with them.
    ///
    /// sharedMaterial, never material: .material clones per renderer and breaks
    /// batching (.claude/rules/performance.md). Ghosts batch exactly as the real
    /// players do.
    ///
    /// THE REAL RENDERERS ARE HIDDEN FOR THE DURATION AND ONLY FOR THE DURATION.
    /// Their enabled state is recorded at Show and put back at Hide, which the
    /// owning view calls on every way a replay can end — its natural end, the
    /// snap, the final whistle, the highlight reel closing, OnDisable and
    /// OnDestroy. Systems_BallView forces
    /// its renderer on in every LateUpdate, so EnforceHidden re-asserts the hide
    /// once per frame from a later execution order rather than trusting one write.
    ///
    /// SHADOWS MOVE WITH THE GHOSTS. Systems_StadiumRigView puts a ShadowCaster2D
    /// on every player. A hidden renderer does not stop its caster, so without
    /// this twenty-two shadows would stand still at the whistle spot while their
    /// bodies ran the play again beside them. Each ghost whose player has a caster
    /// gets its own, built the way the rig builds them, and the real ones are
    /// switched off for the replay and restored after.
    ///
    /// THE BALL IS LIFTED AND SHADOWED THE WAY Systems_BallView DOES IT. The
    /// constants below mirror that view's serialized defaults (height-to-screen
    /// 0.6, shadow at 0.8 scale shrinking to 0.45 by 4 m, 35% black, order 0). It
    /// keeps them private and serialized, so they are restated here rather than
    /// read; if that view's inspector values are changed, a replayed pass will arc
    /// slightly differently from the live one, which is cosmetic.
    /// </summary>
    internal sealed class Systems_ReplayGhosts
    {
        private static readonly int CarrierPropertyId = Shader.PropertyToID("_Carrier");
        private static readonly int ImpactPropertyId = Shader.PropertyToID("_Impact");

        private const float BALL_LIFT = 0.6f;
        private const float SHADOW_BASE_SCALE = 0.8f;
        private const float SHADOW_MIN_SCALE = 0.45f;
        private const float SHADOW_FADE_HEIGHT = 4f;
        private const float SHADOW_Z_OFFSET = 0.5f;
        private const int SHADOW_SORTING_ORDER = 0;

        /// <summary>Below this the ball is on the turf and casts no separate shadow.</summary>
        private const float ALOFT_HEIGHT = 0.01f;

        private static readonly Color ShadowColor = new Color(0f, 0f, 0f, 0.35f);

        private readonly Systems_IPlayerHandle[] _handles;
        private readonly SpriteRenderer[] _sources;
        private readonly int _count;

        private readonly SpriteRenderer[] _ghosts;
        private readonly Transform[] _ghostTransforms;
        private readonly float[] _depths;
        private readonly Color[] _baseColors;
        private readonly bool[] _sourceWasEnabled;

        private readonly ShadowCaster2D[] _realCasters;
        private readonly bool[] _realCasterWasEnabled;

        private readonly SpriteRenderer _ballSource;
        private readonly SpriteRenderer _ballGhost;
        private readonly Transform _ballTransform;
        private readonly SpriteRenderer _ballShadow;
        private readonly Transform _ballShadowTransform;
        private bool _ballSourceWasEnabled;
        private float _ballDepth;
        private Vector3 _ballShadowBaseScale;

        private readonly GameObject _root;
        private readonly MaterialPropertyBlock _block = new MaterialPropertyBlock();

        /// <summary>Which ghost is currently painted as the carrier.</summary>
        private int _paintedCarrier = Systems_ReplayTape.NO_CARRIER;

        public Systems_ReplayGhosts(
            Systems_IPlayerHandle[] handles,
            SpriteRenderer[] sources,
            int count,
            SpriteRenderer ballSource)
        {
            _handles = handles;
            _sources = sources;
            _count = count;
            _ballSource = ballSource;

            _ghosts = new SpriteRenderer[count];
            _ghostTransforms = new Transform[count];
            _depths = new float[count];
            _baseColors = new Color[count];
            _sourceWasEnabled = new bool[count];
            _realCasters = new ShadowCaster2D[count];
            _realCasterWasEnabled = new bool[count];

            // Unparented, like Systems_BallView's shadow: a ghost's world transform
            // is written directly every frame, and a parent with any scale or
            // offset of its own would put every ghost somewhere else. It is
            // destroyed explicitly in Destroy.
            _root = new GameObject("ReplayGhosts");

            for (int player = 0; player < count; player++)
            {
                GameObject ghost = new GameObject("ReplayGhost");
                Transform ghostTransform = ghost.transform;
                ghostTransform.SetParent(_root.transform, false);

                SpriteRenderer renderer = ghost.AddComponent<SpriteRenderer>();
                _ghosts[player] = renderer;
                _ghostTransforms[player] = ghostTransform;

                SpriteRenderer source = sources[player];

                if (source == null)
                {
                    continue;
                }

                CopyLook(source, renderer);
                ghostTransform.localScale = source.transform.lossyScale;

                // Built while the ghost is upright at the origin with its sprite
                // already assigned. A runtime caster outlines Renderer.bounds — see
                // Systems_StadiumRigView — and the bounds of a rotated sprite are
                // a larger box than the sprite.
                if (source.TryGetComponent(out ShadowCaster2D realCaster))
                {
                    _realCasters[player] = realCaster;

                    // castingOption carries self-shadowing too (selfShadows is
                    // derived from it), so this one copy matches the rig's caster.
                    ShadowCaster2D caster = ghost.AddComponent<ShadowCaster2D>();
                    caster.castingOption = realCaster.castingOption;
                }
            }

            if (ballSource != null)
            {
                GameObject ball = new GameObject("ReplayGhostBall");
                _ballTransform = ball.transform;
                _ballTransform.SetParent(_root.transform, false);
                _ballGhost = ball.AddComponent<SpriteRenderer>();

                GameObject shadow = new GameObject("ReplayGhostBallShadow");
                _ballShadowTransform = shadow.transform;
                _ballShadowTransform.SetParent(_root.transform, false);
                _ballShadow = shadow.AddComponent<SpriteRenderer>();
                _ballShadow.color = ShadowColor;
                _ballShadow.sortingOrder = SHADOW_SORTING_ORDER;
            }

            // Nothing is drawn, cast or culled until a replay asks for it.
            _root.SetActive(false);
        }

        public bool IsShowing { get; private set; }

        /// <summary>
        /// Takes over from the real bodies: copies their current look onto the
        /// ghosts, hides them, and switches their shadows to the ghosts'.
        /// </summary>
        public void Show()
        {
            if (IsShowing || _root == null)
            {
                return;
            }

            for (int player = 0; player < _count; player++)
            {
                SpriteRenderer source = _sources[player];
                SpriteRenderer ghost = _ghosts[player];

                if (source == null)
                {
                    ghost.enabled = false;
                    continue;
                }

                ghost.enabled = true;
                CopyLook(source, ghost);

                Transform sourceTransform = source.transform;
                _ghostTransforms[player].localScale = sourceTransform.lossyScale;
                _depths[player] = sourceTransform.position.z;

                // Read before anything is repainted: the base colour of the man
                // holding the ball at the whistle is under his white highlight.
                _baseColors[player] = BaseColorOf(player);
                ghost.color = _baseColors[player];

                source.GetPropertyBlock(_block);
                _block.SetFloat(ImpactPropertyId, 0f);
                _block.SetFloat(CarrierPropertyId, 0f);
                ghost.SetPropertyBlock(_block);

                _sourceWasEnabled[player] = source.enabled;
                source.enabled = false;

                ShadowCaster2D realCaster = _realCasters[player];

                if (realCaster != null)
                {
                    _realCasterWasEnabled[player] = realCaster.enabled;
                    realCaster.enabled = false;
                }
            }

            if (_ballSource != null)
            {
                CopyLook(_ballSource, _ballGhost);

                Transform ballSourceTransform = _ballSource.transform;
                _ballTransform.localScale = ballSourceTransform.lossyScale;
                _ballTransform.rotation = ballSourceTransform.rotation;
                _ballDepth = ballSourceTransform.position.z;

                _ballShadow.sprite = _ballSource.sprite;
                _ballShadow.sortingLayerID = _ballSource.sortingLayerID;
                _ballShadowBaseScale = ballSourceTransform.lossyScale * SHADOW_BASE_SCALE;
                _ballShadow.enabled = false;

                _ballSourceWasEnabled = _ballSource.enabled;
                _ballSource.enabled = false;
            }

            _paintedCarrier = Systems_ReplayTape.NO_CARRIER;
            _root.SetActive(true);
            IsShowing = true;
        }

        /// <summary>
        /// Puts every ghost where the tape says it was at <paramref name="time"/>,
        /// in frames. Allocates nothing: one Vector3 and one Quaternion per body,
        /// both structs.
        /// </summary>
        public void Pose(Systems_ReplayTape tape, float time)
        {
            if (!IsShowing || tape == null)
            {
                return;
            }

            int bodies = Mathf.Min(_count, tape.PlayerCount);

            for (int player = 0; player < bodies; player++)
            {
                tape.SamplePlayer(time, player, out Vector2 position, out float angle);

                _ghostTransforms[player].SetPositionAndRotation(
                    new Vector3(position.x, position.y, _depths[player]),
                    Quaternion.Euler(0f, 0f, angle));
            }

            int carrier = tape.CarrierAt(time);

            if (carrier != _paintedCarrier)
            {
                PaintCarrier(_paintedCarrier, false);
                PaintCarrier(carrier, true);
                _paintedCarrier = carrier;
            }

            PoseBall(tape, time);
        }

        /// <summary>
        /// Overrides the colours copied at Show with the ones a recorded play
        /// actually wore: <paramref name="offenseColor"/> on every offense body,
        /// <paramref name="defenseColor"/> on every defense body. For the
        /// highlights, whose play may belong to a possession the real renderers no
        /// longer show — see Systems_ReplayHighlight. The ghost painted as the
        /// carrier keeps its white; it takes the new base colour when it gives the
        /// ball up. Twenty-two colour writes, called once per highlight.
        /// </summary>
        public void PaintSides(Color offenseColor, Color defenseColor)
        {
            if (!IsShowing)
            {
                return;
            }

            for (int player = 0; player < _count; player++)
            {
                Systems_IPlayerHandle handle = _handles[player];

                if (handle == null || _sources[player] == null)
                {
                    continue;
                }

                _baseColors[player] = handle.Side == Systems_TeamSide.Offense
                    ? offenseColor
                    : defenseColor;

                _ghosts[player].color = player == _paintedCarrier
                    ? Color.white
                    : _baseColors[player];
            }
        }

        /// <summary>
        /// Re-hides the real renderers. Called once per LateUpdate while showing,
        /// from an execution order after Systems_BallView, which turns the ball
        /// back on every frame. Twenty-three bool writes.
        /// </summary>
        public void EnforceHidden()
        {
            if (!IsShowing)
            {
                return;
            }

            for (int player = 0; player < _count; player++)
            {
                SpriteRenderer source = _sources[player];

                if (source != null)
                {
                    source.enabled = false;
                }
            }

            if (_ballSource != null)
            {
                _ballSource.enabled = false;
            }
        }

        /// <summary>Hands the field back to the real bodies, exactly as it found them.</summary>
        public void Hide()
        {
            if (!IsShowing)
            {
                return;
            }

            IsShowing = false;

            for (int player = 0; player < _count; player++)
            {
                SpriteRenderer source = _sources[player];

                if (source != null)
                {
                    source.enabled = _sourceWasEnabled[player];
                }

                ShadowCaster2D realCaster = _realCasters[player];

                if (realCaster != null)
                {
                    realCaster.enabled = _realCasterWasEnabled[player];
                }
            }

            if (_ballSource != null)
            {
                _ballSource.enabled = _ballSourceWasEnabled;
            }

            if (_root != null)
            {
                _root.SetActive(false);
            }
        }

        public void Destroy()
        {
            Hide();

            if (_root != null)
            {
                UnityEngine.Object.Destroy(_root);
            }
        }

        private void PoseBall(Systems_ReplayTape tape, float time)
        {
            if (_ballSource == null)
            {
                return;
            }

            tape.SampleBall(time, out Vector2 position, out float height);

            _ballTransform.position = new Vector3(
                position.x, position.y + (height * BALL_LIFT), _ballDepth);

            bool aloft = height > ALOFT_HEIGHT;

            if (_ballShadow.enabled != aloft)
            {
                _ballShadow.enabled = aloft;
            }

            if (!aloft)
            {
                return;
            }

            _ballShadowTransform.position = new Vector3(
                position.x, position.y, _ballDepth + SHADOW_Z_OFFSET);

            float climb = Mathf.Clamp01(height / SHADOW_FADE_HEIGHT);
            _ballShadowTransform.localScale =
                _ballShadowBaseScale * Mathf.Lerp(1f, SHADOW_MIN_SCALE, climb);
        }

        /// <summary>
        /// White for the carrier, as Agent_FootballPlayer.SetCarrier paints the
        /// real one, plus the shader's carrier glow.
        /// </summary>
        private void PaintCarrier(int player, bool isCarrier)
        {
            if (player < 0 || player >= _count || _sources[player] == null)
            {
                return;
            }

            SpriteRenderer ghost = _ghosts[player];
            ghost.color = isCarrier ? Color.white : _baseColors[player];

            ghost.GetPropertyBlock(_block);
            _block.SetFloat(CarrierPropertyId, isCarrier ? 1f : 0f);
            ghost.SetPropertyBlock(_block);
        }

        /// <summary>
        /// The colour this player wears when he does not have the ball. For
        /// everyone but the carrier that is simply his renderer's colour; the
        /// carrier's is hidden under the white highlight, so it is borrowed from a
        /// teammate — both units are painted one colour per side.
        /// </summary>
        private Color BaseColorOf(int player)
        {
            Systems_IPlayerHandle handle = _handles[player];

            if (handle == null || !handle.IsCarrier)
            {
                return _sources[player].color;
            }

            for (int other = 0; other < _count; other++)
            {
                Systems_IPlayerHandle teammate = _handles[other];

                if (other == player || teammate == null || _sources[other] == null)
                {
                    continue;
                }

                if (teammate.Side == handle.Side && !teammate.IsCarrier)
                {
                    return _sources[other].color;
                }
            }

            return _sources[player].color;
        }

        private static void CopyLook(SpriteRenderer source, SpriteRenderer ghost)
        {
            ghost.sprite = source.sprite;
            ghost.sharedMaterial = source.sharedMaterial;
            ghost.color = source.color;
            ghost.flipX = source.flipX;
            ghost.flipY = source.flipY;
            ghost.sortingLayerID = source.sortingLayerID;
            ghost.sortingOrder = source.sortingOrder;
        }
    }
}
