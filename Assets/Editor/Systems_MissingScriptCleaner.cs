using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PoFootball.EditorTools
{
    /// <summary>
    /// Strips components whose script no longer exists from every open scene.
    ///
    /// WHY THIS IS A MENU ITEM AND NOT AN MCP CALL. Deleting a MonoBehaviour's .cs
    /// leaves every GameObject that carried it holding a component with a null
    /// script. Unity reports one "The referenced script (Unknown) on this Behaviour
    /// is missing!" per instance at load and otherwise carries on, so the scene
    /// keeps working and the rot is easy to miss — `get_scene_hierarchy` does not
    /// list them either, because there is no type to name.
    ///
    /// They cannot be removed through the usual tooling: a component with no script
    /// does not resolve from a GlobalObjectId, so remove_component cannot address
    /// it, and the Editor API for this (GameObjectUtility) has no menu command of
    /// its own. CLAUDE.md asks that scene edits go through MCP rather than bespoke
    /// Editor scripts; this exists so that they still can — the MCP `menu` command
    /// invokes it, and nothing hand-edits `.unity` YAML.
    ///
    /// Left in the project rather than deleted after use, because the situation
    /// recurs every time a view is pruned.
    /// </summary>
    internal static class Systems_MissingScriptCleaner
    {
        [MenuItem("PoFootball/Clean Missing Scripts In Open Scenes")]
        internal static void Clean()
        {
            int scenesTouched = 0;
            int componentsRemoved = 0;

            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                Scene scene = SceneManager.GetSceneAt(index);

                if (!scene.isLoaded)
                {
                    continue;
                }

                int removedHere = CleanScene(scene);

                if (removedHere <= 0)
                {
                    continue;
                }

                componentsRemoved += removedHere;
                scenesTouched++;

                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }

            Debug.Log(
                $"[PoFootball] Removed {componentsRemoved} missing-script component(s) "
                + $"from {scenesTouched} scene(s).");
        }

        private static int CleanScene(Scene scene)
        {
            int removed = 0;

            GameObject[] roots = scene.GetRootGameObjects();

            for (int rootIndex = 0; rootIndex < roots.Length; rootIndex++)
            {
                // Includes the root itself, and inactive children — a disabled
                // GameObject holds a broken reference just as well as a live one.
                Transform[] transforms =
                    roots[rootIndex].GetComponentsInChildren<Transform>(true);

                for (int index = 0; index < transforms.Length; index++)
                {
                    GameObject gameObject = transforms[index].gameObject;

                    int count = GameObjectUtility
                        .GetMonoBehavioursWithMissingScriptCount(gameObject);

                    if (count <= 0)
                    {
                        continue;
                    }

                    Debug.Log(
                        $"[PoFootball] {scene.name}: removing {count} missing-script "
                        + $"component(s) from '{GetPath(gameObject)}'.");

                    removed += GameObjectUtility
                        .RemoveMonoBehavioursWithMissingScript(gameObject);
                }
            }

            return removed;
        }

        private static string GetPath(GameObject gameObject)
        {
            string path = gameObject.name;
            Transform current = gameObject.transform.parent;

            while (current != null)
            {
                path = $"{current.name}/{path}";
                current = current.parent;
            }

            return $"/{path}";
        }
    }
}
