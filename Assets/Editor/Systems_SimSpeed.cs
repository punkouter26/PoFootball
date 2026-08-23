using UnityEditor;
using UnityEngine;

namespace PoFootball.EditorTools
{
    /// <summary>
    /// Runs a played game faster than real time, for MEASUREMENT ONLY.
    ///
    /// WHY THIS IS ALLOWED TO EXIST WHEN NOTHING ELSE WRITES Time.timeScale.
    /// CLAUDE.md and Systems_BroadcastCameraView both record that the last writer of
    /// Time.timeScale was removed on purpose: it was a PRESENTATION view driving a
    /// global, and a scene unload mid-effect stranded the editor at 0.35. That
    /// objection is about shipped gameplay code, and every word of it still stands —
    /// this file is in Assets/Editor/, so it is excluded from every build
    /// automatically and cannot run in a player.
    ///
    /// It exists because balancing this game is an empirical loop: change a
    /// constant, play a full game, read the REALISM line
    /// Systems_GameFlowSystem logs at the final whistle, decide. A full game is
    /// about ten minutes at 1x, which is a very expensive unit of information when
    /// the question is "did yards per play come down".
    ///
    /// WHAT IT DOES NOT CHANGE. Time.fixedDeltaTime is untouched — CLAUDE.md pins it
    /// and every policy is fitted against it. Raising timeScale makes Unity run MORE
    /// fixed steps per rendered frame; each one is still exactly 0.02 s of simulated
    /// time, so the physics a play experiences is identical and the measurement is
    /// the same measurement. What suffers is only the frame rate you watch it at.
    ///
    /// Unity clamps how much it will catch up in one frame at Time.maximumDeltaTime,
    /// so past roughly 8x the wall-clock gain flattens out while the game view turns
    /// into a slideshow. The menu stops at 8 for that reason.
    ///
    /// ALWAYS RESTORED. The scale is applied on entering play mode and put back to 1
    /// on leaving it, so it cannot survive into a normal session the way the effect
    /// that got deleted did.
    /// </summary>
    [InitializeOnLoad]
    internal static class Systems_SimSpeed
    {
        private const string PREF_KEY = "PoFootball.SimSpeed";
        private const string MENU_ROOT = "Tools/PoFootball/Sim Speed/";

        static Systems_SimSpeed()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private static float Desired
        {
            get => EditorPrefs.GetFloat(PREF_KEY, 1f);
            set => EditorPrefs.SetFloat(PREF_KEY, value);
        }

        private static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredPlayMode)
            {
                Time.timeScale = Desired;

                if (!Mathf.Approximately(Desired, 1f))
                {
                    Debug.Log(
                        $"[PoFootball] Sim speed {Desired:F0}x — MEASUREMENT MODE. "
                        + "Fixed timestep is unchanged; only wall-clock is compressed.");
                }

                return;
            }

            // Covers ExitingPlayMode and EnteredEditMode both, so an interrupted
            // session cannot leave the editor running at 8x.
            if (change == PlayModeStateChange.ExitingPlayMode
                || change == PlayModeStateChange.EnteredEditMode)
            {
                Time.timeScale = 1f;
            }
        }

        private static void Set(float scale)
        {
            Desired = scale;

            if (EditorApplication.isPlaying)
            {
                Time.timeScale = scale;
            }

            Debug.Log($"[PoFootball] Sim speed set to {scale:F0}x.");
        }

        [MenuItem(MENU_ROOT + "1x (normal)")]
        private static void SetNormal() => Set(1f);

        [MenuItem(MENU_ROOT + "1x (normal)", true)]
        private static bool ValidateNormal() => Validate(1f);

        [MenuItem(MENU_ROOT + "4x")]
        private static void SetFour() => Set(4f);

        [MenuItem(MENU_ROOT + "4x", true)]
        private static bool ValidateFour() => Validate(4f);

        [MenuItem(MENU_ROOT + "8x")]
        private static void SetEight() => Set(8f);

        [MenuItem(MENU_ROOT + "8x", true)]
        private static bool ValidateEight() => Validate(8f);

        private static bool Validate(float scale)
        {
            Menu.SetChecked(MENU_ROOT + Label(scale), Mathf.Approximately(Desired, scale));
            return true;
        }

        private static string Label(float scale)
        {
            if (Mathf.Approximately(scale, 1f))
            {
                return "1x (normal)";
            }

            return $"{scale:F0}x";
        }
    }
}
