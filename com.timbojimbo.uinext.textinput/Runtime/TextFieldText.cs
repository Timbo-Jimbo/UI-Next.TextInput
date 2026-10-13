using System;
using System.Collections.Generic;
using TimboJimbo.UINext.Text;
using TimboJimbo.UINext.TextInput;
using UnityEngine;

namespace TimboJimbo.UINext
{
    /// <summary>
    /// A <see cref="TextField"/>'s text, drawn on a node inside the field: the text (or the placeholder while it is
    /// empty), the selection behind it, the underline under text still being composed and the caret, scrolled to keep
    /// the caret in view. Its node is as wide as the field and fits the text's height, one line at least; the field's
    /// node clips it.
    /// </summary>
    [AddComponentMenu("Timbo Jimbo/UI Next/Text Field Text")]
    public sealed class TextFieldText : UIDrawable, IMeasurable
    {
        private const float CaretWidth = 2f;
        private const float SelectionRadius = 2f;

        private static readonly List<Rect> s_rects = new();

        [NonSerialized] private TextField _field;

        // The layouts of the text and of the placeholder, and what they were made from. A single line is laid out
        // unbounded, and placed in the field by its alignment; several lines are wrapped to the node's width.
        [NonSerialized] private TextLayout _layout;
        [NonSerialized] private TextLayoutInput _laidOut;
        [NonSerialized] private bool _made;
        [NonSerialized] private bool _drawable;
        [NonSerialized] private TextLayout _placeholder;
        [NonSerialized] private TextLayoutInput _placeholderLaidOut;
        [NonSerialized] private bool _placeholderMade;
        [NonSerialized] private bool _placeholderDrawable;

        // How far the text is scrolled in the field (x right, y down), and where its layout's top-left corner was last
        // drawn, in this node's space.
        [NonSerialized] private Vector2 _scroll;
        [NonSerialized] private Vector2 _origin;

        // What it measures, as its watch last read it from the field (see Watch).
        [NonSerialized] private Effect _watch;
        [NonSerialized] private TextLayoutInput _watched;
        [NonSerialized] private bool _watching;

        // The field it is the text of: set by the field, or found above it.
        internal TextField Field
        {
            get => _field != null ? _field : _field = GetComponentInParent<TextField>(true);
            set => _field = value;
        }

        // ── Measuring ────────────────────────────────────────────────────────────

        public Vector2 Measure(float availableWidth)
        {
            var field = Field;
            if (field == null) return Vector2.zero;
            bool multiline = field.IsMultiline;
            var input = field.LayoutInput(multiline ? availableWidth : -1f);
            var layout = Make(in input);
            float height = ContentHeight(layout, in input);
            float width = multiline && availableWidth >= 0f ? availableWidth : TextWidth(in input) + CaretWidth;
            return new Vector2(width, height);
        }

        // ── Drawing ──────────────────────────────────────────────────────────────

        protected internal override void Draw(Painter painter)
        {
            var field = Field;
            if (field == null) return;
            var box = painter.ContentRect;
            var value = field.TrackedValue;
            bool editing = field.TrackedEditing;
            bool multiline = field.IsMultiline;
            var input = field.LayoutInput(multiline ? box.width : -1f);
            var layout = Make(in input);

            // A single line is placed in the field by its alignment (the start of text read right to left at its right);
            // past the field's width, it starts at its start and scrolls.
            float x = box.x;
            if (!multiline)
            {
                float free = box.width - TextWidth(in input) - CaretWidth;
                if (free > 0f)
                    x += free * AlignmentShare(input.Alignment, layout.IsRightToLeft);
            }
            var unscrolled = new Vector2(x, box.y + layout.TopPx);

            // The caret kept in view as it moves, within the field's padding (what scrolls past it shows under the
            // padding, as in UITextView), and a single line back at its start once editing ends.
            var padding = field.Node.Padding.Peek;
            var visible = new Rect(new Vector2(padding.Left, padding.Top) - Node.LaidOutRect.position,
                field.Node.Size - new Vector2(padding.Horizontal, padding.Vertical));
            if (editing && field.Reveal)
            {
                field.Reveal = false;
                RevealCaret(layout, value, field, unscrolled, visible);
            }
            if (!editing && !multiline)
                _scroll = Vector2.zero;
            _scroll = Clamp(_scroll, layout, input, unscrolled, visible, multiline);
            _origin = unscrolled - _scroll;

            // The placeholder, while there is nothing to show.
            if (value.Text.Length == 0 && !value.IsComposing)
            {
                var placeholderInput = field.LayoutInput(multiline ? box.width : -1f, placeholder: true);
                if (!string.IsNullOrEmpty(placeholderInput.Text))
                {
                    var placeholder = MakePlaceholder(in placeholderInput);
                    if (_placeholderDrawable)
                    {
                        float px = box.x;
                        if (!multiline)
                        {
                            float free = box.width - TextLayout.Measure(in placeholderInput).x;
                            if (free > 0f)
                                px += free * AlignmentShare(placeholderInput.Alignment, placeholder.IsRightToLeft);
                        }
                        painter.Text(placeholder, new Vector2(px, box.y + placeholder.TopPx), field.PlaceholderTint);
                    }
                }
            }

            var selection = value.Selection;
            var composing = value.Composing;
            // The selection behind the text; inside a composition, the part selected is its thicker underline instead.
            if (editing && !selection.IsCollapsed)
            {
                var tint = field.SelectionTint;
                if (value.IsComposing)
                {
                    Highlight(painter, layout, selection.Start, Mathf.Min(selection.End, composing.Start), tint);
                    Highlight(painter, layout, Mathf.Max(selection.Start, composing.End), selection.End, tint);
                }
                else
                {
                    Highlight(painter, layout, selection.Start, selection.End, tint);
                }
            }

            if (_drawable)
                painter.Text(layout, _origin, field.TextColor);

            // Text still being composed is underlined, thicker where a selection lies inside it (the clause a Japanese
            // keyboard is converting).
            if (value.IsComposing)
            {
                float thickness = Mathf.Max(1f, input.FontSizePx * 0.06f);
                var tint = field.TextColor;
                Underline(painter, layout, composing.Start, composing.End, thickness, tint);
                int start = Mathf.Max(selection.Start, composing.Start), end = Mathf.Min(selection.End, composing.End);
                if (end > start)
                    Underline(painter, layout, start, end, thickness * 2f, tint);
            }

            if (editing && selection.IsCollapsed && field.TrackedCaretOn)
            {
                var caret = CaretRect(layout, selection.Extent, field.UpstreamAt(selection.Extent));
                painter.Rect(caret, field.CaretTint, CaretWidth * 0.5f);
            }
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            _watch = new Effect(Watch);
            _watch.Invalidate();
        }

        // Reads the field's text and how it is set again whenever one of them changes, as values resolve, before layout:
        // when what it measures changes, its node is laid out again (ContentChanged). Measuring reads them untracked.
        private void Watch()
        {
            var field = Field;
            if (field == null) return;
            var input = field.LayoutInput(-1f);
            if (_watching && Same(in input, in _watched)) return;
            _watched = input;
            _watching = true;
            ContentChanged();
        }

        protected override void OnDisable()
        {
            _watch?.Dispose();
            _watch = null;
            _watching = false;
            base.OnDisable();
            _layout?.Dispose();
            _layout = null;
            _made = false;
            _placeholder?.Dispose();
            _placeholder = null;
            _placeholderMade = false;
        }

        // ── What the field asks ──────────────────────────────────────────────────

        /// <summary>Asks for it to be drawn again and its node laid out again (the field's config changed).</summary>
        internal void Redraw() => ContentChanged();

        // The layout of the field's text as it is now, at the node's width (several lines), for questions about where its
        // characters are.
        internal TextLayout CurrentLayout()
        {
            var field = Field;
            if (field == null) return null;
            var size = Node.Size;
            float width = Mathf.Max(0f, size.x - Node.Padding.Peek.Horizontal);
            var input = field.LayoutInput(field.IsMultiline ? width : -1f);
            return Make(in input);
        }

        // The place nearest a point in the root's points, in the text as it was last drawn.
        internal int IndexAt(Vector2 point, out bool upstream)
        {
            var layout = CurrentLayout();
            upstream = false;
            if (layout == null) return 0;
            return layout.GetIndexAt(Node.RootToLocal(point) - _origin, out upstream);
        }

        // The caret at an index, in this node's space, as it was last drawn.
        internal bool TryGetCaretRect(int index, bool upstream, out Rect rect)
        {
            var layout = CurrentLayout();
            rect = layout != null ? CaretRect(layout, index, upstream) : default;
            return layout != null;
        }

        // The bounds of a range's characters, in this node's space, as it was last drawn; false when none are laid out.
        internal bool TryGetRangeBounds(TextRange range, out Rect bounds)
        {
            bounds = default;
            var layout = CurrentLayout();
            if (layout == null || !range.IsValid || range.IsEmpty) return false;
            layout.GetSourceRangeRects(range.Start, range.End, s_rects);
            if (s_rects.Count == 0) return false;
            bounds = s_rects[0];
            for (int i = 1; i < s_rects.Count; i++)
                bounds = Rect.MinMaxRect(Mathf.Min(bounds.xMin, s_rects[i].xMin), Mathf.Min(bounds.yMin, s_rects[i].yMin),
                    Mathf.Max(bounds.xMax, s_rects[i].xMax), Mathf.Max(bounds.yMax, s_rects[i].yMax));
            bounds.position += _origin;
            return true;
        }

        // ── Placing ──────────────────────────────────────────────────────────────

        // The drawn caret at an index, in this node's space: CaretWidth wide, centred on the insertion point.
        private Rect CaretRect(TextLayout layout, int index, bool upstream)
        {
            var caret = layout.GetCaretRect(index, upstream);
            return new Rect(_origin.x + caret.x - CaretWidth * 0.5f, _origin.y + caret.y, CaretWidth, caret.height);
        }

        // Scrolls only as far as brings the caret (the end of the selection that moved) into view, as UITextField and
        // UITextView do.
        private void RevealCaret(TextLayout layout, in TextEditingValue value, TextField field, Vector2 unscrolled, Rect visible)
        {
            int extent = value.Selection.Extent;
            var caret = layout.GetCaretRect(extent, field.UpstreamAt(extent));
            float left = unscrolled.x + caret.x - CaretWidth - _scroll.x;
            float right = unscrolled.x + caret.x + CaretWidth - _scroll.x;
            float top = unscrolled.y + caret.y - _scroll.y;
            float bottom = unscrolled.y + caret.yMax - _scroll.y;
            if (right > visible.xMax) _scroll.x += right - visible.xMax;
            else if (left < visible.xMin) _scroll.x -= visible.xMin - left;
            if (bottom > visible.yMax) _scroll.y += bottom - visible.yMax;
            else if (top < visible.yMin) _scroll.y -= visible.yMin - top;
        }

        // The scroll kept to what there is to see: none while the text fits, and never past its ends.
        private Vector2 Clamp(Vector2 scroll, TextLayout layout, in TextLayoutInput input, Vector2 unscrolled, Rect visible, bool multiline)
        {
            if (multiline)
            {
                float bottom = unscrolled.y + ContentHeight(layout, in input);
                scroll.x = 0f;
                scroll.y = Mathf.Clamp(scroll.y, 0f, Mathf.Max(0f, bottom - visible.yMax));
                return scroll;
            }
            float start = layout.GetCaretRect(0).x, end = layout.GetCaretRect(input.Text.Length).x;
            float min = unscrolled.x + Mathf.Min(start, end) - CaretWidth, max = unscrolled.x + Mathf.Max(start, end) + CaretWidth;
            scroll.y = 0f;
            if (max - min <= visible.width)
                scroll.x = 0f;
            else
                scroll.x = Mathf.Clamp(scroll.x, min - visible.xMin, max - visible.xMax);
            return scroll;
        }

        private void Highlight(Painter painter, TextLayout layout, int start, int end, Color tint)
        {
            if (end <= start) return;
            layout.GetSourceRangeRects(start, end, s_rects);
            for (int i = 0; i < s_rects.Count; i++)
            {
                var rect = s_rects[i];
                rect.position += _origin;
                painter.Rect(rect, tint, SelectionRadius);
            }
        }

        private void Underline(Painter painter, TextLayout layout, int start, int end, float thickness, Color tint)
        {
            if (end <= start) return;
            layout.GetSourceRangeRects(start, end, s_rects);
            for (int i = 0; i < s_rects.Count; i++)
            {
                var rect = s_rects[i];
                painter.Rect(new Rect(_origin.x + rect.xMin, _origin.y + rect.yMax - thickness, rect.width, thickness), tint, default);
            }
        }

        // How far along the free room a line goes for an alignment: none on the left, half in the centre, all on the right,
        // the other way round for text read right to left (left and right are its start and end).
        private static float AlignmentShare(TextAnchor alignment, bool rightToLeft)
        {
            float share = ((int)alignment % 3) switch { 0 => 0f, 1 => 0.5f, _ => 1f };
            return rightToLeft ? 1f - share : share;
        }

        // ── Laying out ───────────────────────────────────────────────────────────

        private TextLayout Make(in TextLayoutInput input)
        {
            _layout ??= new TextLayout();
            if (!_made || !Same(in input, in _laidOut))
            {
                _drawable = _layout.Generate(in input);
                _laidOut = input;
                _made = true;
            }
            return _layout;
        }

        private TextLayout MakePlaceholder(in TextLayoutInput input)
        {
            _placeholder ??= new TextLayout();
            if (!_placeholderMade || !Same(in input, in _placeholderLaidOut))
            {
                _placeholderDrawable = _placeholder.Generate(in input);
                _placeholderLaidOut = input;
                _placeholderMade = true;
            }
            return _placeholder;
        }

        // How tall the text is: a line's pitch (baseline to baseline) for each of its lines, an empty one after a final line
        // break counting, so an empty field is as tall as one with a line in it and a field showing so many lines shows
        // no part of the next; taller where its glyphs are (a taller fallback font, say).
        private static float ContentHeight(TextLayout layout, in TextLayoutInput input)
        {
            float line = TextLayout.LineHeightOf(input.Font, input.FontSizePx);
            return Mathf.Max(line * layout.LineCount, layout.GetCaretRect(input.Text.Length).yMax);
        }

        // The width of the text in one line, unbounded.
        private float TextWidth(in TextLayoutInput input)
        {
            if (string.IsNullOrEmpty(input.Text)) return 0f;
            var unbounded = input;
            unbounded.WidthPx = -1f;
            unbounded.WordWrap = false;
            return TextLayout.Measure(in unbounded).x;
        }

        private static bool Same(in TextLayoutInput a, in TextLayoutInput b) =>
            a.Text == b.Text && a.Font == b.Font && a.FontSizePx == b.FontSizePx && a.WordWrap == b.WordWrap
            && a.Alignment == b.Alignment && a.Direction == b.Direction && a.WidthPx == b.WidthPx;
    }
}
