using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PoFootball.Views
{
    /// <summary>
    /// Puts exactly one <see cref="Systems_StatusHudView"/> into every
    /// player-facing scene, at runtime, with no scene authoring at all.
    ///
    /// WHY NOT JUST DROP THE COMPONENT IN BOTH SCENES. Because that is how the two
    /// screens drifted apart in the first place. Chrome that is authored twice
    /// disagrees with itself the first time someone edits one scene and not the
    /// other, and no compiler and no test catches it — it is caught by a person
    /// looking at two screenshots side by side, which is to say usually not at all.
    /// One spawner means the corners of the HUD have exactly one definition.
    ///
    /// It also keeps the shipping scenes untouched, which matters more here than it
    /// usually would: this repository's own hooks block direct .unity edits on
    /// purpose, and a status bar is not worth a scene migration.
    ///
    /// AfterSceneLoad, and then once per scene load after that, mirroring
    /// <see cref="Systems_DisplayBootstrap"/> — the hook fires once per process, so
    /// the menu-to-game transition needs the event as well.
    ///
    /// TRAINING IS EXCLUDED, and for the same reason the display bootstrap excludes
    /// it: SCN_TRAIN_FOOTBALL runs headless under mlagents-learn at four hundred
    /// steps a second, and a UI panel rebuilding labels four times a second is pure
    /// cost against a run that has no display to show them on.
    /// </summary>
    internal static class Systems_StatusHudBootstrap
    {
        private const string HOST_NAME = "StatusHud";

        /// <summary>
        /// Prefix of every training scene, per UNITY_RULES section 1
        /// (`SCN_TRAIN_&lt;NAME&gt;`). Matched rather than compared against one
        /// literal, so a second training scene is covered the day it is added.
        /// </summary>
        private const string TRAINING_SCENE_PREFIX = "SCN_TRAIN_";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            // Unsubscribe first. With "Enter Play Mode without domain reload" the
            // statics survive a play session, and a second subscription would put
            // two status HUDs on the screen — one of them stacking its own tap
            // sounds on every button in the game.
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;

            Spawn(SceneManager.GetActiveScene());
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Spawn(scene);
        }

        /// <summary>
        /// Whether this scene gets a status HUD. Public to the assembly because
        /// <see cref="Systems_PerformanceOverlayView"/> has to answer the same
        /// question to know whether to stand down, and it must get the same answer
        /// — the alternative is a scene scan looking for the HUD, which is both a
        /// FindObjectOfType and a race with whichever component happens to run
        /// first.
        /// </summary>
        internal static bool WantsStatusHud(Scene scene)
        {
            if (!scene.IsValid())
            {
                return false;
            }

            // Nothing to draw on and nothing to look at it. A batch-mode run is
            // either a training env or a build machine.
            if (Application.isBatchMode)
            {
                return false;
            }

            return !scene.name.StartsWith(
                TRAINING_SCENE_PREFIX, StringComparison.Ordinal);
        }

        internal static bool IsMenuScene(Scene scene)
        {
            return scene.IsValid()
                && string.Equals(
                    scene.name, Systems_SceneRouter.MENU_SCENE, StringComparison.Ordinal);
        }

        private static void Spawn(Scene scene)
        {
            if (!WantsStatusHud(scene) || !scene.isLoaded)
            {
                return;
            }

            // A scene reloaded onto itself, or an additive load, must not end up
            // with two. Cheap: this runs once per scene load, never per frame, and
            // the alternative is trusting that no code path ever loads a scene twice.
            GameObject[] roots = scene.GetRootGameObjects();

            for (int index = 0; index < roots.Length; index++)
            {
                if (roots[index].GetComponent<Systems_StatusHudView>() != null)
                {
                    return;
                }
            }

            var host = new GameObject(HOST_NAME);

            // Objects are created into the ACTIVE scene, which during an additive
            // load is not the scene that just loaded. Moving it explicitly means the
            // HUD is unloaded with the screen it belongs to rather than outliving it.
            SceneManager.MoveGameObjectToScene(host, scene);

            // UIDocument comes with it — Systems_ScreenView requires the component,
            // and AddComponent honours [RequireComponent].
            host.AddComponent<Systems_StatusHudView>();
        }
    }
}
