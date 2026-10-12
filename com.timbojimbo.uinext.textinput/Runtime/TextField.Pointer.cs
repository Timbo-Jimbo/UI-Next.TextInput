using TimboJimbo.UINext.TextInput;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TimboJimbo.UINext
{
    // The pointer, told apart by what it is, never by the platform, so a touch screen on a laptop and a mouse on an iPad
    // each get their own.
    //
    // A mouse or a pen selects as on a desktop: a press places the caret (shift extends the selection to it), a drag
    // selects from there, a double click selects a word and a triple click a paragraph, a drag after either going a word
    // or a paragraph at a time; held outside the field, the selection carries on and the text scrolls after it.
    //
    // Touch works as on a phone: a tap places the caret (beginning editing), a tap on the caret or inside the selection
    // shows the platform's edit menu, and a double tap or a long press selects a word and shows it. Other drags are not
    // the field's: they go to what is above it, so the page under it still scrolls and swipes back.
    public sealed partial class TextField
    {
        private enum Granularity
        {
            Character,
            Word,
            Paragraph,
        }

        // Presses this close in time, and this close together (points: a few for a mouse, about a fingertip for touch),
        // count as one more click or tap.
        private const float MultiClickTime = 0.5f;
        private const float MultiClickDistance = 4f;
        private const float MultiTapDistance = 24f;

        // How far a touch may move and still be a tap.
        private const float TapSlop = 10f;

        private int _clicks;
        private float _clickTime = float.NegativeInfinity;
        private Vector2 _clickPoint;
        private float _tapTime = float.NegativeInfinity;
        private Vector2 _tapPoint;

        // The pointer pressing it, until it lets go or something else takes it; where it is, in the root's points; and
        // the frame a pointer last went down on it (a mouse press elsewhere ends editing).
        private int _pointer = -1;
        private PointerKind _pointerKind;
        private Vector2 _pointerAt;
        private int _pressFrame = -1;

        // A mouse press, or a touch after a long press, selecting as it drags: by what (characters, words, paragraphs),
        // and from what was first taken.
        private bool _selecting;
        private Granularity _granularity;
        private TextRange _anchor;

        private bool _menuShown;

        void IPointerHandler.OnPointerDown(in PointerEvent e)
        {
            _pressFrame = Time.frameCount;
            if (_pointer >= 0 || !Pressable.Interactable.Peek || _text == null) return;
            _pointer = e.PointerId;
            _pointerKind = e.Kind;
            _pointerAt = e.Position;
            // A touch is acted on as it lets go (a tap) or once it has been held (a long press), and dropped if it drags.
            if (e.Kind == PointerKind.Touch) return;
            // A mouse or a pen selects from the first point it moves: the pointer is the field's.
            UIInput.Claim(this, e.PointerId);
            PressMouse(e.Position);
        }

        void IPointerHandler.OnPointerMove(in PointerEvent e)
        {
            if (e.PointerId != _pointer) return;
            _pointerAt = e.Position;
            if (_selecting)
                DragSelectTo(e.Position);
        }

        void IPointerHandler.OnPointerUp(in PointerEvent e)
        {
            if (e.PointerId != _pointer) return;
            bool touch = _pointerKind == PointerKind.Touch;
            bool selected = _selecting && touch;
            bool tap = touch && !_selecting && e.Translation.magnitude <= TapSlop;
            LetGoOfPointer();
            if (selected)
                ShowMenu();
            else if (tap)
                Tap(e.Position);
        }

        void IPointerHandler.OnPointerCancel(in PointerEvent e)
        {
            if (e.PointerId == _pointer)
                LetGoOfPointer();
        }

        private void LetGoOfPointer()
        {
            _pointer = -1;
            _selecting = false;
        }

        // ── Mouse and pen ────────────────────────────────────────────────────────

        private void PressMouse(Vector2 point)
        {
            float now = Time.unscaledTime;
            bool again = now - _clickTime <= MultiClickTime && (point - _clickPoint).sqrMagnitude <= MultiClickDistance * MultiClickDistance;
            _clicks = again ? Mathf.Min(_clicks + 1, 3) : 1;
            _clickTime = now;
            _clickPoint = point;
            _selecting = true;

            int index = IndexAt(point, out bool upstream);
            var keyboard = Keyboard.current;
            if (_clicks == 1 && _editing.Peek && keyboard != null && keyboard.shiftKey.isPressed)
            {
                // Shift extends the selection to the press, from where it started.
                var from = Current.Selection.Base;
                _granularity = Granularity.Character;
                _anchor = TextRange.Collapsed(from);
                SetSelection(new TextSelection(from, index), upstream);
            }
            else
            {
                _granularity = _clicks == 1 ? Granularity.Character : _clicks == 2 ? Granularity.Word : Granularity.Paragraph;
                _anchor = Reach(index);
                SetSelection(new TextSelection(_anchor.Start, _anchor.End), upstream && _anchor.End == index);
            }

            if (!_editing.Peek)
            {
                _placed = true;
                BeginEditing();
            }
        }

        // The selection from the far end of what was first taken (a word, a paragraph) to the far end of what the pointer
        // is over, the way it went.
        private void DragSelectTo(Vector2 point)
        {
            int index = IndexAt(point, out bool upstream);
            var reach = Reach(index);
            var selection = reach.Start < _anchor.Start
                ? new TextSelection(_anchor.End, reach.Start)
                : new TextSelection(_anchor.Start, Mathf.Max(reach.End, _anchor.End));
            SetSelection(selection, upstream && selection.Extent == index);
        }

        // What a press at `index` takes, by the granularity selecting goes by. A secure field has no words to show, so a
        // word is the whole text.
        private TextRange Reach(int index)
        {
            var text = Current.Text;
            switch (_granularity)
            {
                case Granularity.Word:
                    return _config.IsSecure ? new TextRange(0, text.Length) : TextBoundaries.WordAt(text, index);
                case Granularity.Paragraph:
                    return TextBoundaries.ParagraphAt(text, index);
                default:
                    return TextRange.Collapsed(index);
            }
        }

        // ── Touch ────────────────────────────────────────────────────────────────

        private void Tap(Vector2 point)
        {
            if (!Pressable.Interactable.Peek) return;
            float now = Time.unscaledTime;
            bool second = _editing.Peek && now - _tapTime <= MultiClickTime && (point - _tapPoint).sqrMagnitude <= MultiTapDistance * MultiTapDistance;
            // A double tap starts over: a third tap is a first again.
            _tapTime = second ? float.NegativeInfinity : now;
            _tapPoint = point;
            int index = IndexAt(point, out bool upstream);

            if (second)
            {
                SelectWordAt(index);
                ShowMenu();
                return;
            }
            if (!_editing.Peek)
            {
                SetSelection(TextSelection.Collapsed(index), upstream);
                _placed = true;
                BeginEditing();
                return;
            }
            var selection = Current.Selection;
            bool onSelection = selection.IsCollapsed
                ? index == selection.Extent
                : index >= selection.Start && index <= selection.End;
            if (!onSelection)
                SetSelection(TextSelection.Collapsed(index), upstream);
            else if (_menuShown)
                HideMenu();
            else
                ShowMenu();
        }

        // A touch held still long enough selects the word under it, beginning editing, and the drag that follows selects
        // on a word at a time, rather than scrolling the page.
        private void OnLongPressed()
        {
            if (_pointer < 0 || _pointerKind != PointerKind.Touch || !Pressable.Interactable.Peek) return;
            UIInput.Claim(this, _pointer);
            var word = SelectWordAt(IndexAt(_pointerAt, out _));
            _granularity = Granularity.Word;
            _anchor = word;
            _selecting = true;
            if (!_editing.Peek)
            {
                _placed = true;
                BeginEditing();
            }
        }

        private TextRange SelectWordAt(int index)
        {
            var text = Current.Text;
            var word = _config.IsSecure ? new TextRange(0, text.Length) : TextBoundaries.WordAt(text, index);
            if (!_editing.Peek) _placed = true;
            SetSelection(new TextSelection(word.Start, word.End));
            return word;
        }

        // ── The edit menu ────────────────────────────────────────────────────────

        // The platform's copy and paste bubble, over the selection or the caret, where there is one (iOS and Android).
        private void ShowMenu()
        {
            if (!_editing.Peek || !TextInputSystem.SupportsEditMenu || _text == null) return;
            var selection = Current.Selection;
            var actions = TextEditActions.Paste;
            if (!selection.IsCollapsed && !_config.IsSecure)
                actions |= TextEditActions.Cut | TextEditActions.Copy;
            if (selection.Range.Length < Current.Text.Length)
                actions |= TextEditActions.SelectAll;
            var root = Node.Tree?.Root;
            if (root == null) return;
            Rect local;
            if (selection.IsCollapsed || !_text.TryGetRangeBounds(selection.Range, out local))
            {
                if (!_text.TryGetCaretRect(selection.Extent, UpstreamAt(selection.Extent), out local)) return;
            }
            TextInputSystem.ShowEditMenu(this, ScreenRect(_text.Node, local, root), actions);
            _menuShown = true;
        }

        private void HideMenu()
        {
            if (!_menuShown) return;
            _menuShown = false;
            TextInputSystem.HideEditMenu();
        }

        // ── Where the pointer is in the text ─────────────────────────────────────

        // The place the caret can stop in the text nearest a point in the root's points.
        private int IndexAt(Vector2 point, out bool upstream)
        {
            upstream = false;
            if (_text == null) return Current.Text.Length;
            return Snap(_text.IndexAt(point, out upstream));
        }
    }
}
