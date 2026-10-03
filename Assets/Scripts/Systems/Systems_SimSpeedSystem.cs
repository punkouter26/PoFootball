using System;
using UnityEngine;

namespace PoFootball.Systems
{
    /// <summary>
    /// The viewer's playback speed: 1x, 2x or 4x, cycled from the HUD.
    ///
    /// WHY A SYSTEM MAY DO WHAT A VIEW MAY NOT. Time.timeScale is the global this
    /// project once lost control of: a presentation view drove it for a slow-motion
    /// touchdown, a scene unloaded mid-effect, and the Editor was left at 0.35 (see
    /// Systems_BroadcastCameraView). The objection was to a VIEW owning a global and
    /// to nothing owning its reset. This is a Game-mode registration in
    /// Systems_GameLifetimeScope, so the container that created it disposes it when
    /// SCN_GAME unloads, and Dispose puts the scale back to 1 — there is no route out
    /// of a game that leaves the menu running fast.
    ///
    /// WHAT IT DOES NOT CHANGE. Time.fixedDeltaTime is untouched, as it is in
    /// Editor_SimSpeed: a faster game runs more 0.02 s ticks per rendered frame, so
    /// every play is simulated exactly as at 1x. Nothing here can alter a result.
    ///
    /// Capped at 4x on a phone. Unity stops catching up at Time.maximumDeltaTime,
    /// so beyond about 4x on a mid-range handset the clock would stop keeping pace
    /// and the picture would turn into a slideshow without getting any faster.
    /// </summary>
    public sealed class Systems_SimSpeedSystem : IDisposable
    {
        private static readonly float[] Speeds = { 1f, 2f, 4f };

        /// <summary>
        /// Starts at 1x WITHOUT writing it. Construction happens while the scene
        /// loads, and the Editor's measurement modes (Editor_SimSpeed,
        /// Editor_RealismEval) set their own scale around that moment; writing 1 here
        /// would race them. A player is already at 1 when a game starts.
        /// </summary>
        private int _speedIndex;

        /// <summary>The multiplier the viewer last chose: 1, 2 or 4.</summary>
        public float Speed => Speeds[_speedIndex];

        /// <summary>1x -> 2x -> 4x -> 1x.</summary>
        public void Cycle()
        {
            _speedIndex = (_speedIndex + 1) % Speeds.Length;
            Apply();
        }

        public void Dispose()
        {
            Time.timeScale = 1f;
        }

        private void Apply()
        {
            Time.timeScale = Speeds[_speedIndex];
        }
    }
}
