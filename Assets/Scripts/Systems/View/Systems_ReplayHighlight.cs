using PoFootball.Models;
using PoFootball.Systems;
using UnityEngine;

namespace PoFootball.Views
{
    /// <summary>
    /// One of the game's best plays, frozen: its own copy of the tape and the few
    /// facts the highlight reel captions it with.
    ///
    /// THE SCORE IS DELIBERATELY ARITHMETIC A VIEWER COULD DO IN THEIR HEAD:
    ///
    ///     |net yards|                        a long gain or a big loss
    ///   + 20 for a touchdown                 the rarest thing on the field
    ///   + 12 for a takeaway or a safety      the ball changing hands by force
    ///   + closing speed of the tackle, m/s   how hard the play was stopped
    ///
    /// So a 60-yard touchdown is 80, a pick at the line is 12, and a four-yard
    /// run that ended on a 9 m/s hit is 13 — it beats a quiet six-yard gain but
    /// not a real play. Nothing in it is tuned; the point is that "best" is a
    /// thing a reader can check against the box score, not a learned opinion.
    ///
    /// The tape is allocated once, at the size of the live tape, and refilled in
    /// place whenever a better play displaces this one. Three of these are the
    /// whole of the highlight memory for a game.
    ///
    /// THE TEAM COLOURS TRAVEL WITH THE TAPE. The same eleven bodies play offense
    /// for both teams and Systems_RoleShapeApplier repaints them whenever the ball
    /// changes hands, so at the final whistle the real renderers wear the colours
    /// of the LAST possession. A highlight from the other team's drive, replayed
    /// with the ghosts copying those renderers, would show the wrong team scoring.
    /// So the colours the field actually wore during this play are kept here and
    /// the ghosts are repainted with them.
    /// </summary>
    internal sealed class Systems_ReplayHighlight
    {
        public Systems_ReplayHighlight(int capacity, int playerCount)
        {
            Tape = new Systems_ReplayTape(capacity, playerCount);
        }

        public Systems_ReplayTape Tape { get; }

        public bool IsFilled { get; private set; }

        public float Score { get; private set; }

        public Systems_DownResult Result { get; private set; }

        public Systems_PlayOutcome Outcome { get; private set; }

        public float YardsGained { get; private set; }

        /// <summary>False if the colours could not be read when the play was snapped; the ghosts then keep the real renderers' look.</summary>
        public bool HasTeamColors { get; private set; }

        /// <summary>What the offense unit wore on this play.</summary>
        public Color OffenseColor { get; private set; }

        /// <summary>What the defense unit wore on this play.</summary>
        public Color DefenseColor { get; private set; }

        public void Fill(
            Systems_ReplayTape source,
            int frames,
            float score,
            Systems_DownResolvedMessage message,
            bool hasTeamColors,
            Color offenseColor,
            Color defenseColor)
        {
            Tape.CopyNewestFrom(source, frames);

            IsFilled = Tape.Count > 0;
            Score = score;
            Result = message.Result;
            Outcome = message.Outcome;
            YardsGained = message.YardsGained;
            HasTeamColors = hasTeamColors;
            OffenseColor = offenseColor;
            DefenseColor = defenseColor;
        }
    }
}
