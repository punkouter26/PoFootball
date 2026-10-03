using PoFootball.Models;
using UnityEngine;

namespace PoFootball.Systems
{
    /// <summary>
    /// The viewer's hand on the broadcast: pick a player to follow, drag the shot,
    /// pinch it closer or wider, and hand it back to the operator.
    ///
    /// Input-agnostic, per .claude/rules/architecture.md. It is told a point on
    /// the field or a distance in metres and never learns whether that came from a
    /// finger, a mouse wheel or a test — the HUD's field surface does the
    /// conversion from screen to world, because only a view may know the camera.
    ///
    /// IT NEVER TOUCHES THE CAMERA. Systems_BroadcastCameraView remains the only
    /// writer of its own transform (its header explains what happened the last
    /// time a second writer existed); this only records what was asked for in
    /// Systems_SpectatorModel, and the camera decides how to honour it.
    /// </summary>
    public sealed class Systems_SpectatorSystem
    {
        /// <summary>
        /// How close to a player a tap must land to select him, in metres. A body
        /// is under a metre across and a fingertip on a 6-inch screen covers about
        /// three at the tight shot, so anything smaller is a tap that misses.
        /// </summary>
        public const float PICK_RADIUS = 3f;

        /// <summary>Closest the viewer may pull the shot, as a fraction of the operator's framing.</summary>
        public const float MIN_ZOOM = 0.4f;

        /// <summary>Widest. Past this the camera's own field clamp is doing all the work.</summary>
        public const float MAX_ZOOM = 1.6f;

        /// <summary>
        /// Furthest the shot may be dragged off the operator's framing, in metres —
        /// half the field's length, so either goal line is reachable from midfield.
        /// </summary>
        public const float MAX_PAN = 55f;

        private readonly Systems_SpectatorModel _model;
        private readonly Systems_PlayerRegistry _registry;

        public Systems_SpectatorSystem(Systems_SpectatorModel model, Systems_PlayerRegistry registry)
        {
            _model = model;
            _registry = registry;
        }

        /// <summary>
        /// A tap on the field. The nearest player inside PICK_RADIUS becomes the
        /// focus; tapping him again, or tapping open grass, lets go.
        /// </summary>
        public void TapAt(Vector2 worldPoint)
        {
            int nearestId = Systems_SpectatorModel.NO_FOCUS;
            float nearestSqr = PICK_RADIUS * PICK_RADIUS;

            for (int slotIndex = 0; slotIndex < Systems_PlayerRegistry.CAPACITY; slotIndex++)
            {
                Systems_IPlayerHandle handle = _registry.Get(slotIndex);

                if (handle == null)
                {
                    continue;
                }

                float sqrDistance = (handle.Position - worldPoint).sqrMagnitude;

                if (sqrDistance < nearestSqr)
                {
                    nearestSqr = sqrDistance;
                    nearestId = handle.Id;
                }
            }

            if (nearestId == Systems_SpectatorModel.NO_FOCUS || nearestId == _model.FocusedPlayerId)
            {
                _model.ClearFocus();
                return;
            }

            _model.SetFocus(nearestId);
        }

        public void ReleaseFocus()
        {
            _model.ClearFocus();
        }

        /// <summary>Moves the shot by a distance on the field, in metres.</summary>
        public void Pan(Vector2 worldDelta)
        {
            Vector2 pan = Vector2.ClampMagnitude(_model.PanOffset + worldDelta, MAX_PAN);
            _model.SetView(_model.ZoomScale, pan);
        }

        /// <summary>Scales the framing: below one closes in, above one opens up.</summary>
        public void Zoom(float factor)
        {
            if (factor <= 0f)
            {
                return;
            }

            float zoom = Mathf.Clamp(_model.ZoomScale * factor, MIN_ZOOM, MAX_ZOOM);
            _model.SetView(zoom, _model.PanOffset);
        }

        /// <summary>Hands the shot back to the operator. The focus, if any, is kept.</summary>
        public void ResetView()
        {
            _model.ResetView();
        }
    }
}
