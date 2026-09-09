using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace PoFootball.EditorTools
{
    /// <summary>
    /// Builds the Android APK for on-device testing — the artifact you sideload
    /// with adb, as opposed to the .aab that goes to Play. Same scenes and same
    /// signing key as the bundle, so what you test is what you ship.
    /// Reports through the "BUILD RESULT:" line in the editor log.
    /// </summary>
    public static class Editor_BuildAndroid
    {
        private const string OUTPUT_PATH = "Builds/Android/PoFootball.apk";

        /// <summary>
        /// Set by <see cref="BuildDevelopment"/> for the duration of one build.
        ///
        /// WHY A DEVELOPMENT APK IS A SEPARATE ENTRY POINT AND NOT A FLAG SOMEONE
        /// TICKS. Systems_StatusHudView's diagnostic sheet is gated on
        /// Debug.isDebugBuild, which is exactly right — a retail install has no
        /// business quoting heap sizes at a player. But that also means the sheet
        /// cannot be verified on a device unless the artifact under test is a
        /// development build, and "tick Development Build in the dialog, then
        /// remember to untick it" is how an unsigned-feeling debug APK ends up on
        /// an internal testing track. Two named entry points, no shared state
        /// between runs, and the log line says which one produced the file.
        /// </summary>
        private static bool _development;

        [MenuItem("Tools/PoFootball/Build Android APK")]
        public static void Build()
        {
            if (EditorApplication.isPlaying)
            {
                Debug.LogError("BUILD RESULT: Aborted — exit Play mode first.");
                return;
            }

            string password = Editor_BuildAndroidAAB.ResolveKeystorePassword();
            bool hasKeystore = !string.IsNullOrEmpty(password)
                               && System.IO.File.Exists(Editor_BuildAndroidAAB.KEYSTORE_PATH);

            // THE UPLOAD KEY IS REQUIRED FOR A RELEASE APK AND NOT FOR A
            // DEVELOPMENT ONE, and the difference is not a relaxed standard — it is
            // what each artifact is for.
            //
            // A release APK exists to be installed OVER a Play build, which is the
            // whole reason it is signed with the upload key rather than a debug one;
            // producing it unsigned or debug-signed gives a file that looks right,
            // installs on a clean phone, and then refuses to update a real install
            // with a signature mismatch weeks later. That still aborts.
            //
            // A development APK can never be uploaded anywhere — Play rejects a
            // debuggable bundle outright — so the upload key buys it nothing, and
            // demanding it means a missing or not-yet-created keystore blocks the
            // one artifact whose entire job is to get onto a test handset today.
            // Unity falls back to the Android SDK's debug keystore, which is exactly
            // what every other Android toolchain does for a debug build.
            if (!hasKeystore && !_development)
            {
                Debug.LogError(
                    "BUILD RESULT: Aborted — no usable upload keystore. Expected "
                    + Editor_BuildAndroidAAB.KEYSTORE_PATH + " with its .pass "
                    + "beside it, or the " + Editor_BuildAndroidAAB.PASS_ENV_VAR
                    + " environment variable. Build the development APK instead if "
                    + "this is for a test device.");
                return;
            }

            // Editor tooling must not ship. com.ivanmurzak.unity.mcp's resolver
            // re-marks every assembly under Assets/Plugins/NuGet as "any platform"
            // on each resolve, so the pin is asserted here rather than trusted —
            // this is the only moment it has to be true. See Editor_NuGetPluginGuard.
            Editor_NuGetPluginGuard.PinBeforeBuild();

            var scenes = Editor_BuildAndroidAAB.ResolveShipScenes();
            if (scenes == null)
            {
                return;
            }

            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, Editor_BuildAndroidAAB.APP_ID);
            PlayerSettings.Android.useCustomKeystore = hasKeystore;

            if (hasKeystore)
            {
                PlayerSettings.Android.keystoreName = Editor_BuildAndroidAAB.KEYSTORE_PATH;
                PlayerSettings.Android.keystorePass = password;
                PlayerSettings.Android.keyaliasName = Editor_BuildAndroidAAB.KEYALIAS;
                PlayerSettings.Android.keyaliasPass = password;
            }
            else
            {
                // Loud, and a warning rather than a log line: an APK on the debug
                // key is fine on a bare test phone and is a dead end the moment
                // anything is on Play under this application id, so nobody should
                // discover which one they built by reading the file date.
                Debug.LogWarning(
                    "BUILD SIGNING: no upload keystore at "
                    + Editor_BuildAndroidAAB.KEYSTORE_PATH
                    + " — this development APK is signed with the Android SDK debug "
                    + "key. It installs on a test device and can NEVER update a Play "
                    + "install of " + Editor_BuildAndroidAAB.APP_ID + ".");
            }

            // The AAB builder leaves buildAppBundle = true persisted in
            // EditorUserBuildSettings. Without this the "APK" build silently emits an
            // app bundle to PoFootball.apk, which adb cannot install.
            EditorUserBuildSettings.buildAppBundle = false;

            // The APK must claim its own version code. Sideloading a build whose
            // code matches the one already on the phone installs cleanly and looks
            // identical, so "my fix is not in the build" and "I installed the same
            // build twice" are the same observation until something increments.
            int versionCode = Editor_BuildAndroidAAB.NextVersionCode();

            EditorUserBuildSettings.development = _development;
            EditorUserBuildSettings.androidBuildType = _development
                ? AndroidBuildType.Development
                : AndroidBuildType.Release;

            var options = new BuildPlayerOptions
            {
                scenes = scenes.ToArray(),
                locationPathName = OUTPUT_PATH,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,

                // IL2CPP stays on Release in both cases — the compiler
                // configuration is set once by Configure Android Release Settings
                // and this flag only decides whether Debug.isDebugBuild is true and
                // whether the profiler can attach. A development APK is therefore
                // the SAME code the release one runs, plus diagnostics.
                options = _development ? BuildOptions.Development : BuildOptions.None,
            };

            Debug.Log($"BUILD START: {Editor_BuildAndroidAAB.APP_ID} " +
                      $"v{PlayerSettings.bundleVersion} (code {versionCode}) " +
                      $"{(_development ? "development" : "release")} " +
                      $"scenes={scenes.Count}");

            BuildReport report;
            try
            {
                report = BuildPipeline.BuildPlayer(options);
            }
            finally
            {
                PlayerSettings.Android.keystorePass = string.Empty;
                PlayerSettings.Android.keyaliasPass = string.Empty;

                // Cleared here rather than at the end of BuildDevelopment, so an
                // exception inside BuildPlayer cannot leave the next release build
                // silently marked development.
                _development = false;
            }

            BuildSummary summary = report.summary;
            Debug.Log($"BUILD RESULT: {summary.result} | errors={summary.totalErrors} | " +
                      $"code={versionCode} | " +
                      $"size={summary.totalSize / (1024 * 1024)}MB | " +
                      $"time={summary.totalTime.TotalMinutes:F1}min | {OUTPUT_PATH}");
        }

        /// <summary>
        /// The same APK, signed with the same key, with Development Build on — which
        /// is what makes Debug.isDebugBuild true and therefore what makes the DEBUG
        /// sheet in Systems_StatusHudView appear. This is the artifact to put on a
        /// test handset; <see cref="Build"/> is the one that matches what Play gets.
        /// </summary>
        [MenuItem("Tools/PoFootball/Build Android APK (development)")]
        public static void BuildDevelopment()
        {
            _development = true;
            Build();
        }
    }
}
