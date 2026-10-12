// The C side of IosTextInputBackend.cs: what C# calls, and what native code calls back. The structs and constants here
// are mirrored field for field in that file; TJNTI_Init checks the struct sizes, so a mismatch is reported rather than
// read as garbage.
//
// Everything runs on the main thread: Unity's player loop runs there (from the display link), and so do UIKit's calls
// into the proxy view and its keyboard notifications. Text crosses as UTF-16 (a pointer and a length in code units), so
// indices mean the same as C#'s string indices and NSString's ranges. Rects are in the Unity view's points, origin
// top-left; C# converts from and to Unity's screen pixels (origin bottom-left).

#pragma once

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

// What the field holds (C#'s TextContentType).
enum
{
    TJNTIContentStandard = 0,
    TJNTIContentEmail = 1,
    TJNTIContentUrl = 2,
    TJNTIContentNumber = 3,
    TJNTIContentDecimal = 4,
    TJNTIContentPhone = 5,
    TJNTIContentName = 6,
    TJNTIContentUsername = 7,
    TJNTIContentPassword = 8,
    TJNTIContentNewPassword = 9,
    TJNTIContentOneTimeCode = 10,
    TJNTIContentSearch = 11,
};

// What the return key says (C#'s TextReturnKey).
enum
{
    TJNTIReturnDefault = 0,
    TJNTIReturnDone = 1,
    TJNTIReturnGo = 2,
    TJNTIReturnNext = 3,
    TJNTIReturnSearch = 4,
    TJNTIReturnSend = 5,
};

// Which letters start in capitals (C#'s TextCapitalization).
enum
{
    TJNTICapitalizeSentences = 0,
    TJNTICapitalizeNone = 1,
    TJNTICapitalizeWords = 2,
    TJNTICapitalizeCharacters = 3,
};

// What the edit menu offers (C#'s TextEditActions).
enum
{
    TJNTIActionCut = 1,
    TJNTIActionCopy = 2,
    TJNTIActionPaste = 4,
    TJNTIActionSelectAll = 8,
};

// What the proxy reports besides edits: the return key, and the hardware keyboard's keys that need the field's own
// layout (left and right as the text shows on screen, by a character, a word or to the line's end; a line up or down)
// or its own history (undo), and Escape, which ends editing.
enum
{
    TJNTIIntentReturn = 0,
    TJNTIIntentMoveLeft = 1,
    TJNTIIntentMoveRight = 2,
    TJNTIIntentMoveUp = 3,
    TJNTIIntentMoveDown = 4,
    TJNTIIntentMoveWordLeft = 5,
    TJNTIIntentMoveWordRight = 6,
    TJNTIIntentMoveLineLeft = 7,
    TJNTIIntentMoveLineRight = 8,
    TJNTIIntentUndo = 9,
    TJNTIIntentRedo = 10,
    TJNTIIntentCancel = 11,
};

// How the keyboard moves to where a keyboard event says it is going.
enum
{
    // It is there at once.
    TJNTICurveNone = 0,
    // A CASpringAnimation's damped spring: mass, stiffness, damping and initialVelocity.
    TJNTICurveSpring = 1,
    // A cubic Bézier timing curve through (c1x, c1y) and (c2x, c2y), as CAMediaTimingFunction's.
    TJNTICurveBezier = 2,
};

typedef struct TJNTIRect
{
    float x, y, width, height;
} TJNTIRect;

// What a field asks of the keyboard. The derived flags are worked out on the C# side (TextInputConfig), so both sides
// agree on what a config means.
typedef struct TJNTIConfig
{
    int32_t contentType;
    int32_t returnKey;
    int32_t capitalization;
    int32_t multiline;
    int32_t secure;
    int32_t correctsWords;
    int32_t returnInsertsNewline;
    int32_t enablesReturnKeyAutomatically;
} TJNTIConfig;

// The keyboard is moving (or has moved) to a new place. `inset` is how far up from the bottom of the Unity view a docked
// keyboard will cover it, in points; a floating or split keyboard covers nothing. The move starts at `startTime`
// (CACurrentMediaTime) and lasts `duration` seconds along `curve`.
typedef struct TJNTIKeyboardEvent
{
    double startTime;
    double duration;
    float inset;
    float viewHeight;
    float mass;
    float stiffness;
    float damping;
    float initialVelocity;
    float c1x, c1y, c2x, c2y;
    int32_t curve;
    int32_t visible;
} TJNTIKeyboardEvent;

// Native code calls these; each only queues what it is told on the C# side and returns. `text` is valid only during the
// call. A composing range of (-1, -1) is none.
typedef struct TJNTICallbacks
{
    // The keyboard changed the value (typing, autocorrect, marked text, dictation, the edit menu), based on the value
    // pushed with serial `baseSerial`.
    void (*onEdit)(int32_t session, int32_t baseSerial, const uint16_t* text, int32_t length,
                   int32_t selectionBase, int32_t selectionExtent, int32_t composingStart, int32_t composingEnd);
    // A TJNTIIntent; `extend`: shift is held.
    void (*onIntent)(int32_t session, int32_t intent, int32_t extend);
    // The session was ended by the system: the user dismissed the keyboard, or something else became first responder.
    void (*onEnded)(int32_t session);
    void (*onKeyboard)(const TJNTIKeyboardEvent* keyboard);
    // Whether the keyboard in use writes right to left (Arabic, Hebrew): told when a session's proxy takes the keyboard,
    // when the user switches keyboards, and when UIKit sets the text's direction to the keyboard's.
    void (*onKeyboardDirection)(int32_t rightToLeft);
} TJNTICallbacks;

// Registers the callbacks and starts following the keyboard. Returns 0, and does nothing, if the sizes C# passes for the
// structs differ from native's.
int32_t TJNTI_Init(const TJNTICallbacks* callbacks, int32_t callbacksSize, int32_t configSize, int32_t keyboardEventSize);

// A session starts: a new proxy view holding this value (pushed with serial `serial`) becomes first responder.
void TJNTI_Attach(int32_t session, const TJNTIConfig* config, const uint16_t* text, int32_t length,
                 int32_t selectionBase, int32_t selectionExtent, int32_t composingStart, int32_t composingEnd, int32_t serial);

// The session ends; the keyboard goes down at the end of this run loop turn, unless another session attaches first.
void TJNTI_Detach(int32_t session);

// The session's config changed: the keyboard is reloaded.
void TJNTI_SetConfig(int32_t session, const TJNTIConfig* config);

// C# changed the value itself: the keyboard is told what changed.
void TJNTI_SetValue(int32_t session, int32_t serial, const uint16_t* text, int32_t length,
                   int32_t selectionBase, int32_t selectionExtent, int32_t composingStart, int32_t composingEnd);

// Where the caret and the composing text are (points, top-left). `hasComposing` is 0 when there is no composing rect.
void TJNTI_SetGeometry(int32_t session, TJNTIRect caret, TJNTIRect composing, int32_t hasComposing);

// Which way the session's text reads, as C# laid it out. `rightToLeft` is the base writing direction UIKit is told, and
// which way left and right go when UIKit moves by them itself. `caretRightToLeft`, the direction of the character at the
// caret, is not used: none of the proxy's answers carries a character's own direction.
void TJNTI_SetDirection(int32_t session, int32_t rightToLeft, int32_t caretRightToLeft);

// Shows the system edit menu by `target` (points, top-left), offering the TJNTIAction flags in `actions`.
void TJNTI_ShowEditMenu(int32_t session, TJNTIRect target, int32_t actions);

void TJNTI_HideEditMenu(void);

// The media time (CACurrentMediaTime's clock) at which the frame being made now is expected on screen. Asked during
// Unity's frame, which runs in Unity's display link's callback.
double TJNTI_PresentationTime(void);

// The Unity view's size, in points.
void TJNTI_GetViewSize(float* width, float* height);

#ifdef __cplusplus
}
#endif
