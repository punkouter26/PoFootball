using System;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

namespace PoFootball.Views
{
    /// <summary>
    /// The persistent status HUD: the five-corner chrome that is identical on
    /// every player-facing screen.
    ///
    ///     top-left      the title, so a screenshot always says what it is of
    ///     top-centre    the live frame rate
    ///     top-right     MENU
    ///     bottom-left   DEBUG, which opens the diagnostic sheet — development
    ///                   builds and the Editor only, see DiagnosticsAllowed
    ///     bottom-right  the build version
    ///
    /// WHY IT IS SPAWNED AND NOT PLACED IN THE SCENES. Every other screen in this
    /// project is a component someone dropped on a GameObject, which means the two
    /// player-facing scenes can and did drift apart — SCN_GAME had a frame-time
    /// pill and SCN_MENU had none, SCN_MENU had a version stamp and SCN_GAME had
    /// none. "Consistent" is not something you can assert about chrome that is
    /// authored twice. <see cref="Systems_StatusHudBootstrap"/> creates exactly one
    /// of these per player-facing scene load, so there is one authority for where
    /// the five corners are and no scene edit can disagree with it.
    ///
    /// IT REPLACED Systems_PerformanceOverlayView, which stood down in every scene
    /// this HUD is spawned in and has since been deleted along with its SCN_GAME
    /// object; the frame timing and draw-call readings it carried live here.
    ///
    /// THE DEBUG SHEET IS WRITTEN IN ENGLISH, WORST FIRST. The old panel printed
    /// four rows of instrument readings — "FRAME 16.8 ms p50 41.2 ms p99" — which
    /// tells someone who already knows this codebase what is wrong and tells
    /// everyone else nothing. Every line on this sheet is a finding: what is wrong,
    /// how bad, and what to do about it, sorted so the worst thing is the first
    /// thing read. The raw numbers are still there, underneath, for when the
    /// finding is not enough.
    ///
    /// It costs one float written into a pre-allocated ring per frame. Everything
    /// that allocates is behind a quarter-second countdown, and the findings are
    /// only composed while the sheet is actually open.
    ///
    /// IT CAN SAY WHERE THE TIME WENT, NOT ONLY THAT IT WENT. "Below target" used
    /// to end with "it is rendering or simulation load", which is the question,
    /// not the answer. Six ProfilerRecorders settle it: the render counters say
    /// how much was drawn, and two player-loop timers say what a physics tick
    /// costs before anything is drawn at all. They are read by the engine, not
    /// sampled here, so they add nothing to the per-frame path above.
    ///
    /// EVERY ONE OF THOSE NAMES WAS READ OFF THE RUNNING EDITOR, and two of the
    /// obvious ones were wrong. Unity 6.6 has no "Draw Calls Count" in the Render
    /// category — it is split by how the call was issued — and the counter that
    /// does carry that exact name belongs to UI Toolkit, so a recorder opened by
    /// name alone silently reports the HUD's own draw calls as the game's. And
    /// ML-Agents brackets inference with the legacy Profiler.BeginSample, which a
    /// ProfilerRecorder cannot see at all: there is no separate inference timer
    /// here because there is nothing to attach one to. The agent step below
    /// contains it.
    /// </summary>
    [DefaultExecutionOrder(-70)]
    [DisallowMultipleComponent]
    public sealed class Systems_StatusHudView : Systems_ScreenView
    {
        /// <summary>Frames kept for the percentile window — two seconds at 60 FPS.</summary>
        private const int SAMPLE_CAPACITY = 120;

        /// <summary>How often the labels are rewritten. Faster than this is unreadable.</summary>
        private const float REFRESH_INTERVAL = 0.25f;

        /// <summary>Above the HUD and above the old diagnostic panel, so it is never occluded.</summary>
        private const float PANEL_SORT_ORDER = 300f;

        private const float BYTES_PER_MEGABYTE = 1024f * 1024f;

        /// <summary>Longest log line the sheet quotes back. Beyond this it is a stack trace, not a message.</summary>
        private const int MAX_QUOTED_LOG = 110;

        /// <summary>Findings the sheet will show. Beyond six the point is lost anyway.</summary>
        private const int MAX_FINDINGS = 6;

        /// <summary>
        /// Frames each timer keeps. One second at 60 FPS: the step cost is steady
        /// from tick to tick, so a short window is enough and stays current.
        /// </summary>
        private const int TIMER_WINDOW = 60;

        /// <summary>
        /// Every MonoBehaviour FixedUpdate in one tick. In a game scene that is
        /// the ML-Agents academy step and nothing else of any size: twenty-two
        /// players observing, deciding — heuristic or model — and applying the
        /// action. The referee and the director are VContainer entry points and
        /// run in their own player-loop slot, outside this marker.
        /// </summary>
        private const string AGENT_STEP_MARKER = "FixedUpdate.ScriptRunBehaviourFixedUpdate";

        private const string PHYSICS_STEP_MARKER = "FixedUpdate.Physics2DFixedUpdate";

        /// <summary>
        /// What one tick may cost before it is a finding, in milliseconds. Half a
        /// 60 FPS frame: at 1x a frame carries a tick five times in six, so past
        /// this the simulation has taken most of the budget before the renderer
        /// has started.
        /// </summary>
        private const float STEP_BUDGET_MS = 8f;

        /// <summary>
        /// Set-pass calls a frame before it is a finding: twice what SCN_GAME
        /// costs today.
        ///
        /// MEASURED 2026-10-02, 1080x1920, every effect on: 139 set-pass calls,
        /// of which 52 are everything that is not a shadow. The other ninety are
        /// Systems_StadiumRigView's one casting light — about four passes per
        /// caster. With two lights casting, which is how this rig used to be
        /// built, the same scene cost 225.
        ///
        /// A tripwire rather than a limit. Nobody has measured what a handset
        /// tolerates, so this cannot say 139 is too many; it can say the number
        /// doubled, which nothing in this game should make it do.
        /// </summary>
        private const int SET_PASS_BUDGET = 280;

        /// <summary>A timer that has no reading yet, or no marker to read.</summary>
        private const float NO_READING = -1f;

        private const float NANOSECONDS_PER_MILLISECOND = 1000000f;

        private readonly float[] _samples = new float[SAMPLE_CAPACITY];
        private readonly float[] _sortScratch = new float[SAMPLE_CAPACITY];
        private readonly StringBuilder _builder = new StringBuilder(512);

        /// <summary>Pre-allocated, so composing the sheet does not allocate a list every quarter second.</summary>
        private readonly Finding[] _findings = new Finding[MAX_FINDINGS];

        private int _sampleCount;
        private int _sampleCursor;
        private float _refreshCountdown;

        private long _lastMonoHeap;
        private float _allocPerSecond;

        private int _errorCount;
        private int _warningCount;
        private string _firstError = string.Empty;

        private Label _fps;
        private Button _debugButton;
        private VisualElement _sheet;
        private Label _sheetFindings;
        private Label _sheetRaw;
        private bool _sheetOpen;

        /// <summary>Worst p99 seen across the whole session, for the telemetry file.</summary>
        private float _worstFrameMs;

        private bool _telemetryWritten;

        // Instruments. Structs wrapping a native handle: started in OnEnable,
        // disposed in OnDisable, and an invalid one reads as "no reading" rather
        // than throwing, which is what a renamed counter in a later Unity will be.
        private ProfilerRecorder _setPassCalls;
        private ProfilerRecorder _standardDraws;
        private ProfilerRecorder _instancedDraws;
        private ProfilerRecorder _batcherDraws;
        private ProfilerRecorder _agentStep;
        private ProfilerRecorder _physicsStep;

        /// <summary>
        /// One diagnosed problem. Severity is the sort key and nothing else — the
        /// numbers are arbitrary, their ORDER is the claim being made. A struct in a
        /// pre-allocated array rather than a class in a list, because this is
        /// composed four times a second while the sheet is open.
        /// </summary>
        private struct Finding
        {
            public int Severity;
            public string Text;
        }

        /// <summary>
        /// Whether this build may show the DEBUG chip and the sheet behind it.
        ///
        /// THE GATE WAS DOCUMENTED IN TWO PLACES AND IMPLEMENTED IN NEITHER.
        /// CLAUDE.md calls the development APK "the only artifact whose DEBUG sheet
        /// appears", and Editor_BuildAndroid says this sheet "is gated on
        /// Debug.isDebugBuild, which is exactly right — a retail install has no
        /// business quoting heap sizes at a player". Both were describing
        /// the old Systems_PerformanceOverlayView, which did carry the check. When this
        /// class superseded that one the check did not come with it, so the signed
        /// Play bundle would have shipped a DEBUG button one tap from the device
        /// model, the heap size and the first logged error.
        ///
        /// Only the chip and the sheet are gated. The frame rate, the version and
        /// the session telemetry file are what a bug report from a retail install
        /// needs, and none of them is an instrument panel.
        ///
        /// Debug.isDebugBuild rather than #if DEVELOPMENT_BUILD, for the reason
        /// the class this replaced gives: Unity 6.6 deprecates the symbol.
        /// </summary>
        private static bool DiagnosticsAllowed => Debug.isDebugBuild;

        protected override void Awake()
        {
            base.Awake();

            if (TryGetComponent(out UIDocument document))
            {
                document.sortingOrder = PANEL_SORT_ORDER;
            }
        }

        private void OnEnable()
        {
            // The single most useful thing a debug sheet can say is "something threw".
            // Nothing else in this project was watching for that on a device.
            Application.logMessageReceived += OnLogMessage;
            StartInstruments();
        }

        private void OnDisable()
        {
            Application.logMessageReceived -= OnLogMessage;

            // Telemetry first: it reads the instruments it is about to lose.
            WriteTelemetry();
            StopInstruments();
        }

        protected override void Start()
        {
            _lastMonoHeap = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
            base.Start();
        }

        // --- Layout ------------------------------------------------------------

        protected override void BuildUi()
        {
            VisualElement layer = Systems_UiTheme.Layer();
            Root.Add(layer);

            layer.Add(BuildTopBar());
            layer.Add(BuildBottomBar());

            if (DiagnosticsAllowed)
            {
                _sheet = BuildDebugSheet();
                layer.Add(_sheet);

                ApplySheetVisibility();
            }

            Refresh();
        }

        private VisualElement BuildTopBar()
        {
            VisualElement bar = new VisualElement();
            bar.style.position = Position.Absolute;
            bar.style.left = 0;
            bar.style.right = 0;
            bar.style.top = 0;
            bar.style.height = Systems_UiTheme.STATUS_BAR_HEIGHT;
            bar.style.flexDirection = FlexDirection.Row;
            bar.style.alignItems = Align.Center;
            bar.style.justifyContent = Justify.SpaceBetween;
            bar.style.backgroundColor = Systems_UiTheme.SurfaceOverField;
            Systems_UiTheme.SetPadding(
                bar, Systems_UiTheme.SPACE_XS, Systems_UiTheme.SPACE_M);
            Systems_UiTheme.ApplyElevation(bar);

            // TOP-LEFT. The wordmark rather than the scene name: this is what a
            // screenshot in a bug report needs to identify, and "SCN_GAME" means
            // nothing to whoever is holding the phone.
            Label title = Systems_UiTheme.Text(
                "POFOOTBALL", Systems_UiTheme.TEXT_BODY, Systems_UiTheme.Accent,
                FontStyle.Bold, Systems_UiTheme.Typeface.Display);
            title.style.letterSpacing = 2f;
            bar.Add(title);

            // TOP-RIGHT.
            bar.Add(BuildChip("MENU", OnMenuPressed));

            // TOP-CENTRE, AND ABSOLUTE RATHER THAN A THIRD FLEX CHILD. A child
            // between two others is centred only while those two happen to be the
            // same width, and "POFOOTBALL" is nothing like the width of MENU.
            // Spanning the bar and centring the text inside it puts the number in
            // the actual centre of the screen on every device.
            _fps = Systems_UiTheme.Text(
                "-- FPS", Systems_UiTheme.TEXT_CAPTION, Systems_UiTheme.TextMuted,
                FontStyle.Bold, Systems_UiTheme.Typeface.Body);
            _fps.style.position = Position.Absolute;
            _fps.style.left = 0;
            _fps.style.right = 0;
            _fps.style.unityTextAlign = TextAnchor.MiddleCenter;
            _fps.pickingMode = PickingMode.Ignore;
            bar.Add(_fps);

            return bar;
        }

        private VisualElement BuildBottomBar()
        {
            VisualElement bar = new VisualElement();
            bar.style.position = Position.Absolute;
            bar.style.left = 0;
            bar.style.right = 0;
            bar.style.bottom = 0;
            bar.style.height = Systems_UiTheme.STATUS_FOOTER_HEIGHT;
            bar.style.flexDirection = FlexDirection.Row;
            bar.style.alignItems = Align.Center;
            bar.style.justifyContent = Justify.SpaceBetween;
            bar.style.backgroundColor = Systems_UiTheme.SurfaceOverField;
            Systems_UiTheme.SetPadding(
                bar, Systems_UiTheme.SPACE_XS, Systems_UiTheme.SPACE_M);

            // BOTTOM-LEFT. A spacer where the chip would be in a release build:
            // the bar spreads its children apart, and with one child the version
            // would land in the left corner instead of the right.
            if (DiagnosticsAllowed)
            {
                _debugButton = BuildChip("DEBUG", OnDebugPressed);
                bar.Add(_debugButton);
            }
            else
            {
                bar.Add(Systems_UiTheme.Filler());
            }

            // BOTTOM-RIGHT. CLAUDE.md section 3 asked for the build number on the
            // opening screen and got it in the top-left corner of SCN_MENU alone.
            // It is on every screen now, in the corner this HUD reserves for it.
            Label version = Systems_UiTheme.Text(
                "v" + Application.version, Systems_UiTheme.TEXT_CAPTION,
                Systems_UiTheme.TextMuted, FontStyle.Normal,
                Systems_UiTheme.Typeface.Body);
            version.pickingMode = PickingMode.Ignore;
            bar.Add(version);

            return bar;
        }

        /// <summary>
        /// A small pressable. Not one of Systems_UiTheme's action sizers: those size
        /// a 78%-wide primary action, and these are chrome that must not eat the
        /// corners of a portrait screen. Still comfortably over a 48 dp touch
        /// target once the 1080-wide panel is scaled onto a handset.
        /// </summary>
        private static Button BuildChip(string label, Action onClick)
        {
            Button chip = Systems_UiTheme.Button(
                label, Systems_UiTheme.SurfaceRaised, onClick);
            chip.style.color = Systems_UiTheme.TextPrimary;
            chip.style.fontSize = Systems_UiTheme.TEXT_CAPTION;
            chip.style.minHeight = Systems_UiTheme.STATUS_CHIP_HEIGHT;
            chip.style.minWidth = 150;
            Systems_UiTheme.SetPadding(
                chip, Systems_UiTheme.SPACE_XS, Systems_UiTheme.SPACE_M);
            return chip;
        }

        private VisualElement BuildDebugSheet()
        {
            VisualElement sheet = Systems_UiTheme.Column();
            sheet.style.position = Position.Absolute;
            sheet.style.left = Systems_UiTheme.SPACE_M;
            sheet.style.right = Systems_UiTheme.SPACE_M;
            sheet.style.bottom = Systems_UiTheme.STATUS_FOOTER_HEIGHT + Systems_UiTheme.SPACE_S;

            // Capped, not sized. A device that finds six problems must not push the
            // sheet up behind the scoreboard; one that finds none must not leave a
            // half-empty slab over the field.
            sheet.style.maxHeight = Length.Percent(64f);
            sheet.style.backgroundColor = Systems_UiTheme.SurfaceScrim;
            Systems_UiTheme.SetPadding(sheet, Systems_UiTheme.SPACE_M);
            Systems_UiTheme.SetRadius(sheet, Systems_UiTheme.RADIUS);
            Systems_UiTheme.ApplyElevation(sheet);

            Label heading = Systems_UiTheme.Caption("WHAT NEEDS ATTENTION");
            heading.style.marginBottom = Systems_UiTheme.SPACE_S;
            sheet.Add(heading);

            _sheetFindings = Systems_UiTheme.Text(
                string.Empty, Systems_UiTheme.TEXT_CAPTION,
                Systems_UiTheme.TextPrimary, FontStyle.Normal,
                Systems_UiTheme.Typeface.Body);

            // Findings are sentences, not rows of digits. Without this each is one
            // clipped line on a portrait screen, which is the exact failure this
            // sheet exists to replace.
            _sheetFindings.style.whiteSpace = WhiteSpace.Normal;
            sheet.Add(_sheetFindings);

            Label rawHeading = Systems_UiTheme.Caption("READINGS");
            rawHeading.style.marginTop = Systems_UiTheme.SPACE_M;
            rawHeading.style.marginBottom = Systems_UiTheme.SPACE_XS;
            sheet.Add(rawHeading);

            _sheetRaw = Systems_UiTheme.Text(
                string.Empty, Systems_UiTheme.TEXT_CAPTION,
                Systems_UiTheme.TextMuted, FontStyle.Normal,
                Systems_UiTheme.Typeface.Body);
            _sheetRaw.style.whiteSpace = WhiteSpace.Normal;
            sheet.Add(_sheetRaw);

            return sheet;
        }

        // --- Input -------------------------------------------------------------

        private void OnMenuPressed()
        {
            // Already there. Reloading the menu on top of itself would throw the
            // LAST GAME card away, which is the one piece of state this screen
            // carries between scenes.
            if (Systems_StatusHudBootstrap.IsMenuScene(gameObject.scene))
            {
                return;
            }

            Systems_SceneRouter.LoadMenu();
        }

        private void OnDebugPressed()
        {
            _sheetOpen = !_sheetOpen;
            ApplySheetVisibility();

            if (_sheetOpen)
            {
                Refresh();
            }
        }

        private void ApplySheetVisibility()
        {
            _sheet.style.display = _sheetOpen ? DisplayStyle.Flex : DisplayStyle.None;
            _debugButton.style.color = _sheetOpen
                ? Systems_UiTheme.Accent
                : Systems_UiTheme.TextPrimary;
        }

        // --- Sampling ----------------------------------------------------------

        protected override void Update()
        {
            base.Update();

            if (!IsBuilt)
            {
                return;
            }

            // Unscaled. This measures how long the frame took to produce, which is a
            // wall-clock question and must not move when Sim Speed scales time.
            _samples[_sampleCursor] = Time.unscaledDeltaTime * 1000f;
            _sampleCursor = (_sampleCursor + 1) % SAMPLE_CAPACITY;

            if (_sampleCount < SAMPLE_CAPACITY)
            {
                _sampleCount++;
            }

            _refreshCountdown -= Time.unscaledDeltaTime;

            if (_refreshCountdown > 0f)
            {
                return;
            }

            _refreshCountdown = REFRESH_INTERVAL;
            Refresh();
        }

        private void Refresh()
        {
            float median = Percentile(0.50f);
            float p99 = Percentile(0.99f);

            // The first two seconds of a scene are dominated by shader compilation
            // and asset loads. Reporting that as the session's worst frame would put
            // a false finding at the top of every sheet, so the window has to be
            // full before anything is recorded as a peak.
            if (_sampleCount >= SAMPLE_CAPACITY && p99 > _worstFrameMs)
            {
                _worstFrameMs = p99;
            }

            long monoHeap = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
            long delta = monoHeap - _lastMonoHeap;
            _lastMonoHeap = monoHeap;

            // A negative delta means a collection ran inside the window. Reporting
            // that as a negative allocation rate would be nonsense; zero is honest.
            _allocPerSecond = Mathf.Max(0f, delta / REFRESH_INTERVAL);

            RefreshFps(median);

            if (_sheetOpen)
            {
                RefreshSheet(median, p99, monoHeap);
            }
        }

        private void RefreshFps(float median)
        {
            float fps = median > 0f ? 1000f / median : 0f;

            _builder.Clear();
            AppendFixed(_builder, fps, 0);
            _builder.Append(" FPS");
            _fps.text = _builder.ToString();

            // 60 is the contract (CLAUDE.md section 3). Amber below 50, red below 30.
            _fps.style.color = fps < 30f
                ? Systems_UiTheme.Negative
                : fps < 50f
                    ? Systems_UiTheme.Accent
                    : Systems_UiTheme.Positive;
        }

        // --- The sheet ---------------------------------------------------------

        private void RefreshSheet(float median, float p99, long monoHeap)
        {
            int count = Diagnose(median, p99, monoHeap);
            SortFindings(count);

            _builder.Clear();

            if (count == 0)
            {
                _builder.Append(
                    "All clear. The frame rate is on target, nothing has thrown, "
                    + "and the renderer and the physics tick are both what this "
                    + "build expects.");
            }
            else
            {
                for (int index = 0; index < count; index++)
                {
                    if (index > 0)
                    {
                        _builder.Append('\n');
                    }

                    _builder.Append(index + 1);
                    _builder.Append(". ");
                    _builder.Append(_findings[index].Text);
                }
            }

            _sheetFindings.text = _builder.ToString();
            _sheetFindings.style.color = count == 0
                ? Systems_UiTheme.Positive
                : _findings[0].Severity >= 80
                    ? Systems_UiTheme.Negative
                    : Systems_UiTheme.Accent;

            RefreshRawReadings(median, p99, monoHeap);
        }

        /// <summary>
        /// Turns readings into findings. Severity orders the sheet and nothing
        /// else: something threw beats something is slow beats something is
        /// misconfigured.
        /// </summary>
        private int Diagnose(float median, float p99, long monoHeap)
        {
            int count = 0;

            if (_errorCount > 0)
            {
                Add(ref count, 100,
                    _errorCount + (_errorCount == 1 ? " error has" : " errors have")
                    + " been logged this session. First one: \"" + _firstError
                    + "\". Fix this before anything below it — the rest may be "
                    + "symptoms. Full text is in `adb logcat -s Unity`.");
            }

            if (p99 > 33f)
            {
                Add(ref count, 90,
                    "The game stutters. The worst frames in the last two seconds "
                    + "took " + Fixed(p99, 0) + " ms, a visible hitch against a "
                    + "16.7 ms budget. Usually a garbage collection or a "
                    + "play-boundary spike: check the allocation rate below, and if "
                    + "it is high the cause is code allocating inside Update or "
                    + "FixedUpdate.");
            }
            else if (median > 20f)
            {
                Add(ref count, 70,
                    "The game is running below target: " + Fixed(1000f / median, 0)
                    + " FPS against 60. This is a sustained cost rather than a "
                    + "spike, so it is load, not garbage. " + AttributeFrameCost(median));
            }

            // Clamped, because a timer with no reading reports NO_READING and one
            // missing instrument must not subtract from the other.
            ReadTimer(_agentStep, out float agentStepMs, out _);
            ReadTimer(_physicsStep, out float physicsStepMs, out _);

            agentStepMs = Mathf.Max(0f, agentStepMs);
            physicsStepMs = Mathf.Max(0f, physicsStepMs);

            if (agentStepMs + physicsStepMs > STEP_BUDGET_MS)
            {
                Add(ref count, 72,
                    "The simulation step is expensive: " + Fixed(agentStepMs, 1)
                    + " ms for the twenty-two players to decide and act and "
                    + Fixed(physicsStepMs, 1) + " ms of physics, on every 20 ms "
                    + "tick. That is spent before anything is drawn, so no graphics "
                    + "setting gets it back. If a trained brain is loaded this is "
                    + "inference; if not, it is the heuristic.");
            }

            long setPass = Reading(_setPassCalls);

            if (setPass > SET_PASS_BUDGET)
            {
                Add(ref count, 45,
                    "The renderer is changing state " + setPass + " times a frame, "
                    + "more than twice what this scene normally costs. Either "
                    + "something has stopped batching — most often code touching "
                    + "`renderer.material`, which clones the material per object "
                    + "— or a second light has started casting shadows, at about "
                    + "ninety set-pass calls each.");
            }

            if (_allocPerSecond > 8192f)
            {
                Add(ref count, 85,
                    "Something allocates every frame: "
                    + Fixed(_allocPerSecond / 1024f, 0) + " KB per second of managed "
                    + "heap. performance.md's rule is zero allocation in Update, "
                    + "FixedUpdate and LateUpdate. The usual causes are string "
                    + "concatenation, LINQ, or a `new` in a per-frame path.");
            }

            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan)
            {
                Add(ref count, 60,
                    "This device is not on Vulkan — it fell back to "
                    + SystemInfo.graphicsDeviceType
                    + ". The build lists Vulkan first, so either the driver refused "
                    + "it or the device is on Unity's Vulkan deny list. Expect lower "
                    + "frame rates than the target hardware.");
            }

            if (Application.targetFrameRate != 60 || QualitySettings.vSyncCount != 0)
            {
                Add(ref count, 55,
                    "The frame-rate cap is not what this build asked for "
                    + "(targetFrameRate=" + Application.targetFrameRate + ", vSync="
                    + QualitySettings.vSyncCount + "; it should be 60 and 0). vSync "
                    + "above zero makes targetFrameRate a no-op — see "
                    + "Systems_DisplayBootstrap.");
            }

            // 0.02 exactly. Every brain in this project was fitted against that tick
            // and a different one silently evaluates them on different dynamics.
            if (!Mathf.Approximately(Time.fixedDeltaTime, 0.02f))
            {
                Add(ref count, 75,
                    "The physics tick is " + Fixed(Time.fixedDeltaTime * 1000f, 1)
                    + " ms, not the 20 ms this project pins. Every trained policy was "
                    + "fitted against 20 ms, so this is not the simulation they were "
                    + "trained on. Do not ship this build.");
            }

            // Severity 15, not 40, since the HUD's own speed chip (Systems_SimSpeedSystem)
            // is a legitimate writer. This sheet cannot tell a chosen 2x from a stray
            // one, so it says which two things may set it and leaves the reader to
            // know whether they tapped the chip.
            if (!Mathf.Approximately(Time.timeScale, 1f))
            {
                Add(ref count, 15,
                    "Time is scaled to x" + Fixed(Time.timeScale, 2)
                    + ". Expected if the game's speed chip or Tools > PoFootball > Sim "
                    + "Speed is set; the per-frame numbers below are then per real "
                    + "second, not per game second. Anything else setting it is a bug.");
            }

            if (monoHeap > 256L * 1024L * 1024L)
            {
                Add(ref count, 50,
                    "The managed heap has grown to "
                    + Fixed(monoHeap / BYTES_PER_MEGABYTE, 0)
                    + " MB. Android kills the process well before this becomes "
                    + "unbounded, so something is holding references it should have "
                    + "released — most likely an undisposed MessagePipe subscription.");
            }

            if (UnityEngine.Screen.cutouts != null
                && UnityEngine.Screen.cutouts.Length > 0
                && Mathf.Approximately(Root.resolvedStyle.paddingTop, 0f))
            {
                Add(ref count, 65,
                    "This display has a cutout but the top safe-area inset is zero, "
                    + "so the HUD is being drawn underneath it. "
                    + "Systems_ScreenView.ExpandForCutouts should have caught that — "
                    + "it means Screen.cutouts reported a rectangle the inset logic "
                    + "did not classify as a top cutout.");
            }

            if (_warningCount > 0)
            {
                Add(ref count, 10,
                    _warningCount + (_warningCount == 1 ? " warning was" : " warnings were")
                    + " logged. Nothing is broken, but they are worth reading in "
                    + "`adb logcat -s Unity` before a release build.");
            }

            return count;
        }

        private void Add(ref int count, int severity, string text)
        {
            if (count >= MAX_FINDINGS)
            {
                return;
            }

            _findings[count].Severity = severity;
            _findings[count].Text = text;
            count++;
        }

        /// <summary>
        /// Insertion sort, descending by severity. Six elements at most, four times
        /// a second, on a pre-allocated array — there is nothing here worth a
        /// comparer delegate and the allocation it would bring.
        /// </summary>
        private void SortFindings(int count)
        {
            for (int index = 1; index < count; index++)
            {
                Finding current = _findings[index];
                int scan = index - 1;

                while (scan >= 0 && _findings[scan].Severity < current.Severity)
                {
                    _findings[scan + 1] = _findings[scan];
                    scan--;
                }

                _findings[scan + 1] = current;
            }
        }

        private void RefreshRawReadings(float median, float p99, long monoHeap)
        {
            _builder.Clear();

            _builder.Append("Frame ");
            AppendFixed(_builder, median, 1);
            _builder.Append(" ms typical, ");
            AppendFixed(_builder, p99, 1);
            _builder.Append(" ms worst   |   heap ");
            AppendFixed(_builder, monoHeap / BYTES_PER_MEGABYTE, 1);
            _builder.Append(" MB, ");
            AppendFixed(_builder, _allocPerSecond / 1024f, 0);
            _builder.Append(" KB/s\n");

            _builder.Append(SystemInfo.graphicsDeviceType);
            _builder.Append("   |   ");
            _builder.Append(UnityEngine.Screen.width);
            _builder.Append('x');
            _builder.Append(UnityEngine.Screen.height);
            _builder.Append("   |   safe area ");
            AppendFixed(_builder, Root.resolvedStyle.paddingTop, 0);
            _builder.Append(" top / ");
            AppendFixed(_builder, Root.resolvedStyle.paddingBottom, 0);
            _builder.Append(" bottom\n");

            _builder.Append("physics ");
            AppendFixed(_builder, Time.fixedDeltaTime * 1000f, 1);
            _builder.Append(" ms   |   time x");
            AppendFixed(_builder, Time.timeScale, 2);
            _builder.Append("   |   ");
            _builder.Append(_errorCount);
            _builder.Append(" errors, ");
            _builder.Append(_warningCount);
            _builder.Append(" warnings\n");

            AppendRenderReadings(_builder);
            _builder.Append('\n');
            AppendStepReadings(_builder);
            _builder.Append('\n');

            _builder.Append(SystemInfo.deviceModel);
            _builder.Append("   |   ");
            _builder.Append(SystemInfo.operatingSystem);

            _sheetRaw.text = _builder.ToString();
        }

        // --- Instruments -------------------------------------------------------

        private void StartInstruments()
        {
            // By category AND name. "Draw Calls Count" on its own resolves to UI
            // Toolkit's counter, not the renderer's — see the class note.
            _setPassCalls = ProfilerRecorder.StartNew(
                ProfilerCategory.Render, "SetPass Calls Count");
            _standardDraws = ProfilerRecorder.StartNew(
                ProfilerCategory.Render, "Standard Draw Calls Count");
            _instancedDraws = ProfilerRecorder.StartNew(
                ProfilerCategory.Render, "Standard Instanced Draw Calls Count");
            _batcherDraws = ProfilerRecorder.StartNew(
                ProfilerCategory.Render, "SRP Batcher Draw Calls Count");

            _agentStep = StartTimer(AGENT_STEP_MARKER);
            _physicsStep = StartTimer(PHYSICS_STEP_MARKER);
        }

        private void StopInstruments()
        {
            // Dispose is safe on a recorder that never became valid, and on one
            // already disposed — OnDisable runs again on a scene unload.
            _setPassCalls.Dispose();
            _standardDraws.Dispose();
            _instancedDraws.Dispose();
            _batcherDraws.Dispose();
            _agentStep.Dispose();
            _physicsStep.Dispose();
        }

        /// <summary>
        /// By name alone, because the player-loop markers sit in a category
        /// ProfilerCategory has no constant for, and these two names are unique.
        /// </summary>
        private static ProfilerRecorder StartTimer(string marker)
        {
            return new ProfilerRecorder(
                marker, TIMER_WINDOW,
                ProfilerRecorderOptions.Default | ProfilerRecorderOptions.StartImmediately);
        }

        private static long Reading(ProfilerRecorder counter)
        {
            return counter.Valid ? counter.LastValue : 0L;
        }

        /// <summary>
        /// Draw calls of every kind the 2D renderer issues. The engine counts them
        /// separately by how they were submitted; a viewer wants the sum.
        /// </summary>
        private long DrawCalls()
        {
            return Reading(_standardDraws) + Reading(_instancedDraws)
                + Reading(_batcherDraws);
        }

        /// <summary>
        /// Mean cost of a marker over its window, two ways: per call, which for a
        /// player-loop marker is per physics tick and does not move with Sim
        /// Speed, and per rendered frame, which does and is what a frame-rate
        /// finding needs. Both are NO_READING until the marker has fired.
        /// </summary>
        private static void ReadTimer(
            ProfilerRecorder timer, out float perCallMs, out float perFrameMs)
        {
            perCallMs = NO_READING;
            perFrameMs = NO_READING;

            if (!timer.Valid)
            {
                return;
            }

            int frames = timer.Count;
            long nanoseconds = 0L;
            long calls = 0L;

            for (int frame = 0; frame < frames; frame++)
            {
                ProfilerRecorderSample sample = timer.GetSample(frame);
                nanoseconds += sample.Value;
                calls += sample.Count;
            }

            if (calls == 0L || frames == 0)
            {
                return;
            }

            perCallMs = nanoseconds / (float)calls / NANOSECONDS_PER_MILLISECOND;
            perFrameMs = nanoseconds / (float)frames / NANOSECONDS_PER_MILLISECOND;
        }

        /// <summary>
        /// The second half of the below-target finding: which side of the frame
        /// the time is on, from the two step timers.
        /// </summary>
        private string AttributeFrameCost(float frameMs)
        {
            ReadTimer(_agentStep, out _, out float agentFrameMs);
            ReadTimer(_physicsStep, out _, out float physicsFrameMs);

            if (agentFrameMs < 0f || physicsFrameMs < 0f)
            {
                return "Clear Presentation Effects on the scene's lifetime scope "
                    + "to see whether the field alone holds 60.";
            }

            float simulationMs = agentFrameMs + physicsFrameMs;

            if (simulationMs > frameMs * 0.5f)
            {
                return "Most of it is the simulation: " + Fixed(simulationMs, 1)
                    + " ms of each " + Fixed(frameMs, 1) + " ms frame goes on "
                    + "stepping the players and the physics, which no graphics "
                    + "setting will change.";
            }

            return "It is not the simulation, which takes only "
                + Fixed(simulationMs, 1) + " ms of each " + Fixed(frameMs, 1)
                + " ms frame — it is rendering: " + DrawCalls() + " draw calls in "
                + Reading(_setPassCalls) + " set-pass calls. Clear Presentation "
                + "Effects on the scene's lifetime scope to confirm the field "
                + "alone holds 60.";
        }

        private void AppendRenderReadings(StringBuilder builder)
        {
            builder.Append("draw ");

            if (!_setPassCalls.Valid)
            {
                builder.Append("counters unavailable");
                return;
            }

            builder.Append(DrawCalls());
            builder.Append(" calls (");
            builder.Append(Reading(_batcherDraws));
            builder.Append(" SRP-batched)   |   ");
            builder.Append(Reading(_setPassCalls));
            builder.Append(" set-pass");
        }

        private void AppendStepReadings(StringBuilder builder)
        {
            ReadTimer(_agentStep, out float agentStepMs, out _);
            ReadTimer(_physicsStep, out float physicsStepMs, out _);

            builder.Append("per tick: agents ");
            AppendMilliseconds(builder, agentStepMs);
            builder.Append("   |   physics ");
            AppendMilliseconds(builder, physicsStepMs);
        }

        private static void AppendMilliseconds(StringBuilder builder, float milliseconds)
        {
            if (milliseconds < 0f)
            {
                builder.Append("n/a");
                return;
            }

            AppendFixed(builder, milliseconds, 2);
            builder.Append(" ms");
        }

        // --- Logs --------------------------------------------------------------

        private void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            if (type == LogType.Warning)
            {
                _warningCount++;
                return;
            }

            if (type != LogType.Error && type != LogType.Exception
                && type != LogType.Assert)
            {
                return;
            }

            _errorCount++;

            // The FIRST error, not the latest: a single fault usually cascades, and
            // the tenth message is a consequence of the first one.
            if (_errorCount == 1)
            {
                _firstError = condition.Length > MAX_QUOTED_LOG
                    ? condition.Substring(0, MAX_QUOTED_LOG) + "..."
                    : condition;
            }
        }

        // --- Telemetry ---------------------------------------------------------

        /// <summary>
        /// Writes one JSON file per session under persistentDataPath, so a device
        /// run can be pulled off with `adb pull` and diffed against the previous
        /// one. There was no off-device telemetry of any kind before this: the only
        /// places the game reported anything were the Editor console and logcat,
        /// both of which are gone the moment the app closes.
        ///
        /// Deliberately not JsonUtility — that needs a serializable DTO with public
        /// fields, which csharp-unity.md's encapsulation rule exists to prevent, and
        /// this runs once per session, where there is no cost to spelling it out.
        /// </summary>
        private void WriteTelemetry()
        {
            if (_telemetryWritten || _sampleCount == 0)
            {
                return;
            }

            _telemetryWritten = true;

            try
            {
                string directory = Path.Combine(
                    Application.persistentDataPath, "telemetry");
                Directory.CreateDirectory(directory);

                string file = Path.Combine(
                    directory,
                    "session-" + gameObject.scene.name + "-"
                    + DateTime.UtcNow.ToString(
                        "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                    + ".json");

                float median = Percentile(0.50f);

                var json = new StringBuilder(768);
                json.Append("{\n");
                AppendJson(json, "version", Application.version);
                AppendJson(json, "scene", gameObject.scene.name);
                AppendJson(json, "device", SystemInfo.deviceModel);
                AppendJson(json, "os", SystemInfo.operatingSystem);
                AppendJson(json, "graphics", SystemInfo.graphicsDeviceType.ToString());
                AppendJson(json, "screen",
                    UnityEngine.Screen.width + "x" + UnityEngine.Screen.height);
                AppendJson(json, "firstError", _firstError);
                AppendNumber(json, "frameMsTypical", Fixed(median, 2));
                AppendNumber(json, "frameMsWorst", Fixed(_worstFrameMs, 2));
                AppendNumber(json, "fpsTypical",
                    Fixed(median > 0f ? 1000f / median : 0f, 1));
                AppendNumber(json, "heapMb", Fixed(
                    UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong()
                    / BYTES_PER_MEGABYTE, 2));
                AppendNumber(json, "physicsMs", Fixed(Time.fixedDeltaTime * 1000f, 2));

                // The last second before the scene closed, not a session mean:
                // these are steady-state costs and the window is all that is kept.
                // A timer with no reading is written as -1, not omitted, so every
                // file has the same keys.
                ReadTimer(_agentStep, out float agentStepMs, out _);
                ReadTimer(_physicsStep, out float physicsStepMs, out _);

                AppendNumber(json, "agentStepMs", Fixed(agentStepMs, 3));
                AppendNumber(json, "physicsStepMs", Fixed(physicsStepMs, 3));
                AppendNumber(json, "drawCalls",
                    DrawCalls().ToString(CultureInfo.InvariantCulture));
                AppendNumber(json, "setPassCalls",
                    Reading(_setPassCalls).ToString(CultureInfo.InvariantCulture));
                AppendNumber(json, "sessionSeconds", Fixed(Time.realtimeSinceStartup, 1));
                AppendNumber(json, "errors",
                    _errorCount.ToString(CultureInfo.InvariantCulture));
                AppendNumber(json, "warnings",
                    _warningCount.ToString(CultureInfo.InvariantCulture));

                // The last entry carries no comma. Written out rather than trimmed,
                // because a trailing comma is invalid JSON and this file exists to
                // be read by a tool, not by eye.
                json.Append("  \"targetFrameRate\": ");
                json.Append(Application.targetFrameRate.ToString(
                    CultureInfo.InvariantCulture));
                json.Append("\n}\n");

                File.WriteAllText(file, json.ToString());
            }
            catch (Exception exception)
            {
                // A diagnostic that crashes the app it is diagnosing is worse than
                // no diagnostic. Storage can be full or read-only; say so, carry on.
                Debug.LogWarning(
                    "[PoFootball] Telemetry not written: " + exception.Message);
            }
        }

        /// <summary>
        /// Escapes the control characters that actually occur here, not the whole
        /// JSON grammar. A logged exception message carries a newline between the
        /// message and "Parameter name: ...", and a raw newline inside a JSON string
        /// is invalid — the first telemetry file pulled off a device would not parse.
        /// </summary>
        private static void AppendJson(StringBuilder builder, string key, string value)
        {
            builder.Append("  \"");
            builder.Append(key);
            builder.Append("\": \"");
            builder.Append(value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t"));
            builder.Append("\",\n");
        }

        private static void AppendNumber(
            StringBuilder builder, string key, string value)
        {
            builder.Append("  \"");
            builder.Append(key);
            builder.Append("\": ");
            builder.Append(value);
            builder.Append(",\n");
        }

        // --- Numbers -----------------------------------------------------------

        private float Percentile(float fraction)
        {
            if (_sampleCount == 0)
            {
                return 0f;
            }

            Array.Copy(_samples, _sortScratch, _sampleCount);
            Array.Sort(_sortScratch, 0, _sampleCount);

            int index = Mathf.Clamp(
                Mathf.RoundToInt(fraction * (_sampleCount - 1)), 0, _sampleCount - 1);

            return _sortScratch[index];
        }

        /// <summary>
        /// Fixed-decimal formatting without a format string, which would box the
        /// float on every call. Invariant culture throughout: a debug sheet that
        /// prints "16,8 ms" on a German handset is a bug nobody can reproduce.
        /// </summary>
        private static string Fixed(float value, int decimals)
        {
            var builder = new StringBuilder(16);
            AppendFixed(builder, value, decimals);
            return builder.ToString();
        }

        private static void AppendFixed(
            StringBuilder builder, float value, int decimals)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                builder.Append('0');
                return;
            }

            if (value < 0f)
            {
                builder.Append('-');
                value = -value;
            }

            int scale = 1;
            for (int power = 0; power < decimals; power++)
            {
                scale *= 10;
            }

            int scaled = Mathf.RoundToInt(value * scale);
            builder.Append((scaled / scale).ToString(CultureInfo.InvariantCulture));

            if (decimals <= 0)
            {
                return;
            }

            builder.Append('.');
            int remainder = scaled % scale;

            for (int divisor = scale / 10; divisor >= 1; divisor /= 10)
            {
                builder.Append(
                    (remainder / divisor).ToString(CultureInfo.InvariantCulture));
                remainder %= divisor;
            }
        }
    }
}
