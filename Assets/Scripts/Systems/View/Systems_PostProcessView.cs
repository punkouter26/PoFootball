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
    /// THE FOUR EFFECTS, AND WHY EACH IS HERE:
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

        private Systems_PresentationBudget _budget;

        private VolumeProfile _profile;
        private Volume _volume;

        [Inject]
        public void Construct(Systems_PresentationBudget budget)
        {
            _budget = budget;
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
        }

        /// <summary>
        /// The profile is created here and destroyed with this component. It is a
        /// ScriptableObject that was never an asset, so nothing else will collect
        /// it — leaving it would leak one profile plus four volume components per
        /// scene load, and REMATCH reloads the scene.
        /// </summary>
        private void OnDestroy()
        {
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
    }
}
