using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;

namespace PoFootball.EditorTools
{
    /// <summary>
    /// Sprite atlas tooling, and the audit that says whether this project should
    /// use one.
    ///
    /// WHY THE AUDIT EXISTS AND THE ATLAS MOSTLY DOES NOT. .claude/rules/
    /// performance.md is unambiguous — "All 2D sprites MUST use Sprite Atlases" —
    /// and this project had none, with 410 PNGs on disk. That looks like a
    /// straightforward finding until you check which sprites the game actually
    /// draws, and then two facts land:
    ///
    ///   402 OF THOSE PNGs ARE REFERENCED BY NOTHING. The whole Kenney SportsPack
    ///   and UIPack are unused — no scene, no ScriptableObject and no script names
    ///   any of their GUIDs. Atlasing them would not save a draw call, because they
    ///   never produce one; it would pack megabytes of dead art into an atlas
    ///   marked include-in-build and make the .aab larger. Dead art is a deletion
    ///   question, not a batching one, which is what <see cref="AuditSpriteUsage"/>
    ///   is for.
    ///
    ///   THE EIGHT SPRITES THE GAME DOES DRAW MUST NOT BE ATLASED. PoFootball/Player
    ///   builds its outline in PoFootball_PlayerBody.hlsl by sampling _MainTex at
    ///   OFFSET UVs and counting anything outside the 0..1 rectangle as empty. That
    ///   file states the reason in full: the lineman's square fills its source
    ///   texture to the border, so without the out-of-bounds test it reads as having
    ///   no edge at all. In an atlas a sprite's uv is the ATLAS rectangle, so the
    ///   0..1 test can never fail and the offset taps — up to 0.07 of UV space —
    ///   land on whatever neighbour got packed alongside. Atlasing the shapes would
    ///   silently delete the square's outline and bleed other role shapes into every
    ///   body's rim: precisely the bug that shader was written to prevent.
    ///
    /// There is also no draw call to win. Systems_PlayerAppearanceView documents
    /// that the twenty-two players are twenty-two draw calls BY DESIGN, because each
    /// carries per-instance state through a MaterialPropertyBlock; an atlas cannot
    /// batch them whatever it packs. The field is a single procedural quad and the
    /// entire UI is UI Toolkit built from code with no sprites in it.
    ///
    /// So <see cref="BuildAtlas"/> is left here as working infrastructure for the
    /// day this project gains real UI or crowd art, and it refuses the shapes
    /// folder rather than trusting whoever runs it to remember why.
    /// </summary>
    internal static class Editor_AtlasBuilder
    {
        private const string ATLAS_FOLDER = "Assets/Art/Atlases";

        /// <summary>
        /// The sprites PoFootball/Player samples with offset UVs. Packing these
        /// breaks the shader — see the class note.
        /// </summary>
        private const string SHADER_COUPLED_FOLDER = "Assets/Art/Shapes";

        /// <summary>
        /// Where UI art would live. Nothing is here yet; the menu item exists so
        /// the first person to add some does not have to work out the settings.
        /// </summary>
        private const string UI_ART_FOLDER = "Assets/Art/Ui";

        /// <summary>Mobile ceiling, per .claude/rules/performance.md.</summary>
        private const int MAX_ATLAS_SIZE = 2048;

        private const int ATLAS_PADDING = 4;

        [MenuItem("Tools/PoFootball/Audit Sprite Usage")]
        private static void AuditSpriteUsage()
        {
            string[] textureGuids = AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets" });

            List<string> unused = new List<string>();
            long unusedBytes = 0;
            int considered = 0;

            foreach (string guid in textureGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);

                // Icons and store art are referenced by the build pipeline and by
                // PlayerSettings rather than by an asset, and screenshots are
                // documentation. None of them are sprites the renderer draws.
                //
                // ANYTHING UNDER Resources IS EXEMPT, AND THIS IS A CORRECTNESS FIX
                // RATHER THAN A CONVENIENCE. Resources.Load takes a PATH, so a
                // resource is referenced by a string in C# and its GUID appears in
                // no asset file at all. A reference scan therefore reports every
                // single thing in Resources as dead. The first run of this audit
                // did exactly that and listed Assets/Resources/Fonts — the two
                // typefaces the entire UI is set in — as unused, which is precisely
                // the kind of confident wrong answer that gets load-bearing files
                // deleted.
                if (path.StartsWith("Assets/Icons")
                    || path.StartsWith("Assets/Screenshots")
                    || path.StartsWith("Assets/StoreAssets")
                    || path.StartsWith("Assets/Resources")
                    || path.StartsWith("Assets/Settings"))
                {
                    continue;
                }

                considered++;

                if (IsReferenced(guid))
                {
                    continue;
                }

                unused.Add(path);

                FileInfo info = new FileInfo(path);

                if (info.Exists)
                {
                    unusedBytes += info.Length;
                }
            }

            StringBuilder report = new StringBuilder();
            // One decimal place. Integer megabytes printed "0 MB" for a real
            // 900 KB of dead art, which reads as "nothing to do here".
            report.AppendLine(
                $"[PoFootball] Sprite audit: {considered} textures considered, "
                + $"{unused.Count} referenced by nothing "
                + $"({unusedBytes / 1024f / 1024f:F1} MB on disk).");

            // Folders, not files. A list of four hundred paths is not a finding
            // anybody acts on; "this entire pack is dead" is.
            Dictionary<string, int> byFolder = new Dictionary<string, int>();

            foreach (string path in unused)
            {
                string folder = Path.GetDirectoryName(path).Replace('\\', '/');
                byFolder.TryGetValue(folder, out int count);
                byFolder[folder] = count + 1;
            }

            foreach (KeyValuePair<string, int> entry in byFolder)
            {
                report.AppendLine($"    {entry.Value,4}  {entry.Key}");
            }

            Debug.Log(report.ToString());
        }

        /// <summary>
        /// Whether any asset in the project mentions this GUID.
        ///
        /// A text scan of the scenes, prefabs and ScriptableObjects rather than
        /// AssetDatabase.GetDependencies, which answers the opposite question —
        /// what an asset uses, not who uses it — and would need a full forward
        /// sweep to invert. Force Text serialization is on in this project, so the
        /// GUID is literally present in any file that references it.
        /// </summary>
        private static bool IsReferenced(string guid)
        {
            string[] consumerGuids = AssetDatabase.FindAssets(
                "t:Scene t:Prefab t:ScriptableObject t:Material", new[] { "Assets" });

            foreach (string consumerGuid in consumerGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(consumerGuid);

                if (!File.Exists(path))
                {
                    continue;
                }

                if (File.ReadAllText(path).Contains(guid))
                {
                    return true;
                }
            }

            return false;
        }

        [MenuItem("Tools/PoFootball/Build UI Sprite Atlas")]
        private static void BuildAtlas()
        {
            if (!AssetDatabase.IsValidFolder(UI_ART_FOLDER))
            {
                Debug.LogWarning(
                    $"[PoFootball] No UI art at {UI_ART_FOLDER}, so there is nothing to "
                    + "atlas. This tool deliberately does NOT pack "
                    + $"{SHADER_COUPLED_FOLDER}: PoFootball/Player samples _MainTex at "
                    + "offset UVs and treats outside-0..1 as empty, which an atlas "
                    + "makes impossible. See Editor_AtlasBuilder's class comment.");
                return;
            }

            EnsureFolder(ATLAS_FOLDER);

            SpriteAtlasAsset atlas = new SpriteAtlasAsset();
            atlas.SetIncludeInBuild(true);

            SpriteAtlasPackingSettings packing = new SpriteAtlasPackingSettings
            {
                padding = ATLAS_PADDING,
                enableRotation = true,
                enableTightPacking = true,
                blockOffset = 1,
            };

            atlas.SetPackingSettings(packing);

            SpriteAtlasTextureSettings texture = new SpriteAtlasTextureSettings
            {
                readable = false,
                generateMipMaps = false,
                sRGB = true,
                filterMode = FilterMode.Bilinear,
            };

            atlas.SetTextureSettings(texture);

            TextureImporterPlatformSettings platform = new TextureImporterPlatformSettings
            {
                maxTextureSize = MAX_ATLAS_SIZE,
                format = TextureImporterFormat.Automatic,
                textureCompression = TextureImporterCompression.Compressed,
            };

            atlas.SetPlatformSettings(platform);

            Object folder = AssetDatabase.LoadAssetAtPath<Object>(UI_ART_FOLDER);
            atlas.Add(new[] { folder });

            string atlasPath = $"{ATLAS_FOLDER}/UiAtlas.spriteatlasv2";
            SpriteAtlasAsset.Save(atlas, atlasPath);

            AssetDatabase.Refresh();

            Debug.Log($"[PoFootball] Wrote {atlasPath} packing {UI_ART_FOLDER}.");
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder))
            {
                return;
            }

            string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            string leaf = Path.GetFileName(folder);

            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
