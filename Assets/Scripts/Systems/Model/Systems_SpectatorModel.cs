using UnityEngine;

namespace PoFootball.Models
{
    /// <summary>
    /// What the viewer has asked the broadcast to do: follow one player, and how
    /// far they have moved the shot off the operator's own framing.
    ///
    /// Presentation state only. Systems_SpectatorSystem is the one writer; the
    /// camera and the HUD read. Nothing in the simulation looks at it, and it is
    /// registered in both modes only because the camera view is injected in both.
    ///
    /// Polled rather than subscribed, like Systems_PlayModel: the camera reads it
    /// every LateUpdate regardless, and a drag writes it every frame.
    /// </summary>
    public sealed class Systems_SpectatorModel
    {
        public const int NO_FOCUS = -1;

        /// <summary>Systems_IPlayerHandle.Id of the player being followed, or NO_FOCUS.</summary>
        public int FocusedPlayerId { get; private set; } = NO_FOCUS;

        /// <summary>
        /// Multiplier on the operator's framing size. Below one is closer than the
        /// operator would go, above one is wider.
        /// </summary>
        public float ZoomScale { get; private set; } = 1f;

        /// <summary>Metres the shot is held away from where the operator wants it.</summary>
        public Vector2 PanOffset { get; private set; }

        public bool HasFocus => FocusedPlayerId != NO_FOCUS;

        /// <summary>True once the viewer has dragged or pinched; cleared by ResetView.</summary>
        public bool IsManual { get; private set; }

        public void SetFocus(int playerId)
        {
            FocusedPlayerId = playerId;
        }

        public void ClearFocus()
        {
            FocusedPlayerId = NO_FOCUS;
        }

        public void SetView(float zoomScale, Vector2 panOffset)
        {
            ZoomScale = zoomScale;
            PanOffset = panOffset;
            IsManual = true;
        }

        public void ResetView()
        {
            ZoomScale = 1f;
            PanOffset = Vector2.zero;
            IsManual = false;
        }
    }
}
