using PoFootball.Models;
using PoFootball.Systems;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using VContainer;

namespace PoFootball.Views
{
    /// <summary>
    /// The stadium light rig, and the shadows the twenty-two bodies cast in it.
    ///
    /// WHY THIS DID NOT EXIST AND HAD TO. Both of this project's shaders were
    /// written to be lit — PoFootball/Turf and PoFootball/Player each declare the
    /// full three-pass URP 2D set (Universal2D, NormalsRendering, UniversalForward)
    /// and resolve their albedo through CombinedShapeLightShared, which is real
    /// work done specifically so a light rig would land on them. There was no rig.
    /// SCN_GAME carried exactly one Light2D, a Global at intensity 1 in pure white,
    /// which is the lighting equivalent of no lighting at all: every fragment on
    /// the field received the same illumination from every direction, so the entire
    /// lit path resolved to the unlit path with extra steps. The turf shader's own
    /// header note — "the turf is the surface every 2D shadow lands on" — described
    /// a thing that had never once happened, because `grep ShadowCaster2D` matched
    /// nothing in any of the three scenes.
    ///
    /// WHAT IT BUILDS. A dimmed, cool global for ambient fill, plus four warm banks
    /// arranged as a real stadium's are — outside the sidelines, past both
    /// twenty-five yard lines. The banks are what make a player read as an object
    /// standing ON the turf rather than a decal printed on it, and they are what
    /// give the turf shader's mow stripes and wear patch something to be shaded by.
    ///
    /// ONLY TWO OF THE FOUR CAST SHADOWS, AND THAT IS A BUDGET, NOT AN OVERSIGHT.
    /// URP's 2D shadows cost one shadow-mesh render per caster per shadow-casting
    /// light, so four casting banks against twenty-two players is eighty-eight
    /// shadow draws a frame on a phone that also has to run the simulation. Two
    /// casting banks on the near side give the direction cue — every body's shadow
    /// falls the same way, which is what tells the eye where the ground is — and
    /// the two far banks are pure fill at half intensity. Doubling the shadow count
    /// would not have made the second shadow readable at this camera distance; it
    /// would only have halved the frame rate.
    ///
    /// THE SHADOW SHAPE IS THE SPRITE'S BOUNDING BOX, NOT ITS SILHOUETTE. A
    /// ShadowCaster2D added at runtime cannot be told to derive its outline from
    /// the sprite: ShadowCaster2D.shadowCastingSource and its provider are both
    /// `internal` to the URP assembly, and the provider auto-detection in its Awake
    /// is wrapped in `#if UNITY_EDITOR`, so a player build always falls through to
    /// ShapeEditor and builds a quad from Renderer.bounds. That is the shape this
    /// gets in a build, so it is the shape it should be judged on — and at a camera
    /// forty metres up, the difference between a hexagon's shadow and its bounding
    /// box's shadow is under two pixels. Reflecting into the private field to do
    /// better would be a private-API dependency for a sub-pixel gain.
    ///
    /// EVERYTHING HERE IS GATED ON Systems_PresentationBudget. That class's own
    /// documentation names "builds a light rig" as the first example of what it
    /// exists to prevent during training, and a rig plus twenty-two shadow casters
    /// is precisely the per-step wall-clock cost a headless sweep must not pay.
    /// </summary>
    [DefaultExecutionOrder(-80)]
    [DisallowMultipleComponent]
    public sealed class Systems_StadiumRigView : MonoBehaviour, Systems_IInjectableView
    {
        // EVERY NUMBER BELOW IS SERIALIZED, NOT const.
        //
        // The rig is still BUILT AT RUNTIME and that is deliberate — see the class
        // note and Systems_PresentationBudget, which requires "do not construct"
        // rather than "do not play", and DimExistingAmbient, which has to find and
        // change a Global this component did not create. Authoring the banks as
        // scene objects would put twenty-two shadow casters into a training scene
        // that then has to tear them down again, which is the exact cost the budget
        // exists to avoid.
        //
        // What was actually hard to tune was the numbers, all of which were const
        // and none of which were reachable from the Inspector — so every lighting
        // experiment meant an edit, a domain reload, and a re-entry into play mode.
        // They are fields now. The rig builds itself; you tune it on the component.

        [Header("Ambient")]
        [Tooltip(
            "Ambient level the existing scene Global is pulled down to. "
            + "NOT ZERO AND NOT ONE, and the ratio to the banks is the whole "
            + "tuning. At 1 — which is what SCN_GAME shipped — the banks are "
            + "invisible because everything is already fully lit. At 0 the corners "
            + "of both end zones go black. The subtler trap is in between: 0.88 "
            + "produced a perfectly legible field with NO VISIBLE SHADOWS, because "
            + "a shadow can only subtract the light a bank contributed, and against "
            + "a bright ambient that contribution is a small fraction of the total. "
            + "This has to sit BELOW the casting banks for a shadow to darken "
            + "anything. Measured against a live capture, not reasoned about.")]
        [Range(0f, 1f)]
        [SerializeField] private float _ambientIntensity = 0.62f;

        [Tooltip("Cool blue-grey. Stadium ambient is skylight, not sunlight.")]
        [SerializeField] private Color _ambientColor = new Color(0.72f, 0.79f, 0.92f, 1f);

        [Header("Banks")]
        [Tooltip("Warm white, the colour of a metal-halide floodlight.")]
        [SerializeField] private Color _bankColor = new Color(1f, 0.96f, 0.88f, 1f);

        [Tooltip("Intensity of the two near, shadow-casting banks.")]
        [SerializeField] private float _bankIntensity = 1.25f;

        [Tooltip("Fill banks run quieter so the shadow direction stays unambiguous.")]
        [SerializeField] private float _fillIntensity = 0.65f;

        [Tooltip(
            "How far outside the sideline a bank stands, in metres. Real "
            + "floodlights are outside the field of play; putting them on it would "
            + "light the middle of the pitch brightest, which is the one place a "
            + "stadium never is.")]
        [SerializeField] private float _bankSidelineOffset = 14f;

        [Tooltip(
            "Distance from the 50 to each bank along the length of the field. The "
            + "default puts the four banks roughly over the two 25 yard lines.")]
        [SerializeField]
        private float _bankLengthwiseOffset = Systems_FieldModel.PLAYING_LENGTH * 0.32f;

        [Tooltip(
            "Outer radius of a bank. Large enough that the four together cover the "
            + "whole playing surface with overlap — a gap between two floodlights "
            + "reads as a rendering fault rather than as lighting.")]
        [SerializeField] private float _bankOuterRadius = 85f;

        [Tooltip(
            "Inner radius, the fully-bright core. The gap to the outer radius is "
            + "the penumbra, and a wide penumbra is what stops a 2D point light "
            + "from looking like a circle stamped on the grass.")]
        [SerializeField] private float _bankInnerRadius = 8f;

        [Tooltip("Falloff curve. 0 = hard edge, 1 = maximally soft.")]
        [Range(0f, 1f)]
        [SerializeField] private float _bankFalloff = 0.35f;

        [Header("Shadows")]
        [Range(0f, 1f)]
        [SerializeField] private float _shadowIntensity = 0.82f;

        [Range(0f, 1f)]
        [SerializeField] private float _shadowSoftness = 0.55f;

        [Tooltip(
            "Add ShadowCaster2D to the players. Clear it to keep the rig and drop "
            + "the per-caster cost on a device that cannot afford it.")]
        [SerializeField] private bool _castPlayerShadows = true;

        private Systems_PresentationBudget _budget;
        private Systems_PlayerRegistry _registry;

        [Inject]
        public void Construct(
            Systems_PresentationBudget budget, Systems_PlayerRegistry registry)
        {
            _budget = budget;
            _registry = registry;
        }

        /// <summary>
        /// Start, not Awake, for two independent reasons. The budget arrives by
        /// injection during the scope's Awake, and the registry is not populated
        /// until every Agent_FootballPlayer has registered itself — which also
        /// happens in Awake, in an order this component cannot depend on.
        /// </summary>
        private void Start()
        {
            if (_budget == null || !_budget.EffectsEnabled)
            {
                enabled = false;
                return;
            }

            DimExistingAmbient();
            BuildBanks();

            if (_castPlayerShadows)
            {
                AttachShadowCasters();
            }
        }

        /// <summary>
        /// Pulls the scene's existing Global down instead of adding a second one.
        ///
        /// Two Globals do not average — Light2D blends them additively within a
        /// blend style, so adding a dim one to a bright one produces a brighter
        /// one. The scene's Global has to be found and changed, and a Global that
        /// this component did not create is exactly what SCN_GAME ships with.
        /// </summary>
        private void DimExistingAmbient()
        {
            Light2D[] lights = FindObjectsByType<Light2D>(FindObjectsInactive.Include);

            bool foundGlobal = false;

            for (int index = 0; index < lights.Length; index++)
            {
                if (lights[index].lightType != Light2D.LightType.Global)
                {
                    continue;
                }

                lights[index].intensity = _ambientIntensity;
                lights[index].color = _ambientColor;

                // AND IT MUST NOT CAST. The class note budgets this rig at two
                // casting lights precisely because URP 2D spends one shadow-mesh
                // render per caster per casting light, and there are twenty-two
                // casters — but it only ever set that flag on the banks it BUILDS.
                // The Global is the one light it inherits rather than creates, and
                // SCN_GAME shipped it with shadows on, so the scene ran three
                // casting lights: sixty-six shadow draws a frame against the
                // forty-four the design allows.
                //
                // Measured in play mode: 5 Light2D, 3 with shadowsEnabled, 22
                // ShadowCaster2D, 217 draw calls. Clearing this is twenty-two fewer
                // shadow-mesh renders every frame, about a tenth of the whole draw
                // call count, for no visual loss — a Global has no position, so the
                // "shadow" it casts has no direction to come from and only flattens
                // the ambient fill that the banks are supposed to be read against.
                lights[index].shadowsEnabled = false;

                foundGlobal = true;
            }

            if (!foundGlobal)
            {
                GameObject holder = BeginLightHolder("Ambient", Vector3.zero);

                Light2D ambient = holder.AddComponent<Light2D>();
                ambient.lightType = Light2D.LightType.Global;
                ambient.intensity = _ambientIntensity;
                ambient.color = _ambientColor;
                ambient.shadowsEnabled = false;

                holder.SetActive(true);
            }
        }

        /// <summary>
        /// Four banks at the corners. The two on the +X sideline cast; the two
        /// opposite are fill — see the class note on the shadow budget.
        /// </summary>
        private void BuildBanks()
        {
            float x = Systems_FieldModel.HALF_WIDTH + _bankSidelineOffset;
            float y = _bankLengthwiseOffset;

            CreateBank("Bank_NearNorth", new Vector3(x, y, 0f), castsShadows: true);
            CreateBank("Bank_NearSouth", new Vector3(x, -y, 0f), castsShadows: true);
            CreateBank("Bank_FarNorth", new Vector3(-x, y, 0f), castsShadows: false);
            CreateBank("Bank_FarSouth", new Vector3(-x, -y, 0f), castsShadows: false);
        }

        private void CreateBank(string bankName, Vector3 position, bool castsShadows)
        {
            GameObject holder = BeginLightHolder(bankName, position);

            Light2D bank = holder.AddComponent<Light2D>();
            bank.lightType = Light2D.LightType.Point;
            bank.color = _bankColor;
            bank.intensity = castsShadows ? _bankIntensity : _fillIntensity;
            bank.pointLightOuterRadius = _bankOuterRadius;
            bank.pointLightInnerRadius = _bankInnerRadius;
            bank.falloffIntensity = _bankFalloff;
            bank.shadowsEnabled = castsShadows;
            bank.shadowIntensity = _shadowIntensity;
            bank.shadowSoftness = _shadowSoftness;

            holder.SetActive(true);
        }

        /// <summary>
        /// Builds a light on a child GameObject that starts INACTIVE, configures it,
        /// and only then activates it.
        ///
        /// The order is load-bearing. Light2D registers itself with the 2D renderer
        /// and generates its mesh in OnEnable, reading lightType and the point radii
        /// as they stand at that instant. A light created active and configured
        /// afterwards is registered as a zero-radius Parametric — the enum's default
        /// — and never rebuilds, so it renders nothing at all while every property
        /// on it reads correctly in the inspector.
        /// </summary>
        /// <summary>
        /// Creates the child GameObject a light will live on, inactive.
        ///
        /// Returns the GameObject rather than the Light2D, and NO METHOD IN THIS
        /// FILE MENTIONS Light2D IN ITS SIGNATURE. That is a hard constraint, not a
        /// style choice: Light2D derives from Light2DBase, which is an engine
        /// internal in UnityEngine.U2DRuntimeModule that URP can see and this
        /// assembly cannot. The compiler resolves Light2D happily inside a method
        /// body — local declarations, generic arguments to AddComponent, static
        /// member access — and reports CS0246 the moment it appears as a parameter
        /// type or a return type, because a signature forces it to walk the base
        /// chain. A helper that returned a Light2D therefore could not compile
        /// while its own body could, which is a confusing enough failure to be
        /// worth this comment.
        /// </summary>
        private GameObject BeginLightHolder(string lightName, Vector3 position)
        {
            GameObject holder = new GameObject(lightName);
            holder.SetActive(false);
            holder.transform.SetParent(transform, false);
            holder.transform.localPosition = position;
            return holder;
        }

        /// <summary>
        /// Puts a ShadowCaster2D on every registered player.
        ///
        /// Walks the registry rather than the scene, because the registry is the
        /// project's answer to "who is a player" and a scene sweep would also find
        /// the ball, the field quad and anything else a SpriteRenderer is attached
        /// to. Casting is set to CastShadow rather than CastAndSelfShadow: self
        /// shadowing darkens the inside of the sprite, and PoFootball/Player already
        /// owns the inside of the sprite — its bevel, carrier glow, fatigue tint and
        /// impact flash all live there, and a self shadow would fight all four.
        /// </summary>
        private void AttachShadowCasters()
        {
            if (_registry == null)
            {
                return;
            }

            int attached = 0;

            for (int slot = 0; slot < Systems_PlayerRegistry.CAPACITY; slot++)
            {
                Systems_IPlayerHandle handle = _registry.Get(slot);

                if (!(handle is MonoBehaviour behaviour))
                {
                    continue;
                }

                if (behaviour.TryGetComponent(out ShadowCaster2D _))
                {
                    continue;
                }

                ShadowCaster2D caster = behaviour.gameObject.AddComponent<ShadowCaster2D>();
                caster.castingOption = ShadowCaster2D.ShadowCastingOptions.CastShadow;
                caster.selfShadows = false;

                attached++;
            }

            Debug.Log(
                $"[PoFootball] Stadium rig: 4 banks, 2 casting, {attached} shadow casters.");
        }
    }
}
