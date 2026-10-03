using PoFootball.Models;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoFootball.Views
{
    /// <summary>
    /// The post-game highlight player: which highlight, a board to watch it on,
    /// a scrubber over its ticks, and play, pause and single-tick steps.
    ///
    /// DOCKED UNDER THE FINAL CARD, NOT INSIDE IT. Systems_HudView owns the final
    /// overlay and centres its content — headline, box score, REMATCH, MENU — in
    /// the full height of the screen. This reel is a separate layer in a separate
    /// document drawn above it, so it has to stay out of that column by
    /// arithmetic rather than by layout: it fills the space from
    /// <see cref="FINAL_OVERLAY_HALF_HEIGHT"/> below the screen's centre down to
    /// the status footer, and the board gives up height first when that space is
    /// short. On a 1080x1920 panel that is about five hundred units, which the
    /// whole reel fits at full size; a taller handset only adds slack above it.
    ///
    /// FINAL_OVERLAY_HALF_HEIGHT IS A COUPLING, STATED HERE SO IT IS FOUND. It is
    /// half the height of the final overlay's centred column, measured off
    /// Systems_HudView and Systems_BoxScoreCard as they stand: the 58-unit
    /// headline, a nine-row card at caption size, two 96-unit buttons and their
    /// margins come to about 720, so the MENU button ends ~345 below centre and
    /// 380 leaves a gap. Add a row to the box score and this must grow with it,
    /// or the reel's top edge slides under MENU.
    ///
    /// NOTHING HERE STEALS A TAP. The overlay, its content and the dock that holds
    /// the card are all PickingMode.Ignore; only the card itself is pickable. The
    /// document sits above Systems_HudView's, so a pickable full-screen container
    /// here would silently kill REMATCH and MENU — the exact failure
    /// Systems_ScreenView's picking note describes.
    ///
    /// PLAYBACK IS WALL-CLOCK. The game is over, the simulation is not ticking,
    /// and Tools > PoFootball > Sim Speed may have left Time.timeScale at 8 —
    /// none of which should make a highlight play eight times too fast. The owner
    /// passes unscaled time; nothing here reads or writes Time at all.
    /// </summary>
    internal sealed class Systems_HighlightReel
    {
        /// <summary>See the class note. Half the final overlay's centred column, plus a gap.</summary>
        private const int FINAL_OVERLAY_HALF_HEIGHT = 380;

        /// <summary>The board's height when there is room for it, and the least it gives up to.</summary>
        private const float BOARD_MAX_HEIGHT = 300f;
        private const float BOARD_MIN_HEIGHT = 120f;

        /// <summary>
        /// Width every transport control is held to, so PLAY and PAUSE do not
        /// resize the row when they swap. Sized for "PAUSE" at TEXT_BODY in the
        /// display face plus its padding.
        /// </summary>
        private const int CONTROL_MIN_WIDTH = 120;

        /// <summary>Recorded ticks per second of playback: real time.</summary>
        private const float TICKS_PER_SECOND = 1f / Systems_GameRules.SECONDS_PER_TICK;

        private readonly Systems_ReplayHighlight[] _highlights;
        private readonly int _count;
        private readonly Systems_ReplayBoard _board;
        private readonly Systems_UiOverlay _overlay;

        private Label _indexLabel;
        private Label _detailLabel;
        private Slider _slider;
        private Button _playButton;

        private int _index;
        private float _cursor;
        private bool _playing;

        /// <param name="highlights">Filled highlights, best first.</param>
        public Systems_HighlightReel(
            Systems_ReplayHighlight[] highlights, int count, Systems_ReplayBoard board)
        {
            _highlights = highlights;
            _count = count;
            _board = board;

            _overlay = new Systems_UiOverlay(
                "HighlightReel", blocksInput: false, scrim: Color.clear);

            _overlay.Content.pickingMode = PickingMode.Ignore;
            _overlay.Content.Add(BuildDock());
        }

        public VisualElement Root => _overlay.Root;

        public void Show()
        {
            if (_count == 0)
            {
                return;
            }

            Select(0);
            _overlay.Show();
        }

        /// <summary>Advances playback. Called from the owning view's Update with unscaled time.</summary>
        public void Tick(float unscaledDeltaTime)
        {
            if (!_playing || _count == 0)
            {
                return;
            }

            float last = LastFrame();
            _cursor += unscaledDeltaTime * TICKS_PER_SECOND;

            if (_cursor >= last)
            {
                _cursor = last;
                SetPlaying(false);
            }

            _slider.SetValueWithoutNotify(_cursor);
            _board.Pose(_cursor);
        }

        // --- Layout -------------------------------------------------------------

        private VisualElement BuildDock()
        {
            // Spans the free strip under the final card. Its top is the screen's
            // centre plus the card's half height — a percentage and a padding,
            // because UI Toolkit has no calc() to add them.
            VisualElement dock = Systems_UiTheme.Column();
            dock.style.position = Position.Absolute;
            dock.style.top = Length.Percent(50f);
            dock.style.bottom = Systems_UiTheme.STATUS_FOOTER_HEIGHT + Systems_UiTheme.SPACE_S;
            dock.style.left = Systems_UiTheme.SPACE_M;
            dock.style.right = Systems_UiTheme.SPACE_M;
            dock.style.paddingTop = FINAL_OVERLAY_HALF_HEIGHT;
            dock.style.justifyContent = Justify.FlexEnd;
            dock.pickingMode = PickingMode.Ignore;

            dock.Add(BuildCard());
            return dock;
        }

        private VisualElement BuildCard()
        {
            VisualElement card = Systems_UiTheme.Column();
            card.style.backgroundColor = Systems_UiTheme.SurfaceRaised;
            card.style.flexShrink = 1f;
            card.style.minHeight = 0f;
            Systems_UiTheme.SetPadding(card, Systems_UiTheme.SPACE_M);
            Systems_UiTheme.SetRadius(card, Systems_UiTheme.RADIUS);
            Systems_UiTheme.ApplyElevation(card);

            card.Add(BuildHeader());

            VisualElement board = _board.Root;
            board.style.height = BOARD_MAX_HEIGHT;
            board.style.minHeight = BOARD_MIN_HEIGHT;
            board.style.flexShrink = 1f;
            board.style.marginTop = Systems_UiTheme.SPACE_S;
            card.Add(board);

            card.Add(BuildScrubber());
            card.Add(BuildTransport());
            return card;
        }

        private VisualElement BuildHeader()
        {
            VisualElement header = Systems_UiTheme.Row();
            header.style.justifyContent = Justify.SpaceBetween;
            header.style.flexShrink = 0f;

            _indexLabel = Systems_UiTheme.Caption(string.Empty);

            _detailLabel = Systems_UiTheme.Text(
                string.Empty, Systems_UiTheme.TEXT_BODY,
                Systems_UiTheme.TextPrimary, FontStyle.Bold);

            header.Add(_indexLabel);
            header.Add(_detailLabel);
            return header;
        }

        /// <summary>
        /// A stock UI Toolkit Slider over the tick index, restyled from code
        /// because the project ships no USS. The tracker and dragger are found by
        /// the slider's own published class names rather than by walking its
        /// children, so a theme that reorders them cannot break this.
        /// </summary>
        private VisualElement BuildScrubber()
        {
            _slider = new Slider(0f, 1f);
            _slider.style.flexShrink = 0f;
            _slider.style.height = Systems_UiTheme.STATUS_CHIP_HEIGHT;
            _slider.style.marginTop = Systems_UiTheme.SPACE_S;
            _slider.style.marginLeft = 0f;
            _slider.style.marginRight = 0f;
            _slider.style.marginBottom = 0f;

            VisualElement tracker = _slider.Q(className: BaseSlider<float>.trackerUssClassName);

            if (tracker != null)
            {
                tracker.style.top = Length.Percent(50f);
                tracker.style.height = Systems_UiTheme.SPACE_S;
                tracker.style.marginTop = -Systems_UiTheme.SPACE_S / 2;
                tracker.style.backgroundColor = Systems_UiTheme.SurfaceScrim;
                Systems_UiTheme.SetRadius(tracker, Systems_UiTheme.SPACE_S / 2);
            }

            VisualElement dragger = _slider.Q(className: BaseSlider<float>.draggerUssClassName);

            if (dragger != null)
            {
                dragger.style.top = Length.Percent(50f);
                dragger.style.width = Systems_UiTheme.SPACE_XL;
                dragger.style.height = Systems_UiTheme.SPACE_XL;
                dragger.style.marginTop = -Systems_UiTheme.SPACE_XL / 2;
                dragger.style.backgroundColor = Systems_UiTheme.Action;
                Systems_UiTheme.SetRadius(dragger, Systems_UiTheme.SPACE_XL / 2);
            }

            _slider.RegisterValueChangedCallback(OnScrubbed);
            return _slider;
        }

        private VisualElement BuildTransport()
        {
            VisualElement row = Systems_UiTheme.Row();
            row.style.justifyContent = Justify.Center;
            row.style.flexShrink = 0f;
            row.style.marginTop = Systems_UiTheme.SPACE_S;

            row.Add(Control("PREV", Systems_UiTheme.SurfaceScrim, OnPrevious));
            row.Add(Control("NEXT", Systems_UiTheme.SurfaceScrim, OnNext));

            VisualElement gap = new VisualElement();
            gap.style.width = Systems_UiTheme.SPACE_L;
            gap.pickingMode = PickingMode.Ignore;
            row.Add(gap);

            row.Add(Control("-1", Systems_UiTheme.SurfaceScrim, OnStepBack));

            _playButton = Control("PAUSE", Systems_UiTheme.Action, OnPlayPressed);
            _playButton.style.color = Systems_UiTheme.SurfaceRaised;
            row.Add(_playButton);

            row.Add(Control("+1", Systems_UiTheme.SurfaceScrim, OnStepForward));
            return row;
        }

        /// <summary>
        /// A transport button at chip height rather than TAP_TARGET: this is a
        /// row of five under a card on an already-full screen, and the status
        /// HUD's chips make the same trade at the same height for the same reason.
        /// </summary>
        private static Button Control(string label, Color tint, System.Action onClick)
        {
            Button button = Systems_UiTheme.Button(label, tint, onClick);
            button.style.color = Systems_UiTheme.TextPrimary;
            button.style.fontSize = Systems_UiTheme.TEXT_BODY;
            button.style.minHeight = Systems_UiTheme.STATUS_CHIP_HEIGHT;
            button.style.minWidth = CONTROL_MIN_WIDTH;
            button.style.marginLeft = Systems_UiTheme.SPACE_XS;
            button.style.marginRight = Systems_UiTheme.SPACE_XS;
            Systems_UiTheme.SetPadding(
                button, Systems_UiTheme.SPACE_XS, Systems_UiTheme.SPACE_M);
            return button;
        }

        // --- Control --------------------------------------------------------------

        /// <summary>Puts a highlight on the board from its first tick and plays it.</summary>
        private void Select(int index)
        {
            _index = index;
            Systems_ReplayHighlight highlight = _highlights[index];

            _slider.lowValue = 0f;
            _slider.highValue = Mathf.Max(1f, highlight.Tape.Count - 1);

            _cursor = 0f;
            _slider.SetValueWithoutNotify(0f);

            // Text built only on selection, never per frame.
            _indexLabel.text = "HIGHLIGHT " + (index + 1) + "/" + _count;
            _detailLabel.text = Systems_DisplayText.ResultBanner(
                    CaptionResult(highlight), highlight.Outcome)
                + "   " + Systems_DisplayText.YardageDetail(highlight.YardsGained);

            _board.Show(highlight);
            SetPlaying(true);
        }

        /// <summary>
        /// The result to caption a highlight with. A play that ended a quarter or
        /// the game is announced on the HUD as the period ending — "FINAL" — and
        /// that is the right banner at the time and the wrong caption on a
        /// highlight: the reason it is a highlight is what happened on it.
        /// </summary>
        private static Systems_DownResult CaptionResult(Systems_ReplayHighlight highlight)
        {
            if (highlight.Result != Systems_DownResult.EndOfGame
                && highlight.Result != Systems_DownResult.EndOfQuarter)
            {
                return highlight.Result;
            }

            switch (highlight.Outcome)
            {
                case Systems_PlayOutcome.Touchdown: return Systems_DownResult.Touchdown;
                case Systems_PlayOutcome.Interception: return Systems_DownResult.Interception;
                case Systems_PlayOutcome.FumbleLost: return Systems_DownResult.FumbleLost;
                case Systems_PlayOutcome.Safety: return Systems_DownResult.Safety;
                default: return Systems_DownResult.NextDown;
            }
        }

        private void OnPrevious()
        {
            Select((_index + _count - 1) % _count);
        }

        private void OnNext()
        {
            Select((_index + 1) % _count);
        }

        private void OnPlayPressed()
        {
            if (!_playing && _cursor >= LastFrame())
            {
                // Pressing play on the last frame means "again", not "nothing".
                _cursor = 0f;
                _slider.SetValueWithoutNotify(0f);
                _board.Pose(0f);
            }

            SetPlaying(!_playing);
        }

        private void OnStepBack()
        {
            Step(-1);
        }

        private void OnStepForward()
        {
            Step(1);
        }

        /// <summary>One recorded tick — 20 ms of the play — from the nearest whole tick.</summary>
        private void Step(int ticks)
        {
            SetPlaying(false);

            _cursor = Mathf.Clamp(Mathf.Round(_cursor) + ticks, 0f, LastFrame());
            _slider.SetValueWithoutNotify(_cursor);
            _board.Pose(_cursor);
        }

        /// <summary>A drag on the scrubber takes over from playback.</summary>
        private void OnScrubbed(ChangeEvent<float> evt)
        {
            SetPlaying(false);

            _cursor = Mathf.Clamp(evt.newValue, 0f, LastFrame());
            _board.Pose(_cursor);
        }

        private void SetPlaying(bool playing)
        {
            _playing = playing;
            _playButton.text = playing ? "PAUSE" : "PLAY";
        }

        private float LastFrame()
        {
            return Mathf.Max(0f, _highlights[_index].Tape.Count - 1);
        }
    }
}
