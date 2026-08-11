using Cysharp.Threading.Tasks;
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
    /// </summary>
    public static class Systems_SceneRouter
    {
        public const string MENU_SCENE = "SCN_MENU";
        public const string GAME_SCENE = "SCN_GAME";

        private static bool _isLoading;

        public static void LoadMenu()
        {
            Load(MENU_SCENE);
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
        }
    }
}
