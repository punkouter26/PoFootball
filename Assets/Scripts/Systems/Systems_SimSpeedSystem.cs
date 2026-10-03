using System;
using UnityEngine;

namespace PoFootball.Systems
{
    /// <summary>
    /// The viewer's playback speed: 1x, 2x, 4x or half speed, cycled from the HUD,
    /// and a pause that sits on top of whichever of those is chosen.
    ///
    /// WHY A SYSTEM MAY DO WHAT A VIEW MAY NOT. Time.timeScale is the global this
    /// project once lost control of: a presentation view drove it for a slow-motion
    /// touchdown, a scene unloaded mid-effect, and the Editor was left at 0.35 (see
    /// Systems_BroadcastCameraView). The objection was to a VIEW owning a global and
    /// to nothing owning its reset. This is a Game-mode registration in
    /// Systems_GameLifetimeScope, so the container that created it disposes it when
    /// SCN_GAME unloads, and Dispose puts the scale back to 1 — there is no route out
    /// of a game that leaves the menu running fast, slow or stopped.
    ///
    /// WHAT IT DOES NOT CHANGE. Time.fixedDeltaTime is untouched, as it is in
    /// Editor_SimSpeed: a faster game runs more 0.02 s ticks per rendered frame and
    /// a slower one fewer, so every play is simulated exactly as at 1x. Nothing
    /// here can alter a result.
    ///
    /// Capped at 4x on a phone. Unity stops catching up at Time.maximumDeltaTime,
    /// so beyond about 4x on a mid-range handset the clock would stop keeping pace
    /// and the picture would turn into a slideshow without getting any faster.
    ///
    /// HALF SPEED COMES AFTER 4x, NOT BEFORE 1x. The cycle starts where a game
    /// starts, and the first two taps are still the two most people want; slow
    /// motion is one further tap round rather than a stop every viewer has to pass
    /// through to get to 2x.
    /// </summary>
    public sealed class Systems_SimSpeedSystem : IDisposable
    {
        private static readonly float[] Speeds = { 1f, 2f, 4f, 0.5f };

        /// <summary>
        /// Starts at 1x WITHOUT writing it. Construction happens while the scene
        /// loads, and the Editor's measurement modes (Editor_SimSpeed,
        /// Editor_RealismEval) set their own scale around that moment; writing 1 here
        /// would race them. A player is already at 1 when a game starts.
        /// </summary>
        private int _speedIndex;

        /// <summary>The multiplier the viewer last chose: 1, 2, 4 or 0.5. Unchanged by a pause.</summary>
        public float Speed => Speeds[_speedIndex];

        public bool IsPaused { get; private set; }

        /// <summary>1x -> 2x -> 4x -> 0.5x -> 1x. Applies when play resumes if paused.</summary>
        public void Cycle()
        {
            _speedIndex = (_speedIndex + 1) % Speeds.Length;
            Apply();
        }

        /// <summary>
        /// Stops the simulation dead, or lets it go again at the chosen speed. The
        /// camera and the HUD keep running — both read unscaled time while this is
        /// set — so a paused play can still be panned around and inspected.
        /// </summary>
        public void TogglePause()
        {
            IsPaused = !IsPaused;
            Apply();
        }

        public void Dispose()
        {
            Time.timeScale = 1f;
        }

        private void Apply()
        {
            Time.timeScale = IsPaused ? 0f : Speeds[_speedIndex];
        }
    }
}
