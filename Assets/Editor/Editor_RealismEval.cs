using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using PoFootball.Agents;
using PoFootball.Views;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PoFootball.EditorTools
{
    /// <summary>
    /// Plays three full games of SCN_GAME at 8x and writes their REALISM lines to
    /// results/realism/realism-&lt;stamp&gt;.json, for
    /// `Tools/promote_brain.py --check-realism` to judge.
    ///
    /// WHY THIS EXISTS. CLAUDE.md makes the REALISM line the only test of whether
    /// the simulation is football, and says single games are too noisy to judge
    /// (4.48 to 7.37 yards a play on one config). Collecting three by hand meant
    /// watching three games and copying numbers out of the console. A promoted brain
    /// is exactly when that measurement matters most and is most likely to be
    /// skipped, so the promotion gate now asks for this file.
    ///
    /// HEADLESS AS WELL. `Unity.exe -batchmode -projectPath . -executeMethod
    /// PoFootball.EditorTools.Editor_RealismEval.RunBatch` (no -quit) plays the same
    /// three games and exits 0, or 1 if a game never reached the whistle. Batch mode
    /// turns the presentation off through Systems_PresentationBudget, which changes
    /// nothing a policy sees.
    ///
    /// The games go through Systems_SceneRouter.LoadGame, the same route REMATCH
    /// takes, so each starts with a fresh container and a fresh per-game seed.
    /// Fixed delta time is untouched: 8x is Editor_SimSpeed's measurement mode.
    /// State lives in SessionState because entering play mode may reload the domain.
    /// </summary>
    [InitializeOnLoad]
    internal static class Editor_RealismEval
    {
        private const string GAME_SCENE_PATH = "Assets/Scenes/SCN_GAME.unity";
        private const int GAMES = 3;
        private const float SIM_SPEED = 8f;

        /// <summary>
        /// Real seconds one game may take before the run is abandoned. A game is about
        /// 100 s at 8x; ten times that means it is not going to finish.
        /// </summary>
        private const double GAME_TIMEOUT_SECONDS = 1000.0;

        private const string KEY_REMAINING = "PoFootball.Realism.Remaining";
        private const string KEY_RESULTS = "PoFootball.Realism.Results";
        private const string KEY_BATCH = "PoFootball.Realism.Batch";
        private const string KEY_GAME_STARTED = "PoFootball.Realism.GameStarted";
        private const string KEY_EXIT_CODE = "PoFootball.Realism.ExitCode";

        /// <summary>Must match the Debug.Log in Systems_GameFlowSystem's final whistle.</summary>
        private static readonly Regex RealismLine = new Regex(
            @"\[PoFootball\] REALISM\s+yards/play ([\d.]+) \| 4th downs faced (\d+) "
            + @"\| TD/drive ([\d.]+) \| scrimmage plays (\d+)");

        static Editor_RealismEval()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            SceneManager.sceneLoaded += OnSceneLoaded;
            EditorApplication.update += WatchTimeout;

            // Re-attached after every domain reload, including the one entering play
            // mode can cause, because the handler list does not survive it.
            if (IsRunning)
            {
                Application.logMessageReceived += OnLog;
            }
        }

        private static bool IsRunning => SessionState.GetInt(KEY_REMAINING, 0) > 0;

        [MenuItem("Tools/PoFootball/Evaluate Realism (3 games)")]
        private static void RunFromMenu()
        {
            Begin(false);
        }

        /// <summary>Entry point for -executeMethod. Leaves the Editor running; it exits when done.</summary>
        public static void RunBatch()
        {
            Begin(true);
        }

        private static void Begin(bool batch)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogError("[PoFootball] Evaluate Realism: leave play mode first.");
                return;
            }

            if (!batch && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }

            SessionState.SetInt(KEY_REMAINING, GAMES);
            SessionState.SetString(KEY_RESULTS, string.Empty);
            SessionState.SetBool(KEY_BATCH, batch);
            SessionState.SetInt(KEY_EXIT_CODE, 0);
            MarkGameStarted();

            EditorSceneManager.OpenScene(GAME_SCENE_PATH);

            Application.logMessageReceived -= OnLog;
            Application.logMessageReceived += OnLog;

            Debug.Log($"[PoFootball] Evaluate Realism: playing {GAMES} games at {SIM_SPEED:F0}x.");
            EditorApplication.EnterPlaymode();
        }

        private static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredPlayMode && IsRunning)
            {
                // After Editor_SimSpeed, which applies its own setting on the same event.
                EditorApplication.delayCall += ApplySimSpeed;
                return;
            }

            if (change == PlayModeStateChange.ExitingPlayMode && IsRunning)
            {
                Debug.LogWarning("[PoFootball] Evaluate Realism abandoned: play mode was exited.");
                SessionState.SetInt(KEY_EXIT_CODE, 1);
                Clear();
                return;
            }

            if (change == PlayModeStateChange.EnteredEditMode && SessionState.GetBool(KEY_BATCH, false))
            {
                SessionState.SetBool(KEY_BATCH, false);
                EditorApplication.Exit(SessionState.GetInt(KEY_EXIT_CODE, 1));
            }
        }

        /// <summary>
        /// Each game's container is disposed on the reload, and anything it owned that
        /// touched the time scale puts it back to 1, so the speed is reapplied per game.
        /// </summary>
        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (IsRunning && EditorApplication.isPlaying)
            {
                EditorApplication.delayCall += ApplySimSpeed;
            }
        }

        private static void ApplySimSpeed()
        {
            if (IsRunning && EditorApplication.isPlaying)
            {
                Time.timeScale = SIM_SPEED;
            }
        }

        private static void OnLog(string condition, string stackTrace, LogType type)
        {
            if (!IsRunning)
            {
                return;
            }

            Match match = RealismLine.Match(condition);

            if (!match.Success)
            {
                return;
            }

            string game = string.Join(
                ",",
                match.Groups[1].Value,
                match.Groups[2].Value,
                match.Groups[3].Value,
                match.Groups[4].Value);

            SessionState.SetString(KEY_RESULTS, SessionState.GetString(KEY_RESULTS, string.Empty) + game + ";");

            int remaining = SessionState.GetInt(KEY_REMAINING, 0) - 1;
            SessionState.SetInt(KEY_REMAINING, Math.Max(remaining, 0));

            Debug.Log($"[PoFootball] Evaluate Realism: game {GAMES - remaining} of {GAMES} recorded.");

            if (remaining > 0)
            {
                // Deferred: this runs inside the final whistle's own log call.
                MarkGameStarted();
                EditorApplication.delayCall += Systems_SceneRouter.LoadGame;
                return;
            }

            // IsRunning is already false here, so the play-mode exit Finish causes is
            // not mistaken for an abandoned run. The results are still in SessionState.
            EditorApplication.delayCall += Finish;
        }

        private static void Finish()
        {
            string path = WriteReport(SessionState.GetString(KEY_RESULTS, string.Empty));
            Debug.Log($"[PoFootball] Evaluate Realism: wrote {path}. Judge it with "
                + "Tools/promote_brain.py --check-realism.");

            SessionState.SetInt(KEY_EXIT_CODE, 0);
            Clear();
            EditorApplication.ExitPlaymode();
        }

        private static void WatchTimeout()
        {
            if (!IsRunning)
            {
                return;
            }

            double started = double.Parse(
                SessionState.GetString(KEY_GAME_STARTED, "0"), CultureInfo.InvariantCulture);

            if (EditorApplication.timeSinceStartup - started < GAME_TIMEOUT_SECONDS)
            {
                return;
            }

            Debug.LogError(
                $"[PoFootball] Evaluate Realism: no final whistle within {GAME_TIMEOUT_SECONDS:F0} s. "
                + "Abandoned; nothing written.");

            SessionState.SetInt(KEY_EXIT_CODE, 1);
            Clear();

            if (EditorApplication.isPlaying)
            {
                EditorApplication.ExitPlaymode();
            }
            else if (SessionState.GetBool(KEY_BATCH, false))
            {
                EditorApplication.Exit(1);
            }
        }

        private static void MarkGameStarted()
        {
            SessionState.SetString(
                KEY_GAME_STARTED,
                EditorApplication.timeSinceStartup.ToString(CultureInfo.InvariantCulture));
        }

        private static void Clear()
        {
            Application.logMessageReceived -= OnLog;
            SessionState.EraseInt(KEY_REMAINING);
            SessionState.EraseString(KEY_RESULTS);
            SessionState.EraseString(KEY_GAME_STARTED);
        }

        private static string WriteReport(string results)
        {
            Agent_BrainTable table = Resources.Load<Agent_BrainTable>(Agent_BrainTable.RESOURCE_PATH);
            bool brainsLoaded = table != null && table.MatchesCurrentContract;

            StringBuilder json = new StringBuilder();
            json.Append("{\n");
            json.Append($"  \"contract_revision\": {Agent_ActionContract.CONTRACT_REVISION},\n");
            json.Append($"  \"brains_loaded\": {(brainsLoaded ? "true" : "false")},\n");
            json.Append($"  \"sim_speed\": {SIM_SPEED.ToString(CultureInfo.InvariantCulture)},\n");
            json.Append("  \"games\": [\n");

            string[] games = results.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

            for (int gameIndex = 0; gameIndex < games.Length; gameIndex++)
            {
                string[] fields = games[gameIndex].Split(',');
                json.Append("    {")
                    .Append($"\"yards_per_play\": {fields[0]}, ")
                    .Append($"\"fourth_downs\": {fields[1]}, ")
                    .Append($"\"touchdowns_per_drive\": {fields[2]}, ")
                    .Append($"\"scrimmage_plays\": {fields[3]}")
                    .Append(gameIndex < games.Length - 1 ? "},\n" : "}\n");
            }

            json.Append("  ]\n}\n");

            string directory = Path.Combine(Directory.GetCurrentDirectory(), "results", "realism");
            Directory.CreateDirectory(directory);

            string path = Path.Combine(
                directory, $"realism-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.json");

            File.WriteAllText(path, json.ToString());
            return path;
        }
    }
}
