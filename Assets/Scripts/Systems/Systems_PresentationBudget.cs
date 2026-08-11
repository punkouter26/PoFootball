using PoFootball.Models;
using UnityEngine;

namespace PoFootball.Systems
{
    /// <summary>
    /// The single answer to "may presentation spend anything on this frame?".
    ///
    /// Every graphics and audio embellishment in PoFootball.Views asks this before
    /// it builds a light rig, a particle system, a post-processing volume or a
    /// crowd bed. It exists because CLAUDE.md's MLOps section makes one promise
    /// that a graphics pass is uniquely good at breaking: an .onnx must be
    /// evaluated against the same environment it was fitted in, and a training
    /// sweep must not slow down because the game got prettier.
    ///
    /// Three things switch it off, and each is a real failure mode:
    ///
    ///   Training mode — mlagents-learn drives the same scene graph at time_scale
    ///   20. Twenty-two shadow casters, a bloom pass and four crowd beds cost
    ///   wall-clock per step for nobody's benefit; the trainer never looks at the
    ///   frame and never hears the crowd.
    ///
    ///   Batch mode — `--no-graphics` has no swap chain and no audio device.
    ///   Volume components and AudioSources are not errors there, they are just
    ///   waste, and a headless sweep runs 4–8 of these environments at once.
    ///
    ///   An explicit scene opt-out — for profiling the simulation on its own, so
    ///   a frame-time regression can be attributed without deleting objects.
    ///
    /// NOT A SERVICE LOCATOR. It carries one boolean and answers one question. A
    /// view that injects this is declaring "I am optional decoration", which is
    /// exactly the dependency it has (.claude/rules/architecture.md).
    /// </summary>
    public sealed class Systems_PresentationBudget
    {
        public Systems_PresentationBudget(Systems_SimMode simMode, bool sceneOptIn)
        {
            // Application.isBatchMode is the honest test for --no-graphics. A null
            // graphics device also reports here, which covers a build launched
            // headless without the flag.
            bool headless = Application.isBatchMode
                || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null;

            EffectsEnabled = sceneOptIn && !headless && simMode == Systems_SimMode.Game;
        }

        /// <summary>
        /// False whenever this environment is being trained, measured or run
        /// headless. Views must treat it as "do not construct", not merely "do not
        /// play" — the cost being avoided is the setup, not the trigger.
        /// </summary>
        public bool EffectsEnabled { get; }
    }
}
