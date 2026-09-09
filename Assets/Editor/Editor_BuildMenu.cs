using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace PoFootball.EditorTools
{
    /// <summary>
    /// The two builds this project actually produces, each with its own scene list.
    ///
    /// WHY THIS FILE EXISTS. One EditorBuildSettings list was being asked to serve
    /// two incompatible builds. It held SCN_TRAIN_FOOTBALL and nothing else, which
    /// is correct for the trainer's env and wrong for everything else: neither
    /// player-facing scene was in the build, so a retail player booted straight into
    /// the training scene, and every navigation in the game — PLAY, QUIT, REMATCH
    /// and the post-game MENU button, all four of which call
    /// SceneManager.LoadSceneAsync by name — could not resolve its target, because
    /// LoadScene only sees scenes on that list.
    ///
    /// The list is now SCN_MENU, SCN_GAME, SCN_TRAIN_FOOTBALL, so scene loading
    /// works in the Editor and in a player. That fixes the game and breaks the env,
    /// because the env must open SCN_TRAIN_FOOTBALL at index 0 or the trainer's
    /// handshake talks to a scene with no Academy and no agents in it. Hence a
    /// build entry point per target that sets its own scenes explicitly, instead of
    /// a shared list that is only ever right for whichever build was made last.
    ///
    /// Deliberately not Build Profiles: profile assets are another thing to keep in
    /// sync with the scene names, and the whole problem here was configuration
    /// drifting away from what the code expects. Naming the scenes in the same
    /// repository as the code that loads them is the point.
    /// </summary>
    internal static class Editor_BuildMenu
    {
        private const string MENU_SCENE = "Assets/Scenes/SCN_MENU.unity";
        private const string GAME_SCENE = "Assets/Scenes/SCN_GAME.unity";
        private const string TRAIN_SCENE = "Assets/Scenes/SCN_TRAIN_FOOTBALL.unity";

        /// <summary>
        /// Where CLAUDE.md's mlagents-learn invocation expects the env to be:
        /// `--env=Builds\FootballEnv\PoFootball.exe`.
        /// </summary>
        private const string ENV_OUTPUT = "Builds/FootballEnv/PoFootball.exe";

        private const string GAME_OUTPUT = "Builds/Game/PoFootball.exe";



        [MenuItem("Tools/PoFootball/Build Game")]
        private static void BuildGame()
        {
            // SCN_MENU first: index 0 is what a player opens on launch.
            Build(
                "Game",
                new[] { MENU_SCENE, GAME_SCENE },
                GAME_OUTPUT,
                BuildOptions.None);
        }

        [MenuItem("Tools/PoFootball/Build Training Env")]
        private static void BuildTrainingEnv()
        {
            // The training scene ALONE, at index 0. Shipping the menu inside the env
            // would boot the trainer into a front end with no Academy in it, and the
            // handshake would time out against a scene that never registers a single
            // agent.
            Build(
                "Training env",
                new[] { TRAIN_SCENE },
                ENV_OUTPUT,
                BuildOptions.None);
        }

        // ANDROID IS NOT BUILT FROM HERE — DELIBERATELY.
        //
        // This file used to carry a "Build Android" item that set the application
        // id to com.punkouter.pofootball and wrote an UNSIGNED apk to
        // Builds/Android/PoFootball.apk. Editor_BuildAndroid writes a SIGNED apk to
        // that same path under com.punkoutersoftware.pofootball, which CLAUDE.md
        // pins as permanent. Whichever menu item ran last won the file, and running
        // this one rewrote the shipped application id in ProjectSettings — where it
        // persisted silently into the next release build.
        //
        // Android now has exactly one owner per artifact: Editor_BuildAndroid for
        // the sideload apk and Editor_BuildAndroidAAB for the Play bundle. Both read
        // their identity and keystore from Editor_BuildAndroidAAB's constants, so
        // there is one definition of who this app is.

        private static void Build(
            string label, string[] scenes, string outputPath, BuildOptions options)
        {
            BuildPlayerOptions playerOptions = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = outputPath,
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = options,
            };

            BuildReport report = BuildPipeline.BuildPlayer(playerOptions);
            BuildSummary summary = report.summary;

            if (summary.result == BuildResult.Succeeded)
            {
                Debug.Log(
                    $"[Build] {label} -> {outputPath} "
                    + $"({summary.totalSize / (1024 * 1024)} MB, "
                    + $"{summary.totalTime.TotalSeconds:F0}s, "
                    + $"{scenes.Length} scene(s)).");
                return;
            }

            Debug.LogError(
                $"[Build] {label} {summary.result} with {summary.totalErrors} error(s). "
                + "See the Console and the BuildReport for detail.");
        }
    }
}
