using PoFootball.Models;
using PoFootball.Systems;
using UnityEngine;

namespace PoFootball.Views
{
    /// <summary>
    /// Gives every player on the field its shape and its team colour, so position
    /// is legible at a glance instead of twenty-two identical circles.
    ///
    /// This is the "geometry encodes position" line in CLAUDE.md, and the one
    /// docs/ASSETS.md has called the project's whole art dependency since before
    /// anything implemented it.
    ///
    /// ONE APPLIER, NOT TWENTY-TWO COMPONENTS. Every player would carry an
    /// identical component with identical settings, which is duplication the scene
    /// then has to keep in step. Sweeping the scene once at Awake follows the
    /// pattern Systems_GameLifetimeScope.InjectPlayerHandles already established
    /// for exactly this situation, and it picks up players added later for free.
    ///
    /// EXECUTION ORDER IS LOAD-BEARING. Agent_FootballPlayer caches its base colour
    /// in Awake and restores it whenever it stops carrying the ball. If this ran
    /// after that cache was taken, every player would revert to the old white the
    /// first time it was tackled and the team colours would drain off the field one
    /// player at a time. The negative execution order puts this first — but still
    /// after Systems_GameLifetimeScope at -5000, which does not touch renderers.
    ///
    /// Scale is deliberately untouched. The collider is a fixed 0.5 m radius for
    /// every player (Systems_SimConstants.PLAYER_RADIUS) and the shape sprites
    /// import at 256 px per unit, so a shape at scale 1 is exactly as big as the
    /// body that collides. Drawing linemen chunkier would look better and lie about
    /// where contact happens, which in a physics sim is the wrong trade.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [DisallowMultipleComponent]
    public sealed class Systems_RoleShapeApplier : MonoBehaviour
    {
        /// <summary>Resources path of the shared shape set.</summary>
        private const string SHAPE_SET_RESOURCE = "PoFootballRoleShapes";

        [Tooltip("Leave empty to load the shared set from Resources.")]
        [SerializeField] private Systems_RoleShapeSet _shapeSet;

        private void Awake()
        {
            if (_shapeSet == null)
            {
                _shapeSet = Resources.Load<Systems_RoleShapeSet>(SHAPE_SET_RESOURCE);
            }

            if (_shapeSet == null)
            {
                Debug.LogError(
                    $"{nameof(Systems_RoleShapeApplier)}: no shape set assigned and none at "
                    + $"Resources/{SHAPE_SET_RESOURCE}. Players keep their authored sprites.");
                return;
            }

            Apply();
        }

        private void Apply()
        {
            MonoBehaviour[] behaviours = FindObjectsByType<MonoBehaviour>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            int painted = 0;

            for (int index = 0; index < behaviours.Length; index++)
            {
                // The role lives on the agent, behind the interface the systems
                // layer owns. Reading it through Systems_IPlayerHandle rather than
                // duplicating the formation slot index onto a view is what stops
                // the two from ever disagreeing.
                if (!(behaviours[index] is Systems_IPlayerHandle handle))
                {
                    continue;
                }

                if (!behaviours[index].TryGetComponent(out SpriteRenderer renderer))
                {
                    continue;
                }

                Paint(renderer, handle.Role, handle.Side);
                painted++;
            }

            Debug.Log($"[PoFootball] Role shapes applied to {painted} players.");
        }

        private void Paint(
            SpriteRenderer renderer, Systems_PlayerRole role, Systems_TeamSide side)
        {
            Sprite shape = _shapeSet.ShapeOf(role);

            if (shape != null)
            {
                renderer.sprite = shape;
            }
            else
            {
                Debug.LogWarning(
                    $"{nameof(Systems_RoleShapeApplier)}: the shape set has no sprite for "
                    + $"{role}. That player keeps its authored sprite.");
            }

            renderer.color = _shapeSet.ColorOf(side);

            // Defenders draw over the offense so a tackle reads as the defender
            // arriving on top rather than two shapes fighting for the same pixel.
            renderer.sortingOrder = side == Systems_TeamSide.Defense ? 2 : 1;
        }
    }
}
