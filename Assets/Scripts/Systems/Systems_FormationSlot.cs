using PoFootball.Models;

namespace PoFootball.Systems
{
    /// <summary>One player's starting position, expressed relative to the line of scrimmage.</summary>
    public readonly struct Systems_FormationSlot
    {
        public readonly Systems_PlayerRole Role;

        /// <summary>Metres from the centre of the field. Positive is to the offense's right.</summary>
        public readonly float OffsetX;

        /// <summary>Metres from the line of scrimmage. Negative is behind it for the offense.</summary>
        public readonly float OffsetY;

        public Systems_FormationSlot(Systems_PlayerRole role, float offsetX, float offsetY)
        {
            Role = role;
            OffsetX = offsetX;
            OffsetY = offsetY;
        }
    }
}
