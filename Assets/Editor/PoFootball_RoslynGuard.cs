using UnityEditor;
using UnityEngine;

namespace PoFootball.EditorTools
{
    /// <summary>
    /// Switches the NuGet Roslyn assemblies off for the Editor.
    ///
    /// WHY THIS KEEPS NEEDING DOING. com.ivanmurzak.unity.mcp ships a dependency
    /// resolver that rewrites the import settings of everything under
    /// Assets/Plugins/NuGet, and it turns Microsoft.CodeAnalysis back on for the
    /// Editor every time it runs. A second Roslyn then loads beside the one inside
    /// com.unity.pipeline, and every Roslyn type fails with
    ///
    ///     TypeLoadException: ... has invalid vtable method slot N with method none
    ///
    /// which takes out `unity eval` and the Device Simulator window with it. The
    /// committed .meta state is "off for the Editor" for exactly this reason.
    ///
    /// Driving it through PluginImporter rather than editing the .meta by hand is
    /// deliberate: UNITY_RULES forbids hand-edited .meta files, and this is the one
    /// API that writes the same fields.
    /// </summary>
    internal static class PoFootball_RoslynGuard
    {
        private static readonly string[] RoslynAssemblies =
        {
            "Assets/Plugins/NuGet/Microsoft.CodeAnalysis.dll",
            "Assets/Plugins/NuGet/Microsoft.CodeAnalysis.CSharp.dll",
        };

        [MenuItem("Tools/PoFootball/Disable NuGet Roslyn In Editor")]
        private static void DisableRoslynInEditor()
        {
            int changed = 0;

            for (int index = 0; index < RoslynAssemblies.Length; index++)
            {
                string path = RoslynAssemblies[index];
                PluginImporter importer = AssetImporter.GetAtPath(path) as PluginImporter;

                if (importer == null)
                {
                    Debug.LogWarning($"[RoslynGuard] no PluginImporter at {path}");
                    continue;
                }

                importer.SetCompatibleWithAnyPlatform(false);
                importer.SetCompatibleWithEditor(false);
                importer.SaveAndReimport();
                changed++;
            }

            Debug.Log($"[RoslynGuard] disabled {changed} Roslyn assemblies for the Editor.");
        }
    }
}
