using System.Collections.Generic;
using PoFootball.Models;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoFootball.Views
{
    /// <summary>
    /// The look of every screen, and the factory helpers that build one.
    ///
    /// CLAUDE.md section 3 requires UI Toolkit with no .uxml and no .uss, so the
    /// style rules that would live in a stylesheet live here as C# instead. Keeping
    /// them in one static class rather than inline at each call site is what stops
    /// that rule from producing twelve slightly different greys.
    ///
    /// Colours are authored for the portrait broadcast look: a near-black surface
    /// so the green field reads as the brightest thing on screen, one warm accent
    /// reserved exclusively for the chains, and team colours that stay
    /// distinguishable when they are only a 20 px dot.
    ///
    /// SPACING IS A SCALE, NOT A NUMBER. The first version of this class exposed
    /// SetPadding(element, amount) and nothing else, so every box in the game had
    /// identical padding on all four sides. That is why the UI read flat: a card
    /// wants more room above its title than beside it, and a bottom bar on a phone
    /// wants more room below than above. The directional overloads exist so that
    /// asymmetry is expressible without anyone reaching for raw style properties.
    ///
    /// EVERYTHING HERE HAS A CALLER. This class had grown a speculative surface —
    /// a Card factory, a disabled-state helper, a reference resolution, a score
    /// type size — that nothing in the project used. Members that only this file
    /// consumes are private. Before adding a public member, name the screen that
    /// will call it.
    /// </summary>
    public static class Systems_UiTheme
    {
        // --- Palette ---------------------------------------------------------
        private static readonly Color Surface = new Color(0.043f, 0.059f, 0.051f, 1f);
        public static readonly Color SurfaceRaised = new Color(0.086f, 0.106f, 0.094f, 1f);
        public static readonly Color SurfaceScrim = new Color(0.02f, 0.03f, 0.025f, 0.92f);

        /// <summary>
        /// Chrome that sits ON TOP of the field. Deliberately translucent: the
        /// scoreboard used to be an opaque band and it cost a strip of playfield on
        /// every frame of every game for information that only changes once a play.
        /// </summary>
        public static readonly Color SurfaceOverField = new Color(0.02f, 0.031f, 0.026f, 0.72f);

        public static readonly Color TextPrimary = new Color(0.949f, 0.969f, 0.953f, 1f);
        public static readonly Color TextMuted = new Color(0.604f, 0.659f, 0.624f, 1f);

        private static readonly Color HomeColor = new Color(0.204f, 0.545f, 0.937f, 1f);
        private static readonly Color AwayColor = new Color(0.922f, 0.365f, 0.298f, 1f);

        /// <summary>
        /// Reserved for the chains and nothing else, so it always means "the line".
        ///
        /// THAT RESERVATION WAS A FICTION UNTIL NOW. Accent was simultaneously the
        /// down and distance, the possession dot, and the tint of every primary
        /// button on both screens — four meanings, which is the same as none. The
        /// possession dot now uses the team's own colour, which is what it was
        /// actually indicating, and buttons use <see cref="Action"/>.
        /// </summary>
        public static readonly Color Accent = new Color(0.984f, 0.788f, 0.220f, 1f);

        /// <summary>
        /// The tint of a primary action. Deliberately neutral: every hue in this
        /// palette already means something on a football field — amber is the
        /// chains, blue and orange are the two teams, green and red are good and
        /// bad news — and a button is the one element that means "press me"
        /// regardless of what is happening in the game.
        ///
        /// The same value as <see cref="TextPrimary"/>, and a separate token on
        /// purpose: they are one colour serving two meanings, and either may need
        /// to move without the other.
        /// </summary>
        public static readonly Color Action = new Color(0.949f, 0.969f, 0.953f, 1f);

        public static readonly Color Positive = new Color(0.353f, 0.812f, 0.482f, 1f);

        /// <summary>
        /// Bad news — a turnover, a safety.
        ///
        /// PUSHED TOWARD CRIMSON, AWAY FROM ORANGE. The previous value was
        /// (0.937, 0.412, 0.404) against an away team of (0.922, 0.365, 0.298):
        /// the same colour to any eye, at a glance, on a 20 px banner. So an
        /// interception announced itself in what the viewer had just been taught
        /// was the away team's colour, whichever team had actually made it.
        /// </summary>
        public static readonly Color Negative = new Color(0.914f, 0.267f, 0.451f, 1f);

        /// <summary>Ring drawn around whatever currently has keyboard or gamepad focus.</summary>
        private static readonly Color FocusRing = new Color(1f, 1f, 1f, 0.85f);

        // --- Metrics ---------------------------------------------------------
        public const int SPACE_XS = 4;
        public const int SPACE_S = 8;
        public const int SPACE_M = 16;
        public const int SPACE_L = 24;
        public const int SPACE_XL = 40;

        // The type scale. Every font size in the project comes from here — a
        // literal at a call site is how the menu ended up with a 76 px title and a
        // 44 px button that belonged to no scale and matched nothing on the other
        // screen.
        public const int TEXT_CAPTION = 20;
        public const int TEXT_BODY = 26;
        public const int TEXT_TITLE = 34;

        /// <summary>
        /// The two team scores on the HUD. Second-largest thing on the bar, below
        /// the down and distance, which is the number that changes every play.
        ///
        /// On the scale because it was a bare 44 at the call site — precisely the
        /// thing the note above says must never happen, sitting in the file that
        /// says it.
        /// </summary>
        public const int TEXT_SCORE = 44;

        /// <summary>Primary action buttons. Between TITLE and BANNER on purpose.</summary>
        public const int TEXT_ACTION = 44;

        public const int TEXT_BANNER = 58;

        /// <summary>The one-per-screen identity size: the menu wordmark.</summary>
        public const int TEXT_DISPLAY = 76;

        public const int RADIUS = 12;

        /// <summary>Diameter of the possession indicator beside a team's tag.</summary>
        public const int DOT_SIZE = 16;

        /// <summary>
        /// Minimum touch target. Every pressable thing is at least this tall
        /// regardless of what its text needs — at the reference width of 1080 this
        /// is a comfortable thumb target on a phone.
        /// </summary>
        public const int TAP_TARGET = 96;

        /// <summary>Standard fade for anything that appears and disappears.</summary>
        public const float TRANSITION_SECONDS = 0.22f;

        public static Color ColorOf(Systems_TeamId team)
        {
            return team == Systems_TeamId.Home ? HomeColor : AwayColor;
        }

        // --- Typeface ---------------------------------------------------------

        /// <summary>
        /// Which of the two faces a piece of text is set in.
        ///
        /// THE PROJECT SHIPPED TWO FONTS AND USED NEITHER. BebasNeue-Regular.ttf and
        /// Oswald-Variable.ttf have been in the repository since the art pass, and
        /// every label in the game was rendering in UI Toolkit's default runtime
        /// face because nothing ever assigned them — the PanelSettings had
        /// textSettings unset and no call site touched unityFontDefinition. A
        /// scoreboard set in the engine default does not read as a broadcast
        /// graphic no matter how well the boxes are arranged.
        ///
        /// The two faces divide the work the way a broadcast package does. Bebas is
        /// a tall condensed display face with no lowercase — wrong for a sentence,
        /// exactly right for a two-digit score, a 58 px result banner and the
        /// wordmark. Oswald is condensed but has a full lowercase and real weights,
        /// so it carries every label, caption and button.
        /// </summary>
        public enum Typeface
        {
            /// <summary>Pick from the size — display at TITLE and above, body below.</summary>
            Auto = 0,

            /// <summary>Bebas Neue. Numerals, headlines, the wordmark.</summary>
            Display = 1,

            /// <summary>Oswald. Everything that has to be read rather than seen.</summary>
            Body = 2,
        }

        private const string DISPLAY_FONT_RESOURCE = "Fonts/BebasNeue-Regular";
        private const string BODY_FONT_RESOURCE = "Fonts/Oswald-Variable";

        /// <summary>
        /// The size at and above which <see cref="Typeface.Auto"/> switches to the
        /// display face. TITLE is the boundary because it is the smallest size the
        /// project uses for something a viewer glances at rather than reads.
        /// </summary>
        private const int DISPLAY_FACE_MIN_SIZE = TEXT_TITLE;

        private static Font _displayFont;
        private static Font _bodyFont;
        private static bool _fontsResolved;

        /// <summary>
        /// Loads both faces once per domain.
        ///
        /// A missing font is NOT an error and must not throw. UI Toolkit falls back
        /// to its default face when unityFontDefinition is left alone, so the game
        /// stays entirely legible — it just loses the identity. Logging once and
        /// carrying on is the right failure for something purely cosmetic; a null
        /// reference here would take the whole HUD down with it.
        /// </summary>
        private static void ResolveFonts()
        {
            if (_fontsResolved)
            {
                return;
            }

            _fontsResolved = true;

            _displayFont = Resources.Load<Font>(DISPLAY_FONT_RESOURCE);
            _bodyFont = Resources.Load<Font>(BODY_FONT_RESOURCE);

            if (_displayFont == null || _bodyFont == null)
            {
                Debug.LogWarning(
                    "[PoFootball] UI fonts missing from Resources — expected "
                    + $"{DISPLAY_FONT_RESOURCE} and {BODY_FONT_RESOURCE}. Screens fall "
                    + "back to the UI Toolkit default face.");
            }
        }

        /// <summary>
        /// Sets an element's face. Public because the two screens build a handful of
        /// elements that are not Labels — a Button's text, the wordmark — and they
        /// have to be able to opt one of them into the display face explicitly.
        /// </summary>
        public static void ApplyFont(VisualElement element, Typeface typeface, int size)
        {
            ResolveFonts();

            bool wantsDisplay = typeface == Typeface.Display
                || (typeface == Typeface.Auto && size >= DISPLAY_FACE_MIN_SIZE);

            Font font = wantsDisplay ? _displayFont : _bodyFont;

            if (font == null)
            {
                return;
            }

            element.style.unityFontDefinition = new StyleFontDefinition(font);
        }

        // --- Factories -------------------------------------------------------

        /// <summary>
        /// Full-bleed root. Every screen starts with one of these; it owns the page
        /// background so a transparent panel can never show whatever the camera
        /// happens to be clearing to.
        /// </summary>
        public static VisualElement Screen()
        {
            VisualElement root = new VisualElement();
            root.style.flexGrow = 1f;
            root.style.backgroundColor = Surface;
            return root;
        }

        /// <summary>
        /// A full-bleed layer that does NOT paint a background, for chrome laid
        /// over the game. Distinct from <see cref="Screen"/> because a HUD that
        /// painted Surface would hide the field entirely.
        /// </summary>
        public static VisualElement Layer()
        {
            VisualElement layer = new VisualElement();
            layer.style.flexGrow = 1f;

            // Chrome layers are mostly empty space. Left pickable, every tap
            // anywhere on screen would be swallowed by the HUD.
            layer.pickingMode = PickingMode.Ignore;
            return layer;
        }

        public static VisualElement Row()
        {
            VisualElement row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            return row;
        }

        public static VisualElement Column()
        {
            VisualElement column = new VisualElement();
            column.style.flexDirection = FlexDirection.Column;
            return column;
        }

        /// <summary>Spacer that eats whatever room is left, pushing its siblings apart.</summary>
        public static VisualElement Filler()
        {
            VisualElement filler = new VisualElement();
            filler.style.flexGrow = 1f;
            filler.pickingMode = PickingMode.Ignore;
            return filler;
        }

        /// <summary>
        /// A spacer with a proportional weight. Two fillers of weight 1 and 2 split
        /// the leftover space one third / two thirds, which is how a layout fills a
        /// screen whose height it does not know in advance.
        /// </summary>
        public static VisualElement Filler(float weight)
        {
            VisualElement filler = Filler();
            filler.style.flexGrow = weight;
            return filler;
        }

        public static Label Text(
            string value, int size, Color color, FontStyle fontStyle = FontStyle.Normal,
            Typeface typeface = Typeface.Auto)
        {
            Label label = new Label(value);
            label.style.fontSize = size;
            label.style.color = color;
            label.style.unityFontStyleAndWeight = fontStyle;
            ApplyFont(label, typeface, size);

            // Labels default to picking up pointer events, which silently swallows
            // clicks meant for whatever is underneath them.
            label.pickingMode = PickingMode.Ignore;
            return label;
        }

        public static Label Caption(string value)
        {
            Label label = Text(value, TEXT_CAPTION, TextMuted, FontStyle.Bold);
            label.style.letterSpacing = 2f;
            return label;
        }

        /// <summary>
        /// A pressable button, built on UI Toolkit's own Button.
        ///
        /// The previous version was a bare VisualElement with PointerDown and
        /// PointerUp callbacks. It looked identical and behaved like a button for a
        /// finger, but it was not focusable, so it could not be reached with a
        /// keyboard or a gamepad, it had no disabled state, and it fired on pointer
        /// up wherever the pointer had started. Button gives all of that from the
        /// framework: focus, NavigationSubmit, and a clicked event that only fires
        /// when press and release both land on the element.
        ///
        /// The default runtime theme does style Button, which is why every one of
        /// those defaults is overwritten below rather than inherited — the project
        /// ships no .uss and the theme's grey-with-a-border look is not this game.
        /// </summary>
        public static Button Button(string label, Color tint, System.Action onClick)
        {
            Button button = new Button(onClick) { text = label };

            // Wipe the theme defaults. Margin especially — the built-in Button
            // carries 3 px on every side, which silently breaks any layout that
            // measures its own gutters.
            SetMargin(button, 0);
            SetPadding(button, SPACE_M);
            SetRadius(button, RADIUS);
            SetBorder(button, 0, Color.clear);

            button.style.backgroundColor = tint;
            button.style.color = Surface;
            button.style.fontSize = TEXT_TITLE;
            button.style.unityFontStyleAndWeight = FontStyle.Bold;
            button.style.letterSpacing = 1f;

            // Buttons are set in the DISPLAY face regardless of size. A label on a
            // button is read the way a score is — glanced at, never parsed — and
            // Auto would flip PLAY and QUIT into different faces purely because
            // ApplyControlActionSize drops the latter to TEXT_BODY.
            ApplyFont(button, Typeface.Display, TEXT_TITLE);
            button.style.minHeight = TAP_TARGET;
            button.style.unityTextAlign = TextAnchor.MiddleCenter;
            button.style.whiteSpace = WhiteSpace.NoWrap;

            EnableTransition(button, "opacity");

            // Hover and press are opacity rather than colour so one implementation
            // works for every tint the game uses.
            button.RegisterCallback<PointerEnterEvent>(_ => button.style.opacity = 0.9f);
            button.RegisterCallback<PointerLeaveEvent>(_ => button.style.opacity = 1f);
            button.RegisterCallback<PointerDownEvent>(_ => button.style.opacity = 0.72f);
            button.RegisterCallback<PointerUpEvent>(_ => button.style.opacity = 1f);

            // A focus ring, because a focusable control the user cannot see the
            // focus on is worse than one that never takes focus at all.
            button.RegisterCallback<FocusInEvent>(_ => SetBorder(button, 4, FocusRing));
            button.RegisterCallback<FocusOutEvent>(_ => SetBorder(button, 0, Color.clear));

            return button;
        }

        /// <summary>
        /// Sizes a button as the primary action on its screen.
        ///
        /// One helper rather than per-screen styling. The menu used to size PLAY by
        /// percentage HEIGHT with min/max clamps while the HUD sized its buttons by
        /// percentage WIDTH, so the same component came out visibly different on the
        /// two screens for no reason a user could infer. Width is the axis that
        /// matters on a 9:16 phone; height comes from the tap target and the type.
        /// </summary>
        public static void ApplyPrimaryActionSize(Button button)
        {
            button.style.width = Length.Percent(78f);
            button.style.maxWidth = 620;
            button.style.minHeight = TAP_TARGET;
            button.style.fontSize = TEXT_ACTION;
            button.style.marginBottom = SPACE_M;
        }

        /// <summary>
        /// A secondary action: same footprint, quieter type. Used for the choice
        /// a user is not expected to want by default.
        /// </summary>
        public static void ApplySecondaryActionSize(Button button)
        {
            ApplyPrimaryActionSize(button);
            button.style.fontSize = TEXT_TITLE;
        }

        /// <summary>
        /// A control that sits over the live field — QUIT, the speed cycle.
        /// Deliberately smaller and quieter than the two above: these are an escape
        /// hatch, not the point of the screen.
        ///
        /// Here rather than in Systems_HudView, where it lived as a third private
        /// sizer. The whole reason the other two are in this class is that the same
        /// component coming out different on two screens is a bug a user cannot
        /// explain, and a sizer that opts out of that rule by living elsewhere is
        /// how the drift starts again.
        /// </summary>
        public static void ApplyControlActionSize(Button button)
        {
            button.style.width = Length.Percent(34f);
            button.style.maxWidth = 260;
            button.style.minHeight = TAP_TARGET;
            button.style.fontSize = TEXT_BODY;
        }

        // --- Transitions -------------------------------------------------------

        /// <summary>
        /// Hands a property over to UI Toolkit's own transition system.
        ///
        /// Everything that used to fade in this project did so from a float
        /// counted down in a MonoBehaviour Update, which meant the HUD did work on
        /// every frame of every game in order to animate something for a fifth of a
        /// second after a play. The panel can interpolate its own styles; it just
        /// has to be told which ones.
        /// </summary>
        public static void EnableTransition(
            VisualElement element, string property, float seconds = TRANSITION_SECONDS)
        {
            element.style.transitionProperty =
                new List<StylePropertyName> { new StylePropertyName(property) };

            element.style.transitionDuration =
                new List<TimeValue> { new TimeValue(seconds, TimeUnit.Second) };

            element.style.transitionTimingFunction =
                new List<EasingFunction> { new EasingFunction(EasingMode.EaseOutCubic) };
        }

        // --- Motion ------------------------------------------------------------

        /// <summary>How much a pulsed element overshoots before settling back.</summary>
        private const float PULSE_SCALE = 1.14f;

        /// <summary>
        /// Prepares an element to be pulsed. Call once, at build time.
        ///
        /// Separate from <see cref="Pulse"/> because a transition is a STYLE, not an
        /// animation: it says "interpolate this property whenever it changes", and
        /// re-declaring it on every pulse would reset the interpolation mid-flight.
        /// </summary>
        public static void EnablePulse(VisualElement element)
        {
            EnableTransition(element, "scale", TRANSITION_SECONDS * 0.6f);
        }

        /// <summary>
        /// Briefly swells an element and lets it settle.
        ///
        /// WHAT IT IS FOR. A number that changes without moving is a number a viewer
        /// has to be already looking at to notice. The score, the down and the
        /// possession dot all changed silently — the HUD's own header notes that the
        /// down "changes on every single play and is the one number that tells a
        /// viewer what they are about to watch", and it announced itself by
        /// redrawing two glyphs in place. A sixth of a second of movement is what
        /// makes a glance find it.
        ///
        /// Driven by the panel's own scheduler rather than a coroutine or an Update
        /// countdown. The HUD deleted its per-frame fade timer for exactly this
        /// reason: nothing should tick every frame to animate something that happens
        /// six times a game.
        /// </summary>
        public static void Pulse(VisualElement element)
        {
            element.style.scale = new Scale(new Vector3(PULSE_SCALE, PULSE_SCALE, 1f));

            element.schedule
                .Execute(() => element.style.scale = new Scale(Vector3.one))
                .StartingIn((long)(TRANSITION_SECONDS * 0.6f * 1000f));
        }

        // --- Spacing -----------------------------------------------------------

        public static void SetPadding(VisualElement element, int amount)
        {
            SetPadding(element, amount, amount, amount, amount);
        }

        /// <summary>Vertical and horizontal padding, the common asymmetric case.</summary>
        public static void SetPadding(VisualElement element, int vertical, int horizontal)
        {
            SetPadding(element, vertical, horizontal, vertical, horizontal);
        }

        public static void SetPadding(
            VisualElement element, int top, int right, int bottom, int left)
        {
            element.style.paddingTop = top;
            element.style.paddingRight = right;
            element.style.paddingBottom = bottom;
            element.style.paddingLeft = left;
        }

        private static void SetMargin(VisualElement element, int amount)
        {
            element.style.marginTop = amount;
            element.style.marginRight = amount;
            element.style.marginBottom = amount;
            element.style.marginLeft = amount;
        }

        public static void SetRadius(VisualElement element, int amount)
        {
            element.style.borderTopLeftRadius = amount;
            element.style.borderTopRightRadius = amount;
            element.style.borderBottomLeftRadius = amount;
            element.style.borderBottomRightRadius = amount;
        }

        private static void SetBorder(VisualElement element, int width, Color color)
        {
            element.style.borderLeftWidth = width;
            element.style.borderRightWidth = width;
            element.style.borderTopWidth = width;
            element.style.borderBottomWidth = width;
            element.style.borderLeftColor = color;
            element.style.borderRightColor = color;
            element.style.borderTopColor = color;
            element.style.borderBottomColor = color;
        }

        // --- Elevation ---------------------------------------------------------

        /// <summary>
        /// Hairline along the top edge of a raised surface — the lit edge.
        /// </summary>
        private static readonly Color EdgeHighlight = new Color(1f, 1f, 1f, 0.10f);

        /// <summary>
        /// Line along the bottom edge — the surface's own shadow, standing in for
        /// the box-shadow the runtime panel does not have.
        /// </summary>
        private static readonly Color EdgeShadow = new Color(0f, 0f, 0f, 0.45f);

        /// <summary>
        /// Lifts a surface off the one behind it.
        ///
        /// WHY IT IS NOT A BOX SHADOW. UI Toolkit's runtime panel implements no
        /// shadow property at all — box-shadow is a USS feature the runtime
        /// renderer ignores, and this project ships no USS in the first place
        /// (CLAUDE.md section 3). What it does support is per-edge border colours,
        /// and a light hairline on top with a dark line underneath is the same cue a
        /// shadow gives: it says which way is up. On a near-black palette it is
        /// actually the stronger of the two, because a black shadow on a black
        /// surface is invisible.
        ///
        /// Applied to the scoreboard, the situation pill and the call chip — the
        /// three surfaces that sit ON the field and were previously distinguished
        /// from it by nothing but a translucent fill.
        /// </summary>
        public static void ApplyElevation(VisualElement element)
        {
            element.style.borderTopWidth = 1;
            element.style.borderBottomWidth = 2;
            element.style.borderLeftWidth = 0;
            element.style.borderRightWidth = 0;

            element.style.borderTopColor = EdgeHighlight;
            element.style.borderBottomColor = EdgeShadow;
            element.style.borderLeftColor = Color.clear;
            element.style.borderRightColor = Color.clear;
        }

        /// <summary>Pins an element to all four edges of its parent.</summary>
        public static void FillParent(VisualElement element)
        {
            element.style.position = Position.Absolute;
            element.style.left = 0;
            element.style.right = 0;
            element.style.top = 0;
            element.style.bottom = 0;
        }

        /// <summary>
        /// The build number, top-left, outside any scroll view and non-pickable —
        /// CLAUDE.md section 3 requires it on the opening screen. Absolute so it
        /// cannot be pushed around by whatever layout it is dropped into.
        /// </summary>
        public static Label VersionStamp()
        {
            Label label = Text($"v{Application.version}", TEXT_CAPTION, TextMuted);
            label.style.position = Position.Absolute;
            label.style.left = SPACE_M;
            label.style.top = SPACE_S;
            label.pickingMode = PickingMode.Ignore;
            return label;
        }
    }
}
