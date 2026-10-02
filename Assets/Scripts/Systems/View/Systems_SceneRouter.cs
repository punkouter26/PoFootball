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
    ///
    /// THE SUMMARY IS OFFERED AT THE WHISTLE, NOT HANDED OVER BY ONE BUTTON. It used
    /// to ride on a LoadMenu(summary) overload that only the final overlay's MENU
    /// button called. The status HUD's MENU chip sits on top of that overlay, reads
    /// identically, and called the plain overload — so which of two buttons with
    /// the same label a thumb landed on decided whether the LAST GAME card
    /// appeared. Now the game offers its result once, when it ends, and every
    /// route to the menu carries it; a rematch is the one route that discards it.
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
        /// Leaves a finished game's numbers for the menu to show, whichever control
        /// takes the player there.
        /// </summary>
        public static void OfferSummary(Systems_GameSummary summary)
        {
            _pendingSummary = summary;
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
            if (_isLoading)
            {
                return;
            }

            // A new game makes the last one's result stale. Dropped here rather
            // than in Load, and only once the guard has passed, so a second tap
            // swallowed by an in-flight load to the MENU cannot throw away the
            // card that load is carrying.
            _pendingSummary = null;
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
            // THE GUARD IS RELEASED IN A finally, BECAUSE A LOAD THAT FAILS MUST NOT
            // TAKE EVERY LATER ONE WITH IT. LoadSceneAsync returns null for a scene
            // that is not in the build, and awaiting that threw — past the line
            // that cleared _isLoading, so the flag stayed set for the life of the
            // process and PLAY, MENU and REMATCH all became buttons that click and
            // do nothing. One bad load was a dead end with no error on screen.
            try
            {
                // No CancellationToken: there is nothing left to cancel into. The
                // caller's GameObject is destroyed by the load itself, and
                // abandoning a half-loaded scene would leave the game with no
                // scene at all.
                AsyncOperation load =
                    SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);

                if (load == null)
                {
                    Debug.LogError(
                        $"[PoFootball] Scene '{sceneName}' could not be loaded — it "
                        + "is not in the build's scene list. Staying on the current "
                        + "screen.");
                    return;
                }

                await load.ToUniTask();
            }
            finally
            {
                _isLoading = false;
            }
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
