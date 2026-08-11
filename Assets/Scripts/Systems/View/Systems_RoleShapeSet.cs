using PoFootball.Models;
using UnityEngine;

namespace PoFootball.Views
{
    /// <summary>
    /// Which flat shape stands for which position, and what the two sides are
    /// tinted. One asset, shared by all twenty-two players, so a shape language
    /// change is one edit rather than twenty-two.
    ///
    /// This is the "geometry encodes position" promise in CLAUDE.md made real —
    /// docs/ASSETS.md has described it as the whole art dependency since before
    /// anything implemented it, and every player was a circle until now.
    ///
    /// Shapes repeat across the two sides (a square is a lineman on either) because
    /// colour already separates offense from defense. Within one side every shape
    /// is distinct, which is the level a viewer actually has to read.
    /// </summary>
    [CreateAssetMenu(menuName = "PoFootball/Role Shape Set", fileName = "PoFootballRoleShapes")]
    public sealed class Systems_RoleShapeSet : ScriptableObject
    {
        [Header("Shapes")]
        [SerializeField] private Sprite _circle;
        [SerializeField] private Sprite _square;
        [SerializeField] private Sprite _triangle;
        [SerializeField] private Sprite _diamond;
        [SerializeField] private Sprite _pentagon;
        [SerializeField] private Sprite _hexagon;

        [Header("Team tints")]
        [SerializeField] private Color _offenseColor = new Color(0.204f, 0.545f, 0.937f, 1f);
        [SerializeField] private Color _defenseColor = new Color(0.922f, 0.365f, 0.298f, 1f);

        /// <summary>
        /// The carrier highlight is NOT here. Agent_FootballPlayer owns it, because
        /// it has to restore the base colour the instant it stops carrying and
        /// PoFootball.Agents does not reference PoFootball.Views — reaching this
        /// asset from there would invert the assembly dependency. The two colours
        /// only have to stay distinguishable, not co-located.
        /// </summary>
        public Color ColorOf(Systems_TeamSide side)
        {
            return side == Systems_TeamSide.Offense ? _offenseColor : _defenseColor;
        }

        /// <summary>
        /// Shape for a position. Returns null only if the asset has an unassigned
        /// slot, which the view reports rather than silently rendering nothing.
        /// </summary>
        public Sprite ShapeOf(Systems_PlayerRole role)
        {
            switch (role)
            {
                // Offense
                case Systems_PlayerRole.Quarterback: return _diamond;
                case Systems_PlayerRole.RunningBack: return _triangle;
                case Systems_PlayerRole.Fullback: return _pentagon;
                case Systems_PlayerRole.WideReceiver: return _circle;
                case Systems_PlayerRole.TightEnd: return _hexagon;
                case Systems_PlayerRole.OffensiveLine: return _square;

                // Defense
                case Systems_PlayerRole.DefensiveLine: return _square;
                case Systems_PlayerRole.Linebacker: return _hexagon;
                case Systems_PlayerRole.Cornerback: return _circle;
                default: return _diamond;
            }
        }
    }
}
