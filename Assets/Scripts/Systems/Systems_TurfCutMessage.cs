using UnityEngine;

namespace PoFootball.Systems
{
    /// <summary>
    /// Published by Systems_TurfScuffView when the ball carrier makes a cut hard
    /// enough to throw turf, so Systems_AudioView can make it heard.
    ///
    /// A MESSAGE RATHER THAN THE AUDIO VIEW LOOKING FOR CUTS ITSELF. The scuff view
    /// already tracks the one player who can cut and measures how hard he did; the
    /// audio view finding that out on its own would mean watching the carrier a
    /// second time, and the per-frame cleat-scuff walk this project deleted is the
    /// shape that takes. Pushed once per spray, already rate limited at the source.
    /// </summary>
    public readonly struct Systems_TurfCutMessage
    {
        /// <summary>Where the carrier was when he cut, in world space.</summary>
        public readonly Vector2 Point;

        /// <summary>The cut's lateral acceleration over the spray's range, in [0, 1].</summary>
        public readonly float Strength;

        public Systems_TurfCutMessage(Vector2 point, float strength)
        {
            Point = point;
            Strength = strength;
        }
    }
}
