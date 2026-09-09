using System.Collections.Generic;
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
    internal static class Editor_DeadBrainRefCleaner
    {
        private const string MENU_PATH = "Tools/PoFootball/Clear Dead m_Model References In Open Scenes";

        /// <summary>
        /// BehaviorParameters' serialized name for the brain reference — the field
        /// the scene YAML writes as <c>m_Model</c>.
        /// </summary>
        private const string MODEL_FIELD = "m_Model";

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

                    // THE SERIALIZED FIELD, NOT THE PROPERTY. This is the whole of
                    // why the first version of this tool cleaned nothing.
                    //
                    // A reference to an asset that no longer exists is a MISSING
                    // reference, and Unity surfaces those as null through the
                    // managed API — parameters.Model returns null for a dead GUID
                    // exactly as it does for a field nobody ever set. The old code
                    // read that property, saw null, and skipped with a note saying
                    // "when it returns null the serialization is already null",
                    // which is the one thing that is not true here. Assigning null
                    // over null also changes nothing, so the scene never went dirty
                    // and SaveScene wrote the same 22 GUIDs straight back.
                    //
                    // SerializedProperty can tell the two apart:
                    // objectReferenceInstanceIDValue is still non-zero for a missing
                    // reference while objectReferenceValue is null. Zeroing the
                    // instance id is the documented way to drop one, and it is a
                    // real modification, so the scene dirties and the GUID actually
                    // leaves the file.
                    using (var serialized = new SerializedObject(parameters))
                    {
                        SerializedProperty property = serialized.FindProperty(MODEL_FIELD);

                        if (property == null
                            || property.propertyType != SerializedPropertyType.ObjectReference)
                        {
                            continue;
                        }

                        bool isMissing = property.objectReferenceValue == null
                            && !property.objectReferenceEntityIdValue.Equals(default(UnityEngine.EntityId));

                        bool isDeadGuid = false;

                        if (property.objectReferenceValue != null)
                        {
                            string path = AssetDatabase.GetAssetPath(property.objectReferenceValue);
                            string guid = AssetDatabase.AssetPathToGUID(path);
                            isDeadGuid = string.IsNullOrEmpty(guid) || !aliveGuids.Contains(guid);
                        }

                        if (!isMissing && !isDeadGuid)
                        {
                            continue;
                        }

                        property.objectReferenceEntityIdValue = default;
                        property.objectReferenceValue = null;
                        serialized.ApplyModifiedPropertiesWithoutUndo();

                        EditorUtility.SetDirty(parameters);
                        touched++;
                    }
                }
            }

            return touched;
        }
    }
}
