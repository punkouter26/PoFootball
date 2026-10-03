using PoFootball.Models;
using UnityEngine;

namespace PoFootball.Systems
{
    /// <summary>
    /// Published on every physics tick a defender stays in contact with the ball
    /// carrier: the wrap-up, the drag, the pile moving.
    ///
    /// WHY THE TACKLE AND THE CONTACT MESSAGE WERE NOT ENOUGH. Both are instants —
    /// one when two bodies meet, one when the play ends. Everything between them
    /// was silent, and that is where the wrap-up rule actually runs: a defender
    /// who has hold of a back for six ticks and one who bounced off him sounded
    /// the same until the whistle said which it was.
    ///
    /// IT CARRIES RELATIVE SPEED, because that is what a scrape is. Two bodies
    /// locked together and moving as one make no noise against each other; a
    /// carrier dragging a defender who is losing him does. It is the same number
    /// Systems_Referee.ReportSustainedContact is handed on the same tick.
    ///
    /// PUBLISHED AT 50 HZ WHILE IT LASTS, and that is fine: a readonly struct
    /// through MessagePipe allocates nothing, the binding that publishes it only
    /// exists when presentation does, and the one subscriber keeps a maximum.
    /// </summary>
    public readonly struct Systems_GrindMessage
    {
        /// <summary>
        /// Relative speed, in metres per second, below which two bodies in contact
        /// are moving together and nothing is heard.
        /// </summary>
        public const float AUDIBLE_SPEED = 0.4f;

        /// <summary>Where the two bodies are touching, in world space.</summary>
        public readonly Vector2 Point;

        /// <summary>Speed of one body relative to the other, in metres per second.</summary>
        public readonly float RelativeSpeed;

        public Systems_GrindMessage(Vector2 point, float relativeSpeed)
        {
            Point = point;
            RelativeSpeed = relativeSpeed;
        }

        /// <summary>
        /// The relative speed over the audible range, in [0, 1]. The top of the
        /// range is the closing speed that counts as a tackle outright: contact
        /// any faster than that ended the play on the tick it began, so it is the
        /// hardest a sustained one can be.
        /// </summary>
        public float Strength => Mathf.InverseLerp(
            AUDIBLE_SPEED, Systems_SimConstants.TACKLE_CLOSING_SPEED, RelativeSpeed);
    }
}
