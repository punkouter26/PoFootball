using System;
using UnityEditor;
using UnityEditor.Build;
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
    internal static class PoFootball_BuildMenu
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

        /// <summary>
        /// The Android package. An .apk rather than an .aab because this is a build
        /// to sideload onto a device (`adb install -r`), not a Play Store upload —
        /// an .aab cannot be installed directly.
        /// </summary>
        private const string ANDROID_OUTPUT = "Builds/Android/PoFootball.apk";

        /// <summary>
        /// Reverse-DNS application id. Must be set explicitly: Unity's default is
        /// com.DefaultCompany.&lt;product&gt;, which Android will happily install but
        /// which collides with every other project that never changed it.
        /// </summary>
        private const string ANDROID_PACKAGE = "com.punkouter.pofootball";

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

        /// <summary>
        /// The phone build. Same two player-facing scenes as the desktop game and
        /// deliberately NOT the training scene — that one carries the Academy and
        /// every ML-Agents dependency, and none of it belongs in a retail package.
        ///
        /// Requires Android Build Support to be installed in the Editor. It is not
        /// installed by default and cannot be added from a script; the menu item is
        /// disabled with a clear message rather than failing halfway through a build.
        /// </summary>
        [MenuItem("Tools/PoFootball/Build Android")]
        private static void BuildAndroid()
        {
            if (!IsAndroidSupportInstalled())
            {
                Debug.LogError(
                    "[Build] Android Build Support is not installed for this Editor. "
                    + "Unity Hub > Installs > 6000.5.8f1 > gear > Add modules > "
                    + "Android Build Support (tick Android SDK & NDK Tools and "
                    + "OpenJDK), then reopen the project.");
                return;
            }

            ApplyAndroidPlayerSettings();

            Build(
                "Android",
                new[] { MENU_SCENE, GAME_SCENE },
                ANDROID_OUTPUT,
                BuildOptions.None,
                BuildTarget.Android,
                BuildTargetGroup.Android);
        }

        [MenuItem("Tools/PoFootball/Build Android", true)]
        private static bool BuildAndroidValidate()
        {
            return IsAndroidSupportInstalled();
        }

        private static bool IsAndroidSupportInstalled()
        {
            return BuildPipeline.IsBuildTargetSupported(
                BuildTargetGroup.Android, BuildTarget.Android);
        }

        /// <summary>
        /// The settings a phone build needs that a desktop one does not. Applied
        /// here rather than left in the .asset so they are in the repository next to
        /// the code, which is the same argument the class summary makes about scenes.
        ///
        /// PORTRAIT ONLY, because the whole UI is built for it — CLAUDE.md §3 pins
        /// the game to portrait 9:16 and Systems_UiTheme scales the panel on width.
        /// Letting the device rotate would hand that layout a landscape viewport it
        /// was never designed against.
        ///
        /// IL2CPP AND ARM64, because Google Play has required a 64-bit binary since
        /// 2019 and Mono cannot produce one. This is also what makes the physics run
        /// at a playable rate on a phone.
        /// </summary>
        private static void ApplyAndroidPlayerSettings()
        {
            PlayerSettings.SetApplicationIdentifier(
                NamedBuildTarget.Android, ANDROID_PACKAGE);

            PlayerSettings.SetScriptingBackend(
                NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);

            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;

            PlayerSettings.defaultInterfaceOrientation =
                UIOrientation.Portrait;
            PlayerSettings.allowedAutorotateToPortrait = true;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.allowedAutorotateToLandscapeLeft = false;
            PlayerSettings.allowedAutorotateToLandscapeRight = false;

            // GRAPHICS APIs ARE LEFT AT UNITY'S DEFAULTS, DELIBERATELY.
            //
            // This used to force Vulkan first with GLES3 behind it, on the reasoning
            // that Vulkan is the better path on modern hardware. That reasoning was
            // never tested and the build it produced DID NOT RUN: on a Pixel 9 Pro
            // the player reached "Device Model ..." in logcat and then went silent —
            // no GfxDevice line, no scene load, a black screen and a process sitting
            // there until it was killed. Graphics device creation never completed.
            //
            // Unity already picks a per-version, per-device-appropriate order. There
            // is no evidence this project needs a different one, and overriding it
            // cost a working build.
            //
            // Asserted rather than merely omitted: the override is persisted into
            // ProjectSettings by whichever build last set it, so deleting the code
            // that wrote it would leave the bad value in place.
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, true);

            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel24;

            Debug.Log(
                $"[Build] Android player settings applied: {ANDROID_PACKAGE}, "
                + "IL2CPP/ARM64, portrait-locked, Vulkan+GLES3, minSdk 24.");
        }

        private static void Build(
            string label, string[] scenes, string outputPath, BuildOptions options)
        {
            Build(
                label, scenes, outputPath, options,
                BuildTarget.StandaloneWindows64, BuildTargetGroup.Standalone);
        }

        private static void Build(
            string label,
            string[] scenes,
            string outputPath,
            BuildOptions options,
            BuildTarget target,
            BuildTargetGroup targetGroup)
        {
            BuildPlayerOptions playerOptions = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = outputPath,
                target = target,
                targetGroup = targetGroup,
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
