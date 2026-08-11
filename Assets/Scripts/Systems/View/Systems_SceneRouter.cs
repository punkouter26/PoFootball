using Cysharp.Threading.Tasks;
using PoFootball.Models;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PoFootball.Views
{
    /// <summary>
    /// Moves between the two player-facing scenes.
    ///
    /// Static, and deliberately so — this is not a service with state or
    /// dependencies to inject, it is two scene names and a load call. The
    /// no-singletons rule in .claude/rules/architecture.md is about shared mutable
    /// state and hidden dependencies; there is neither here.
    ///
    /// Loads are async through UniTask per the same rules, and guarded so a second
    /// tap while a scene is already loading is ignored rather than queued.
    ///
    /// IT ALSO CARRIES THE ONE THING THAT HAS TO SURVIVE A LOAD. A finished game's
    /// numbers live in the game scene's container, which `LoadSceneMode.Single`
    /// destroys on the way to the menu. Cross-scene navigation arguments are what a
    /// router is for, so the summary rides along with the transition that needs it
    /// rather than becoming a second static somewhere else.
    ///
    /// It is write-once, read-once: <see cref="TakeSummary"/> clears it. A stale
    /// summary shown after a later trip to the menu would be a lie about a game
    /// that is not the one just played, and consuming it makes that impossible.
    /// </summary>
    public static class Systems_SceneRouter
    {
        public const string MENU_SCENE = "SCN_MENU";
        public const string GAME_SCENE = "SCN_GAME";

        private static bool _isLoading;

        private static Systems_GameSummary _pendingSummary;

        public static void LoadMenu()
        {
            Load(MENU_SCENE);
        }

        /// <summary>
        /// Returns to the menu carrying a finished game's numbers for it to show.
        /// </summary>
        public static void LoadMenu(Systems_GameSummary summary)
        {
            _pendingSummary = summary;
            Load(MENU_SCENE);
        }

        /// <summary>
        /// The summary left by the last finished game, or null when the menu was
        /// reached any other way — first launch, or quitting a game in progress.
        /// Clears it, so it is shown exactly once.
        /// </summary>
        public static Systems_GameSummary TakeSummary()
        {
            Systems_GameSummary summary = _pendingSummary;
            _pendingSummary = null;
            return summary;
        }

        public static void LoadGame()
        {
            Load(GAME_SCENE);
        }

        private static void Load(string sceneName)
        {
            if (_isLoading)
            {
                return;
            }

            _isLoading = true;
            LoadAsync(sceneName).Forget();
        }

        private static async UniTaskVoid LoadAsync(string sceneName)
        {
            // No CancellationToken: there is nothing left to cancel into. The
            // caller's GameObject is destroyed by the load itself, and abandoning a
            // half-loaded scene would leave the game with no scene at all.
            await SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single).ToUniTask();

            _isLoading = false;
        }

        /// <summary>
        /// Cleared on a domain reload so a load interrupted by exiting play mode
        /// does not leave the guard stuck on for the next session.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetState()
        {
            _isLoading = false;
            _pendingSummary = null;
        }
    }
}
