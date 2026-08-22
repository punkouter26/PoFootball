using PoFootball.Models;
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
            // Behind everything, and full-bleed rather than inside the safe area —
            // see BuildFieldBackdrop.
            UnsafeRoot.Add(BuildFieldBackdrop());

            VisualElement screen = Systems_UiTheme.Screen();
            screen.style.justifyContent = Justify.Center;

            // Transparent, so the backdrop shows through. Systems_UiTheme.Screen()
            // paints the surface colour by default, which is what would keep this
            // screen a flat black rectangle no matter what was placed behind it.
            screen.style.backgroundColor = Color.clear;

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

            // ONE COMPOSITION, NOT TWO ISLANDS. The weights used to be 0.6 above the
            // title, 1.4 between the title and PLAY, and 1.35 below — which on a tall
            // handset put a third of the screen between the wordmark and the only
            // button, and left both of them floating in their own pool of black. The
            // eye read them as two unrelated things rather than one title card.
            //
            // Now the title and the action are a single centred block separated by a
            // FIXED gap, with equal flexible space above and below it. The gap no
            // longer grows with the screen, so the composition holds its proportions
            // from a small phone to a foldable instead of stretching apart.
            content.Add(Systems_UiTheme.Filler(1f));
            content.Add(BuildTitle());

            VisualElement action = Systems_UiTheme.Column();
            action.style.marginTop = Systems_UiTheme.SPACE_XL * 2;

            // WITHOUT THIS THE PRIMARY BUTTON SITS AGAINST THE LEFT EDGE. A column
            // aligns its children to the cross-axis start unless told otherwise, and
            // ApplyPrimaryActionSize gives PLAY an explicit 78% width — so it has no
            // reason to stretch and every reason to sit at x = 0. The hint below it
            // has no width, so it stretches the full span and its centred TEXT reads
            // as correct, which is exactly what made this hard to spot: one element
            // looked centred, the other was not, and nothing looked obviously broken
            // in isolation. It was only visible on a device screenshot.
            //
            // The title block sets the same property for the same reason, and the
            // final overlay gets it from Systems_UiOverlay.Centered().
            action.style.alignItems = Align.Center;

            Button play = Systems_UiTheme.Button(
                "PLAY", Systems_UiTheme.Action, Systems_SceneRouter.LoadGame);

            // Sized by the shared helper, so PLAY and the buttons on the final
            // overlay are the same object at the same size on both screens.
            Systems_UiTheme.ApplyPrimaryActionSize(play);
            action.Add(play);

            // "Watch two learned teams play a full game" was two problems in one
            // line. It was not true — no brain is promoted, so every player is
            // running the built-in heuristic and nothing on that field has learned
            // anything — and it explained the product where it should have described
            // the button. A caption under a primary action says what the tap does.
            Label hint = Systems_UiTheme.Text(
                "A full game, start to finish.",
                Systems_UiTheme.TEXT_CAPTION,
                Systems_UiTheme.TextMuted);
            hint.style.unityTextAlign = TextAnchor.MiddleCenter;
            action.Add(hint);

            content.Add(action);

            // Trailing space, or the game that just finished.
            //
            // The flexible space below the title card always sums to 1 — the same
            // weight as above it — so on a first launch the composition is exactly
            // centred, and when a result card appears it takes its room from the
            // bottom rather than shoving PLAY up the screen. A button that moves
            // between visits is a button the hand has to look for.
            //
            // Consumed, not read: arriving here from anywhere but a finished game
            // leaves the zone empty exactly as before.
            Systems_GameSummary summary = Systems_SceneRouter.TakeSummary();

            if (summary == null)
            {
                content.Add(Systems_UiTheme.Filler(1f));
            }
            else
            {
                content.Add(Systems_UiTheme.Filler(0.55f));
                content.Add(BuildFinalCard(summary));
                content.Add(Systems_UiTheme.Filler(0.45f));
            }

            Root.Add(screen);

            // CLAUDE.md section 3: build number top-left of the opening scene, on an
            // inset layer, outside any ScrollView, non-pickable. Added to Root
            // rather than to the padded screen so its inset is measured from the
            // edge of the SAFE AREA — on the previous version it was measured from
            // the edge of the panel and rendered underneath the camera cutout.
            Root.Add(Systems_UiTheme.VersionStamp());
        }

        /// <summary>
        /// The last game's line, as a three-column table: stat name, home, away.
        ///
        /// A table rather than the HUD's single block of text because two teams'
        /// numbers only mean anything next to each other — "312 yards" is a fact,
        /// "312 to 96" is the game. The columns are fixed-weight so the digits line
        /// up down the screen instead of jittering with the width of each label.
        /// </summary>
        private static VisualElement BuildFinalCard(Systems_GameSummary summary)
        {
            VisualElement card = Systems_UiTheme.Column();
            card.style.backgroundColor = Systems_UiTheme.SurfaceRaised;
            Systems_UiTheme.SetPadding(card, Systems_UiTheme.SPACE_M);
            Systems_UiTheme.SetRadius(card, Systems_UiTheme.RADIUS);

            Label heading = Systems_UiTheme.Caption(
                summary.IsTie
                    ? "LAST GAME — TIE"
                    : $"LAST GAME — {Systems_DisplayText.TeamName(summary.Winner)} WON");
            heading.style.unityTextAlign = TextAnchor.MiddleCenter;
            heading.style.marginBottom = Systems_UiTheme.SPACE_S;
            card.Add(heading);

            card.Add(BuildHeaderRow());

            Systems_TeamSummary home = summary.Home;
            Systems_TeamSummary away = summary.Away;

            card.Add(StatRow("SCORE", $"{home.Points}", $"{away.Points}", true));
            card.Add(StatRow(
                "TOTAL YDS",
                $"{Mathf.RoundToInt(home.TotalYards)}",
                $"{Mathf.RoundToInt(away.TotalYards)}"));
            card.Add(StatRow(
                "RUSH YDS",
                $"{Mathf.RoundToInt(home.RushingYards)}",
                $"{Mathf.RoundToInt(away.RushingYards)}"));
            card.Add(StatRow(
                "PASS YDS",
                $"{Mathf.RoundToInt(home.PassingYards)}",
                $"{Mathf.RoundToInt(away.PassingYards)}"));
            card.Add(StatRow(
                "PASSING",
                $"{home.Completions}/{home.PassAttempts}",
                $"{away.Completions}/{away.PassAttempts}"));
            card.Add(StatRow("1ST DOWNS", $"{home.FirstDowns}", $"{away.FirstDowns}"));
            card.Add(StatRow(
                "YDS/PLAY",
                home.YardsPerPlay.ToString("F1"),
                away.YardsPerPlay.ToString("F1")));
            card.Add(StatRow("TURNOVERS", $"{home.Turnovers}", $"{away.Turnovers}"));
            card.Add(StatRow(
                "TIME OF POSS",
                Systems_DisplayText.Clock(home.TimeOfPossession),
                Systems_DisplayText.Clock(away.TimeOfPossession)));

            return card;
        }

        private static VisualElement BuildHeaderRow()
        {
            VisualElement row = Systems_UiTheme.Row();
            row.style.marginBottom = Systems_UiTheme.SPACE_XS;

            row.Add(Cell(string.Empty, Systems_UiTheme.TextMuted, 1.5f));

            // Tinted to match the shapes on the field, so the column and the team
            // are the same thing to look at.
            row.Add(Cell(
                Systems_DisplayText.TeamName(Systems_TeamId.Home),
                Systems_UiTheme.ColorOf(Systems_TeamId.Home),
                1f,
                FontStyle.Bold));

            row.Add(Cell(
                Systems_DisplayText.TeamName(Systems_TeamId.Away),
                Systems_UiTheme.ColorOf(Systems_TeamId.Away),
                1f,
                FontStyle.Bold));

            return row;
        }

        private static VisualElement StatRow(
            string label, string homeValue, string awayValue, bool emphasise = false)
        {
            VisualElement row = Systems_UiTheme.Row();

            // The score line is the only row anyone reads first, so it is the only
            // one given weight.
            FontStyle weight = emphasise ? FontStyle.Bold : FontStyle.Normal;

            row.Add(Cell(label, Systems_UiTheme.TextMuted, 1.5f));
            row.Add(Cell(homeValue, Systems_UiTheme.TextPrimary, 1f, weight));
            row.Add(Cell(awayValue, Systems_UiTheme.TextPrimary, 1f, weight));

            return row;
        }

        private static Label Cell(
            string value, Color color, float weight, FontStyle fontStyle = FontStyle.Normal)
        {
            Label cell = Systems_UiTheme.Text(
                value, Systems_UiTheme.TEXT_CAPTION, color, fontStyle);
            cell.style.flexGrow = weight;
            cell.style.flexBasis = 0f;
            cell.style.unityTextAlign = TextAnchor.MiddleCenter;
            return cell;
        }

        /// <summary>
        /// The wordmark: name, a two-colour rule, and the line under it.
        ///
        /// THE RULE IS THE ONE PIECE OF ORNAMENT ON THIS SCREEN, and it is carrying
        /// meaning rather than decorating. Its two halves are the home and away
        /// colours — the same two the shapes on the field are tinted with and the
        /// same two the box score columns use — so the front of the app is quietly
        /// stating what the game is about before a single play has been drawn. A
        /// neutral grey line would have looked equally tidy and said nothing.
        ///
        /// Letter spacing came down from 6 to 4. At 6 the wordmark ran to within a
        /// few units of the padding on the reference panel, and since the panel
        /// matches on width that margin is the same on every device — there was no
        /// screen on which it looked comfortable, only screens where it happened not
        /// to clip.
        /// </summary>
        private static VisualElement BuildTitle()
        {
            VisualElement block = Systems_UiTheme.Column();
            block.style.alignItems = Align.Center;

            Label title = Systems_UiTheme.Text(
                "PO FOOTBALL", Systems_UiTheme.TEXT_DISPLAY,
                Systems_UiTheme.TextPrimary, FontStyle.Bold);
            title.style.letterSpacing = 4f;

            Label subtitle = Systems_UiTheme.Caption("SELF-TAUGHT 2D FOOTBALL");
            subtitle.style.marginTop = Systems_UiTheme.SPACE_M;

            block.Add(title);
            block.Add(BuildTeamRule());
            block.Add(subtitle);
            return block;
        }

        /// <summary>
        /// A short horizontal rule in two segments, home colour then away colour.
        /// </summary>
        private static VisualElement BuildTeamRule()
        {
            const int SEGMENT_WIDTH = 44;
            const int THICKNESS = 4;

            VisualElement rule = Systems_UiTheme.Row();
            rule.style.marginTop = Systems_UiTheme.SPACE_M;

            rule.Add(Segment(Systems_TeamId.Home, SEGMENT_WIDTH, THICKNESS));
            rule.Add(Segment(Systems_TeamId.Away, SEGMENT_WIDTH, THICKNESS));

            return rule;
        }

        private static VisualElement Segment(Systems_TeamId team, int width, int thickness)
        {
            VisualElement segment = new VisualElement();
            segment.style.width = width;
            segment.style.height = thickness;
            segment.style.backgroundColor = Systems_UiTheme.ColorOf(team);
            segment.pickingMode = PickingMode.Ignore;
            return segment;
        }

        /// <summary>
        /// A field, seen from the same overhead angle the game is played at, dimmed
        /// most of the way down to the surface colour.
        ///
        /// WHY THIS EXISTS. The front end was a flat black rectangle with a title
        /// and a button in the middle of it and roughly two thirds of the display
        /// carrying nothing at all. Every element on it was correctly placed — the
        /// composition notes above are all still true — and it still read as an
        /// unfinished screen, because a correct layout of three items cannot fill a
        /// 9:16 handset on its own. There was also nothing anywhere on the opening
        /// screen to say what the game was; "PO FOOTBALL" was doing that job alone.
        ///
        /// A backdrop is the cheapest honest answer: it says football before a word
        /// is read, it uses the same yard lines and the same two team colours the
        /// game itself does, and it costs no art. Everything here is a plain
        /// VisualElement with a background colour.
        ///
        /// FULL-BLEED, SO IT GOES ON UnsafeRoot. Systems_ScreenView keeps that root
        /// specifically for chrome that should reach the physical edge of the
        /// display — a wash like this one looks wrong inset from a punch-hole, and
        /// no text lives here to be clipped by one.
        ///
        /// KEPT WELL DOWN IN CONTRAST ON PURPOSE. This is behind a title and a
        /// primary action, and a backdrop that competes with them has taken over the
        /// screen rather than furnished it.
        /// </summary>
        private static VisualElement BuildFieldBackdrop()
        {
            const int YARD_LINE_COUNT = 11;
            const float TURF_GREEN_R = 0.055f;
            const float TURF_GREEN_G = 0.12f;
            const float TURF_GREEN_B = 0.075f;

            VisualElement backdrop = new VisualElement { name = "FieldBackdrop" };
            Systems_UiTheme.FillParent(backdrop);
            backdrop.style.backgroundColor =
                new Color(TURF_GREEN_R, TURF_GREEN_G, TURF_GREEN_B, 1f);

            // Non-pickable throughout: this is scenery, and a tap that lands on it
            // must still reach whatever is underneath the pointer.
            backdrop.pickingMode = PickingMode.Ignore;

            // Yard lines, evenly spaced down the screen. Percent positions rather
            // than pixels so the spacing holds from a small phone to a foldable
            // without anyone having to know the height.
            for (int index = 0; index < YARD_LINE_COUNT; index++)
            {
                float percent = (index + 0.5f) * (100f / YARD_LINE_COUNT);

                VisualElement line = new VisualElement();
                line.style.position = Position.Absolute;
                line.style.left = 0;
                line.style.right = 0;
                line.style.top = Length.Percent(percent);
                line.style.height = 2;

                // The centre line is the halfway line, and it is the one line on a
                // real field that is drawn differently.
                bool isHalfway = index == YARD_LINE_COUNT / 2;

                line.style.backgroundColor = new Color(
                    0.85f, 0.95f, 0.88f, isHalfway ? 0.16f : 0.07f);

                line.pickingMode = PickingMode.Ignore;
                backdrop.Add(line);
            }

            // The two end zones, in the team colours the rest of the app already
            // uses — the same pairing the wordmark's rule makes, at the two ends of
            // the field where a real one puts them.
            backdrop.Add(EndZone(Systems_TeamId.Home, true));
            backdrop.Add(EndZone(Systems_TeamId.Away, false));

            // A scrim over the whole thing. Without it the yard lines run straight
            // under the title and the caption text loses its contrast; with it the
            // field is present but clearly behind everything.
            VisualElement scrim = new VisualElement();
            Systems_UiTheme.FillParent(scrim);
            scrim.style.backgroundColor = new Color(0.02f, 0.03f, 0.025f, 0.72f);
            scrim.pickingMode = PickingMode.Ignore;
            backdrop.Add(scrim);

            return backdrop;
        }

        /// <summary>
        /// A tinted band at one end of the backdrop field.
        /// </summary>
        private static VisualElement EndZone(Systems_TeamId team, bool atTop)
        {
            const float END_ZONE_PERCENT = 9f;

            VisualElement zone = new VisualElement();
            zone.style.position = Position.Absolute;
            zone.style.left = 0;
            zone.style.right = 0;
            zone.style.height = Length.Percent(END_ZONE_PERCENT);

            if (atTop)
            {
                zone.style.top = 0;
            }
            else
            {
                zone.style.bottom = 0;
            }

            Color teamColor = Systems_UiTheme.ColorOf(team);
            zone.style.backgroundColor =
                new Color(teamColor.r, teamColor.g, teamColor.b, 0.22f);

            zone.pickingMode = PickingMode.Ignore;
            return zone;
        }
    }
}
