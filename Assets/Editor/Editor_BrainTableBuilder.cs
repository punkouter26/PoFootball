using System;
using System.IO;
using PoFootball.Agents;
using PoFootball.Models;
using UnityEditor;
using UnityEngine;

namespace PoFootball.EditorTools
{
    /// <summary>
    /// Builds Resources/PoFootballBrains.asset from a promoted Assets/Agents folder.
    ///
    /// THE STEP THAT WAS MISSING. Tools/promote_brain.py gates the `.onnx` files
    /// against the contract and copies them into Assets/Agents/Football_vNN, and
    /// then says "assign them in the scene" and stops. Nothing built the
    /// Agent_BrainTable those files have to be listed in, so
    /// Agent_BrainRegistry.ModelFor returned null for all six groups and every
    /// player ran Heuristic — with a promoted, contract-checked run sitting on disk
    /// unused. The Python side cannot do this: a ScriptableObject holding
    /// ModelAsset references is Unity serialization, not a file copy.
    ///
    /// Driven through SerializedObject rather than a public setter because
    /// Agent_BrainTable.Entry is a private nested struct with private serialized
    /// fields, and it should stay that way — the table is read-only at runtime by
    /// design. SerializedProperty is the supported way for an Editor tool to write
    /// private serialized state without widening the runtime API to suit it.
    ///
    /// The revision is stamped from Agent_ActionContract.CONTRACT_REVISION rather
    /// than typed in, so a table built here can never claim a revision the code does
    /// not actually implement. Getting that number wrong is precisely the failure
    /// Football_v01 was deleted for.
    /// </summary>
    internal static class Editor_BrainTableBuilder
    {
        private const string AGENTS_ROOT = "Assets/Agents";
        private const string TABLE_PATH = "Assets/Resources/PoFootballBrains.asset";
        private const string AGENT_FOLDER_PREFIX = "Football_v";

        [MenuItem("Tools/PoFootball/Build Brain Table")]
        internal static void Build()
        {
            string agentDir = NewestAgentFolder();

            if (agentDir == null)
            {
                Debug.LogError(
                    $"[BrainTable] no {AGENT_FOLDER_PREFIX}NN folder under {AGENTS_ROOT}. "
                    + "Run Tools/promote_brain.py first.");
                return;
            }

            Array groups = Enum.GetValues(typeof(Systems_BrainGroup));
            UnityEngine.Object[] models = new UnityEngine.Object[groups.Length];

            for (int index = 0; index < groups.Length; index++)
            {
                Systems_BrainGroup group = (Systems_BrainGroup)groups.GetValue(index);
                string path = $"{agentDir}/{group}.onnx";

                // Loaded as a plain Object so this file needs no reference to
                // Unity.InferenceEngine; the SerializedProperty is typed by the
                // field it is written to, and it rejects a wrong type itself.
                UnityEngine.Object model =
                    AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);

                if (model == null)
                {
                    Debug.LogError($"[BrainTable] missing {path}. Table not written.");
                    return;
                }

                models[index] = model;
            }

            Agent_BrainTable table = AssetDatabase.LoadAssetAtPath<Agent_BrainTable>(
                TABLE_PATH);
            bool isNew = table == null;

            if (isNew)
            {
                table = ScriptableObject.CreateInstance<Agent_BrainTable>();
                AssetDatabase.CreateAsset(table, TABLE_PATH);
            }

            SerializedObject serialized = new SerializedObject(table);

            serialized.FindProperty("_contractRevision").intValue =
                Agent_ActionContract.CONTRACT_REVISION;

            SerializedProperty entries = serialized.FindProperty("_entries");
            entries.arraySize = groups.Length;

            for (int index = 0; index < groups.Length; index++)
            {
                SerializedProperty entry = entries.GetArrayElementAtIndex(index);
                entry.FindPropertyRelative("_group").enumValueIndex = index;
                entry.FindPropertyRelative("_model").objectReferenceValue = models[index];
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(table);
            AssetDatabase.SaveAssets();

            Debug.Log(
                $"[BrainTable] {(isNew ? "created" : "updated")} {TABLE_PATH} — "
                + $"{groups.Length} brains from {agentDir}, contract revision "
                + $"{Agent_ActionContract.CONTRACT_REVISION}.");
        }

        /// <summary>
        /// The highest-numbered Football_vNN folder. Sorted as text, which is
        /// correct while the versions are the two-digit strings promote_brain.py
        /// insists on.
        /// </summary>
        private static string NewestAgentFolder()
        {
            if (!Directory.Exists(AGENTS_ROOT))
            {
                return null;
            }

            string[] directories = Directory.GetDirectories(
                AGENTS_ROOT, AGENT_FOLDER_PREFIX + "*");

            if (directories.Length == 0)
            {
                return null;
            }

            Array.Sort(directories, StringComparer.Ordinal);

            return directories[directories.Length - 1].Replace('\\', '/');
        }
    }
}
