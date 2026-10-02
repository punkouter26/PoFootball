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

        /// <summary>
        /// The UIDocument belonging to one named screen.
        ///
        /// THIS USED TO BE FindObjectsByType&lt;UIDocument&gt;()[0], AND THAT WAS ONLY
        /// EVER CORRECT BY ACCIDENT. It worked while each scene had exactly one
        /// document. SCN_GAME now has two — the HUD, and the diagnostic overlay on
        /// its own panel (Systems_PerformanceOverlayView) — and FindObjectsByType
        /// makes no ordering guarantee, so `documents[0]` became a coin flip between
        /// them and the HUD assertions failed roughly whenever the overlay won.
        ///
        /// Asking for the screen by TYPE says what each test actually means, and it
        /// stays correct however many panels the scene grows. Every screen is a
        /// Systems_ScreenView, which is [RequireComponent(typeof(UIDocument))], so
        /// the component is guaranteed to be on the same GameObject.
        /// </summary>
        private static UIDocument FindDocumentFor<TScreen>()
            where TScreen : Views.Systems_ScreenView
        {
            TScreen screen = Object.FindAnyObjectByType<TScreen>();

            Assert.That(
                screen, Is.Not.Null, $"no {typeof(TScreen).Name} in the loaded scene");

            UIDocument document = screen.GetComponent<UIDocument>();

            Assert.That(
                document, Is.Not.Null, $"{typeof(TScreen).Name} has no UIDocument");

            return document;
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

        private static Button FindButton(
            System.Collections.Generic.List<VisualElement> elements, string text)
        {
            for (int index = 0; index < elements.Count; index++)
            {
                if (elements[index] is Button button && button.text == text)
                {
                    return button;
                }
            }

            Assert.Fail($"no {text} button");
            return null;
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

            UIDocument document = FindDocumentFor<Views.Systems_MenuView>();
            Assert.That(document.panelSettings, Is.Not.Null, "PanelSettings did not resolve");

            VisualElement root = document.rootVisualElement;
            Assert.That(root, Is.Not.Null, "rootVisualElement was null");

            System.Collections.Generic.List<VisualElement> all = Descendants(root);

            Assert.That(all, Is.Not.Empty, "the menu built no elements at all");
            Assert.That(HasButton(all, "PLAY"), Is.True, "no PLAY button");
            Assert.That(
                HasLabelContaining(all, "PO FOOTBALL"), Is.True, "no wordmark");
        }

        /// <summary>
        /// The five-corner chrome, on the screen a player sees first.
        ///
        /// THE VERSION ASSERTION USED TO LIVE IN THE MENU TEST ABOVE, AND FAILED
        /// THERE FOR AS LONG AS THE STAMP HAS BEEN IN THE RIGHT PLACE. The build
        /// number moved off Systems_MenuView and onto the status HUD — its own
        /// document, on its own panel — and the test went on searching the menu's
        /// tree for it. It is asserted where it is drawn now, alongside the other
        /// corners CLAUDE.md section 3 names.
        ///
        /// Not in batch mode: Systems_StatusHudBootstrap deliberately spawns no
        /// HUD there, because there is no display to draw it on.
        /// </summary>
        [UnityTest]
        public IEnumerator StatusHud_CarriesTheCornersOnTheMenu()
        {
            if (Application.isBatchMode)
            {
                Assert.Ignore("No status HUD is spawned in batch mode, by design.");
            }

            yield return LoadAndSettle("SCN_MENU");

            System.Collections.Generic.List<VisualElement> all = Descendants(
                FindDocumentFor<Views.Systems_StatusHudView>().rootVisualElement);

            Assert.That(HasLabelContaining(all, "POFOOTBALL"), Is.True, "no title");
            Assert.That(HasLabelContaining(all, "FPS"), Is.True, "no frame rate");
            Assert.That(HasButton(all, "MENU"), Is.True, "no MENU chip");
            Assert.That(
                HasLabelContaining(all, Application.version),
                Is.True,
                "no version stamp");

            // The Editor is a debug build, so the chip is expected here. A release
            // player has none — see Systems_StatusHudView.DiagnosticsAllowed.
            Assert.That(
                HasButton(all, "DEBUG"), Is.EqualTo(Debug.isDebugBuild), "DEBUG chip");
        }

        /// <summary>
        /// PLAY must be in the same place whether or not the last game's card is
        /// under it. It was not: the card took its height out of the space the
        /// title block was centred in, and the button rode 263 units up a
        /// 1920-unit panel on every return from a finished game.
        /// </summary>
        [UnityTest]
        public IEnumerator Menu_PlayButtonDoesNotMoveWhenAResultCardIsShown()
        {
            yield return LoadAndSettle("SCN_MENU");
            yield return null;

            float firstLaunchY = FindButton(
                Descendants(FindDocumentFor<Views.Systems_MenuView>().rootVisualElement),
                "PLAY").worldBound.y;

            Views.Systems_SceneRouter.OfferSummary(new Models.Systems_GameSummary(
                default(Models.Systems_TeamSummary), default(Models.Systems_TeamSummary)));

            yield return LoadAndSettle("SCN_MENU");
            yield return null;

            System.Collections.Generic.List<VisualElement> all = Descendants(
                FindDocumentFor<Views.Systems_MenuView>().rootVisualElement);

            Assert.That(
                HasLabelContaining(all, "LAST GAME"), Is.True, "the result card did not build");

            Assert.That(
                FindButton(all, "PLAY").worldBound.y,
                Is.EqualTo(firstLaunchY).Within(1f),
                "PLAY moved when the result card appeared");
        }

        /// <summary>
        /// A finished game's summary is shown once and is not carried into the
        /// next game. The router holds it from the whistle so that either MENU
        /// button delivers it; these are the two ways it must stop being held.
        /// </summary>
        [UnityTest]
        public IEnumerator Router_SummaryIsTakenOnceAndDroppedByANewGame()
        {
            Models.Systems_GameSummary summary = new Models.Systems_GameSummary(
                default(Models.Systems_TeamSummary), default(Models.Systems_TeamSummary));

            Views.Systems_SceneRouter.OfferSummary(summary);

            Assert.That(
                Views.Systems_SceneRouter.TakeSummary(), Is.SameAs(summary), "summary lost");
            Assert.That(
                Views.Systems_SceneRouter.TakeSummary(), Is.Null, "summary shown twice");

            Views.Systems_SceneRouter.OfferSummary(summary);
            Views.Systems_SceneRouter.LoadGame();

            Assert.That(
                Views.Systems_SceneRouter.TakeSummary(),
                Is.Null,
                "a rematch carried the previous game's result with it");

            // Let the load finish, so the next test does not start inside it.
            yield return new WaitUntil(
                () => SceneManager.GetActiveScene().name == Views.Systems_SceneRouter.GAME_SCENE);
            yield return null;
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

            VisualElement root = FindDocumentFor<Views.Systems_MenuView>().rootVisualElement;

            Assert.That(root.resolvedStyle.width, Is.GreaterThan(0f), "zero-width panel");
            Assert.That(root.resolvedStyle.height, Is.GreaterThan(0f), "zero-height panel");
        }

        /// <summary>
        /// The HUD in a played game. The way out is asserted specifically: there
        /// was a window in which the only controls lived inside the final overlay,
        /// so a live game had no exit at all. That exit is the status HUD's MENU
        /// chip now — the in-field QUIT button duplicated it and is gone.
        /// </summary>
        [UnityTest]
        public IEnumerator Hud_BuildsItsTreeWithLiveGameControls()
        {
            yield return LoadAndSettle("SCN_GAME");

            UIDocument document = FindDocumentFor<Views.Systems_HudView>();
            VisualElement root = document.rootVisualElement;
            Assert.That(root, Is.Not.Null, "rootVisualElement was null");

            System.Collections.Generic.List<VisualElement> all = Descendants(root);

            Assert.That(all, Is.Not.Empty, "the HUD built no elements at all");
            Assert.That(HasButton(all, "QUIT"), Is.False, "QUIT is back on the field");
            Assert.That(HasButton(all, "REMATCH"), Is.True, "no rematch on the final overlay");
            Assert.That(HasButton(all, "MENU"), Is.True, "no menu button on the final overlay");

            // No status HUD exists in batch mode, by design, so there is nothing
            // to look for there.
            if (!Application.isBatchMode)
            {
                Assert.That(
                    HasButton(
                        Descendants(
                            FindDocumentFor<Views.Systems_StatusHudView>().rootVisualElement),
                        "MENU"),
                    Is.True,
                    "no way to leave a live game");
            }
        }
    }
}
