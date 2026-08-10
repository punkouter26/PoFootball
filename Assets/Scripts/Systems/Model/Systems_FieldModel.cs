using UnityEngine;

namespace PoFootball.Models
{
    /// <summary>
    /// Field geometry. Vertical orientation: +Y runs the length of the field and
    /// is the direction the offense attacks; X runs across the width.
    /// Origin is the centre of the field, so the 50 yard line is Y = 0.
    ///
    /// Scale is 1 sprite = 1 metre (256 px sprite imported at 256 px/unit).
    /// Pure data and pure functions — no Unity API beyond Vector2/Mathf.
    /// </summary>
    public sealed class Systems_FieldModel
    {
        /// <summary>Metres per yard.</summary>
        public const float YARD = 0.9144f;

        /// <summary>Distance between the two goal lines: 100 yd.</summary>
        public const float PLAYING_LENGTH = 100f * YARD;

        /// <summary>End zone depth: 10 yd.</summary>
        public const float END_ZONE_DEPTH = 10f * YARD;

        /// <summary>Sideline-to-sideline: 53.3 yd.</summary>
        public const float FIELD_WIDTH = 53.3f * YARD;

        /// <summary>Total length including both end zones: 120 yd.</summary>
        public const float TOTAL_LENGTH = PLAYING_LENGTH + (2f * END_ZONE_DEPTH);

        public const float HALF_WIDTH = FIELD_WIDTH * 0.5f;

        /// <summary>Y of the goal line the offense is attacking.</summary>
        public const float ATTACKING_GOAL_LINE_Y = PLAYING_LENGTH * 0.5f;

        /// <summary>Y of the goal line the offense is defending.</summary>
        public const float OWN_GOAL_LINE_Y = -ATTACKING_GOAL_LINE_Y;

        /// <summary>Y of the back of the end zone the offense is attacking.</summary>
        public const float ATTACKING_BACK_LINE_Y = ATTACKING_GOAL_LINE_Y + END_ZONE_DEPTH;

        public const float OWN_BACK_LINE_Y = -ATTACKING_BACK_LINE_Y;

        /// <summary>
        /// Line of scrimmage is drawn from this range each episode: from the
        /// offense's own 10 to the opponent's 40 (acceptance criteria, Episode variety).
        /// </summary>
        public const float LOS_MIN_Y = OWN_GOAL_LINE_Y + (10f * YARD);
        public const float LOS_MAX_Y = ATTACKING_GOAL_LINE_Y - (40f * YARD);

        public bool IsOutsideSidelines(float x)
        {
            return x < -HALF_WIDTH || x > HALF_WIDTH;
        }

        public bool HasScored(float y)
        {
            return y >= ATTACKING_GOAL_LINE_Y;
        }

        /// <summary>
        /// Minimal rules have no safety: the carrier leaving the back of its own
        /// end zone is treated as out of bounds (Edge Cases table).
        /// </summary>
        public bool HasExitedOwnEndZone(float y)
        {
            return y <= OWN_BACK_LINE_Y;
        }

        public float YardsToAttackingGoal(float y)
        {
            return (ATTACKING_GOAL_LINE_Y - y) / YARD;
        }

        /// <summary>
        /// Y mapped to [-1, 1] across the full field including end zones.
        /// Observations must already be normalized because the trainer config
        /// sets normalize: false (acceptance criterion #14).
        /// </summary>
        public float NormalizeY(float y)
        {
            return Mathf.Clamp(y / ATTACKING_BACK_LINE_Y, -1f, 1f);
        }

        /// <summary>X mapped to [-1, 1] across the field width.</summary>
        public float NormalizeX(float x)
        {
            return Mathf.Clamp(x / HALF_WIDTH, -1f, 1f);
        }

        /// <summary>Clamps a spot to the playable area so a dead-ball spot is always legal.</summary>
        public Vector2 ClampToField(Vector2 position)
        {
            return new Vector2(
                Mathf.Clamp(position.x, -HALF_WIDTH, HALF_WIDTH),
                Mathf.Clamp(position.y, OWN_BACK_LINE_Y, ATTACKING_BACK_LINE_Y));
        }
    }
}
