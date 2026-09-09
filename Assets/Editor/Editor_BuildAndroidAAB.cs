using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace PoFootball.EditorTools
{
    /// <summary>
    /// Builds the signed release Android App Bundle (.aab) for Google Play.
    ///
    /// The upload keystore and its password live OUTSIDE the repo so neither can
    /// be committed:
    ///     C:/Users/punko/Downloads/PoFootball-Release/pofootball-upload.jks
    ///     C:/Users/punko/Downloads/PoFootball-Release/pofootball-upload.pass   (password, one line)
    ///
    /// Unity deliberately does not serialize keystore passwords into
    /// ProjectSettings.asset, so they must be supplied at build time — that is what
    /// the .pass file (or the POFOOTBALL_KEYSTORE_PASS environment variable, which wins if
    /// set) is for. Without either the build ABORTS, rather than producing an
    /// unsigned artifact that Play would reject minutes later.
    ///
    /// Invoked from the PoFootball menu, or headlessly with -executeMethod
    /// PoFootball.EditorTools.Editor_BuildAndroidAAB.Build. Either way the outcome is the
    /// "AAB BUILD RESULT:" line in the editor log.
    /// </summary>
    public static class Editor_BuildAndroidAAB
    {
        private const string OUTPUT_PATH = "Builds/Android/PoFootball.aab";

        /// PERMANENT once the first bundle is uploaded — Play keys the app on it.
        internal const string APP_ID = "com.punkoutersoftware.pofootball";

        // internal, not private: the APK builder signs with the same upload key, so
        // an APK installed over a Play build does not hit a signature mismatch.
        //
        // MOVED 2026-09-09, from C:/Users/punko/Downloads/PoFootball-Release/.
        // That folder did not exist — the keystores were consolidated into the
        // OneDrive vault below on 2026-09-02 and the old per-app -Release folders
        // deleted, and this constant was never updated, so every Android build in
        // this project had been aborting on "keystore not found" since. The vault's
        // own KEYSTORES-README.txt is the authority on the layout: flat files,
        // app-name prefixed, <name>.jks beside <name>.pass.
        internal const string KEYSTORE_PATH = "C:/Users/punko/OneDrive/VAULT/_CODE/pofootball-upload.jks";
        internal const string KEYALIAS = "pofootball-upload";
        internal const string PASS_ENV_VAR = "POFOOTBALL_KEYSTORE_PASS";

        // Google Play requires new apps and updates to target API 36 from 2026-08-31.
        private const int TARGET_SDK = 36;
        private const int MIN_SDK = 26;

        /// The scenes that belong in a PLAYER, in boot order — index 0 is what the
        /// app opens into. This is an explicit list rather than whatever is ticked
        /// in Build Settings, because Build Settings also carries the training
        /// scenes (SCN_TRAIN_FOOTBALL) and shipping those would
        /// both bloat the bundle and, depending on order, boot a tester into a
        /// training rig.
        internal static readonly string[] SHIP_SCENES =
        {
            "Assets/Scenes/SCN_MENU.unity",
            "Assets/Scenes/SCN_GAME.unity",
        };

        [MenuItem("Tools/PoFootball/Build Android AAB (Play release)")]
        public static void Build()
        {
            if (EditorApplication.isPlaying)
            {
                Debug.LogError("AAB BUILD RESULT: Aborted — exit Play mode first.");
                return;
            }

            string password = ResolveKeystorePassword();
            if (string.IsNullOrEmpty(password))
            {
                Debug.LogError(
                    "AAB BUILD RESULT: Aborted — no keystore password. Set " + PASS_ENV_VAR +
                    " or put the password on the first line of " +
                    Path.ChangeExtension(KEYSTORE_PATH, null) + ".pass");
                return;
            }

            if (!File.Exists(KEYSTORE_PATH))
            {
                Debug.LogError("AAB BUILD RESULT: Aborted — keystore not found at " + KEYSTORE_PATH);
                return;
            }

            // Editor tooling must not ship. com.ivanmurzak.unity.mcp's resolver
            // re-marks every assembly under Assets/Plugins/NuGet as "any platform"
            // on each resolve, so the pin is asserted here rather than trusted —
            // this is the only moment it has to be true. See Editor_NuGetPluginGuard.
            Editor_NuGetPluginGuard.PinBeforeBuild();

            List<string> scenes = ResolveShipScenes();
            if (scenes == null)
            {
                return;
            }

            // --- Identity ---
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, APP_ID);
            PlayerSettings.companyName = "Punkouter Software";
            PlayerSettings.productName = "PoFootball";

            // --- Signing ---
            PlayerSettings.Android.useCustomKeystore = true;
            PlayerSettings.Android.keystoreName = KEYSTORE_PATH;
            PlayerSettings.Android.keystorePass = password;
            PlayerSettings.Android.keyaliasName = KEYALIAS;
            PlayerSettings.Android.keyaliasPass = password;

            // --- Play requirements ---
            PlayerSettings.Android.minSdkVersion = (AndroidSdkVersions)MIN_SDK;
            PlayerSettings.Android.targetSdkVersion = (AndroidSdkVersions)TARGET_SDK;
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetIl2CppCompilerConfiguration(NamedBuildTarget.Android, Il2CppCompilerConfiguration.Release);

            // .aab, not .apk. This is the switch Play cares about most.
            EditorUserBuildSettings.buildAppBundle = true;
            EditorUserBuildSettings.androidBuildType = AndroidBuildType.Release;
            EditorUserBuildSettings.development = false;

            int versionCode = NextVersionCode();

            Directory.CreateDirectory(Path.GetDirectoryName(OUTPUT_PATH));

            var options = new BuildPlayerOptions
            {
                scenes = scenes.ToArray(),
                locationPathName = OUTPUT_PATH,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None,
            };

            Debug.Log($"AAB BUILD START: {APP_ID} v{PlayerSettings.bundleVersion} " +
                      $"(code {versionCode}) " +
                      $"target={TARGET_SDK} min={MIN_SDK} scenes={scenes.Count} " +
                      $"boot={Path.GetFileNameWithoutExtension(scenes[0])}");

            BuildReport report;
            try
            {
                report = BuildPipeline.BuildPlayer(options);
            }
            finally
            {
                // Do not leave the password sitting in the in-memory PlayerSettings.
                PlayerSettings.Android.keystorePass = string.Empty;
                PlayerSettings.Android.keyaliasPass = string.Empty;
            }

            BuildSummary summary = report.summary;
            Debug.Log($"AAB BUILD RESULT: {summary.result} | errors={summary.totalErrors} | " +
                      $"size={summary.totalSize / (1024 * 1024)}MB | " +
                      $"time={summary.totalTime.TotalMinutes:F1}min | {summary.outputPath}");
        }

        /// <summary>
        /// Bumps <c>bundleVersionCode</c> and returns the new value.
        ///
        /// AUTOMATIC, BECAUSE THE MANUAL VERSION OF THIS IS A CONSTANT IN A FILE
        /// NOBODY OPENS. Editor_ConfigureAndroidRelease carries VERSION_CODE = 1 and
        /// re-asserts it on every run, so the documented workflow ("bump
        /// VERSION_CODE for every upload") is one edit away from silently shipping
        /// the same code twice — which Play rejects on upload, and which is
        /// indistinguishable on a device from "the install did not take".
        ///
        /// Called by both builders, so an APK and the AAB beside it never claim to
        /// be the same build. It writes ProjectSettings.asset, so the increment
        /// survives the Editor closing.
        /// </summary>
        internal static int NextVersionCode()
        {
            int next = PlayerSettings.Android.bundleVersionCode + 1;
            PlayerSettings.Android.bundleVersionCode = next;
            AssetDatabase.SaveAssets();
            return next;
        }

        /// Verifies every shipping scene is actually on disk and returns them in
        /// boot order. A missing scene is an abort rather than a warning: a bundle
        /// silently short one scene is a crash on a tester's phone, discovered a
        /// day later.
        internal static List<string> ResolveShipScenes()
        {
            var scenes = new List<string>();
            foreach (string path in SHIP_SCENES)
            {
                if (!File.Exists(path))
                {
                    Debug.LogError("BUILD RESULT: Aborted — shipping scene not found: " + path);
                    return null;
                }
                scenes.Add(path);
            }
            return scenes;
        }

        /// Environment variable wins; otherwise read &lt;keystore&gt;.pass beside the keystore.
        internal static string ResolveKeystorePassword()
        {
            string fromEnv = Environment.GetEnvironmentVariable(PASS_ENV_VAR);
            if (!string.IsNullOrEmpty(fromEnv))
            {
                return fromEnv.Trim();
            }

            string passFile = Path.ChangeExtension(KEYSTORE_PATH, null) + ".pass";
            if (File.Exists(passFile))
            {
                return File.ReadAllText(passFile).Trim();
            }

            string sibling = Path.Combine(Path.GetDirectoryName(KEYSTORE_PATH) ?? ".", "keystore.pass");
            return File.Exists(sibling) ? File.ReadAllText(sibling).Trim() : null;
        }
    }
}
