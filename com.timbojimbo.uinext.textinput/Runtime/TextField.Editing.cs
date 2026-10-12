using System;
using System.Text;
using TimboJimbo.UINext.Text;
using TimboJimbo.UINext.TextInput;
using UnityEngine;

namespace TimboJimbo.UINext
{
    // The editing keys (TextEditIntent) and what the field lets in: line breaks and the length limit. Carried over from
    // UI Next Text Input's own field, whose behaviour this keeps: only how it asks its layout changed.
    public sealed partial class TextField
    {
        // A key from a hardware or desktop keyboard, carried out with the field's own layout, so a word, a line and a
        // page are what it shows. Moves leave the selection's base where it is when `extend` (shift is held) and collapse
        // it otherwise; deletes take what is selected, or else from the caret to where the key reaches. A secure field
        // shows no words, so a word's moves and deletes go to the end of the text, as a browser's password field does.
        // Left and right are where the arrows point on screen, as on iOS, macOS and Android: the caret goes to the place
        // beside it, and in text that reads right to left the word and the line's end towards the left are the next
        // ones. Backward and forward, start and end, are the text's own order.
        void ITextInputClient.PerformIntent(TextEditIntent intent, bool extend)
        {
            var value = Current;
            string text = value.Text;
            var selection = value.Selection;
            // Without shift, a move from a selection starts at the end of it the move goes towards.
            bool collapse = !extend && !selection.IsCollapsed;
            int start = collapse ? selection.Start : selection.Extent;
            int end = collapse ? selection.End : selection.Extent;
            int caret = selection.Extent;
            bool secure = _config.IsSecure;
            int WordBack(int from) => secure ? 0 : TextBoundaries.PreviousWordStart(text, from);
            int WordOn(int from) => secure ? text.Length : TextBoundaries.NextWordEnd(text, from);
            switch (intent)
            {
                case TextEditIntent.MoveLeft:
                    MoveAcross(toRight: false, collapse, extend);
                    break;
                case TextEditIntent.MoveRight:
                    MoveAcross(toRight: true, collapse, extend);
                    break;
                case TextEditIntent.MoveBackward:
                    Move(collapse ? start : TextBoundaries.PreviousCaretStop(text, caret), extend);
                    break;
                case TextEditIntent.MoveForward:
                    Move(collapse ? end : TextBoundaries.NextCaretStop(text, caret), extend);
                    break;
                case TextEditIntent.MoveUp:
                    MoveLines(start, -1, extend);
                    break;
                case TextEditIntent.MoveDown:
                    MoveLines(end, 1, extend);
                    break;
                case TextEditIntent.MovePageUp:
                    MoveLines(start, -PageLines(), extend);
                    break;
                case TextEditIntent.MovePageDown:
                    MoveLines(end, PageLines(), extend);
                    break;
                case TextEditIntent.MoveWordLeft:
                    Move(RightToLeft ? WordOn(end) : WordBack(start), extend);
                    break;
                case TextEditIntent.MoveWordRight:
                    Move(RightToLeft ? WordBack(start) : WordOn(end), extend);
                    break;
                case TextEditIntent.MoveLineStart:
                    Move(LineStart(start), extend);
                    break;
                case TextEditIntent.MoveLineEnd:
                    MoveToLineEnd(end, extend);
                    break;
                case TextEditIntent.MoveLineLeft:
                    if (RightToLeft)
                        MoveToLineEnd(end, extend);
                    else
                        Move(LineStart(start), extend);
                    break;
                case TextEditIntent.MoveLineRight:
                    if (RightToLeft)
                        Move(LineStart(start), extend);
                    else
                        MoveToLineEnd(end, extend);
                    break;
                case TextEditIntent.MoveDocumentStart:
                    Move(0, extend);
                    break;
                case TextEditIntent.MoveDocumentEnd:
                    Move(text.Length, extend);
                    break;
                case TextEditIntent.DeleteBackward:
                    // The whole of an emoji, or the last code point of anything else (a Thai or Devanagari vowel sign
                    // alone), as UIKit and Flutter do.
                    DeleteBack(caret > 0 ? TextBoundaries.DeleteBackwardStart(text, caret) : caret, coalesce: true);
                    break;
                case TextEditIntent.DeleteForward:
                    DeleteOn(TextBoundaries.NextCaretStop(text, caret), coalesce: true);
                    break;
                case TextEditIntent.DeleteWordBackward:
                    DeleteBack(WordBack(caret), coalesce: false);
                    break;
                case TextEditIntent.DeleteWordForward:
                    DeleteOn(WordOn(caret), coalesce: false);
                    break;
                case TextEditIntent.DeleteToLineStart:
                {
                    // At the start of a line already, it joins the line to the one before.
                    int from = LineStart(caret);
                    if (from == caret && caret > 0)
                        from = TextBoundaries.DeleteBackwardStart(text, caret);
                    DeleteBack(from, coalesce: false);
                    break;
                }
                case TextEditIntent.DeleteToLineEnd:
                {
                    int to = LineEnd(caret);
                    if (to == caret)
                        to = TextBoundaries.NextCaretStop(text, caret);
                    DeleteOn(to, coalesce: false);
                    break;
                }
                case TextEditIntent.SelectAll:
                    SetSelection(new TextSelection(0, text.Length));
                    break;
                case TextEditIntent.Copy:
                    Copy();
                    break;
                case TextEditIntent.Cut:
                    if (Copy())
                        Delete(Current.Selection.Range, coalesce: false);
                    break;
                case TextEditIntent.Paste:
                    Put(Clean(GUIUtility.systemCopyBuffer), coalesce: false);
                    break;
                case TextEditIntent.Undo:
                    Restore(undo: true);
                    break;
                case TextEditIntent.Redo:
                    Restore(undo: false);
                    break;
                case TextEditIntent.Newline:
                    // Shift and return: a new line whatever the return key does; a single line has none, and submits.
                    if (Multiline)
                        Put("\n", coalesce: true);
                    else
                        Submit();
                    break;
                case TextEditIntent.Return:
                    if (_config.ReturnInsertsNewline)
                        Put("\n", coalesce: true);
                    else
                        Submit();
                    break;
                case TextEditIntent.Cancel:
                    EndEditing();
                    break;
            }
        }

        // ── Moving ───────────────────────────────────────────────────────────────

        // `upstream`: the caret's affinity where it lands (see _upstream).
        private void Move(int to, bool extend, bool upstream = false)
        {
            var selection = Current.Selection;
            SetSelection(extend ? new TextSelection(selection.Base, to) : TextSelection.Collapsed(to), upstream);
        }

        // The left or the right arrow: the caret (with shift, the selection's extent) to the place beside it on screen,
        // which in text that reads both ways need not be the next index, and past either end of a line to the line before
        // or after, as the text reads; without shift, a selection collapses to its end on that side. With no text laid
        // out, back or on a character.
        private void MoveAcross(bool toRight, bool collapse, bool extend)
        {
            var selection = Current.Selection;
            int extent = selection.Extent;
            var layout = Layout;
            if (layout == null)
            {
                var text = Current.Text;
                if (collapse)
                    Move(toRight ? selection.End : selection.Start, extend);
                else
                    Move(toRight ? TextBoundaries.NextCaretStop(text, extent) : TextBoundaries.PreviousCaretStop(text, extent), extend);
                return;
            }
            if (collapse)
            {
                int side = SelectionSide(layout, selection, toRight);
                Move(side, extend: false, UpstreamAt(side));
                return;
            }
            int beside = layout.GetVisualNeighbour(extent, UpstreamAt(extent), toRight, out bool upstream);
            // The layout stops before each character it draws, which can be inside one of ours (a secure field shows a
            // bullet for each UTF-16 unit of an emoji): such a place is taken on to the end of our character the move
            // goes towards in the text, so the caret gets past it.
            int to = Snap(beside);
            if (to < beside && beside > extent)
                to = TextBoundaries.NextCaretStop(Current.Text, to);
            Move(to, extend, upstream);
        }

        // The end of a selection that is on the left of the other on screen, or on the right for `right`: by where their
        // carets stand when both are on one line, otherwise by the way the text reads, its start on the left of text that
        // reads left to right.
        private int SelectionSide(TextLayout layout, TextSelection selection, bool right)
        {
            int start = selection.Start, end = selection.End;
            bool startOnLeft = !RightToLeft;
            bool startUpstream = UpstreamAt(start), endUpstream = UpstreamAt(end);
            if (layout.GetLineAt(start, startUpstream) == layout.GetLineAt(end, endUpstream))
            {
                float startX = layout.GetCaretRect(start, startUpstream).x, endX = layout.GetCaretRect(end, endUpstream).x;
                if (!Mathf.Approximately(startX, endX))
                    startOnLeft = startX < endX;
            }
            return startOnLeft != right ? start : end;
        }

        // The end of the line `from` is on. The end of a line that wraps is where the next starts: the caret stays on this
        // line, upstream.
        private void MoveToLineEnd(int from, bool extend)
        {
            int to = LineEnd(from);
            Move(to, extend, WrapsAt(to));
        }

        // Up or down `lines` lines from `from`, keeping to the x the caret had as the run of moves up and down began, so it
        // comes back to its column after a shorter line (at the end of a wrapped one past whose end that x is); past the
        // first line it goes to the start, past the last to the end, as on a Mac.
        private void MoveLines(int from, int lines, bool extend)
        {
            var layout = Layout;
            if (layout == null)
            {
                Move(lines < 0 ? 0 : Current.Text.Length, extend);
                return;
            }
            bool fromUpstream = UpstreamAt(from);
            if (float.IsNaN(_goalX))
                _goalX = layout.GetCaretRect(from, fromUpstream).x;
            float goal = _goalX;
            int line = layout.GetLineAt(from, fromUpstream) + lines;
            int to;
            bool upstream = false;
            if (line < 0)
                to = 0;
            else if (line >= layout.LineCount)
                to = Current.Text.Length;
            else
                to = Snap(layout.GetIndexAt(new Vector2(goal, layout.GetCaretRect(layout.GetLineStart(line)).center.y), out upstream));
            Move(to, extend, upstream);
            _goalX = goal;
        }

        // How many lines a page up or down moves: as many as the field shows whole, at least one.
        private int PageLines()
        {
            float line = LineHeight;
            if (line <= 0f) return 1;
            return Mathf.Max(1, Mathf.FloorToInt(Node.Size.y / line));
        }

        // The start and the end of the line `index` is on: the caret's line, by its affinity, when it is the caret.
        private int LineStart(int index)
        {
            var layout = Layout;
            if (layout == null) return 0;
            return layout.GetLineStart(layout.GetLineAt(index, UpstreamAt(index)));
        }

        private int LineEnd(int index)
        {
            var layout = Layout;
            if (layout == null) return Current.Text.Length;
            return layout.GetLineEnd(layout.GetLineAt(index, UpstreamAt(index)));
        }

        // Whether a line wraps at `index`, which then ends that line and starts the next, as the text was last laid out.
        private bool WrapsAt(int index)
        {
            var layout = Layout;
            return layout != null && layout.GetLineAt(index, upstream: true) != layout.GetLineAt(index);
        }

        // Whether the text reads right to left as it is to be shown: found from the value rather than the last layout,
        // which the value can be ahead of.
        private bool RightToLeft => TextLayout.ReadsRightToLeft(DisplayOf(Current.Text),
            _editing.Peek && TextInputSystem.KeyboardRightToLeft && !_config.IsSecure ? TextDirection.AutoRightToLeft : TextDirection.Auto);

        // An index the layout found, as a place the caret can stop in the text (not inside an emoji).
        private int Snap(int index)
        {
            var text = Current.Text;
            return TextBoundaries.SnapToCaretStop(text, Mathf.Clamp(index, 0, text.Length));
        }

        // ── Deleting ─────────────────────────────────────────────────────────────

        // Deletes what is selected, or else from `from` up to the caret. Backspacing a character at a time is typing:
        // its steps join the run before for undo.
        private void DeleteBack(int from, bool coalesce)
        {
            var selection = Current.Selection;
            if (selection.IsCollapsed)
                Delete(new TextRange(from, selection.Extent), coalesce);
            else
                Delete(selection.Range, coalesce: false);
        }

        // Deletes what is selected, or else from the caret up to `to`.
        private void DeleteOn(int to, bool coalesce)
        {
            var selection = Current.Selection;
            if (selection.IsCollapsed)
                Delete(new TextRange(selection.Extent, to), coalesce);
            else
                Delete(selection.Range, coalesce: false);
        }

        private void Delete(TextRange range, bool coalesce)
        {
            if (!range.IsValid || range.IsEmpty) return;
            Change(Current.Replace(range, string.Empty), record: true, coalesce: coalesce, notify: true);
        }

        // ── The clipboard, undo and return ───────────────────────────────────────

        // Copies what is selected; nothing for a secure field, whose text never leaves it, as a browser's password field.
        private bool Copy()
        {
            var selection = Current.Selection;
            if (_config.IsSecure || selection.IsCollapsed) return false;
            GUIUtility.systemCopyBuffer = Current.Text.Substring(selection.Start, selection.End - selection.Start);
            return true;
        }

        // Undo or redo. Undo during a composition takes the composition back first (the history does).
        private void Restore(bool undo)
        {
            TextEditingValue restored;
            bool found = undo ? _history.TryUndo(Current, out restored) : _history.TryRedo(Current, out restored);
            if (found)
                Change(restored, record: false, coalesce: false, notify: true);
        }

        private void Submit()
        {
            if (Current.IsComposing)
                Change(Committed(Current.Selection), record: true, coalesce: false, notify: true);
            _submitted.Invoke(Current.Text);
            // A handler may have begun editing another field, which ended this one already.
            if (_editing.Peek && _config.ReturnKey != TextReturnKey.Send)
                EndEditing();
        }

        // ── What the field lets in ───────────────────────────────────────────────

        // Text from outside (pasted, inserted, set from code, typed): line breaks made "\n", as browsers make a textarea's,
        // and taken out of a single line, as HTML strips them from a text input.
        private string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            if (text.IndexOf('\r') >= 0)
                text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            if (!Multiline && text.IndexOf('\n') >= 0)
                text = text.Replace("\n", string.Empty);
            return text;
        }

        // A single-line field's value with any line breaks taken out (pasted through the platform's edit menu, say), its
        // selection kept on the same characters. A composition is left as it is: it is the keyboard's until committed.
        private TextEditingValue Flatten(in TextEditingValue value)
        {
            var text = value.Text;
            if (Multiline || value.IsComposing || (text.IndexOf('\n') < 0 && text.IndexOf('\r') < 0)) return value;
            var kept = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                if (!IsBreak(text[i]))
                    kept.Append(text[i]);
            }
            var selection = value.Selection;
            return new TextEditingValue(kept.ToString(), new TextSelection(Kept(text, selection.Base), Kept(text, selection.Extent)), TextRange.None);
        }

        private static bool IsBreak(char c) => c == '\n' || c == '\r';

        // Where `index` is once the line breaks before it are taken out.
        private static int Kept(string text, int index)
        {
            int kept = index;
            for (int i = 0; i < index && i < text.Length; i++)
            {
                if (IsBreak(text[i]))
                    kept--;
            }
            return kept;
        }

        // `text` cut short, at a whole character, so that putting it in place of `range` in `value` keeps the text within
        // MaxLength.
        private string Fit(in TextEditingValue value, TextRange range, string text)
        {
            if (_maxLength <= 0) return text;
            int room = _maxLength - (value.Text.Length - range.Length);
            if (text.Length <= room) return text;
            return room <= 0 ? string.Empty : text.Substring(0, TextBoundaries.SnapToCaretStop(text, room));
        }

        // What the keyboard made of `before` (or what committing its composition made of it), with what was put in cut
        // short, at a whole character, so that the text stays within MaxLength: as a browser's maxlength does with a paste,
        // and as Flutter's limit does once a composition is committed. A composition under way is left to the keyboard,
        // uncut, but only it: what one edit put in before it (a keyboard committing a word and starting the next in one
        // go) is cut as typing is. What was put in is what lies between the text the two share at the start and at the
        // end. The start shared goes no further than where the selection started, where typing puts things in, so a
        // letter typed beside the same letter is taken as typed where the caret was. A composition being committed was
        // let through uncut while it was under way, so all of it counts as put in. What follows what was put in moves
        // back by what was cut (a composition under way with it), and a caret inside what was cut comes to its end.
        private TextEditingValue Limit(in TextEditingValue before, in TextEditingValue after)
        {
            string old = before.Text, now = after.Text;
            bool committing = before.IsComposing;
            var composing = after.Composing;
            int exempt = after.IsComposing ? composing.Length : 0;
            if (_maxLength <= 0 || now.Length - exempt <= _maxLength || (!committing && now.Length <= old.Length))
                return after;
            var at = committing ? before.Composing : before.Selection.Range;
            int shortest = Math.Min(old.Length, now.Length);
            int prefixMost = Math.Min(shortest, Math.Max(0, at.Start));
            if (after.IsComposing)
                prefixMost = Math.Min(prefixMost, composing.Start);
            int prefix = 0;
            while (prefix < prefixMost && old[prefix] == now[prefix])
                prefix++;
            int suffixMost = committing ? Math.Min(shortest - prefix, old.Length - at.End) : shortest - prefix;
            if (after.IsComposing)
                suffixMost = Math.Min(suffixMost, now.Length - composing.End);
            int suffix = 0;
            while (suffix < suffixMost && old[old.Length - 1 - suffix] == now[now.Length - 1 - suffix])
                suffix++;
            // What may be cut: what was put in, up to the composition under way, which stays whole.
            int end = now.Length - suffix;
            if (after.IsComposing)
                end = Math.Max(prefix, Math.Min(end, composing.Start));
            int room = Math.Max(0, _maxLength - (now.Length - exempt - (end - prefix)));
            int keptEnd = prefix + Math.Min(room, end - prefix);
            keptEnd = Math.Max(prefix, TextBoundaries.SnapToCaretStop(now, keptEnd));
            int cut = end - keptEnd;
            if (cut <= 0) return after;
            int Map(int index) => index <= keptEnd ? index : Math.Max(keptEnd, index - cut);
            var selection = after.Selection;
            var kept = after.IsComposing ? new TextRange(composing.Start - cut, composing.End - cut) : TextRange.None;
            return new TextEditingValue(now.Remove(keptEnd, cut), new TextSelection(Map(selection.Base), Map(selection.Extent)), kept);
        }
    }
}
