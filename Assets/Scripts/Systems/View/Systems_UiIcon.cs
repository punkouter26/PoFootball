using UnityEngine;
using UnityEngine.UIElements;

namespace PoFootball.Views
{
    /// <summary>
    /// A transport or dismiss glyph, drawn as vector geometry by the panel.
    ///
    /// WHY NOT A CHARACTER. Neither shipped face carries the media glyphs — Bebas
    /// Neue has no lowercase, let alone U+23F8 — so a "▶" in a label falls back to
    /// whatever the device's system font draws, at whatever size and baseline that
    /// font likes. WHY NOT A SPRITE: the project deliberately ships no sprite atlas
    /// (docs/PRESENTATION.md), and five flat shapes do not justify the first one.
    /// Painter2D geometry goes into the same UI Toolkit batch as every other
    /// element's background, costs nothing until it is repainted, and scales with
    /// the panel.
    ///
    /// WHAT AN ICON REPLACES, AND WHAT IT DOES NOT. Pause, play, previous, next and
    /// close are read without a word on every device anyone owns. REMATCH, MENU and
    /// the play call are not shapes anyone has learned, and stay text.
    /// </summary>
    internal sealed class Systems_UiIcon : VisualElement
    {
        internal enum Glyph
        {
            Pause = 0,
            Play = 1,
            Previous = 2,
            Next = 3,
            Close = 4
        }

        /// <summary>Stroke of the close cross, as a fraction of the icon's size.</summary>
        private const float STROKE_FRACTION = 0.12f;

        private Glyph _glyph;
        private Color _tint;

        private Systems_UiIcon(Glyph glyph, int size, Color tint)
        {
            _glyph = glyph;
            _tint = tint;

            style.width = size;
            style.height = size;
            style.flexShrink = 0f;

            // Never a hit target: the press belongs to whatever the icon sits on.
            pickingMode = PickingMode.Ignore;

            generateVisualContent += OnGenerateVisualContent;
        }

        /// <summary>A free-standing glyph, for a surface that is not a Button.</summary>
        internal static Systems_UiIcon Create(Glyph glyph, int size, Color tint)
        {
            return new Systems_UiIcon(glyph, size, tint);
        }

        /// <summary>
        /// Puts a glyph in the middle of a button built with an empty label.
        ///
        /// The button is NAMED for what it does, because a control with no text has
        /// nothing else to be found by — in the UI Toolkit debugger, or by a test.
        /// </summary>
        internal static Systems_UiIcon AddTo(
            Button button, Glyph glyph, int size, Color tint, string name)
        {
            button.name = name;
            button.style.alignItems = Align.Center;
            button.style.justifyContent = Justify.Center;

            Systems_UiIcon icon = new Systems_UiIcon(glyph, size, tint);
            button.Add(icon);
            return icon;
        }

        /// <summary>Guarded, so a caller polling state repaints only on a change.</summary>
        internal void Set(Glyph glyph, Color tint)
        {
            if (glyph == _glyph && tint == _tint)
            {
                return;
            }

            _glyph = glyph;
            _tint = tint;
            MarkDirtyRepaint();
        }

        private void OnGenerateVisualContent(MeshGenerationContext context)
        {
            Rect rect = contentRect;

            if (rect.width <= 0f || rect.height <= 0f)
            {
                return;
            }

            Painter2D painter = context.painter2D;
            painter.fillColor = _tint;

            switch (_glyph)
            {
                case Glyph.Pause:
                    FillBar(painter, rect, 0.20f, 0.42f);
                    FillBar(painter, rect, 0.58f, 0.80f);
                    break;

                case Glyph.Play:
                    FillTriangle(painter, rect, 0.26f, 0.88f);
                    break;

                case Glyph.Previous:
                    FillBar(painter, rect, 0.12f, 0.26f);
                    FillTriangle(painter, rect, 0.88f, 0.32f);
                    break;

                case Glyph.Next:
                    FillBar(painter, rect, 0.74f, 0.88f);
                    FillTriangle(painter, rect, 0.12f, 0.68f);
                    break;

                case Glyph.Close:
                    StrokeCross(painter, rect);
                    break;
            }
        }

        /// <summary>A full-height bar between two horizontal fractions of the icon.</summary>
        private static void FillBar(Painter2D painter, Rect rect, float fromX, float toX)
        {
            painter.BeginPath();
            painter.MoveTo(Point(rect, fromX, 0.14f));
            painter.LineTo(Point(rect, toX, 0.14f));
            painter.LineTo(Point(rect, toX, 0.86f));
            painter.LineTo(Point(rect, fromX, 0.86f));
            painter.ClosePath();
            painter.Fill();
        }

        /// <summary>
        /// A triangle with its flat edge at <paramref name="baseX"/> and its point
        /// at <paramref name="tipX"/> — so the same call draws it facing either way.
        /// </summary>
        private static void FillTriangle(Painter2D painter, Rect rect, float baseX, float tipX)
        {
            painter.BeginPath();
            painter.MoveTo(Point(rect, baseX, 0.12f));
            painter.LineTo(Point(rect, tipX, 0.5f));
            painter.LineTo(Point(rect, baseX, 0.88f));
            painter.ClosePath();
            painter.Fill();
        }

        private void StrokeCross(Painter2D painter, Rect rect)
        {
            painter.strokeColor = _tint;
            painter.lineWidth = Mathf.Min(rect.width, rect.height) * STROKE_FRACTION;
            painter.lineCap = LineCap.Round;

            painter.BeginPath();
            painter.MoveTo(Point(rect, 0.2f, 0.2f));
            painter.LineTo(Point(rect, 0.8f, 0.8f));
            painter.MoveTo(Point(rect, 0.8f, 0.2f));
            painter.LineTo(Point(rect, 0.2f, 0.8f));
            painter.Stroke();
        }

        private static Vector2 Point(Rect rect, float x, float y)
        {
            return new Vector2(rect.xMin + (rect.width * x), rect.yMin + (rect.height * y));
        }
    }
}
