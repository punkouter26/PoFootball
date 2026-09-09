using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace PoFootball.EditorTools
{
    /// <summary>
    /// Pins everything under Assets/Plugins/NuGet to the Editor, and Roslyn off
    /// entirely. Asserted at build time — see WHERE THIS IS ENFORCED.
    ///
    /// WHY THIS EXISTS AT ALL. Those assemblies are the MCP bridge's dependency
    /// closure — SignalR, ASP.NET Core connection plumbing, Microsoft.Extensions.*,
    /// System.Text.Json, Roslyn. Every one of them is Editor tooling. None of them
    /// has any business inside a retail player.
    ///
    /// WHAT IT WAS FIXING WHEN IT GREW. This started as a two-file Roslyn guard.
    /// An audit of the import settings found 40 of the 42 assemblies marked
    /// "compatible with any platform", which is NuGetForUnity's default and which
    /// means the whole 17 MB was being compiled into the Android bundle — a bundle
    /// whose entire gameplay layer is a few hundred KB of C#. Nothing referenced
    /// them at runtime, so nothing failed; they just shipped.
    ///
    /// WHERE THIS IS ENFORCED. com.ivanmurzak.unity.mcp ships a dependency resolver
    /// that rewrites the import settings of this whole folder, and it restores the
    /// permissive defaults every time it runs. That was measured, not assumed:
    /// pinning all 42 by hand and then triggering a single asset refresh put 40 of
    /// them straight back to "any platform".
    ///
    /// Reapplying it automatically was tried and reverted. An
    /// [InitializeOnLoadMethod] plus an AssetPostprocessor put this in a reimport
    /// ping-pong with that resolver: the Editor domain-reloaded continuously and
    /// dropped out of play mode before a game could reach its final whistle. Racing
    /// another package's resolver on every import is not a fight worth winning.
    ///
    /// So the pin is asserted at the one moment it actually has to hold —
    /// immediately before BuildPlayer, from both Android builders — and is
    /// otherwise available from the menu. A build cannot ship the wrong thing even
    /// if the resolver ran five seconds earlier.
    ///
    /// WHY ROSLYN IS OFF RATHER THAN EDITOR-ONLY. com.unity.pipeline carries its own
    /// Microsoft.CodeAnalysis under Runtime/Plugins/CodeAnalysis. With a second copy
    /// loaded beside it every Roslyn type fails to initialise —
    ///
    ///     TypeLoadException: ... has invalid vtable method slot N with method none
    ///
    /// — which takes out `unity eval` and the Device Simulator window. Editor-only
    /// is not enough for these two; they must not load.
    ///
    /// Driving it through PluginImporter rather than editing .meta by hand is
    /// deliberate: UNITY_RULES forbids hand-edited .meta files, and this is the one
    /// API that writes the same fields.
    /// </summary>
    internal static class Editor_NuGetPluginGuard
    {
        private const string PLUGIN_ROOT = "Assets/Plugins/NuGet";

        /// <summary>
        /// The two that must not load in the Editor either, because
        /// com.unity.pipeline already has them. See the class summary.
        /// </summary>
        private static readonly HashSet<string> RoslynAssemblies = new()
        {
            "Microsoft.CodeAnalysis.dll",
            "Microsoft.CodeAnalysis.CSharp.dll",
        };

        [MenuItem("Tools/PoFootball/Pin NuGet Plugins To Editor")]
        private static void PinFromMenu()
        {
            Pin();
        }

        /// <summary>
        /// Called by the Android builders immediately before BuildPlayer. That call
        /// is the one that has to be right: the resolver can undo the pin at any
        /// point between opening the Editor and pressing build, and asserting it
        /// here means the artifact is correct regardless of what happened earlier in
        /// the session.
        /// </summary>
        internal static void PinBeforeBuild()
        {
            Pin();
            StripMcpDefines(UnityEditor.Build.NamedBuildTarget.Android);
        }

        /// <summary>
        /// Strips the MCP gate for one build target and hands back what was there,
        /// so a caller that only wants it gone for the duration of a build can put
        /// it back afterwards.
        ///
        /// STANDALONE NEEDS THIS TOO, AND FINDING OUT COST A BUILD. The pin above is
        /// per-ASSEMBLY, not per-target: once it has run, those 42 DLLs are
        /// Editor-only for every platform. So after any Android build, the very next
        /// Standalone build — the training env — fails with the same CS0234s,
        /// because com.IvanMurzak.Unity.MCP.Runtime is still gated IN for Standalone
        /// and its precompiled references have just been gated OUT. A pre-existing
        /// landmine that only detonates once someone builds for Android first, which
        /// nobody in this project ever had.
        ///
        /// Restored afterwards for Standalone and not for Android, and the asymmetry
        /// is deliberate: Standalone is the target the Editor normally sits on, and
        /// leaving the gate shut there would take the MCP bridge out of everyday
        /// development. Android is a target nobody edits under.
        /// </summary>
        internal static string StripMcpDefines(UnityEditor.Build.NamedBuildTarget target)
        {
            string previous = PlayerSettings.GetScriptingDefineSymbols(target);

            string[] current = previous.Split(
                ';', System.StringSplitOptions.RemoveEmptyEntries);

            var kept = new List<string>(current.Length);
            var removed = new List<string>();

            for (int index = 0; index < current.Length; index++)
            {
                string define = current[index].Trim();

                if (define.Length == 0)
                {
                    continue;
                }

                if (System.Array.IndexOf(McpDefines, define) >= 0)
                {
                    removed.Add(define);
                    continue;
                }

                kept.Add(define);
            }

            if (removed.Count == 0)
            {
                Debug.Log($"[NuGetGuard] {target.TargetName} defines already free of the MCP gate.");
                return previous;
            }

            PlayerSettings.SetScriptingDefineSymbols(target, kept.ToArray());
            AssetDatabase.SaveAssets();

            Debug.Log(
                $"[NuGetGuard] removed {string.Join(", ", removed)} from the "
                + $"{target.TargetName} define set; the MCP bridge's runtime "
                + "assemblies are excluded from the player build.");

            return previous;
        }

        /// <summary>Puts back exactly what <see cref="StripMcpDefines"/> returned.</summary>
        internal static void RestoreDefines(
            UnityEditor.Build.NamedBuildTarget target, string previous)
        {
            if (previous == null)
            {
                return;
            }

            PlayerSettings.SetScriptingDefineSymbols(target, previous);
            AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// Undoes <see cref="Pin"/> for everything except Roslyn — the exact inverse
        /// of what the Android builders want, and deliberately so.
        ///
        /// The pin is per-assembly rather than per-target, so an Android build makes
        /// these Editor-only for the Standalone training env too, and the env then
        /// fails to compile com.IvanMurzak.Unity.MCP.Runtime against references that
        /// have just been taken away from it. The env is a local, git-ignored
        /// artifact that no player ever sees, so the cheap correct answer is to let
        /// the assemblies back in rather than to fight the define gate — see the
        /// comment in Editor_BuildMenu.Build for why the define route needs two
        /// Editor invocations and cannot be a menu item.
        ///
        /// ROSLYN STAYS OFF. Those two are not an Editor-only question: a second
        /// Microsoft.CodeAnalysis loaded beside com.unity.pipeline's own copy makes
        /// every Roslyn type fail to initialise, which is why the class summary
        /// singles them out. Letting them back in here would break `unity eval` and
        /// the Device Simulator to save nothing.
        /// </summary>
        internal static void AllowInStandalonePlayer()
        {
            if (!Directory.Exists(PLUGIN_ROOT))
            {
                return;
            }

            string[] assemblies = Directory.GetFiles(
                PLUGIN_ROOT, "*.dll", SearchOption.AllDirectories);

            int reopened = 0;

            for (int index = 0; index < assemblies.Length; index++)
            {
                string path = assemblies[index].Replace('\\', '/');

                if (AssetImporter.GetAtPath(path) is not PluginImporter importer)
                {
                    continue;
                }

                if (RoslynAssemblies.Contains(Path.GetFileName(path)))
                {
                    continue;
                }

                if (importer.GetCompatibleWithAnyPlatform())
                {
                    continue;
                }

                importer.SetCompatibleWithAnyPlatform(true);
                importer.SaveAndReimport();
                reopened++;
            }

            Debug.Log(
                $"[NuGetGuard] reopened {reopened} assemblies to all platforms for a "
                + "Standalone build. The next Android build re-pins them.");
        }

        /// <summary>
        /// The scripting defines the MCP bridge's own assemblies are gated on. Both
        /// must be absent for the gate to close — the asmdefs list them together, so
        /// dropping one is enough, and dropping both says what is meant.
        /// </summary>
        private static readonly string[] McpDefines =
        {
            "UNITY_MCP_READY",
            "UNITY_MCP_DEPS_3",
        };


        private static void Pin()
        {
            if (!Directory.Exists(PLUGIN_ROOT))
            {
                Debug.LogWarning($"[NuGetGuard] no folder at {PLUGIN_ROOT}; nothing to pin.");
                return;
            }

            string[] assemblies = Directory.GetFiles(
                PLUGIN_ROOT, "*.dll", SearchOption.AllDirectories);

            int repinned = 0;

            for (int index = 0; index < assemblies.Length; index++)
            {
                // Directory.GetFiles hands back OS separators; the AssetDatabase
                // only resolves forward slashes.
                string path = assemblies[index].Replace('\\', '/');

                if (AssetImporter.GetAtPath(path) is not PluginImporter importer)
                {
                    Debug.LogWarning($"[NuGetGuard] no PluginImporter at {path}");
                    continue;
                }

                bool wantEditor = !RoslynAssemblies.Contains(Path.GetFileName(path));

                if (!importer.GetCompatibleWithAnyPlatform()
                    && importer.GetCompatibleWithEditor() == wantEditor)
                {
                    continue;
                }

                importer.SetCompatibleWithAnyPlatform(false);
                importer.SetCompatibleWithEditor(wantEditor);
                importer.SaveAndReimport();
                repinned++;
            }

            Debug.Log(
                $"[NuGetGuard] {assemblies.Length} assemblies under {PLUGIN_ROOT}; "
                + $"re-pinned {repinned} to Editor-only. None ship in a player build.");
        }
    }
}
