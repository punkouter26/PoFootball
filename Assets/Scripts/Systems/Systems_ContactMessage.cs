using UnityEngine;

namespace PoFootball.Systems
{
    /// <summary>
    /// Published when two opponents collide away from the ball: a block landing at
    /// the line, a receiver jammed off his release, a pursuit angle cut off.
    ///
    /// WHY THE TACKLE MESSAGE WAS NOT ENOUGH. Systems_TackleMessage is the one
    /// collision in a play that ends it, and until this existed it was the only
    /// collision presentation could see at all. A run play is ten other collisions
    /// first — the line engaging at the snap is most of the physical contact in the
    /// sport — and every one of them was silent and left no mark. A viewer could
    /// watch a hole open and had no way to tell a guard who drove his man out of it
    /// from two bodies that happened to drift together.
    ///
    /// IT CARRIES THE SOLVER'S IMPULSE, NOT A CLOSING SPEED. The tackle is sized by
    /// closing speed because the tackle RULE is written in closing speed, and an
    /// effect should render the number the simulation acted on. Nothing acts on a
    /// block. What the physics did to those two bodies is the impulse it applied
    /// along the contact normal, and since Systems_RoleTable.MassOf stopped being a
    /// flat 100 kg that is genuinely different information: a 140 kg guard and a
    /// 92 kg corner arriving at the same speed do not hit equally hard, and only
    /// the impulse says so.
    ///
    /// NO PLAYER IDS, DELIBERATELY. The tackle carries two because
    /// Systems_PlayerAppearanceView lights the two bodies. Nothing lights a
    /// blocker — Systems_PostProcessView's bloom is reserved for the carrier and
    /// the hit that stops him — so there is no reader for them.
    /// </summary>
    public readonly struct Systems_ContactMessage
    {
        /// <summary>
        /// Impulse, in newton-seconds, below which a contact is not reported at
        /// all. Two linemen locked together separate and re-touch every few ticks,
        /// and each re-touch is a fresh OnCollisionEnter2D carrying almost nothing.
        ///
        /// MEASURED, NOT CHOSEN. 1,184 off-ball contacts over 22 plays of SCN_GAME
        /// on the heuristic, 2026-10-02, and the distribution has two humps with
        /// nothing between them: half of all contacts are under 11 N·s, which is
        /// the chatter, and the count barely moves from 20 to 60 before the real
        /// collisions start. 60 sits in that gap. It passes about fifteen a play
        /// out of fifty-four.
        /// </summary>
        public const float AUDIBLE_IMPULSE = 60f;

        /// <summary>
        /// The impulse that counts as a full-strength collision: the 99th
        /// percentile of the same sample (405), rounded. The line engaging at the
        /// snap clusters at 150 to 200, so it lands a third of the way up this
        /// range and leaves the top of it to collisions at a run — which is the
        /// order a viewer would put them in. The hardest one seen was 572.
        ///
        /// Both numbers move if Systems_RoleTable.MassOf or the role top speeds
        /// do. Re-measure rather than rescale.
        /// </summary>
        public const float FULL_IMPULSE = 400f;

        /// <summary>Where the two bodies met, in world space.</summary>
        public readonly Vector2 Point;

        /// <summary>
        /// Unit normal of the contact. Which of the two bodies it points away from
        /// is whichever one happened to report, so read it as a line, not as a
        /// direction.
        /// </summary>
        public readonly Vector2 Normal;

        /// <summary>Impulse the solver applied along <see cref="Normal"/>, in N·s.</summary>
        public readonly float Impulse;

        public Systems_ContactMessage(Vector2 point, Vector2 normal, float impulse)
        {
            Point = point;
            Normal = normal;
            Impulse = impulse;
        }

        /// <summary>
        /// The impulse over the audible range, in [0, 1].
        ///
        /// ON THE MESSAGE SO THERE IS ONE OF IT. Systems_ImpactView's note on the
        /// tackle says why: two views normalising the same quantity over ranges
        /// they each picked put the puff and the thud out of step, and then
        /// neither means anything.
        /// </summary>
        public float Strength => Mathf.InverseLerp(AUDIBLE_IMPULSE, FULL_IMPULSE, Impulse);
    }
}
