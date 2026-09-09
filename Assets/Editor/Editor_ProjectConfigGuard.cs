using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PoFootball.EditorTools
{
    /// <summary>
    /// Re-asserts the project configuration CLAUDE.md section 3 requires and that
    /// nothing in the Editor UI stops a person from undoing by accident.
    ///
    /// Two settings, both of which had drifted:
    ///
    ///   PlayerSettings was on AutoRotation with all four orientations enabled,
    ///   against a UI panel that scales on WIDTH from a 1080x1920 reference. A
    ///   device that rotated would multiply every element by the aspect ratio and
    ///   then try to fit a 1920-unit-tall design into 1080 units of height. The
    ///   runtime half of this lives in Systems_DisplayBootstrap; this is the half
    ///   that governs launch orientation and the splash screen, which runs before
    ///   any of our code does.
    ///
    ///   SCN_MENU's camera was Untagged, so Camera.main was null in the front end
    ///   while SCN_GAME's was correct. Nothing reads it there today, which is
    ///   exactly what makes it worth fixing now rather than when something does.
    ///
    /// Driven through the Editor API rather than by editing ProjectSettings.asset
    /// or the scene YAML, because UNITY_RULES forbids hand-edited scene and meta
    /// files and the open Editor would overwrite such an edit on its next save
    /// anyway. Same reasoning as Editor_RoslynGuard, which sits beside it.
    /// </summary>
    internal static class Editor_ProjectConfigGuard
    {
        private const string MENU_SCENE_PATH = "Assets/Scenes/SCN_MENU.unity";
        private const string MAIN_CAMERA_TAG = "MainCamera";

        [MenuItem("Tools/PoFootball/Apply Project Config")]
        internal static void Apply()
        {
            ApplyOrientation();
            ApplyMenuCameraTag();
            AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// Portrait, and only portrait. Upside-down is disabled with the two
        /// landscapes: a phone held inverted is not a case this game has any reason
        /// to support, and leaving it on is the one autorotation that can still fire
        /// while looking like the setting is locked.
        /// </summary>
        private static void ApplyOrientation()
        {
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.Portrait;
            PlayerSettings.allowedAutorotateToPortrait = true;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.allowedAutorotateToLandscapeLeft = false;
            PlayerSettings.allowedAutorotateToLandscapeRight = false;

            Debug.Log(
                "[ProjectConfigGuard] orientation locked to Portrait "
                + "(autorotate: portrait only).");
        }

        /// <summary>
        /// Tags SCN_MENU's camera MainCamera so Camera.main resolves there as it
        /// already does in SCN_GAME.
        ///
        /// Opens the scene additively and closes it again rather than disturbing
        /// whatever the user has open, and only saves when something actually
        /// changed — running this twice must not dirty the scene the second time.
        /// </summary>
        private static void ApplyMenuCameraTag()
        {
            Scene scene = EditorSceneManager.GetSceneByPath(MENU_SCENE_PATH);
            bool wasAlreadyOpen = scene.IsValid() && scene.isLoaded;

            if (!wasAlreadyOpen)
            {
                scene = EditorSceneManager.OpenScene(
                    MENU_SCENE_PATH, OpenSceneMode.Additive);
            }

            if (!scene.IsValid())
            {
                Debug.LogError($"[ProjectConfigGuard] could not open {MENU_SCENE_PATH}.");
                return;
            }

            Camera camera = FindCameraIn(scene);

            if (camera == null)
            {
                Debug.LogError(
                    $"[ProjectConfigGuard] no Camera in {MENU_SCENE_PATH}; "
                    + "nothing to tag.");
            }
            else if (camera.CompareTag(MAIN_CAMERA_TAG))
            {
                Debug.Log("[ProjectConfigGuard] SCN_MENU camera already MainCamera.");
            }
            else
            {
                Undo.RecordObject(camera.gameObject, "Tag Main Camera");
                camera.gameObject.tag = MAIN_CAMERA_TAG;
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                Debug.Log("[ProjectConfigGuard] tagged SCN_MENU camera MainCamera.");
            }

            if (!wasAlreadyOpen)
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        private static Camera FindCameraIn(Scene scene)
        {
            GameObject[] roots = scene.GetRootGameObjects();

            for (int index = 0; index < roots.Length; index++)
            {
                Camera camera = roots[index].GetComponentInChildren<Camera>(true);

                if (camera != null)
                {
                    return camera;
                }
            }

            return null;
        }
    }
}
