using PoFootball.Models;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoFootball.Views
{
    /// <summary>
    /// The post-game highlight player's controls: which highlight, a scrubber over
    /// its ticks, play, pause and single-tick steps, and a way back to the result.
    /// The highlight itself is drawn ON THE FIELD by Systems_ReplayView's ghosts;
    /// this class only decides what is showing and where the cursor is, and the
    /// owner reads <see cref="IsOpen"/>, <see cref="Current"/> and
    /// <see cref="Cursor"/> every frame to pose them.
    ///
    /// WHY THE FIELD AND NOT A BOARD IN THE CARD. This used to draw the play on a
    /// telestrator board inside its own card (Systems_ReplayBoard), because the
    /// final overlay's 92% scrim hid the turf and the broadcast camera was framing
    /// the real ball, parked where the last play ended. Both of those are now
    /// answered by Systems_HighlightPlaybackMessage: the HUD drops the overlay
    /// while a highlight is open and the camera frames the ghost ball. The field
    /// is the better screen by a distance — the real shapes at the real scale,
    /// lit and shadowed — and a board the size of a card was always a compromise
    /// forced by the scrim, not a choice.
    ///
    /// TWO STATES, AND THE RESULT SCREEN IS HOME. At the final whistle the reel
    /// shows only a WATCH HIGHLIGHTS button under the final card; the result, the
    /// box score, REMATCH and MENU are what a viewer sees first. Opening it swaps
    /// the button for the control card and the field takes the screen. RESULT
    /// closes it, and so does the last highlight finishing — each highlight plays
    /// through, holds its whistle frame for <see cref="END_HOLD_SECONDS"/>, and
    /// hands on to the next. Closing is what brings the final overlay back, so
    /// REMATCH and MENU are always one tap away.
    ///
    /// DOCKED AT THE BOTTOM, AND COMPACT, so the field above it is the picture.
    /// The card is a header, a scrubber and one row of transport, about 230 panel
    /// units — an eighth of a 1080x1920 panel and less of a 20:9 one. It needs no
    /// knowledge of the final card's height any more: when the card is open the
    /// final overlay is down, and the closed state is a single 56-unit button,
    /// which sits about 470 units under MENU on the shortest portrait panel.
    ///
    /// NOTHING HERE STEALS A TAP. The overlay, its content and the dock are all
    /// PickingMode.Ignore; only the button and the card are pickable. The
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
        /// <summary>
        /// Width every transport control is held to, so PLAY and PAUSE do not
        /// resize the row when they swap. Sized for "PAUSE" at TEXT_BODY in the
        /// display face plus its padding.
        /// </summary>
        private const int CONTROL_MIN_WIDTH = 120;

        /// <summary>Recorded ticks per second of playback: real time.</summary>
        private const float TICKS_PER_SECOND = 1f / Systems_GameRules.SECONDS_PER_TICK;

        /// <summary>
        /// Wall-clock seconds a finished highlight rests on its last frame before
        /// the next one starts. The last frame is the whistle — the tackle, the
        /// ball over the line — and cutting away on it shows the play without its
        /// ending.
        /// </summary>
        private const float END_HOLD_SECONDS = 1f;

        private readonly Systems_ReplayHighlight[] _highlights;
        private readonly int _count;
        private readonly Systems_UiOverlay _overlay;

        private VisualElement _openRow;
        private VisualElement _card;
        private Label _indexLabel;
        private Label _detailLabel;
        private Slider _slider;
        private Button _playButton;

        private int _index;
        private float _cursor;
        private float _held;
        private bool _playing;

        /// <param name="highlights">Filled highlights, best first.</param>
        public Systems_HighlightReel(Systems_ReplayHighlight[] highlights, int count)
        {
            _highlights = highlights;
            _count = count;

            _overlay = new Systems_UiOverlay(
                "HighlightReel", blocksInput: false, scrim: Color.clear);

            _overlay.Content.pickingMode = PickingMode.Ignore;
            _overlay.Content.Add(BuildDock());

            SetOpen(false);
        }

        public VisualElement Root => _overlay.Root;

        /// <summary>True while the control card is up and a highlight belongs on the field — playing, paused or scrubbed.</summary>
        public bool IsOpen { get; private set; }

        /// <summary>The highlight on the field. Only meaningful while <see cref="IsOpen"/>.</summary>
        public Systems_ReplayHighlight Current => _highlights[_index];

        /// <summary>Where in <see cref="Current"/> the field should be drawn, in recorded ticks.</summary>
        public float Cursor => _cursor;

        /// <summary>Puts the WATCH HIGHLIGHTS button up. Nothing plays until it is pressed.</summary>
        public void Show()
        {
            if (_count == 0)
            {
                return;
            }

            _overlay.Show();
        }

        /// <summary>
        /// Back to the result screen. Called by RESULT, by the last highlight
        /// finishing, and by the owner when it is disabled. Idempotent.
        /// </summary>
        public void Close()
        {
            if (!IsOpen)
            {
                return;
            }

            SetOpen(false);
        }

        /// <summary>Advances playback. Called from the owning view's Update with unscaled time.</summary>
        public void Tick(float unscaledDeltaTime)
        {
            if (!IsOpen || !_playing || _count == 0)
            {
                return;
            }

            float last = LastFrame();

            if (_cursor < last)
            {
                _cursor = Mathf.Min(_cursor + (unscaledDeltaTime * TICKS_PER_SECOND), last);
                _slider.SetValueWithoutNotify(_cursor);
                return;
            }

            _held += unscaledDeltaTime;

            if (_held < END_HOLD_SECONDS)
            {
                return;
            }

            if (_index + 1 < _count)
            {
                Select(_index + 1);
            }
            else
            {
                Close();
            }
        }

        // --- Layout -------------------------------------------------------------

        private VisualElement BuildDock()
        {
            VisualElement dock = Systems_UiTheme.Column();
            dock.style.position = Position.Absolute;
            dock.style.bottom = Systems_UiTheme.STATUS_FOOTER_HEIGHT + Systems_UiTheme.SPACE_S;
            dock.style.left = Systems_UiTheme.SPACE_M;
            dock.style.right = Systems_UiTheme.SPACE_M;
            dock.pickingMode = PickingMode.Ignore;

            dock.Add(BuildOpenRow());
            dock.Add(BuildCard());
            return dock;
        }

        /// <summary>
        /// The closed state: one button, centred, at chip height. Quieter than the
        /// final card's REMATCH and MENU on purpose — the result is the headline of
        /// that screen and this is an extra.
        /// </summary>
        private VisualElement BuildOpenRow()
        {
            _openRow = Systems_UiTheme.Row();
            _openRow.style.justifyContent = Justify.Center;
            _openRow.pickingMode = PickingMode.Ignore;

            Button open = Control("WATCH HIGHLIGHTS", Systems_UiTheme.SurfaceRaised, OnOpenPressed);
            Systems_UiTheme.ApplyElevation(open);
            _openRow.Add(open);
            return _openRow;
        }

        private VisualElement BuildCard()
        {
            _card = Systems_UiTheme.Column();
            _card.style.backgroundColor = Systems_UiTheme.SurfaceRaised;
            Systems_UiTheme.SetPadding(_card, Systems_UiTheme.SPACE_M);
            Systems_UiTheme.SetRadius(_card, Systems_UiTheme.RADIUS);
            Systems_UiTheme.ApplyElevation(_card);

            _card.Add(BuildHeader());
            _card.Add(BuildScrubber());
            _card.Add(BuildTransport());
            return _card;
        }

        /// <summary>
        /// What the old board's frame used to say, as a caption: which highlight
        /// of how many, and what happened on it. RESULT on the right, where a
        /// thumb leaving the transport row finds it without crossing it.
        /// </summary>
        private VisualElement BuildHeader()
        {
            VisualElement header = Systems_UiTheme.Row();
            header.style.justifyContent = Justify.SpaceBetween;
            header.style.flexShrink = 0f;

            VisualElement caption = Systems_UiTheme.Column();
            caption.style.flexGrow = 1f;
            caption.style.flexShrink = 1f;
            caption.style.minWidth = 0f;
            caption.pickingMode = PickingMode.Ignore;

            _indexLabel = Systems_UiTheme.Caption(string.Empty);

            _detailLabel = Systems_UiTheme.Text(
                string.Empty, Systems_UiTheme.TEXT_BODY,
                Systems_UiTheme.TextPrimary, FontStyle.Bold);

            caption.Add(_indexLabel);
            caption.Add(_detailLabel);

            header.Add(caption);
            header.Add(Control("RESULT", Systems_UiTheme.SurfaceScrim, OnResultPressed));
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
        /// row of five on a card that has to leave the field visible, and the
        /// status HUD's chips make the same trade at the same height for the same
        /// reason.
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

        /// <summary>
        /// Swaps the button for the card or back. Display rather than the
        /// overlay's fade: the two never share the screen, and a cross-fade
        /// between them would put both under the thumb for a fifth of a second.
        /// </summary>
        private void SetOpen(bool open)
        {
            IsOpen = open;
            _openRow.style.display = open ? DisplayStyle.None : DisplayStyle.Flex;
            _card.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;

            if (!open)
            {
                SetPlaying(false);
            }
        }

        /// <summary>Puts a highlight on the field from its first tick and plays it.</summary>
        private void Select(int index)
        {
            _index = index;
            Systems_ReplayHighlight highlight = _highlights[index];

            _slider.lowValue = 0f;
            _slider.highValue = Mathf.Max(1f, highlight.Tape.Count - 1);

            _cursor = 0f;
            _held = 0f;
            _slider.SetValueWithoutNotify(0f);

            // Text built only on selection, never per frame.
            _indexLabel.text = "HIGHLIGHT " + (index + 1) + "/" + _count;
            _detailLabel.text = Systems_DisplayText.ResultBanner(
                    CaptionResult(highlight), highlight.Outcome)
                + "   " + Systems_DisplayText.YardageDetail(highlight.YardsGained);

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

        private void OnOpenPressed()
        {
            if (_count == 0)
            {
                return;
            }

            SetOpen(true);
            Select(0);
        }

        private void OnResultPressed()
        {
            Close();
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
            }

            _held = 0f;
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

            _held = 0f;
            _cursor = Mathf.Clamp(Mathf.Round(_cursor) + ticks, 0f, LastFrame());
            _slider.SetValueWithoutNotify(_cursor);
        }

        /// <summary>A drag on the scrubber takes over from playback.</summary>
        private void OnScrubbed(ChangeEvent<float> evt)
        {
            SetPlaying(false);

            _held = 0f;
            _cursor = Mathf.Clamp(evt.newValue, 0f, LastFrame());
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
