using UnityEngine;

namespace PoFootball.Systems
{
    /// <summary>
    /// Published by the post-game highlight reel every frame a highlight is on the
    /// field, and once more, with <see cref="IsPlaying"/> false, when it leaves.
    ///
    /// WHY A MESSAGE AND NOT A REFERENCE. Three views have to agree that a
    /// highlight is showing: Systems_ReplayView draws it with the ghosts,
    /// Systems_HudView takes the final overlay down so the field can be seen, and
    /// Systems_BroadcastCameraView frames the ghost ball instead of the real one,
    /// which after the final whistle is lying wherever the last play ended. Views
    /// do not reference each other (.claude/rules/architecture.md), so the reel
    /// says what it is doing and each of the other two decides what that means
    /// for the thing it owns. The camera stays the only writer of the camera and
    /// the HUD the only writer of its overlay.
    ///
    /// EVERY FRAME, NOT ON CHANGE, for the focus: the ghost ball moves every frame
    /// and the camera has to be told where it is. It is a struct through a
    /// synchronous broker, so the per-frame cost is a handler call and no
    /// allocation. Subscribers that only care about the edge — the HUD — guard on
    /// their own state.
    ///
    /// Game mode only in practice: the reel is built at the final whistle and
    /// Systems_PresentationBudget never lets the replay view exist in training.
    /// The broker is registered in both modes regardless, so a subscriber never
    /// has to ask which one it is in.
    /// </summary>
    public readonly struct Systems_HighlightPlaybackMessage
    {
        /// <summary>
        /// True while a highlight is drawn on the field — playing, paused or being
        /// scrubbed. False exactly once when it stops being drawn.
        /// </summary>
        public readonly bool IsPlaying;

        /// <summary>
        /// The ghost ball's position on the field plane this frame, in world
        /// metres — the same plane Systems_BallModel.Position is on, without the
        /// presentation lift. Meaningless when <see cref="IsPlaying"/> is false.
        /// </summary>
        public readonly Vector2 Focus;

        public Systems_HighlightPlaybackMessage(bool isPlaying, Vector2 focus)
        {
            IsPlaying = isPlaying;
            Focus = focus;
        }
    }
}
