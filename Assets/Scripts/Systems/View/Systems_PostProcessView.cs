using System;
using MessagePipe;
using PoFootball.Models;
using PoFootball.Systems;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using VContainer;

namespace PoFootball.Views
{
    /// <summary>
    /// The post-processing stack, built at runtime and gated on the presentation
    /// budget.
    ///
    /// WHY IT IS BUILT IN CODE AND NOT AUTHORED AS AN ASSET. The project already
    /// had every piece of this except the one that turns it on: Renderer2D.asset
    /// carries a PostProcessData reference, UniversalRP.asset has SupportsHDR set,
    /// and Assets/Settings/DefaultVolumeProfile.asset exists. What no scene had was
    /// a Volume component, so none of it ran. Authoring one into SCN_GAME would
    /// have meant a profile asset whose effects are live during training too —
    /// Systems_PresentationBudget names "a post-processing volume" as one of the
    /// four things it exists to stop a trainer from paying for, and an asset-based
    /// volume in the scene cannot be gated by a constructor argument. Building it
    /// here means the training scene simply never creates one.
    ///
    /// THE EFFECTS, AND WHY EACH IS HERE:
    ///
    ///   Tonemapping — ACES. Without a tonemapper an HDR pipeline clips everything
    ///   above 1.0 to flat white, which is exactly where the carrier glow and the
    ///   impact flash live. It is what makes the two brightest cues in the game
    ///   read as bright rather than as blown-out holes.
    ///
    ///   Bloom — the reason the previous line matters. PoFootball/Player already
    ///   computes an animated rim on the ball carrier and a speed-scaled whitening
    ///   on the two bodies in a collision. Both were being drawn at full intensity
    ///   into a buffer that did nothing with the overshoot. A threshold above 1
    ///   means ONLY those two cues bloom: the turf, the markings and the twenty
    ///   players who are neither carrying nor being hit are all below it and are
    ///   left untouched. That is the whole design — bloom here is a channel for
    ///   information the simulation already computes, not a wash over the picture.
    ///
    ///   Vignette — a portrait 9:16 frame showing a 53-yard-wide field has dead
    ///   space at the top and bottom corners on every single frame. Darkening them
    ///   is free attention.
    ///
    ///   Colour adjustments — a little contrast and saturation. The palette is a
    ///   dark green field under a cool ambient, which is flatter than a broadcast
    ///   picture; this is the grade a camera would have applied.
    ///
    ///   Lens distortion and chromatic aberration — at zero, always, except for a
    ///   fifth of a second after a hit hard enough to strip the ball. Both are
    ///   screen-space and need nothing the 2D renderer lacks; both report
    ///   themselves inactive at zero intensity, so between pulses they cost
    ///   nothing. The pulse is centred on the hit and sized by how far over
    ///   Systems_SimConstants.FUMBLE_CLOSING_SPEED it was — the same threshold
    ///   Systems_ImpactView's ring is drawn at, so the lens and the ring are one
    ///   event. This is what the removed camera kick was for, done without
    ///   moving the camera or writing Time.timeScale.
    ///
    /// NO DEPTH OF FIELD, NO MOTION BLUR, NO FILM GRAIN. The first two need a depth
    /// buffer and motion vectors that the 2D renderer does not produce, so they are
    /// silently inert rather than merely expensive. Grain would obscure the thing
    /// the whole picture exists to show, which is twenty-two small shapes.
    /// </summary>
    [DefaultExecutionOrder(-80)]
    [DisallowMultipleComponent]
    public sealed class Systems_PostProcessView : MonoBehaviour, Systems_IInjectableView
    {
        // SERIALIZED RATHER THAN const, FOR TUNING. The volume is still built at
        // runtime — Systems_PresentationBudget names "a post-processing volume" as
        // something a training run must not construct at all — but the grade itself
        // is the sort of thing you want to push around against a live capture, and
        // that is impossible when every value needs a recompile to change.

        [Header("Bloom")]
        [Tooltip(
            "Bloom threshold in EV. Above 1 so only genuine overbright pixels "
            + "bloom. The carrier rim and the impact flash are the only two things "
            + "in the project authored to exceed it. "
            + "1.35 RATHER THAN 1.05, AND THE DIFFERENCE WAS VISIBLE. At 1.05 the "
            + "saturated team colours cross the threshold on their own once a "
            + "casting bank is over them, so all twenty-two players carried a halo "
            + "and the carrier glow stopped being distinguishable from everybody "
            + "else. Verified against a play-mode capture at both values.")]
        [SerializeField] private float _bloomThreshold = 1.35f;

        [SerializeField] private float _bloomIntensity = 0.9f;

        [Tooltip(
            "How far the bloom spreads. Kept low: a wide scatter on a 22-body field "
            + "bleeds the carrier's glow onto the defenders converging on him, "
            + "which is the exact opposite of what the glow is for.")]
        [Range(0f, 1f)]
        [SerializeField] private float _bloomScatter = 0.55f;

        [Header("Vignette")]
        [Range(0f, 1f)]
        [SerializeField] private float _vignetteIntensity = 0.16f;

        [Range(0f, 1f)]
        [SerializeField] private float _vignetteSmoothness = 0.5f;

        [Header("Grade")]
        [Range(-100f, 100f)]
        [SerializeField] private float _postContrast = 5f;

        [Range(-100f, 100f)]
        [SerializeField] private float _postSaturation = 8f;

        [Tooltip(
            "Above the default of 0 so this volume wins over the pipeline's own "
            + "DefaultVolumeProfile, which is otherwise blended in underneath it.")]
        [SerializeField] private float _volumePriority = 100f;

        [Header("Hit pulse")]
        [Tooltip(
            "Lens distortion at the top of the pulse for the hardest hit the speed "
            + "clamp permits. NEGATIVE, so the picture is pulled in toward the hit "
            + "and every sample stays inside the frame; a positive value pushes the "
            + "edges outward and shows whatever the renderer has past them. Zero "
            + "switches the distortion off and leaves the fringe.")]
        [Range(-0.5f, 0f)]
        [SerializeField] private float _hitLensIntensity = -0.22f;

        [Tooltip("Chromatic aberration at the top of the same pulse.")]
        [Range(0f, 1f)]
        [SerializeField] private float _hitFringeIntensity = 0.45f;

        /// <summary>
        /// Bloom intensity at the top of the touchdown pulse.
        ///
        /// THE ONE MOMENT THE BLOOM IS ALLOWED TO SAY SOMETHING OTHER THAN "CARRIER
        /// OR HIT". The threshold is untouched, so the pulse still only lights what
        /// was already over it — the scorer's rim — and it is back to the
        /// configured value inside a third of a second, before the field resets.
        /// </summary>
        private const float SCORE_PULSE_INTENSITY = 1.4f;

        /// <summary>
        /// Up and back, in real seconds: it is presentation, and at Sim Speed 8x a
        /// game-time pulse would be a single frame.
        /// </summary>
        private const float SCORE_PULSE_SECONDS = 0.3f;

        /// <summary>
        /// Real seconds, for the reason SCORE_PULSE_SECONDS is. About as long as
        /// the impact flash on the two bodies, so the three read as one hit.
        /// </summary>
        private const float HIT_PULSE_SECONDS = 0.22f;

        /// <summary>
        /// The pulse for a hit that only just clears the fumble threshold, as a
        /// fraction of the full one. Not zero: the ring is drawn at full size for
        /// that hit, and a lens that did nothing would leave it unaccompanied.
        /// </summary>
        private const float HIT_PULSE_FLOOR = 0.45f;

        private Systems_PresentationBudget _budget;
        private Systems_PlayerRegistry _registry;
        private ISubscriber<Systems_ScoreMessage> _scoreSubscriber;
        private ISubscriber<Systems_TackleMessage> _tackleSubscriber;
        private IDisposable _scoreSubscription;
        private IDisposable _tackleSubscription;

        private VolumeProfile _profile;
        private Volume _volume;
        private Bloom _bloom;
        private LensDistortion _lens;
        private ChromaticAberration _fringe;
        private Camera _camera;

        /// <summary>Real seconds into the current pulse; negative when none is running.</summary>
        private float _pulseElapsed = -1f;

        /// <summary>Real seconds into the current hit pulse; negative when none is running.</summary>
        private float _hitElapsed = -1f;

        /// <summary>How big the running hit pulse is, in [HIT_PULSE_FLOOR, 1].</summary>
        private float _hitAmount;

        [Inject]
        public void Construct(
            Systems_PresentationBudget budget,
            Systems_PlayerRegistry registry,
            ISubscriber<Systems_ScoreMessage> scoreSubscriber,
            ISubscriber<Systems_TackleMessage> tackleSubscriber)
        {
            _budget = budget;
            _registry = registry;
            _scoreSubscriber = scoreSubscriber;
            _tackleSubscriber = tackleSubscriber;
        }

        private void Start()
        {
            if (_budget == null || !_budget.EffectsEnabled)
            {
                enabled = false;
                return;
            }

            BuildProfile();
            BuildVolume();
            EnableOnCamera();

            _scoreSubscription = _scoreSubscriber?.Subscribe(OnScore);
            _tackleSubscription = _tackleSubscriber?.Subscribe(OnTackle);
        }

        /// <summary>
        /// Runs the two pulses, and nothing else: two comparisons on every frame
        /// there is no touchdown to celebrate and no big hit to mark.
        /// </summary>
        private void Update()
        {
            TickHitPulse();

            if (_pulseElapsed < 0f)
            {
                return;
            }

            _pulseElapsed += Time.unscaledDeltaTime;

            float progress = _pulseElapsed / SCORE_PULSE_SECONDS;

            if (progress >= 1f)
            {
                RestoreBloom();
                return;
            }

            // Never below the configured value, even if it was tuned past 1.4 in
            // the Inspector — a celebration that dimmed the picture would be worse
            // than none.
            float peak = Mathf.Max(SCORE_PULSE_INTENSITY, _bloomIntensity);

            _bloom.intensity.value = Mathf.Lerp(
                _bloomIntensity, peak, Mathf.Sin(Mathf.PI * progress));
        }

        /// <summary>
        /// Puts the configured intensity back exactly, so a scene unloaded
        /// mid-pulse cannot leave the grade brighter than it was tuned — the same
        /// failure that got Time.timeScale taken away from presentation.
        /// </summary>
        private void OnDisable()
        {
            RestoreBloom();
            RestoreLens();
        }

        /// <summary>
        /// The profile is created here and destroyed with this component. It is a
        /// ScriptableObject that was never an asset, so nothing else will collect
        /// it — leaving it would leak one profile plus four volume components per
        /// scene load, and REMATCH reloads the scene.
        /// </summary>
        private void OnDestroy()
        {
            _scoreSubscription?.Dispose();
            _scoreSubscription = null;

            _tackleSubscription?.Dispose();
            _tackleSubscription = null;

            RestoreBloom();
            RestoreLens();

            if (_profile != null)
            {
                Destroy(_profile);
                _profile = null;
            }
        }

        private void BuildProfile()
        {
            _profile = ScriptableObject.CreateInstance<VolumeProfile>();
            _profile.name = "PoFootball_RuntimeVolume";

            // Every override has to be turned on explicitly. A VolumeComponent added
            // to a profile arrives with all of its parameters unoverridden, which
            // means it contributes nothing and looks exactly like a working effect
            // in every inspector.
            Tonemapping tonemapping = _profile.Add<Tonemapping>(true);
            tonemapping.mode.overrideState = true;
            tonemapping.mode.value = TonemappingMode.ACES;

            Bloom bloom = _profile.Add<Bloom>(true);
            _bloom = bloom;
            bloom.threshold.overrideState = true;
            bloom.threshold.value = _bloomThreshold;
            bloom.intensity.overrideState = true;
            bloom.intensity.value = _bloomIntensity;
            bloom.scatter.overrideState = true;
            bloom.scatter.value = _bloomScatter;

            // High quality filtering is the cheap half of bloom's cost and the half
            // that removes the fireflies a 22-sprite scene with hard edges produces.
            bloom.highQualityFiltering.overrideState = true;
            bloom.highQualityFiltering.value = true;

            Vignette vignette = _profile.Add<Vignette>(true);
            vignette.intensity.overrideState = true;
            vignette.intensity.value = _vignetteIntensity;
            vignette.smoothness.overrideState = true;
            vignette.smoothness.value = _vignetteSmoothness;

            ColorAdjustments grade = _profile.Add<ColorAdjustments>(true);
            grade.contrast.overrideState = true;
            grade.contrast.value = _postContrast;
            grade.saturation.overrideState = true;
            grade.saturation.value = _postSaturation;

            // Overridden AT ZERO. Both effects answer IsActive() from their
            // intensity, so this is two components the renderer skips until
            // OnTackle gives them something to do — and an override that was
            // only switched on for the pulse would be one more thing to restore.
            _lens = _profile.Add<LensDistortion>(true);
            _lens.intensity.overrideState = true;
            _lens.intensity.value = 0f;
            _lens.center.overrideState = true;
            _lens.center.value = new Vector2(0.5f, 0.5f);

            _fringe = _profile.Add<ChromaticAberration>(true);
            _fringe.intensity.overrideState = true;
            _fringe.intensity.value = 0f;
        }

        private void BuildVolume()
        {
            GameObject holder = new GameObject("PostProcessVolume");
            holder.transform.SetParent(transform, false);

            // Layer 0. UniversalAdditionalCameraData.volumeLayerMask defaults to the
            // Default layer only, so a volume on any other layer is never sampled —
            // and nothing reports that it was skipped.
            holder.layer = 0;

            _volume = holder.AddComponent<Volume>();
            _volume.isGlobal = true;
            _volume.priority = _volumePriority;
            _volume.weight = 1f;

            // sharedProfile, not profile. The `profile` setter CLONES whatever it is
            // given and hands back an instance, so assigning through it would leave
            // the original hanging as an uncollected ScriptableObject and give
            // OnDestroy the wrong object to clean up.
            _volume.sharedProfile = _profile;
        }

        /// <summary>
        /// Turns post-processing on for the camera itself.
        ///
        /// A Volume in the scene does nothing on its own: the per-camera
        /// renderPostProcessing flag is what makes the renderer run the stack, it
        /// defaults to false on a camera that was never touched in the inspector,
        /// and there is no warning when a fully configured volume is being ignored
        /// because of it.
        /// </summary>
        private void EnableOnCamera()
        {
            Camera camera = Camera.main;

            // Kept for the hit pulse, which has to turn a point on the field into
            // a point on the screen. Camera.main is a scene search; once is enough.
            _camera = camera;

            if (camera == null)
            {
                Debug.LogWarning(
                    $"{nameof(Systems_PostProcessView)}: no camera tagged MainCamera. "
                    + "The volume is built but nothing will render it.");
                return;
            }

            UniversalAdditionalCameraData cameraData =
                camera.GetUniversalAdditionalCameraData();

            if (cameraData == null)
            {
                return;
            }

            cameraData.renderPostProcessing = true;
        }

        private void OnScore(Systems_ScoreMessage message)
        {
            if (_bloom == null)
            {
                return;
            }

            _pulseElapsed = 0f;
        }

        /// <summary>
        /// Starts the lens pulse for a hit hard enough to strip the ball, and for
        /// no other. An ordinary tackle happens every play; a lens that moved on
        /// each of them would be a tic, and it would stop saying which hit was the
        /// one the fumble model is about to roll against.
        /// </summary>
        private void OnTackle(Systems_TackleMessage message)
        {
            if (_lens == null
                || message.ClosingSpeed < Systems_SimConstants.FUMBLE_CLOSING_SPEED)
            {
                return;
            }

            float over = Mathf.InverseLerp(
                Systems_SimConstants.FUMBLE_CLOSING_SPEED,
                Systems_SimConstants.MAX_BODY_SPEED,
                message.ClosingSpeed);

            float amount = Mathf.Lerp(HIT_PULSE_FLOOR, 1f, over);

            // A second hit inside a running pulse restarts it only if it is the
            // bigger one, so a pile-up reads as its hardest collision.
            if (_hitElapsed >= 0f && amount < _hitAmount)
            {
                return;
            }

            _hitAmount = amount;
            _hitElapsed = 0f;
            _lens.center.value = HitCentre(message.CarrierId);
        }

        /// <summary>
        /// Where the hit is on the screen, in the 0..1 space the distortion is
        /// centred in. The middle of the frame when the carrier or the camera
        /// cannot be found — the broadcast camera is on the ball anyway.
        /// </summary>
        private Vector2 HitCentre(int carrierId)
        {
            Vector2 middle = new Vector2(0.5f, 0.5f);

            if (_camera == null || _registry == null)
            {
                return middle;
            }

            Systems_IPlayerHandle carrier = _registry.FindById(carrierId);

            if (carrier == null)
            {
                return middle;
            }

            Vector3 viewport = _camera.WorldToViewportPoint(carrier.Position);

            return new Vector2(Mathf.Clamp01(viewport.x), Mathf.Clamp01(viewport.y));
        }

        /// <summary>
        /// Full on the frame it starts and eased out from there — the shape of an
        /// impact, for the reason Systems_PlayerAppearanceView gives for setting
        /// its own flash hard and letting it fall.
        /// </summary>
        private void TickHitPulse()
        {
            if (_hitElapsed < 0f)
            {
                return;
            }

            _hitElapsed += Time.unscaledDeltaTime;

            float progress = _hitElapsed / HIT_PULSE_SECONDS;

            if (progress >= 1f)
            {
                RestoreLens();
                return;
            }

            float remaining = 1f - progress;
            float level = _hitAmount * remaining * remaining;

            _lens.intensity.value = _hitLensIntensity * level;
            _fringe.intensity.value = _hitFringeIntensity * level;
        }

        /// <summary>
        /// Back to exactly zero, for the reason <see cref="RestoreBloom"/> exists:
        /// a scene unloaded mid-pulse must not leave the picture bent.
        /// </summary>
        private void RestoreLens()
        {
            _hitElapsed = -1f;
            _hitAmount = 0f;

            if (_lens != null)
            {
                _lens.intensity.value = 0f;
            }

            if (_fringe != null)
            {
                _fringe.intensity.value = 0f;
            }
        }

        private void RestoreBloom()
        {
            _pulseElapsed = -1f;

            if (_bloom != null)
            {
                _bloom.intensity.value = _bloomIntensity;
            }
        }
    }
}
