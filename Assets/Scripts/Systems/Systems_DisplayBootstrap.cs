using UnityEngine;
using UnityEngine.SceneManagement;

namespace PoFootball.Systems
{
    /// <summary>
    /// Applies CLAUDE.md section 3's display contract — portrait, 60 FPS — at
    /// startup, for the two player-facing scenes only.
    ///
    /// WHY THIS EXISTS AT RUNTIME AND NOT ONLY IN PROJECT SETTINGS. The frame rate
    /// half of that contract cannot be expressed in the inspector at all:
    /// <see cref="Application.targetFrameRate"/> has no project setting, and it is
    /// silently ignored whenever the active quality level has
    /// <c>vSyncCount &gt; 0</c> — which every shipping configuration of this project
    /// had. Per-platform default quality is Ultra (5) on Standalone and Medium (2)
    /// on Android and iOS, and all three of those levels ship with vSync on. The
    /// Editor happened to sit on "Very Low" (vSync 0), so the cap looked correct in
    /// the only place anyone ever looked and was inert everywhere else.
    ///
    /// The order in <see cref="Apply"/> is the whole point: vSync goes off FIRST or
    /// the assignment after it does nothing. Setting the two the other way round is
    /// the bug this file was written to close.
    ///
    /// THE TRAINING SCENE IS EXCLUDED, AND THAT IS NOT AN OVERSIGHT. Capping the
    /// frame rate caps the simulation, because this game steps physics from the
    /// player loop. `football_base06` runs at 243 steps/s in the Editor and 400+
    /// headless; pegging either to 60 would cost roughly four fifths of the
    /// throughput of every run from here on, and it would do it silently — a slow
    /// trainer looks exactly like a slow machine. A batch-mode check alone is not
    /// enough, because CLAUDE.md's in-editor smoke test ("start the trainer, then
    /// press Play") is not batch mode. The active scene name is what actually
    /// separates the two cases.
    ///
    /// AfterSceneLoad rather than BeforeSceneLoad for the same reason: the scene
    /// has to exist before it can be named. One frame late costs nothing, and the
    /// hook fires once per process — the settings then survive the menu-to-game
    /// load, which is the only scene change either player-facing scene ever makes.
    /// </summary>
    internal static class Systems_DisplayBootstrap
    {
        /// <summary>Frames per second the game is drawn at. CLAUDE.md section 3.</summary>
        private const int TARGET_FRAME_RATE = 60;

        /// <summary>
        /// Unity's "no cap, run as fast as the platform allows". Written explicitly
        /// in the training branch rather than left alone, because targetFrameRate
        /// survives a play session in the Editor.
        /// </summary>
        private const int UNCAPPED_FRAME_RATE = -1;

        /// <summary>
        /// Prefix of every training scene, per UNITY_RULES section 1
        /// (`SCN_TRAIN_&lt;NAME&gt;`). Matched rather than compared to one literal so
        /// a second training scene is covered the day it is added.
        /// </summary>
        private const string TRAINING_SCENE_PREFIX = "SCN_TRAIN_";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Apply()
        {
            // Off unconditionally. vSync is what makes targetFrameRate a no-op, and
            // a trainer has no use for it either — it only ties the loop to a
            // display that a headless run does not have.
            QualitySettings.vSyncCount = 0;

            if (IsTraining())
            {
                // ASSIGNED, NOT MERELY SKIPPED. Application.targetFrameRate is
                // process-wide and the Editor does not reset it between play
                // sessions, so a developer who played SCN_GAME and then pressed Play
                // on the training scene inherited the 60 cap from the previous run
                // and trained at a quarter speed for no visible reason. Falling
                // through without writing anything was the first version of this
                // method and it had exactly that bug. -1 is "as fast as the platform
                // allows", which is what a trainer wants.
                Application.targetFrameRate = UNCAPPED_FRAME_RATE;

                // One line, once per process, matching the four other [PoFootball]
                // startup logs. It earns its place: the difference between "capped"
                // and "not capped" is invisible in the Editor, whose own loop runs
                // at roughly 60 either way, so without this the only way to tell
                // whether the exclusion fired is to make a build.
                Debug.Log(
                    "[PoFootball] Display: training scene — frame rate uncapped "
                    + $"(targetFrameRate={Application.targetFrameRate}), vSync "
                    + $"{QualitySettings.vSyncCount}.");
                return;
            }

            Application.targetFrameRate = TARGET_FRAME_RATE;

            Screen.autorotateToPortrait = true;
            Screen.autorotateToPortraitUpsideDown = false;
            Screen.autorotateToLandscapeLeft = false;
            Screen.autorotateToLandscapeRight = false;

            // Belt and braces over the PlayerSettings lock, which governs the launch
            // orientation and the splash but not a platform that restores a previous
            // one. Landscape is not a cosmetic problem here: the UI panel scales on
            // WIDTH against a 1080x1920 reference, so rotating multiplies every
            // element by the aspect ratio and then asks a 1920-unit-tall design to
            // fit in 1080 units of height. Ignored on desktop, which cannot rotate.
            Screen.orientation = ScreenOrientation.Portrait;

            Debug.Log(
                $"[PoFootball] Display: {Application.targetFrameRate} FPS, "
                + $"vSync {QualitySettings.vSyncCount}, portrait locked.");
        }

        private static bool IsTraining()
        {
            return SceneManager.GetActiveScene().name.StartsWith(
                TRAINING_SCENE_PREFIX, System.StringComparison.Ordinal);
        }
    }
}

