using System;
using System.Text;
using PoFootball.Systems;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.UIElements;
using VContainer;

namespace PoFootball.Views
{
    /// <summary>
    /// The real-time diagnostic overlay: frame time, garbage, draw calls and what
    /// the simulation is actually doing, on screen, while the game runs.
    ///
    /// WHY IT EXISTS. The project had no runtime performance readout of any kind.
    /// The single mention of a frame rate anywhere in the codebase was one startup
    /// log line in Systems_DisplayBootstrap announcing what the cap had been SET to
    /// — which is not the same as what the game achieves, and is exactly the number
    /// that is least interesting. Every performance rule in .claude/rules/
    /// performance.md ("zero heap allocations in Update", "aim for the lowest draw
    /// call count possible") was therefore unfalsifiable at runtime on the device
    /// that matters. Graphy is in the package manifest and would have answered the
    /// first half of that, but it is UGUI, and CLAUDE.md section 3 permits UI
    /// Toolkit only.
    ///
    /// IT REPORTS PERCENTILES, NOT AN AVERAGE. A mean frame time hides precisely
    /// the failure this project is most likely to have: an allocation spike on a
    /// play boundary. Twenty good frames and one 40 ms hitch average out to a
    /// number that looks fine and feels awful, so the panel shows the median and
    /// the 99th percentile of a rolling two-second window. p99 is the one to read.
    ///
    /// IT COSTS ALMOST NOTHING TO RUN. The per-frame path writes one float into a
    /// pre-allocated ring and returns; there is no allocation, no string work and
    /// no layout. The labels are rebuilt four times a second, and only then does
    /// anything allocate — one string per row per update, which is roughly two
    /// hundred bytes a second and is the price of the whole feature. The
    /// percentile sort runs on a pre-allocated scratch array, in place.
    ///
    /// IT STARTS COLLAPSED. Expanded, it covers a third of a portrait screen, which
    /// is not something to ship switched on; collapsed it is a small frame-time pill
    /// in the corner. Tapping either state toggles it. There is no keyboard
    /// shortcut on purpose — the legacy Input API is blocked by the repository's
    /// own hooks, the project ships no .inputactions asset, and a tap target is the
    /// right affordance on the portrait handset this game is built for anyway.
    /// </summary>
    [DefaultExecutionOrder(-70)]
    [DisallowMultipleComponent]
    public sealed class Systems_PerformanceOverlayView : Systems_ScreenView,
        Systems_IInjectableView
    {
        /// <summary>
        /// Frames retained for the percentile window. 120 is two seconds at the 60
        /// FPS target — long enough to catch a play-boundary hitch, short enough
        /// that the number still tracks what is on screen right now.
        /// </summary>
        private const int SAMPLE_CAPACITY = 120;

        /// <summary>
        /// Label refresh rate. Four a second is readable — faster and the digits
        /// blur into noise — and it is what keeps the string allocation negligible.
        /// </summary>
        private const float REFRESH_INTERVAL = 0.25f;

        private const float BYTES_PER_MEGABYTE = 1024f * 1024f;

        /// <summary>Panel sort order, above the HUD so the overlay is never occluded.</summary>
        private const float PANEL_SORT_ORDER = 100f;

        [Tooltip(
            "Build the overlay at all. Clear it to strip the diagnostic panel from "
            + "a build without removing the component from the scene.")]
        [SerializeField] private bool _enableOverlay = true;

        [Tooltip("Start expanded. Off ships a small frame-time pill instead.")]
        [SerializeField] private bool _startExpanded;

        private Systems_PresentationBudget _budget;
        private Systems_PlayerRegistry _registry;

        private readonly float[] _samples = new float[SAMPLE_CAPACITY];

        /// <summary>
        /// Scratch buffer the percentile sort runs in. Pre-allocated because
        /// Array.Sort on a fresh copy four times a second is four 480-byte
        /// allocations a second, in the one class whose whole job is to notice that
        /// kind of thing.
        /// </summary>
        private readonly float[] _sortScratch = new float[SAMPLE_CAPACITY];

        private readonly StringBuilder _builder = new StringBuilder(256);

        private int _sampleCount;
        private int _sampleCursor;
        private float _refreshCountdown;

        private VisualElement _panel;
        private Label _pill;
        private Label _frameLine;
        private Label _memoryLine;
        private Label _renderLine;
        private Label _sceneLine;

        private bool _expanded;

        /// <summary>
        /// Managed heap size at the previous refresh, so the panel can report the
        /// DELTA — bytes allocated per second — rather than the total. The total
        /// only ever climbs and says nothing about whether this frame allocated.
        /// </summary>
        private long _lastMonoHeap;

        [Inject]
        public void Construct(
            Systems_PresentationBudget budget, Systems_PlayerRegistry registry)
        {
            _budget = budget;
            _registry = registry;
        }

        protected override void Awake()
        {
            base.Awake();

            // Its own panel sort order so the overlay draws over the HUD. Both
            // screens share PoFootballPanelSettings, and within one panel the
            // documents are ordered by this value alone.
            if (TryGetComponent(out UIDocument document))
            {
                document.sortingOrder = PANEL_SORT_ORDER;
            }
        }

        /// <summary>
        /// Whether this build is allowed to show a diagnostic panel at all.
        ///
        /// THE SCENE FLAG WAS NOT A RELEASE GATE, and it was being used as one. The
        /// only conditions on the overlay were _enableOverlay — serialized true in
        /// SCN_GAME — and Systems_PresentationBudget.EffectsEnabled, which is true
        /// for ANY non-headless run in Systems_SimMode.Game. A signed Play Store
        /// build is exactly that, so a retail install rendered a pill reading
        /// "16.8 ms" over the game, one tap from a panel quoting draw calls and Mono
        /// heap. Measured on a real run, not inferred.
        ///
        /// Shipping the component but not the panel is deliberate: the scene keeps
        /// one object graph across every build type, and a development build gets
        /// its diagnostics back with no scene edit.
        ///
        /// Debug.isDebugBuild rather than a #if on DEVELOPMENT_BUILD, because Unity
        /// 6.6 deprecates that symbol (UAC0009) and points at this property instead.
        /// It is true in the editor and in any player built with "Development Build"
        /// ticked, and false in exactly the case that matters — the signed release
        /// bundle that goes to Play.
        /// </summary>
        private static bool DiagnosticsAllowed => Debug.isDebugBuild;

        protected override void Start()
        {
            // Never in a release player, never in training, never headless. A
            // diagnostic panel that costs a training sweep wall-clock is measuring
            // the wrong thing, there is no display to draw it on in batch mode, and
            // a retail build has no business showing either.
            if (!DiagnosticsAllowed || !_enableOverlay || _budget == null
                || !_budget.EffectsEnabled)
            {
                enabled = false;
                return;
            }

            _expanded = _startExpanded;
            _lastMonoHeap = Profiler.GetMonoUsedSizeLong();

            base.Start();
        }

        protected override void BuildUi()
        {
            VisualElement layer = Systems_UiTheme.Layer();
            Root.Add(layer);

            _pill = BuildPill();
            layer.Add(_pill);

            _panel = BuildPanel();
            layer.Add(_panel);

            ApplyExpansion();
        }

        /// <summary>
        /// The collapsed state: one frame-time reading, bottom-left, out of the way
        /// of the HUD's scoreboard at the top and its QUIT button at the centre
        /// bottom.
        /// </summary>
        private Label BuildPill()
        {
            Label pill = Systems_UiTheme.Text(
                "-- ms", Systems_UiTheme.TEXT_CAPTION,
                Systems_UiTheme.TextMuted, FontStyle.Bold);

            pill.style.position = Position.Absolute;
            pill.style.left = Systems_UiTheme.SPACE_M;
            pill.style.bottom = Systems_UiTheme.SPACE_M;
            pill.style.backgroundColor = Systems_UiTheme.SurfaceOverField;

            Systems_UiTheme.SetPadding(
                pill, Systems_UiTheme.SPACE_XS, Systems_UiTheme.SPACE_S);

            Systems_UiTheme.SetRadius(pill, Systems_UiTheme.SPACE_S);

            // Labels are built non-pickable by the theme, which is right everywhere
            // else and wrong here: this one is the control that opens the panel.
            pill.pickingMode = PickingMode.Position;
            pill.RegisterCallback<PointerDownEvent>(OnToggle);

            return pill;
        }

        private VisualElement BuildPanel()
        {
            VisualElement panel = Systems_UiTheme.Column();
            panel.style.position = Position.Absolute;
            panel.style.left = Systems_UiTheme.SPACE_M;
            panel.style.right = Systems_UiTheme.SPACE_M;
            panel.style.bottom = Systems_UiTheme.SPACE_M;
            panel.style.backgroundColor = Systems_UiTheme.SurfaceScrim;

            Systems_UiTheme.SetPadding(panel, Systems_UiTheme.SPACE_M);
            Systems_UiTheme.SetRadius(panel, Systems_UiTheme.RADIUS);
            Systems_UiTheme.ApplyElevation(panel);

            panel.pickingMode = PickingMode.Position;
            panel.RegisterCallback<PointerDownEvent>(OnToggle);

            Label heading = Systems_UiTheme.Caption("DIAGNOSTICS");
            heading.style.marginBottom = Systems_UiTheme.SPACE_S;
            panel.Add(heading);

            _frameLine = BuildRow(panel);
            _memoryLine = BuildRow(panel);
            _renderLine = BuildRow(panel);
            _sceneLine = BuildRow(panel);

            return panel;
        }

        private static Label BuildRow(VisualElement parent)
        {
            // Body face explicitly: these are dense rows of digits and units that
            // have to be read rather than glanced at, and the theme's Auto rule
            // would be picking purely on font size.
            Label row = Systems_UiTheme.Text(
                string.Empty, Systems_UiTheme.TEXT_CAPTION,
                Systems_UiTheme.TextPrimary, FontStyle.Normal,
                Systems_UiTheme.Typeface.Body);

            row.style.marginBottom = Systems_UiTheme.SPACE_XS;
            parent.Add(row);
            return row;
        }

        private void OnToggle(PointerDownEvent evt)
        {
            _expanded = !_expanded;
            ApplyExpansion();
            evt.StopPropagation();
        }

        private void ApplyExpansion()
        {
            _panel.style.display = _expanded ? DisplayStyle.Flex : DisplayStyle.None;
            _pill.style.display = _expanded ? DisplayStyle.None : DisplayStyle.Flex;
        }

        /// <summary>
        /// The per-frame path. One float written into a ring, one countdown
        /// decremented. Everything expensive is behind the countdown.
        /// </summary>
        protected override void Update()
        {
            base.Update();

            if (!IsBuilt)
            {
                return;
            }

            // Unscaled: this measures how long the frame took to produce, which is
            // a wall-clock question and must not move when anything scales time.
            RecordSample(Time.unscaledDeltaTime * 1000f);

            _refreshCountdown -= Time.unscaledDeltaTime;

            if (_refreshCountdown > 0f)
            {
                return;
            }

            _refreshCountdown = REFRESH_INTERVAL;
            Refresh();
        }

        private void RecordSample(float milliseconds)
        {
            _samples[_sampleCursor] = milliseconds;
            _sampleCursor = (_sampleCursor + 1) % SAMPLE_CAPACITY;

            if (_sampleCount < SAMPLE_CAPACITY)
            {
                _sampleCount++;
            }
        }

        private void Refresh()
        {
            float median = Percentile(0.50f);
            float p99 = Percentile(0.99f);

            RefreshFrameLine(median, p99);
            RefreshMemoryLine();
            RefreshRenderLine();
            RefreshSceneLine();

            if (!_expanded)
            {
                _builder.Clear();
                AppendFixed(_builder, median, 1);
                _builder.Append(" ms");
                _pill.text = _builder.ToString();

                _pill.style.color = median > 20f
                    ? Systems_UiTheme.Negative
                    : Systems_UiTheme.TextMuted;
            }
        }

        private void RefreshFrameLine(float median, float p99)
        {
            _builder.Clear();
            _builder.Append("FRAME  ");
            AppendFixed(_builder, median, 1);
            _builder.Append(" ms p50   ");
            AppendFixed(_builder, p99, 1);
            _builder.Append(" ms p99   ");

            // Derived from the median rather than counted, so it agrees with the
            // number printed beside it. A separately counted FPS and a percentile
            // frame time disagreeing on the same row is a bug report waiting to
            // happen.
            AppendFixed(_builder, median > 0f ? 1000f / median : 0f, 0);
            _builder.Append(" fps");

            _frameLine.text = _builder.ToString();

            // The target is 60 FPS (CLAUDE.md section 3), so 16.7 ms is the budget.
            // Amber from 20 ms, red from 33 — one dropped frame at 30 Hz.
            _frameLine.style.color = p99 > 33f
                ? Systems_UiTheme.Negative
                : p99 > 20f
                    ? Systems_UiTheme.Accent
                    : Systems_UiTheme.Positive;
        }

        private void RefreshMemoryLine()
        {
            long monoHeap = Profiler.GetMonoUsedSizeLong();
            long delta = monoHeap - _lastMonoHeap;
            _lastMonoHeap = monoHeap;

            // Per second, from a per-interval delta. A negative delta means a
            // collection ran inside the window; reporting it as 0 rather than as a
            // negative allocation rate is the honest reading.
            float perSecond = Mathf.Max(0f, delta / REFRESH_INTERVAL);

            _builder.Clear();
            _builder.Append("MEM    ");
            AppendFixed(_builder, monoHeap / BYTES_PER_MEGABYTE, 1);
            _builder.Append(" MB mono   ");
            AppendFixed(_builder, perSecond / 1024f, 0);
            _builder.Append(" KB/s alloc");

            _memoryLine.text = _builder.ToString();

            // performance.md's golden rule is zero allocation in the per-frame
            // path. Anything sustained here is a finding, so the threshold is low
            // on purpose — this panel itself accounts for well under 1 KB/s.
            _memoryLine.style.color = perSecond > 8192f
                ? Systems_UiTheme.Negative
                : Systems_UiTheme.TextPrimary;
        }

        /// <summary>
        /// Draw calls, batches and SetPass.
        ///
        /// EDITOR ONLY, AND THERE IS NO RUNTIME EQUIVALENT. UnityEditor.UnityStats
        /// is the only API that exposes these counters and it does not exist in a
        /// player, so a build shows the row as unavailable rather than pretending
        /// to a number it cannot read. The alternative — deleting the row in builds
        /// — would make the panel a different shape in the two places it runs.
        /// </summary>
        private void RefreshRenderLine()
        {
            _builder.Clear();
            _builder.Append("DRAW   ");

#if UNITY_EDITOR
            _builder.Append(UnityEditor.UnityStats.drawCalls);
            _builder.Append(" calls   ");
            _builder.Append(UnityEditor.UnityStats.setPassCalls);
            _builder.Append(" setpass   ");
            _builder.Append(UnityEditor.UnityStats.triangles);
            _builder.Append(" tris");
#else
            _builder.Append("editor only");
#endif

            _renderLine.text = _builder.ToString();
        }

        /// <summary>
        /// What the simulation is doing, which is the half of this panel that no
        /// general-purpose profiler overlay could show: the fixed timestep every
        /// trained brain was fitted against, and the physics load underneath it.
        /// </summary>
        private void RefreshSceneLine()
        {
            _builder.Clear();
            _builder.Append("SIM    ");
            AppendFixed(_builder, Time.fixedDeltaTime * 1000f, 1);
            _builder.Append(" ms tick   ");

            // From the registry, not a scene scan. FindObjectsByType allocates an
            // array on every call, and doing that four times a second inside the
            // panel whose entire purpose is to report allocation rate would make
            // this class the largest contributor to its own MEM row.
            _builder.Append(_registry != null ? _registry.RegisteredCount : 0);
            _builder.Append(" players   x");
            AppendFixed(_builder, Time.timeScale, 2);

            _sceneLine.text = _builder.ToString();

            // Δt is pinned at 0.02 by CLAUDE.md and by project settings, and an
            // override silently re-dynamics every .onnx in the project. If it ever
            // reads anything else, that is the most important number on screen.
            _sceneLine.style.color = Mathf.Abs(Time.fixedDeltaTime - 0.02f) > 0.0001f
                ? Systems_UiTheme.Negative
                : Systems_UiTheme.TextPrimary;
        }

        /// <summary>
        /// Nearest-rank percentile over the live window, sorted in place in the
        /// pre-allocated scratch buffer.
        /// </summary>
        private float Percentile(float fraction)
        {
            if (_sampleCount == 0)
            {
                return 0f;
            }

            Array.Copy(_samples, _sortScratch, _sampleCount);
            Array.Sort(_sortScratch, 0, _sampleCount);

            int index = Mathf.Clamp(
                Mathf.CeilToInt(fraction * _sampleCount) - 1, 0, _sampleCount - 1);

            return _sortScratch[index];
        }

        /// <summary>
        /// Appends a fixed-point number without going through float.ToString(format),
        /// which boxes the format and allocates on every call. StringBuilder.Append
        /// (int) does neither.
        /// </summary>
        private static void AppendFixed(StringBuilder builder, float value, int decimals)
        {
            if (decimals <= 0)
            {
                builder.Append(Mathf.RoundToInt(value));
                return;
            }

            int scale = decimals == 1 ? 10 : 100;
            int scaled = Mathf.RoundToInt(value * scale);

            builder.Append(scaled / scale);
            builder.Append('.');

            int remainder = Mathf.Abs(scaled % scale);

            if (decimals == 2 && remainder < 10)
            {
                builder.Append('0');
            }

            builder.Append(remainder);
        }
    }
}
