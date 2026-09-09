using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace PoFootball.EditorTools
{
    /// <summary>
    /// Pins everything under Assets/Plugins/NuGet to the Editor, and Roslyn off
    /// entirely. Runs automatically — see WHY IT IS NOT A MENU ITEM ANY MORE.
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
    /// WHY IT IS NOT A MENU ITEM ANY MORE. com.ivanmurzak.unity.mcp ships a
    /// dependency resolver that rewrites the import settings of this whole folder,
    /// and it restores the permissive defaults every time it runs. That was measured,
    /// not assumed: pinning all 42 by hand and then triggering a single asset refresh
    /// put 40 of them straight back to "any platform". A menu item cannot hold a
    /// line that something else redraws on every resolve — whoever shipped the build
    /// would simply have been the person who forgot to click it last. So the pin is
    /// reapplied on load and after any import that touches the folder.
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

        /// <summary>
        /// SaveAndReimport re-enters the postprocessor. The pass is idempotent —
        /// it only writes importers whose settings are already wrong, so the second
        /// pass finds nothing — but the flag keeps the reimport from nesting.
        /// </summary>
        private static bool _running;

        [InitializeOnLoadMethod]
        private static void PinOnLoad()
        {
            // Deferred: at [InitializeOnLoadMethod] time the AssetDatabase may still
            // be mid-import, and SaveAndReimport from inside that is not safe.
            EditorApplication.delayCall += () => Pin(verbose: false);
        }

        [MenuItem("Tools/PoFootball/Pin NuGet Plugins To Editor")]
        private static void PinFromMenu()
        {
            Pin(verbose: true);
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
            Pin(verbose: true);
        }

        private static void Pin(bool verbose)
        {
            if (_running || !Directory.Exists(PLUGIN_ROOT))
            {
                return;
            }

            _running = true;
            try
            {
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

                if (repinned > 0 || verbose)
                {
                    Debug.Log(
                        $"[NuGetGuard] {assemblies.Length} assemblies under {PLUGIN_ROOT}; "
                        + $"re-pinned {repinned} to Editor-only. None ship in a player build.");
                }
            }
            finally
            {
                _running = false;
            }
        }

        /// <summary>
        /// Reapplies the pin whenever anything under the NuGet folder is imported —
        /// which is exactly when the ivanmurzak resolver has just undone it.
        /// </summary>
        private sealed class Postprocessor : AssetPostprocessor
        {
            private static void OnPostprocessAllAssets(
                string[] imported,
                string[] deleted,
                string[] movedTo,
                string[] movedFrom)
            {
                if (_running)
                {
                    return;
                }

                for (int index = 0; index < imported.Length; index++)
                {
                    if (imported[index].StartsWith(PLUGIN_ROOT, System.StringComparison.Ordinal))
                    {
                        EditorApplication.delayCall += () => Pin(verbose: false);
                        return;
                    }
                }
            }
        }
    }
}
