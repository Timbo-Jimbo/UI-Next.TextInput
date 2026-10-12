# Timbo Jimbo - UI Next Text Input

Text fields for [UI Next](https://github.com/Timbo-Jimbo/UI-Next), typed into with the platform's own keyboard, as browsers and Flutter do: autocorrect, the suggestion bar, composition for Chinese, Japanese and Korean, dictation and the system's copy and paste menu all go through the platform, while the field itself (its text, caret, selection and handles) is drawn and animated like the rest of the UI. The keyboard's height is handed to UI Next's layout, so a node that avoids it (`UINode.AvoidsKeyboard`, as UIKit's keyboard layout guide) keeps its content above the keyboard while its background reaches under it.

It is UI Text Input's platform layer, moved over to UI Next, with UI Next's own text field.

Requires Unity 6000.5 or newer, [UI Next](https://github.com/Timbo-Jimbo/UI-Next), [UI Next Text](https://github.com/Timbo-Jimbo/UI-Next.Text) and the Input System.

## Installation

Install UI Next Text and UI Next first, then open **Window > Package Manager**, click Add, choose **Add package from git URL...** and paste `https://github.com/Timbo-Jimbo/UI-Next.TextInput.git?path=com.timbojimbo.uinext.textinput#0.1.0`.

## How it works

`TextInputSystem` connects one thing being edited (an `ITextInputClient`, such as a `TextField`) to a small native input client:

- **iOS**: a hidden `UIView` adopting `UITextInput`, a subview of Unity's view and first responder while editing, new for each editing session. It answers the keyboard's questions (the text around the caret, the marked text, where the caret is on screen) from a mirror of the field's value, and reports the keyboard's edits back. It also tracks the keyboard's frame and the spring UIKit animates it on, so layout follows it exactly.
- **Android**: a 1×1 view in the player's layout whose `InputConnection` edits a mirror `Editable`. It works with both the GameActivity and the Activity entry points, tracks the keyboard's height frame by frame (`WindowInsetsAnimation` on Android 11 and up, the visible frame below), and hands hardware-keyboard keys to the Input System side.
- **Desktop and the editor**: the Input System's keyboard for typed text and editing keys (with the platform's shortcuts and key repeat), and the IME through `Input.imeCompositionMode`, `Input.compositionString` and `Input.compositionCursorPos`, as Unity's own UIs use it.

The field owns the value; the native side's mirror only answers the keyboard. What the keyboard changes reaches the field once a frame, early in it (`ITextInputClient.ApplyPlatformValue`); what the field changes itself (a tap moving the caret, text set from code, undo) is pushed to the mirror only when it differs from what the keyboard last saw, and the keyboard is told in the least disruptive way: a selection change, a text change, or, only for a composition changed from our side or text replaced as a whole, a restart. Restarting on every change, as Unity's own keyboard does, is what loses autocorrect's state. Once UI Next's frame has laid out and drawn everything, the field's place on screen is sent on, for the keyboard's candidate windows and edit menu.

## Usage

```csharp
var field = row.Child("Message").TextField("Message", 16f, multiline: true, maxLines: 5);
field.Node.Width.Value = Sizing.Grow();
field.Config = new TextInputConfig { ReturnKey = TextReturnKey.Send, Multiline = true };
field.Submitted.AddListener(text => Send(text));
field.TextChanged.AddListener(text => sendButton.Display.Value = text.Length > 0 ? DisplayMode.Visible : DisplayMode.None);
```

- **`TextField`** takes focus as a button does: Enter or a gamepad's A begins editing, as do a tap, a click and `BeginEditing()`. While it is edited it is focus's first responder, and the keys are its own bar Tab. Escape, a gamepad's B, the return key (unless it says Send), the keyboard being dismissed, the field being hidden and `EndEditing()` end it, and so does a mouse press elsewhere, as a browser blurs a field. A touch elsewhere does not, as on iOS: a chat's Send button can be tapped with the keyboard up.
- **`TextInputConfig`** picks the keyboard: `ContentType` (text, email, URL, numbers, phone, name, user name, password, new password, one-time code, search), `ReturnKey`, `Multiline`, `Capitalization`, `Autocorrection`.
- A single-line field is one line high and scrolls sideways to keep the caret in view. A multi-line one grows with its lines, on a quick spring, up to `maxLines`, then scrolls.
- Its colours, font and placeholder are values with variants, so they follow its states: Focused while it is edited, Disabled, Hovered.
- **Touch**: a tap places the caret, a double tap or a long press selects a word, handles adjust the selection, and the platform's edit menu does the rest. **Mouse**: click, drag, double and triple click, shift-click. **Keys**: the platform's editing shortcuts, clipboard and undo.
- `MaxLength` limits typing and pasting, after a composition is committed rather than in the middle of one.
- **Right to left**: the text reads the way its first letter does and starts at that side, as an HTML input with `dir="auto"` does; with no letter yet it reads the way the keyboard in use writes, as on iOS, bar a password, which always reads left to right. The arrows move the caret to the place beside it on screen, as on iOS, macOS and Android.
- **Keyboard avoidance**: `TextInputSystem.KeyboardHeight` is handed to `UIRoot.KeyboardHeight`, and a node that opts in (`UINode.AvoidsKeyboard`, on the page or sheet holding the field) keeps its content above the keyboard; nothing else moves. The nearest scroll view above an editing field brings it into view as the keyboard rises.
- `TextInputSystem.IsEditing` and `ClientChanged` tell game code when to turn its own key bindings off.
- **Trying it in the editor**: **Timbo Jimbo > UI Next > Simulate Soft Keyboard** pretends a keyboard slides up while a field is edited, so layouts that make room for it can be tried in the Game view or the Device Simulator.

## Platforms

- **iOS**: Objective-C++ in `Runtime/Plugins/iOS`, compiled into UnityFramework. iOS 15 or newer. Its classes and functions carry the `TJN` prefix, so a build can hold UI Text Input's too.
- **Android**: Java in `Runtime/Plugins/Android`, compiled into the player's library. Android 8 (API 26) or newer, no extra Gradle dependencies. The classes are reached only through JNI, so a minified build must keep them: add `-keep class com.timbojimbo.uinext.textinput.** { *; }` to `proguard-user.txt`.
- Unity's own `TouchScreenKeyboard`, uGUI's `InputField` and `TMP_InputField` must not be opened while a field is being edited: each takes the keyboard for its own native field.

## Known limits

- Built without a device to hand: everything native needs trying on iOS and Android devices with real keyboards (Gboard, Samsung, SwiftKey; Japanese, Chinese, Korean; dictation; hardware keyboards).
- iOS: the keyboard's questions about where characters are on screen are answered from the caret and the composing range rather than each character, so the autocorrect highlight and the spacebar trackpad are approximate. Scribble is off. The iPad's shortcut-bar undo buttons and shake to undo don't reach the field's history (Cmd+Z does).
- Android: no rich content from the keyboard (GIFs, stickers), no stylus handwriting into the field, and the field's placeholder is not passed to the keyboard as a hint.
- Desktop: no edit menu (the shortcuts do its work); key repeat uses fixed timings rather than the system's; text with no letter yet reads left to right whatever the keyboard layout.
- Gamepads can begin and end editing but not move the caret.
- The whole text takes one direction, from its first letter, as an HTML input does: in a multi-line field, a paragraph in another script reads the first one's way.
- Secure fields show a bullet for each UTF-16 unit and never reveal the last character typed.
