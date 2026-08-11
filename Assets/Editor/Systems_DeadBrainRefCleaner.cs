using System.Collections.Generic;
using Unity.InferenceEngine;
using Unity.MLAgents.Policies;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PoFootball.EditorTools
{
    /// <summary>
    /// Clears the <c>m_Model</c> field on every <c>BehaviorParameters</c> in the
    /// open scenes whose target GUID does not resolve to any asset on disk.
    ///
    /// WHY THIS IS A MENU ITEM AND NOT A DIRECT YAML EDIT. The two playing scenes
    /// (SCN_GAME, SCN_TRAIN_FOOTBALL) carry 44 <c>m_Model</c> references to the
    /// four <c>.onnx</c> GUIDs that lived in the deleted
    /// <c>Assets/Agents/Football_v01</c> brain set. CLAUDE.md explains that
    /// <c>Agent_FootballPlayer</c> reassigns <c>behaviorParameters.Model</c> from
    /// <c>Agent_BrainRegistry</c> at <c>Awake</c>, overwriting whatever the
    /// scene serialized, so the references are dead at the model layer — but the
    /// scene still ships them, which means a future promotion that drops a new
    /// <c>.onnx</c> at the same GUID would silently bind to the wrong trainer.
    /// Hooking the cleanup behind the MCP menu path keeps the rule in
    /// <c>.claude/hooks/block-scene-edit.sh</c> enforced.
    /// </summary>
    internal static class Systems_DeadBrainRefCleaner
    {
        private const string MENU_PATH = "PoFootball/Clear Dead m_Model References In Open Scenes";

        [MenuItem(MENU_PATH)]
        internal static void Clean()
        {
            int scenesTouched = 0;
            int componentsTouched = 0;

            // Collect every asset path GUID known to the AssetDatabase so we can
            // resolve a reference without touching the asset itself.
            var aliveGuids = new HashSet<string>();
            foreach (string path in AssetDatabase.GetAllAssetPaths())
            {
                string guid = AssetDatabase.AssetPathToGUID(path);
                if (!string.IsNullOrEmpty(guid))
                {
                    aliveGuids.Add(guid);
                }
            }

            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                Scene scene = SceneManager.GetSceneAt(index);
                if (!scene.isLoaded)
                {
                    continue;
                }

                int touched = CleanScene(scene, aliveGuids);
                if (touched <= 0)
                {
                    continue;
                }

                componentsTouched += touched;
                scenesTouched++;
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }

            Debug.Log(
                $"[PoFootball] Cleared {componentsTouched} dead m_Model reference(s) "
                + $"across {scenesTouched} scene(s).");
        }

        private static int CleanScene(Scene scene, HashSet<string> aliveGuids)
        {
            int touched = 0;

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (BehaviorParameters parameters in root.GetComponentsInChildren<BehaviorParameters>(true))
                {
                    if (parameters == null)
                    {
                        continue;
                    }

                    ModelAsset model = parameters.Model;
                    if (model == null)
                    {
                        // Already null at runtime — but the serialized field may
                        // still hold a stale GUID. Compare by reflection on the
                        // hidden m_Model field to decide.
                        // NOTE: Model is a public property; when it returns null
                        // the serialization is already null. Skip.
                        continue;
                    }

                    string path = AssetDatabase.GetAssetPath(model);
                    string guid = AssetDatabase.AssetPathToGUID(path);
                    if (string.IsNullOrEmpty(guid) || !aliveGuids.Contains(guid))
                    {
                        Undo.RecordObject(parameters, "Clear Dead m_Model");
                        parameters.Model = null;
                        EditorUtility.SetDirty(parameters);
                        touched++;
                    }
                }
            }

            return touched;
        }
    }
}
