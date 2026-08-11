using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace PoFootball.Tests
{
    /// <summary>
    /// Proves each player-facing screen actually builds its element tree when its
    /// scene is loaded.
    ///
    /// WHY THIS EXISTS. A capture of the Game view showed the field, the players
    /// and the ball but no HUD, and showed the menu as an empty background — which
    /// is equally consistent with "the UI is broken" and with "UI Toolkit overlay
    /// panels do not composite into this particular capture path". Those two have
    /// very different fixes and no screenshot can tell them apart, so the question
    /// is settled here instead: if the tree exists and carries the labels and
    /// buttons it should, the UI layer works and any remaining difference is in
    /// capture, not in the app.
    ///
    /// These are PlayMode tests loading the real scenes rather than EditMode tests
    /// newing up a MonoBehaviour, because everything that could plausibly be wrong —
    /// PanelSettings resolution, the safe-area inset, injection ordering, Start
    /// running at all — only happens in a loaded scene.
    /// </summary>
    public sealed class Systems_ScreenBuildTests
    {
        /// <summary>
        /// UI is built in Start and laid out by the panel afterwards, so a single
        /// frame is not enough to read resolved sizes. Two is.
        /// </summary>
        private static IEnumerator LoadAndSettle(string sceneName)
        {
            yield return SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
            yield return null;
            yield return null;
        }

        private static UIDocument FindDocument()
        {
            UIDocument[] documents =
                Object.FindObjectsByType<UIDocument>(FindObjectsInactive.Exclude);

            Assert.That(documents, Is.Not.Empty, "no UIDocument in the loaded scene");
            return documents[0];
        }

        /// <summary>Every descendant, flattened — the tree is a handful of elements.</summary>
        private static void Collect(VisualElement element, System.Collections.Generic.List<VisualElement> into)
        {
            foreach (VisualElement child in element.Children())
            {
                into.Add(child);
                Collect(child, into);
            }
        }

        private static System.Collections.Generic.List<VisualElement> Descendants(VisualElement root)
        {
            System.Collections.Generic.List<VisualElement> all =
                new System.Collections.Generic.List<VisualElement>();
            Collect(root, all);
            return all;
        }

        private static bool HasButton(
            System.Collections.Generic.List<VisualElement> elements, string text)
        {
            for (int index = 0; index < elements.Count; index++)
            {
                if (elements[index] is Button button && button.text == text)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasLabelContaining(
            System.Collections.Generic.List<VisualElement> elements, string fragment)
        {
            for (int index = 0; index < elements.Count; index++)
            {
                if (elements[index] is Label label
                    && !string.IsNullOrEmpty(label.text)
                    && label.text.Contains(fragment))
                {
                    return true;
                }
            }

            return false;
        }

        [UnityTest]
        public IEnumerator Menu_BuildsItsTreeWithAPlayButton()
        {
            yield return LoadAndSettle("SCN_MENU");

            UIDocument document = FindDocument();
            Assert.That(document.panelSettings, Is.Not.Null, "PanelSettings did not resolve");

            VisualElement root = document.rootVisualElement;
            Assert.That(root, Is.Not.Null, "rootVisualElement was null");

            System.Collections.Generic.List<VisualElement> all = Descendants(root);

            Assert.That(all, Is.Not.Empty, "the menu built no elements at all");
            Assert.That(HasButton(all, "PLAY"), Is.True, "no PLAY button");
            Assert.That(
                HasLabelContaining(all, "PO FOOTBALL"), Is.True, "no wordmark");

            // CLAUDE.md section 3 requires the build number on the opening screen.
            Assert.That(
                HasLabelContaining(all, Application.version),
                Is.True,
                "no version stamp");
        }

        /// <summary>
        /// The menu screen must actually cover the display. A tree that builds but
        /// resolves to zero height renders nothing and looks exactly like a screen
        /// that never built.
        /// </summary>
        [UnityTest]
        public IEnumerator Menu_ResolvesToANonZeroLayout()
        {
            yield return LoadAndSettle("SCN_MENU");

            VisualElement root = FindDocument().rootVisualElement;

            Assert.That(root.resolvedStyle.width, Is.GreaterThan(0f), "zero-width panel");
            Assert.That(root.resolvedStyle.height, Is.GreaterThan(0f), "zero-height panel");
        }

        /// <summary>
        /// The HUD in a played game. QUIT is asserted specifically: there was a
        /// window in which the only controls lived inside the final overlay, so a
        /// live game had no way out of it at all.
        /// </summary>
        [UnityTest]
        public IEnumerator Hud_BuildsItsTreeWithLiveGameControls()
        {
            yield return LoadAndSettle("SCN_GAME");

            UIDocument document = FindDocument();
            VisualElement root = document.rootVisualElement;
            Assert.That(root, Is.Not.Null, "rootVisualElement was null");

            System.Collections.Generic.List<VisualElement> all = Descendants(root);

            Assert.That(all, Is.Not.Empty, "the HUD built no elements at all");
            Assert.That(HasButton(all, "QUIT"), Is.True, "no way to leave a live game");
            Assert.That(HasButton(all, "REMATCH"), Is.True, "no rematch on the final overlay");
            Assert.That(HasButton(all, "MENU"), Is.True, "no menu button on the final overlay");
        }
    }
}
