using UnityEngine;
using UnityEngine.UIElements;

namespace PoFootball.Views
{
    /// <summary>
    /// The front end. One screen, one obvious button.
    ///
    /// The whole point of this screen is that a person who has just opened the app
    /// can be watching football one tap later, so PLAY is the largest thing on it
    /// and nothing stands between it and the game.
    ///
    /// THREE ZONES, PROPORTIONALLY WEIGHTED. The first version stacked title,
    /// Filler, PLAY, hint, card, Filler inside a container that was also set to
    /// justifyContent: Center. Those two mechanisms fight — the fillers try to push
    /// content apart while the centring tries to pull it together — and on a tall
    /// handset the result was the title pinned to the very top, everything else
    /// bunched around the middle, and roughly forty-five percent of the screen
    /// empty. The zones below carry explicit weights instead, so the layout fills
    /// whatever height it is given without anyone having to know that height.
    /// </summary>
    public sealed class Systems_MenuView : Systems_ScreenView
    {
        protected override void BuildUi()
        {
            VisualElement screen = Systems_UiTheme.Screen();
            screen.style.justifyContent = Justify.Center;

            // The content block is capped at roughly the height of the 9:16 panel
            // this design was drawn for, and centred inside whatever it is actually
            // given. Without the cap, a 0.36-aspect foldable stretches three zones
            // across 2658 px and the screen reads as four separate things floating
            // in the dark; with it, the composition stays proportional and the
            // extra height becomes deliberate margin instead of accidental gaps.
            VisualElement content = Systems_UiTheme.Column();
            content.style.width = Length.Percent(100f);
            content.style.height = Length.Percent(100f);
            content.style.maxHeight = 1800;
            Systems_UiTheme.SetPadding(
                content, Systems_UiTheme.SPACE_XL, Systems_UiTheme.SPACE_L);
            screen.Add(content);

            // Zone 1 — identity. Sits a little below the top rather than jammed
            // against it; the safe-area inset handles the cutout, this handles
            // wanting to look deliberate.
            content.Add(Systems_UiTheme.Filler(0.6f));
            content.Add(BuildTitle());

            // Zone 2 — the primary action, given the largest share of the leftover
            // space so it lands near the optical centre on any aspect.
            content.Add(Systems_UiTheme.Filler(1.4f));

            VisualElement action = Systems_UiTheme.Column();

            Button play = Systems_UiTheme.Button(
                "PLAY", Systems_UiTheme.Accent, Systems_SceneRouter.LoadGame);

            // Sized by the shared helper, so PLAY and the buttons on the final
            // overlay are the same object at the same size on both screens.
            Systems_UiTheme.ApplyPrimaryActionSize(play);
            action.Add(play);

            Label hint = Systems_UiTheme.Text(
                "Watch two learned teams play a full game.",
                Systems_UiTheme.TEXT_CAPTION,
                Systems_UiTheme.TextMuted);
            hint.style.unityTextAlign = TextAnchor.MiddleCenter;
            action.Add(hint);

            content.Add(action);

            // Zone 3 — trailing space. This held a career-record card until the
            // persistence layer was cut; the weight stays so the PLAY button keeps
            // landing on the same part of the screen it always did.
            content.Add(Systems_UiTheme.Filler(1.35f));

            Root.Add(screen);

            // CLAUDE.md section 3: build number top-left of the opening scene, on an
            // inset layer, outside any ScrollView, non-pickable. Added to Root
            // rather than to the padded screen so its inset is measured from the
            // edge of the SAFE AREA — on the previous version it was measured from
            // the edge of the panel and rendered underneath the camera cutout.
            Root.Add(Systems_UiTheme.VersionStamp());
        }

        private static VisualElement BuildTitle()
        {
            VisualElement block = Systems_UiTheme.Column();
            block.style.alignItems = Align.Center;

            Label title = Systems_UiTheme.Text(
                "PO FOOTBALL", Systems_UiTheme.TEXT_DISPLAY,
                Systems_UiTheme.TextPrimary, FontStyle.Bold);
            title.style.letterSpacing = 6f;

            Label subtitle = Systems_UiTheme.Caption("SELF-TAUGHT 2D FOOTBALL");
            subtitle.style.marginTop = Systems_UiTheme.SPACE_S;

            block.Add(title);
            block.Add(subtitle);
            return block;
        }

    }
}
