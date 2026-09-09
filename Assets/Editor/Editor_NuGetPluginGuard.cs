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
        }

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
