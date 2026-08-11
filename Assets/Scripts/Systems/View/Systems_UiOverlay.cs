using UnityEngine;
using UnityEngine.UIElements;

namespace PoFootball.Views
{
    /// <summary>
    /// One full-bleed layer that fades in, optionally dims what is behind it, and
    /// optionally takes itself away again after a few seconds.
    ///
    /// WHY THIS EXISTS. SCN_GAME had three of these, hand-built and subtly
    /// different: the result banner after every play, the final-score overlay, and
    /// the box score. Each declared its own absolute full-bleed rect, each picked
    /// its own scrim colour or none, one faded via a float counted down in Update
    /// and the other two snapped between DisplayStyle.None and Flex. Nothing
    /// coordinated them, so the banner could and did draw on top of the final
    /// whistle. One primitive with one visibility model makes that class of bug
    /// unrepresentable.
    ///
    /// HOW THE FADE WORKS, AND WHY NOT DisplayStyle. A transition cannot animate an
    /// element into or out of `display: none` — the element has no layout to
    /// interpolate from, so the fade is skipped and the overlay pops. Visibility is
    /// used instead: the element keeps its box at all times, opacity carries the
    /// animation, and visibility is switched to Hidden only once the fade has
    /// finished so it stops rendering and stops taking input.
    /// </summary>
    public sealed class Systems_UiOverlay
    {
        private readonly VisualElement _root;
        private readonly VisualElement _content;
        private readonly bool _blocksInput;

        private IVisualElementScheduledItem _autoHide;
        private IVisualElementScheduledItem _finishHide;

        /// <param name="blocksInput">
        /// True for anything the player is meant to interact with or be stopped by
        /// — the box score, the final overlay. False for a passive announcement
        /// like the result banner, which must not eat a tap aimed at the field.
        /// </param>
        public Systems_UiOverlay(string name, bool blocksInput, Color scrim)
        {
            _blocksInput = blocksInput;

            _root = new VisualElement { name = name };
            Systems_UiTheme.FillParent(_root);
            _root.style.backgroundColor = scrim;
            _root.style.opacity = 0f;
            _root.style.visibility = Visibility.Hidden;
            _root.pickingMode = PickingMode.Ignore;

            Systems_UiTheme.EnableTransition(_root, "opacity");

            _content = new VisualElement { name = name + "Content" };
            _content.style.flexGrow = 1f;
            _root.Add(_content);
        }

        /// <summary>Add this to the screen tree once, at build time.</summary>
        public VisualElement Root => _root;

        /// <summary>Where callers put their own elements.</summary>
        public VisualElement Content => _content;

        public bool IsVisible { get; private set; }

        /// <summary>
        /// Centres the content and gives it room to breathe. Convenience for the
        /// overlays that are a message rather than a document.
        /// </summary>
        public Systems_UiOverlay Centered()
        {
            _content.style.alignItems = Align.Center;
            _content.style.justifyContent = Justify.Center;
            return this;
        }

        public Systems_UiOverlay Padded(int amount)
        {
            Systems_UiTheme.SetPadding(_content, amount);
            return this;
        }

        public void Show()
        {
            CancelSchedules();
            IsVisible = true;

            _root.style.visibility = Visibility.Visible;
            _root.pickingMode = _blocksInput ? PickingMode.Position : PickingMode.Ignore;

            // Next frame, not this one. The element has just become visible and its
            // resolved opacity is still the old value; setting the target in the
            // same frame gives the transition nothing to animate from and it snaps.
            _root.schedule.Execute(() => _root.style.opacity = 1f).ExecuteLater(0);
        }

        /// <summary>
        /// Shows, then hides itself after <paramref name="seconds"/>. The timer is
        /// the panel's own scheduler rather than a countdown in a MonoBehaviour, so
        /// an overlay that is not currently animating costs nothing per frame.
        /// </summary>
        public void ShowFor(float seconds)
        {
            Show();

            _autoHide = _root.schedule
                .Execute(Hide)
                .StartingIn((long)(seconds * 1000f));
        }

        public void Hide()
        {
            CancelSchedules();
            IsVisible = false;

            _root.style.opacity = 0f;
            _root.pickingMode = PickingMode.Ignore;

            // Stop rendering only once the fade is actually over.
            _finishHide = _root.schedule
                .Execute(() =>
                {
                    if (!IsVisible)
                    {
                        _root.style.visibility = Visibility.Hidden;
                    }
                })
                .StartingIn((long)(Systems_UiTheme.TRANSITION_SECONDS * 1000f));
        }

        /// <summary>
        /// A pending auto-hide from the previous play would otherwise fire in the
        /// middle of the next one, and a pending finish-hide would blank an overlay
        /// that has just been reopened.
        /// </summary>
        private void CancelSchedules()
        {
            _autoHide?.Pause();
            _autoHide = null;

            _finishHide?.Pause();
            _finishHide = null;
        }
    }
}
