## [Unreleased]

### Removed

- `TextFieldText.MinWidth`, with UI Next's `IMeasurable.MinWidth`.

## [0.2.0] - 13/10/2026

### Changed

- Lays text out with UI Next Text's rule-driven layout, which takes no `InlineContext`; a text field's text is plain, as it was.

## [0.1.0] - 12/10/2026

UI Text Input's platform layer, moved over to UI Next, with UI Next's text field.

### Added

- `TextField` for UI Next (moved here from UI Next, which had it in its TextInput assembly), typed into with the platform's own keyboard, its text drawn by UI Next Text: caret, selection and handles, the platform's edit menu, undo, `MaxLength`, single and multi-line, right-to-left text, and states its values follow (Focused while it is edited). `node.TextField(...)` makes one from code.
- `TextInputSystem`, connecting what is edited to a native input client: a hidden `UITextInput` view on iOS, an `InputConnection` on Android, the Input System and the IME on desktop and in the editor. It tracks the software keyboard and hands its height to UI Next's `UIRoot.KeyboardHeight`, and sends the field's place on screen once UI Next's frame has drawn it.
- The editing model (`TextEditingValue`, `TextSelection`, `TextRange`, `TextEditHistory`, `TextBoundaries`), `TextInputConfig` (content type, return key, capitalisation, autocorrection) and **Timbo Jimbo > UI Next > Simulate Soft Keyboard**.

### Coming from UI Text Input

- The namespace is `TimboJimbo.UINext.TextInput` (the field stays in `TimboJimbo.UINext`), its assemblies `TimboJimbo.UINext.TextInput`, `TimboJimboEditor.UINext.TextInput` and `TimboJimboTests.UINext.TextInput`.
- The native side is renamed so a build can hold UI Text Input's as well: the Java package is `com.timbojimbo.uinext.textinput` (keep it in a minified build: `-keep class com.timbojimbo.uinext.textinput.** { *; }`), and the iOS classes and functions carry the `TJN` prefix. Every asset has a new GUID but the field's own, which scenes keep.
- It takes UI Text Input's last fixes to the parts it keeps: iOS replaceRange keeps the selection, Android's late hide, iOS Escape, undo runs and a re-entrant Begin.
- Left behind: the UGUI `TextField`, its builder and its handles.
