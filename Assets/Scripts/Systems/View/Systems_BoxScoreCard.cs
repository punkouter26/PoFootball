using PoFootball.Models;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoFootball.Views
{
    /// <summary>
    /// A finished game's line, as a three-column table: stat name, home, away.
    ///
    /// ONE BUILDER, BECAUSE THE RESULT USED TO BE TOLD IN TWO HALVES. The final
    /// overlay printed two lines of totals and the full table only existed on the
    /// menu, one tap and one scene load later — and only if that tap landed on the
    /// right one of two MENU buttons. A viewer who chose REMATCH never saw it at
    /// all. The whistle is the moment the numbers are wanted, so the overlay shows
    /// the whole table now, and the menu's LAST GAME card is this same element
    /// rather than a second layout that could drift from it.
    ///
    /// A table rather than a block of text because two teams' numbers only mean
    /// anything next to each other — "312 yards" is a fact, "312 to 96" is the
    /// game. The columns are fixed-weight so the digits line up down the screen
    /// instead of jittering with the width of each label.
    /// </summary>
    internal static class Systems_BoxScoreCard
    {
        /// <summary>Flex weight of the stat-name column against the two value columns.</summary>
        private const float LABEL_WEIGHT = 1.5f;

        public static VisualElement Build(string heading, Systems_GameSummary summary)
        {
            VisualElement card = Systems_UiTheme.Column();

            // Opaque. Over the menu backdrop that changes nothing, but on the final
            // overlay the scrim alone let players and yard lines show through the
            // digits — a blue lineman sat inside "340 YDS" in the capture that
            // prompted this.
            card.style.backgroundColor = Systems_UiTheme.SurfaceRaised;
            Systems_UiTheme.SetPadding(card, Systems_UiTheme.SPACE_M);
            Systems_UiTheme.SetRadius(card, Systems_UiTheme.RADIUS);

            Label headingLabel = Systems_UiTheme.Caption(heading);
            headingLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            headingLabel.style.marginBottom = Systems_UiTheme.SPACE_S;
            card.Add(headingLabel);

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

            row.Add(Cell(string.Empty, Systems_UiTheme.TextMuted, LABEL_WEIGHT));

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

            row.Add(Cell(label, Systems_UiTheme.TextMuted, LABEL_WEIGHT));
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
    }
}
