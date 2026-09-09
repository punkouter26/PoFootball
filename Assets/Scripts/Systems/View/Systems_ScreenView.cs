using UnityEngine;
using UnityEngine.UIElements;

namespace PoFootball.Views
{
    /// <summary>
    /// Base for every UI Toolkit screen. Owns the UIDocument, resolves the shared
    /// PanelSettings, applies the device safe area, and hands the subclass a root
    /// to build into.
    ///
    /// The document and its panel settings are attached in code rather than
    /// authored on the GameObject. A screen is then a single component drop with no
    /// inspector wiring to forget, which is what keeps the scenes buildable through
    /// MCP tools instead of by hand.
    ///
    /// The UI is built in Start, not Awake. Systems_GameLifetimeScope injects
    /// through a build callback during its own Awake, so a subclass that touched
    /// an injected model in Awake would be reading a null. By Start every
    /// Systems_IInjectableView has been constructed.
    ///
    /// SAFE AREA. Every screen is inset by Screen.safeArea, converted from pixels
    /// into the panel's own coordinate space. Without it the game clock rendered
    /// underneath the punch-hole camera on the first phone it was shown on, and the
    /// version stamp — which CLAUDE.md specifically requires in the top-left of the
    /// opening scene — sat behind the same cutout. A 9:16 reference resolution says
    /// nothing about where a given handset puts its holes.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    [DisallowMultipleComponent]
    public abstract class Systems_ScreenView : MonoBehaviour
    {
        /// <summary>
        /// Resources path of the shared panel. One asset for every screen: portrait
        /// 1080x1920, scaling on width per CLAUDE.md section 3.
        /// </summary>
        private const string PANEL_SETTINGS_RESOURCE = "PoFootballPanelSettings";

        /// <summary>
        /// Tap sound, from the Kenney UI pack that shipped with this project and had
        /// never been referenced by anything.
        ///
        /// A RECORDING RATHER THAN A SYNTHESISED TICK, and that is not a departure
        /// from Systems_ToneBank's reasoning. That class synthesises because
        /// docs/ASSETS.md records that Asset Store audio "cannot be fetched without
        /// a signed-in human clicking Add to My Assets" — an argument about sounds
        /// the repository does not have. These six .ogg files were already in it,
        /// under Assets/Art/Kenney/UIPack/Sounds, unreferenced by any script. There
        /// is no case for synthesising a click when a good one is sitting in the
        /// project unused.
        /// </summary>
        private const string TAP_CLIP_RESOURCE = "Audio/Ui/click-a";

        private UIDocument _document;
        private VisualElement _safeArea;
        private bool _isBuilt;

        private AudioSource _uiAudio;
        private AudioClip _tapClip;

        private Rect _lastSafeArea;
        private Vector2Int _lastScreenSize;

        /// <summary>
        /// The safe-area-inset root every screen builds into. Null until Start has
        /// run. Anything added here is guaranteed to be reachable and visible.
        /// </summary>
        protected VisualElement Root { get; private set; }

        /// <summary>
        /// The document's true root, outside the safe-area inset. Only for chrome
        /// that genuinely wants to bleed to the physical edge of the display — a
        /// background wash, a full-bleed scrim. Text does not belong here.
        /// </summary>
        protected VisualElement UnsafeRoot { get; private set; }

        /// <summary>Builds the screen's element tree into <see cref="Root"/>.</summary>
        protected abstract void BuildUi();

        protected virtual void Awake()
        {
            _document = GetComponent<UIDocument>();

            if (_document.panelSettings == null)
            {
                PanelSettings settings =
                    Resources.Load<PanelSettings>(PANEL_SETTINGS_RESOURCE);

                if (settings == null)
                {
                    Debug.LogError(
                        $"{GetType().Name}: no PanelSettings at Resources/"
                        + $"{PANEL_SETTINGS_RESOURCE}. The screen cannot render. Recreate it "
                        + "at Assets/Resources/PoFootballPanelSettings.asset.");
                    return;
                }

                _document.panelSettings = settings;
            }
        }

        protected virtual void Start()
        {
            if (_document == null || _document.panelSettings == null)
            {
                return;
            }

            UnsafeRoot = _document.rootVisualElement;

            if (UnsafeRoot == null)
            {
                Debug.LogError($"{GetType().Name}: UIDocument has no root visual element.");
                return;
            }

            UnsafeRoot.Clear();

            _safeArea = new VisualElement();
            _safeArea.name = "SafeArea";
            _safeArea.style.flexGrow = 1f;
            UnsafeRoot.Add(_safeArea);

            Root = _safeArea;

            ApplySafeArea();
            BuildUi();
            _isBuilt = true;

            BuildUiAudio();
        }

        /// <summary>
        /// Gives every pressable thing on every screen a tap sound, in one place.
        ///
        /// WHY IT IS HERE AND NOT IN Systems_UiTheme.Button. The theme is a static
        /// class with no scene presence, so a click handler there would need an
        /// AudioSource from somewhere — which means either a singleton or a service
        /// locator, and .claude/rules/architecture.md rules out both. This class is
        /// already the one thing every screen derives from and it already owns a
        /// GameObject, so it can own one AudioSource and hear every click through
        /// the panel's own event system.
        ///
        /// TrickleDown, so the callback runs on the way DOWN to the target rather
        /// than bubbling back up. A Button that calls StopPropagation on its click —
        /// which the diagnostic overlay's toggle does — would otherwise silence
        /// itself, and "some buttons click and some do not" is a worse bug than no
        /// sound at all.
        ///
        /// The screen registers on UnsafeRoot rather than Root because the version
        /// stamp and any full-bleed chrome live outside the safe-area inset, and a
        /// tap anywhere in the document should sound the same.
        /// </summary>
        private void BuildUiAudio()
        {
            _tapClip = Resources.Load<AudioClip>(TAP_CLIP_RESOURCE);

            if (_tapClip == null)
            {
                // Not an error. A missing click is a silent button, not a broken
                // screen, and every screen in the game still works without it.
                return;
            }

            _uiAudio = gameObject.AddComponent<AudioSource>();
            _uiAudio.playOnAwake = false;
            _uiAudio.loop = false;

            // 2D. A menu button is not somewhere on the field, and in SCN_GAME the
            // listener is a moving broadcast microphone — a positional UI click
            // would pan around as the ball moved.
            _uiAudio.spatialBlend = 0f;

            UnsafeRoot.RegisterCallback<ClickEvent>(OnAnyClick, TrickleDown.TrickleDown);
        }

        private void OnAnyClick(ClickEvent evt)
        {
            // Only actual controls, not the empty chrome between them. Without this
            // test a tap on the scoreboard or on bare field clicks like a button.
            if (_uiAudio == null || !(evt.target is Button))
            {
                return;
            }

            _uiAudio.PlayOneShot(_tapClip, Systems_AudioSettings.EffectsBus);
        }

        /// <summary>
        /// Re-inset when the device rotates, the window resizes, or the simulator
        /// swaps handset. Cheap: two struct comparisons, and it only writes styles
        /// when something actually moved.
        /// </summary>
        protected virtual void Update()
        {
            if (_safeArea == null)
            {
                return;
            }

            ApplySafeArea();
        }

        private void ApplySafeArea()
        {
            Rect safeArea = UnityEngine.Screen.safeArea;
            Vector2Int screenSize = new Vector2Int(
                UnityEngine.Screen.width, UnityEngine.Screen.height);

            if (safeArea == _lastSafeArea && screenSize == _lastScreenSize)
            {
                return;
            }

            _lastSafeArea = safeArea;
            _lastScreenSize = screenSize;

            if (screenSize.x <= 0 || screenSize.y <= 0)
            {
                return;
            }

            // The panel scales, so the inset has to be expressed as a fraction of
            // the screen rather than in pixels — a 100 px notch is not 100 panel
            // units unless the scale happens to be 1.
            float panelWidth = UnsafeRoot.resolvedStyle.width;
            float panelHeight = UnsafeRoot.resolvedStyle.height;

            // Before the first layout pass the resolved size is NaN. Leave the
            // inset at zero and pick it up next frame rather than writing NaN
            // padding, which silently collapses the whole tree.
            if (float.IsNaN(panelWidth) || float.IsNaN(panelHeight)
                || panelWidth <= 0f || panelHeight <= 0f)
            {
                return;
            }

            float leftPixels = safeArea.xMin;
            float rightPixels = screenSize.x - safeArea.xMax;

            // Screen space is y-up and UI Toolkit is y-down, so the safe area's
            // yMin is the distance from the BOTTOM of the display.
            float bottomPixels = safeArea.yMin;
            float topPixels = screenSize.y - safeArea.yMax;

            ExpandForCutouts(screenSize, ref topPixels, ref bottomPixels);

            float left = leftPixels / screenSize.x * panelWidth;
            float right = rightPixels / screenSize.x * panelWidth;
            float bottom = bottomPixels / screenSize.y * panelHeight;
            float top = topPixels / screenSize.y * panelHeight;

            _safeArea.style.paddingLeft = left;
            _safeArea.style.paddingRight = right;
            _safeArea.style.paddingTop = top;
            _safeArea.style.paddingBottom = bottom;
        }

        /// <summary>
        /// Grows the top and bottom insets to clear any display cutout.
        ///
        /// THIS IS THE PART THAT SAFE AREA ALONE DOES NOT DO. Screen.safeArea
        /// generally accounts for the status bar and the home indicator, but on a
        /// great many Android handsets a punch-hole camera is NOT subtracted from
        /// it — it is reported separately in Screen.cutouts, and safeArea happily
        /// claims the full display. Insetting by safeArea alone therefore looks
        /// correct on an iPhone and still renders the game clock underneath the
        /// camera on a Pixel, which is exactly what this project was doing.
        ///
        /// Only cutouts in the top or bottom band are considered. A cutout in the
        /// middle of the screen is a folding hinge, not something to inset around.
        /// </summary>
        private static void ExpandForCutouts(
            Vector2Int screenSize, ref float topPixels, ref float bottomPixels)
        {
            Rect[] cutouts = UnityEngine.Screen.cutouts;

            if (cutouts == null)
            {
                return;
            }

            float middle = screenSize.y * 0.5f;

            for (int index = 0; index < cutouts.Length; index++)
            {
                Rect cutout = cutouts[index];

                if (cutout.height <= 0f || cutout.width <= 0f)
                {
                    continue;
                }

                if (cutout.center.y > middle)
                {
                    // Top cutout: inset far enough to clear its lowest edge.
                    topPixels = Mathf.Max(topPixels, screenSize.y - cutout.yMin);
                }
                else
                {
                    bottomPixels = Mathf.Max(bottomPixels, cutout.yMax);
                }
            }
        }

        /// <summary>
        /// True once BuildUi has run. Message handlers can fire before Start — the
        /// container is built in the scope's Awake — so anything that touches an
        /// element must check this first.
        /// </summary>
        protected bool IsBuilt => _isBuilt;
    }
}
