using System;
using TimboJimbo.UINext.Text;
using TimboJimbo.UINext.TextInput;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;

namespace TimboJimbo.UINext
{
    /// <summary>Raised with a <see cref="TextField"/>'s text.</summary>
    [Serializable]
    public sealed class TextFieldEvent : UnityEvent<string>
    {
    }

    /// <summary>
    /// A text field the UI draws and the platform's own keyboard types into, as a browser's or Flutter's: the field draws
    /// its text, caret, selection and the underline under text still being composed, on a node inside it
    /// (<see cref="TextFieldText"/>) that it clips and scrolls, while UI Next Text Input's <see cref="TextInputSystem"/>
    /// connects it to the keyboard, so autocorrect, suggestions, composition (Chinese, Japanese, Korean), dictation and
    /// the system's edit menu work on iOS and Android, and the Input System's keys and the IME on desktop and in the
    /// editor, with the shortcuts a desktop field has.
    /// <para>
    /// Editing begins on a tap or a click, on Enter or a gamepad's A with focus on it (its <see cref="Pressable"/>), or on
    /// <see cref="BeginEditing"/>. While it is edited it is focus's first responder (<see cref="UIFocus.FirstResponder"/>):
    /// the keys are its own, bar Tab, which moves focus on. Editing ends on <see cref="EndEditing"/>, Escape or a gamepad's
    /// B, the return key (unless it says Send), the keyboard being dismissed, another field beginning, focus moving off
    /// it, the field being hidden, disabled or made uninteractable, and a mouse press elsewhere, as a browser blurs a
    /// field. A touch elsewhere does not end it, as iOS keeps the keyboard up whatever is tapped (a chat's Send).
    /// </para>
    /// <para>
    /// A single-line field is one line high and scrolls sideways to keep the caret in view; a multi-line one grows with
    /// its lines, on a quick spring, up to the height its node allows (<see cref="UIBuild"/>'s TextField makes it Fit up to
    /// a number of lines), then scrolls up and down. Its colours, font and placeholder are values with variants, so they
    /// follow its states: Focused while it is edited, Disabled, Hovered.
    /// </para>
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(UINode), typeof(Pressable))]
    [AddComponentMenu("Timbo Jimbo/UI Next/Text Field")]
    public sealed partial class TextField : MonoBehaviour, ITextInputClient, IPointerHandler
    {
        // What a secure field shows for each UTF-16 unit of its text, so that indices into what is shown are the text's.
        private const char SecureBullet = '•';

        // The caret is solid while things change, and after this long still blinks, on and off each half second.
        private const float BlinkDelay = 0.5f;
        private const float BlinkHalfPeriod = 0.5f;

        [Tooltip("The text it holds.")]
        [SerializeField, TextArea] private string _content = "";

        [Tooltip("Shown while it is empty.")]
        [SerializeField] private UIValue<string> _placeholder = "";

        [SerializeField] private UIValue<FontStack> _font = new UIValue<FontStack>();
        [SerializeField] private UIValue<float> _fontSize = 17f;
        [SerializeField] private UIValue<Color> _color = new Color(0.1f, 0.1f, 0.12f, 1f);
        [SerializeField] private UIValue<Color> _placeholderColor = new Color(0.24f, 0.24f, 0.26f, 0.3f);

        [Tooltip("The colour of the caret.")]
        [SerializeField] private UIValue<Color> _caretColor = new Color(0.04f, 0.52f, 1f, 1f);

        [Tooltip("The colour drawn behind selected text.")]
        [SerializeField] private UIValue<Color> _selectionColor = new Color(0.04f, 0.52f, 1f, 0.25f);

        [Tooltip("Left, centre or right (the start and end of the text, for text read right to left).")]
        [SerializeField] private UIValue<TextAnchor> _alignment = TextAnchor.UpperLeft;

        [Tooltip("What it asks of the platform's keyboard, and whether it is multi-line.")]
        [SerializeField] private TextInputConfig _config;

        [Tooltip("The most UTF-16 units typing and pasting can bring the text to. 0: no limit.")]
        [SerializeField, Min(0)] private int _maxLength;

        [Tooltip("The node inside it its text is drawn on.")]
        [SerializeField] private TextFieldText _text;

        [SerializeField] private TextFieldEvent _textChanged = new();
        [SerializeField] private TextFieldEvent _submitted = new();
        [SerializeField] private UnityEvent _editingBegan = new();
        [SerializeField] private UnityEvent _editingEnded = new();

        [NonSerialized] private readonly TextEditHistory _history = new();

        // What drawing and layout follow: the value, whether it is edited, and whether the caret shows in its blink.
        [NonSerialized] private readonly Observable<TextEditingValue> _value = new(TextEditingValue.Empty);
        [NonSerialized] private readonly Observable<bool> _editing = new(false);
        [NonSerialized] private readonly Observable<bool> _caretOn = new(true);

        [NonSerialized] private UINode _node;
        [NonSerialized] private Pressable _pressable;
        [NonSerialized] private float _blinkFrom;

        // The selection was placed (by a tap, a click or SelectAll) before editing began, so beginning keeps it rather
        // than putting the caret at the end.
        [NonSerialized] private bool _placed;

        // The caret's affinity, kept with the selection's extent, as UIKit's and Flutter's carets keep theirs: at an index
        // where a line wraps, which ends one line and starts the next, whether it stands at the end of the line before
        // (after End, or a tap or a move past a wrapped line's end) rather than at the start of the line after. Any other
        // move, and any edit of the text, puts it back.
        [NonSerialized] private bool _upstream;

        // The x the caret keeps to as it moves up and down through shorter lines, in the text's space; NaN when it is to
        // be taken from where the caret is.
        [NonSerialized] private float _goalX = float.NaN;

        // The caret is to be brought into view as the text is next drawn (it moved, or the text changed).
        [NonSerialized] internal bool Reveal;

        // The keyboard's height (screen pixels) the field was last kept above; NaN to keep it above the keyboard again.
        [NonSerialized] private float _keptAbove = float.NaN;

        /// <summary>Its node: the part of the screen its text is seen through, which clips it.</summary>
        public UINode Node => _node != null ? _node : _node = GetComponent<UINode>();

        /// <summary>The button it is to focus and to the pointer: its Interactable turns the field off.</summary>
        public Pressable Pressable => _pressable != null ? _pressable : _pressable = GetComponent<Pressable>();

        /// <summary>The node its text is drawn on, for placing something over the text (a copy of it flying away as it is sent).</summary>
        public UINode TextNode => _text != null ? _text.Node : null;

        /// <summary>
        /// The text. Set from code, it replaces the whole text (committing any composition first), the caret at its end,
        /// as one step undo can take back; a single-line field takes its line breaks out. The keyboard is told, and
        /// <see cref="TextChanged"/> raised.
        /// </summary>
        public string Text
        {
            get => _value.Peek.Text;
            set
            {
                var text = Clean(value);
                if (string.Equals(text, Current.Text, StringComparison.Ordinal)) return;
                CommitComposition();
                Change(TextEditingValue.FromText(text), record: true, coalesce: false, notify: true);
            }
        }

        /// <summary>The text, what is selected and what is still being composed.</summary>
        public TextEditingValue Value => _value.Peek;

        /// <summary>
        /// What the field asks of the keyboard, and whether it is multi-line. Changed while editing, the keyboard is
        /// reloaded for it; changed to a single line, the text's line breaks are taken out.
        /// </summary>
        public TextInputConfig Config
        {
            get => _config;
            set
            {
                if (_config.Equals(value)) return;
                bool lines = value.Multiline != _config.Multiline;
                _config = value;
                if (lines)
                {
                    var flat = Flatten(Current.CommitComposition());
                    if (!string.Equals(flat.Text, Current.Text, StringComparison.Ordinal))
                        Change(flat, record: true, coalesce: false, notify: true);
                }
                _text?.Redraw();
                TextInputSystem.NotifyConfigChanged(this);
            }
        }

        /// <summary>
        /// The most UTF-16 units typing, pasting and the keyboard can bring the text to (0, the default: no limit), as a
        /// browser's maxlength: what would go past it is cut off, at a whole character. Text still being composed is left
        /// alone until it is committed, and cut then. Text set from code is not cut.
        /// </summary>
        public int MaxLength
        {
            get => _maxLength;
            set => _maxLength = Mathf.Max(0, value);
        }

        /// <summary>Shown while it is empty.</summary>
        public UIValue<string> Placeholder => _placeholder;

        /// <summary>The fonts it draws with; none for the project's default stack.</summary>
        public UIValue<FontStack> Font => _font;

        /// <summary>Its text's size, in points.</summary>
        public UIValue<float> FontSize => _fontSize;

        public UIValue<Color> Color => _color;
        public UIValue<Color> PlaceholderColor => _placeholderColor;
        public UIValue<Color> CaretColor => _caretColor;
        public UIValue<Color> SelectionColor => _selectionColor;
        public UIValue<TextAnchor> Alignment => _alignment;

        /// <summary>Whether it is being edited: it has the keyboard, and draws its caret.</summary>
        public bool IsEditing => _editing.Peek;

        /// <summary>Raised with the text whenever it changes, however it changes.</summary>
        public TextFieldEvent TextChanged => _textChanged;

        /// <summary>
        /// Raised with the text when the return key submits it rather than starting a new line. Editing then ends, as
        /// SwiftUI's TextField gives up focus on submit, unless the return key says Send (a chat's composer, typed on
        /// after each message, as in Messages) or a handler began editing another field.
        /// </summary>
        public TextFieldEvent Submitted => _submitted;

        /// <summary>Raised as editing begins.</summary>
        public UnityEvent EditingBegan => _editingBegan;

        /// <summary>Raised as editing ends.</summary>
        public UnityEvent EditingEnded => _editingEnded;

        /// <summary>
        /// Gives a field built by hand the node its text is drawn on: a node inside it with a <see cref="TextFieldText"/>,
        /// as wide as it and fitting its text's height. The field's own node clips it: give it no padding (pad a node
        /// around it), and for a multi-line field a height that fits up to the most lines it shows.
        /// </summary>
        public void Setup(TextFieldText text)
        {
            _text = text;
            if (_text != null)
                _text.Field = this;
        }

        private TextEditingValue Current => _value.Peek;

        private bool Multiline => _config.Multiline;

        // The layout of the text as it is now, at the width it was last laid out at, for questions about where its
        // characters are; null with nothing to lay out yet.
        private TextLayout Layout => _text != null ? _text.CurrentLayout() : null;

        // ── Editing ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Begins editing: the field takes the keyboard (<see cref="TextInputSystem.Begin"/>) and becomes focus's first
        /// responder, with the caret at the end of the text unless a tap, a click or <see cref="SelectAll"/> placed the
        /// selection first. Another field being edited stops. Only in play mode, while the field is enabled and takes
        /// presses.
        /// </summary>
        public void BeginEditing()
        {
            if (_editing.Peek || !Application.isPlaying || !isActiveAndEnabled || !Pressable.Interactable.Peek || _text == null) return;
            if (!_placed)
                Change(Current.WithSelection(TextSelection.Collapsed(Current.Text.Length)), record: false, coalesce: false, notify: false);
            _editing.Value = true;
            _blinkFrom = Time.unscaledTime;
            _caretOn.Value = true;
            Reveal = true;
            _keptAbove = float.NaN;
            UIFocus.FirstResponder = Node;
            TextInputSystem.Begin(this);
            // The field whose session that ended may have taken the keyboard straight back, which ended this one's.
            if (_editing.Peek)
                _editingBegan.Invoke();
        }

        /// <summary>
        /// Ends editing: any composition is committed as it stands, the keyboard is given back and the caret and
        /// selection go. Focus stays on the field, as a browser's field keeps it after Escape. A single-line field
        /// scrolls back to its start, as UITextField does.
        /// </summary>
        public void EndEditing()
        {
            if (!_editing.Peek) return;
            CommitComposition();
            _editing.Value = false;
            _placed = false;
            Reveal = false;
            LetGoOfPointer();
            HideMenu();
            TextInputSystem.End(this);
            if (UIFocus.FirstResponder == Node)
                UIFocus.FirstResponder = null;
            _editingEnded.Invoke();
        }

        /// <summary>Selects all the text. Before editing begins, beginning keeps it selected.</summary>
        public void SelectAll()
        {
            if (!_editing.Peek) _placed = true;
            SetSelection(new TextSelection(0, Current.Text.Length));
        }

        /// <summary>
        /// Puts <paramref name="text"/> in place of the selection, the caret after it, as typing it would (a suggestion
        /// chip, an emoji picker): any composition is committed first, a single-line field takes its line breaks out,
        /// and <see cref="MaxLength"/> cuts it short. One step for undo.
        /// </summary>
        public void Insert(string text) => Put(Clean(text), coalesce: false);

        // ── Lifecycle ────────────────────────────────────────────────────────────

        private void OnEnable()
        {
            var node = Node;
            foreach (var value in Values())
                value.Attach(node);
            _value.Value = Flatten(TextEditingValue.FromText(_content));
            Pressable.Tapped += OnTapped;
            Pressable.LongPressed += OnLongPressed;
            if (_text != null)
                _text.Field = this;
        }

        private void OnDisable()
        {
            EndEditing();
            LetGoOfPointer();
            foreach (var value in Values())
                value.Detach();
            if (_pressable != null)
            {
                _pressable.Tapped -= OnTapped;
                _pressable.LongPressed -= OnLongPressed;
            }
        }

        private void OnValidate()
        {
            if (!isActiveAndEnabled) return;
            foreach (var value in Values())
                value.Refresh();
            if (!_editing.Peek)
                _value.Value = Flatten(TextEditingValue.FromText(_content));
            if (_text != null)
            {
                _text.Field = this;
                _text.Redraw();
            }
        }

        private IUIValue[] Values() => new IUIValue[]
        {
            _placeholder, _font, _fontSize, _color, _placeholderColor, _caretColor, _selectionColor, _alignment,
        };

        // Once a frame while it is edited: editing ends if the field has been hidden, turned off or left by focus, or a
        // mouse went down elsewhere; a selection being dragged outside the field carries on, the text scrolling after
        // it; and the caret blinks.
        private void Update()
        {
            if (!_editing.Peek) return;
            if (!Pressable.Interactable.Peek || IsHidden() || UIFocus.FirstResponder != Node)
            {
                EndEditing();
                return;
            }
            var mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame && _pressFrame != Time.frameCount)
            {
                EndEditing();
                return;
            }
            if (_selecting && !UIInput.IsOver(Node, _pointerAt))
                DragSelectTo(_pointerAt);
            if (UIRoot.KeyboardHeight != _keptAbove)
            {
                _keptAbove = UIRoot.KeyboardHeight;
                KeepAboveKeyboard();
            }
            bool on = BlinkOn();
            if (_caretOn.Peek != on)
                _caretOn.Value = on;
        }

        // Scrolls the nearest scroll view the field is in the least it takes to show the field within it (clear of what
        // covers its edges, and its margin) and above the keyboard, as iOS brings the field being edited into
        // view as editing begins and as its keyboard rises: once
        // when editing begins, and each frame the keyboard's height changes. A scroll view that avoids the keyboard has
        // room past what it holds for it.
        private void KeepAboveKeyboard()
        {
            var node = Node;
            var root = node.Tree?.Root;
            if (root == null) return;
            const float margin = 12f;
            float keyboardTop = root.Size.y - UIRoot.KeyboardHeight / root.PixelsPerPoint;
            for (var n = node.Parent; n != null; n = n.Parent)
            {
                var scroller = n.Scroller;
                if (scroller == null || !scroller.isActiveAndEnabled) continue;
                var view = n.DrawnRect;
                var field = node.DrawnRect;
                // Clear of what covers the scroll view's edges and its margin, with at least `margin` of room past what
                // covers them, and `margin` above the keyboard; its own points are root points unless it is drawn scaled.
                float scale = scroller.Viewport.y > 0f ? view.height / scroller.Viewport.y : 1f;
                var clear = scroller.ClearInsets;
                var room = scroller.ClearMargin.Peek;
                float clearBottom = view.yMax - clear.Bottom * scale;
                float bottom = keyboardTop < clearBottom ? keyboardTop - margin
                    : clearBottom - Mathf.Max(0f, margin - room.Bottom * scale);
                float top = view.yMin + clear.Top * scale + Mathf.Max(0f, margin - room.Top * scale);
                float by = field.yMax > bottom ? field.yMax - bottom : field.yMin < top ? field.yMin - top : 0f;
                if (Mathf.Abs(by) > 0.5f)
                    scroller.ScrollTo(scroller.ContentOffset + new Vector2(0f, by / scale));
                return;
            }
        }

        private bool BlinkOn()
        {
            float idle = Time.unscaledTime - _blinkFrom;
            return idle < BlinkDelay || Mathf.FloorToInt((idle - BlinkDelay) / BlinkHalfPeriod) % 2 == 1;
        }

        // Whether it, or a node it is in, is hidden (on its way out included), or out of its tree: editing ends, as a
        // browser blurs a field that stops being shown.
        private bool IsHidden()
        {
            var node = Node;
            if (node.Tree == null || node.Index < 0) return true;
            for (var n = node; n != null; n = n.Parent)
            {
                if (n.Display.Peek != DisplayMode.Visible || n.Leaving.Peek)
                    return true;
            }
            return false;
        }

        // Focus pressed it (Enter, Space, a gamepad's A), or a tap let go on it.
        private void OnTapped()
        {
            if (!_editing.Peek)
                BeginEditing();
        }

        // ── What drawing reads ───────────────────────────────────────────────────

        // The value, whether it is edited and whether the caret shows, read so that what reads them follows them.
        internal TextEditingValue TrackedValue => _value.Value;
        internal bool TrackedEditing => _editing.Value;
        internal bool TrackedCaretOn => _caretOn.Value;

        // What laying the text out takes, at a width (negative: unbounded). Its values are read as they are wherever
        // this is asked (drawing and layout follow them). With no text, the placeholder's, for `placeholder`.
        internal TextLayoutInput LayoutInput(float width, bool placeholder = false)
        {
            var input = TextLayoutInput.Default;
            input.Text = placeholder ? _placeholder.Value ?? "" : DisplayOf(_value.Value.Text);
            input.RichText = false;
            input.Font = _font.Value;
            input.FontSizePx = Mathf.Max(1f, _fontSize.Value);
            input.WidthPx = width;
            input.HeightPx = -1f;
            input.WordWrap = Multiline;
            input.Alignment = _alignment.Value;
            input.NaturalAlignment = true;
            input.Overflow = TextOverflow.Overflow;
            input.MaxLines = 0;
            input.AntiAliasMarginPx = 1f;
            // Text with no letter yet reads the way the keyboard in use writes while it is edited, as on iOS, where an
            // Arabic keyboard brings an empty field's caret to its right; a secure field's stays left to right.
            input.Direction = _editing.Value && TextInputSystem.KeyboardRightToLeft && !_config.IsSecure
                ? TextDirection.AutoRightToLeft
                : TextDirection.Auto;
            return input;
        }

        internal Color TextColor => _color.Value;
        internal Color PlaceholderTint => _placeholderColor.Value;
        internal Color CaretTint => _caretColor.Value;
        internal Color SelectionTint => _selectionColor.Value;
        internal bool IsMultiline => Multiline;

        // The line height of its font at its size, in points.
        private float LineHeight => TextLayout.LineHeightOf(_font.Peek, Mathf.Max(1f, _fontSize.Peek));

        // Whether `index` is the caret standing upstream, at the end of a wrapped line (see _upstream).
        internal bool UpstreamAt(int index) => _upstream && index == Current.Selection.Extent;

        private string DisplayOf(string text) =>
            _config.IsSecure && text.Length > 0 ? new string(SecureBullet, text.Length) : text;

        // ── The value ────────────────────────────────────────────────────────────

        // Takes `after` up as the field's value: shown, recorded for undo (when `record`: everything but what undo and redo
        // give back; the history keeps a composition out until it is committed, and a typing run as one step when
        // `coalesce`), the keyboard told when `notify`, and TextChanged raised when the text changed. The caret stops
        // blinking for a moment and is brought into view, and the edit menu goes. An edit or a move puts the caret's
        // affinity back downstream and lets go of the column it kept to. A multi-line field whose text changes grows or
        // shrinks with its lines on a quick spring, as a chat's composer does.
        private void Change(in TextEditingValue after, bool record, bool coalesce, bool notify)
        {
            var before = Current;
            if (after == before) return;
            if (record)
                _history.Record(before, after, coalesce);

            bool textChanged = !string.Equals(before.Text, after.Text, StringComparison.Ordinal);
            _content = after.Text;
            if (textChanged || after.Selection != before.Selection)
            {
                _goalX = float.NaN;
                _upstream = false;
            }
            if (_editing.Peek)
            {
                _blinkFrom = Time.unscaledTime;
                _caretOn.Value = true;
                Reveal = true;
            }
            HideMenu();
            if (textChanged && Multiline && Application.isPlaying)
            {
                var value = after;
                UI.Animate(UIAnimation.Snappy, () => _value.Value = value);
            }
            else
            {
                _value.Value = after;
            }
            if (notify)
                TextInputSystem.NotifyValueChanged(this);
            if (textChanged)
                _textChanged.Invoke(after.Text);
        }

        // Moves the selection, committing any composition first, as tapping elsewhere in a composition commits it.
        // `upstream`: the caret's affinity at the selection's extent (see _upstream), for as long as the commit leaves the
        // extent where it was asked for.
        private void SetSelection(TextSelection selection, bool upstream = false)
        {
            Change(Committed(selection), record: true, coalesce: false, notify: true);
            // The affinity is set after the change, which puts it back downstream as the caret moves.
            upstream &= Current.Selection.Extent == selection.Extent;
            if (upstream == _upstream) return;
            _upstream = upstream;
            if (_editing.Peek)
            {
                _blinkFrom = Time.unscaledTime;
                Reveal = true;
                _text?.Redraw();
            }
        }

        // Commits the composition under way, as it stands bar what MaxLength cuts, as one step for undo. The keyboard is not
        // told: what commits it tells it what follows.
        private void CommitComposition()
        {
            if (Current.IsComposing)
                Change(Committed(Current.Selection), record: true, coalesce: false, notify: false);
        }

        // The value with its composition committed and `selection` (given in the text as it is) in place: what was being
        // composed, let through uncut while it was under way, is cut to fit MaxLength now.
        private TextEditingValue Committed(TextSelection selection)
        {
            var committed = Current.CommitComposition().WithSelection(selection);
            return Current.IsComposing ? Limit(Current, committed) : committed;
        }

        // Puts `text` in place of the selection, committing any composition first, cut to fit MaxLength.
        private void Put(string text, bool coalesce)
        {
            var value = Committed(Current.Selection);
            var range = value.Selection.Range;
            text = Fit(value, range, text);
            if (text.Length == 0) return;
            Change(value.Replace(range, text), record: true, coalesce: coalesce, notify: true);
        }

        // ── ITextInputClient ─────────────────────────────────────────────────────

        TextEditingValue ITextInputClient.Value => Current;

        TextInputConfig ITextInputClient.Config => _config;

        void ITextInputClient.ApplyPlatformValue(in TextEditingValue value)
        {
            var after = Limit(Current, Flatten(value));
            // Filtered, the keyboard is told what the field kept.
            Change(after, record: true, coalesce: true, notify: after != value);
        }

        void ITextInputClient.InsertText(string text)
        {
            text = Clean(text);
            var range = Current.IsComposing ? Current.Composing : Current.Selection.Range;
            text = Fit(Current, range, text);
            if (text.Length == 0) return;
            Change(Current.Replace(range, text), record: true, coalesce: true, notify: true);
        }

        void ITextInputClient.SetComposingText(string text)
        {
            text ??= string.Empty;
            var value = Current;
            var range = value.IsComposing ? value.Composing : value.Selection.Range;
            if (text.Length == 0)
            {
                // The composition gone: its text goes with it.
                if (value.IsComposing)
                    Change(value.Replace(range, string.Empty), record: true, coalesce: true, notify: true);
                return;
            }
            // The composition (or what was selected, as it starts) becomes the text, underlined, the caret at its end.
            var composed = value.Replace(range, text).WithComposing(new TextRange(range.Start, range.Start + text.Length));
            Change(composed, record: true, coalesce: true, notify: true);
        }

        void ITextInputClient.OnSessionEnded() => EndEditing();

        bool ITextInputClient.TryGetScreenGeometry(out Rect field, out Rect caret, out Rect composing)
        {
            field = caret = composing = default;
            var node = Node;
            var root = node.Tree?.Root;
            if (_text == null || !isActiveAndEnabled || root == null) return false;
            var size = node.Size;
            if (size.x <= 0f || size.y <= 0f) return false;
            field = ScreenRect(node, new Rect(Vector2.zero, size), root);
            int extent = Current.Selection.Extent;
            if (!_text.TryGetCaretRect(extent, UpstreamAt(extent), out var caretRect)) return false;
            caret = ScreenRect(_text.Node, caretRect, root);
            if (Current.IsComposing && _text.TryGetRangeBounds(Current.Composing, out var bounds))
                composing = ScreenRect(_text.Node, bounds, root);
            return true;
        }

        // Which way the text reads, as last laid out, and the character after the caret (at the text's end, the text's
        // own way), as Android's TextView flags its insertion marker by.
        void ITextInputClient.GetDirection(out bool rightToLeft, out bool caretRightToLeft)
        {
            rightToLeft = caretRightToLeft = false;
            var layout = Layout;
            if (layout == null) return;
            rightToLeft = layout.IsRightToLeft;
            int extent = Current.Selection.Extent;
            caretRightToLeft = extent < Current.Text.Length ? layout.IsCharacterRightToLeft(extent) : rightToLeft;
        }

        // A rect in a node's space as it covers the screen: screen pixels, y up.
        private static Rect ScreenRect(UINode node, Rect local, UIRoot root)
        {
            var min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            for (int i = 0; i < 4; i++)
            {
                var corner = new Vector2(i == 0 || i == 3 ? local.xMin : local.xMax, i < 2 ? local.yMin : local.yMax);
                var point = root.RootToScreen(node.LocalToRoot(corner));
                min = Vector2.Min(min, point);
                max = Vector2.Max(max, point);
            }
            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }
    }
}
