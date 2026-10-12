// The bridge between IosTextInputBackend.cs and UIKit: sessions, the proxy views, the host view they live in, and the
// callbacks into C#.
//
// A session is one proxy view (TJNTextInputView), made when C# attaches and made first responder; UIKit keys its
// per-document keyboard state to the view, so a new field gets a new one. Switching fields (a detach and an attach in the
// same frame) keeps the keyboard up: the new proxy becomes first responder while the old one still is, and only then is
// the old one removed, so the keyboard never sees a moment without a responder. A detach on its own hides the keyboard
// at the end of the run loop turn, once the frame that detached is done.

#import "TJNTextInputInternal.h"
#import "UnityAppController.h"

#import <QuartzCore/QuartzCore.h>

#include <vector>

extern "C" UIView* UnityGetGLView(void);

// Holds the proxies, over the whole Unity view so that their geometry is the Unity view's. Touches pass through to the
// Unity view, and VoiceOver does not see the proxies (FlutterTextInputViewAccessibilityHider).
@interface TJNTextInputHost : UIView
@end

@implementation TJNTextInputHost

- (UIView*)hitTest:(CGPoint)point withEvent:(UIEvent*)event
{
    return nil;
}

@end

static TJNTICallbacks s_callbacks;
static BOOL s_initialised;
static TJNKeyboardTracker* s_tracker;
static TJNTextInputHost* s_host;

// The proxy of the session under way, and one detached this run loop turn that is still first responder until the turn
// ends (or until a new session takes the keyboard from it).
static TJNTextInputView* s_proxy;
static TJNTextInputView* s_leaving;

// How deep we are in calls into C#, and how many calls from C# wait on the main queue. A call from C# made while C# is
// being called (re-entrantly, from inside a UIKit call) waits until UIKit's call is over; later calls queue behind it,
// so that they are still applied in the order they were made.
static int s_callbackDepth;
static int s_waiting;

static void TJNRunInOrder(dispatch_block_t block)
{
    if (s_callbackDepth == 0 && s_waiting == 0)
    {
        block();
        return;
    }
    s_waiting++;
    dispatch_async(dispatch_get_main_queue(), ^{
        s_waiting--;
        block();
    });
}

static NSString* TJNString(const uint16_t* text, int32_t length)
{
    if (text == NULL || length <= 0)
        return @"";
    return [[NSString alloc] initWithCharacters:(const unichar*)text length:(NSUInteger)length];
}

static CGRect TJNCGRect(TJNTIRect rect)
{
    return CGRectMake(rect.x, rect.y, rect.width, rect.height);
}

static TJNTextInputView* TJNViewFor(int32_t session)
{
    return s_proxy != nil && s_proxy.session == session ? s_proxy : nil;
}

UIView* TJNTIHostView(void)
{
    UIView* unityView = UnityGetGLView();
    if (unityView == nil)
        return nil;
    if (s_host == nil)
    {
        s_host = [[TJNTextInputHost alloc] initWithFrame:unityView.bounds];
        s_host.opaque = NO;
        s_host.backgroundColor = nil;
        s_host.accessibilityElementsHidden = YES;
        s_host.autoresizingMask = UIViewAutoresizingFlexibleWidth | UIViewAutoresizingFlexibleHeight;
    }
    if (s_host.superview != unityView)
    {
        s_host.frame = unityView.bounds;
        [unityView addSubview:s_host];
    }
    return s_host;
}

TJNTextInputView* TJNTIActiveView(void)
{
    return s_proxy;
}

#pragma mark - Into C#

void TJNTIReportEdit(int32_t session, int32_t baseSerial, NSString* text, NSInteger selectionBase, NSInteger selectionExtent,
                    NSRange marked)
{
    if (!s_initialised)
        return;
    static std::vector<unichar> buffer;
    NSUInteger length = text.length;
    buffer.resize(length + 1);
    [text getCharacters:buffer.data() range:NSMakeRange(0, length)];
    BOOL composing = marked.location != NSNotFound;
    s_callbackDepth++;
    s_callbacks.onEdit(session, baseSerial, (const uint16_t*)buffer.data(), (int32_t)length, (int32_t)selectionBase,
                       (int32_t)selectionExtent, composing ? (int32_t)marked.location : -1,
                       composing ? (int32_t)NSMaxRange(marked) : -1);
    s_callbackDepth--;
}

void TJNTIReportIntent(int32_t session, int32_t intent, BOOL extend)
{
    if (!s_initialised)
        return;
    s_callbackDepth++;
    s_callbacks.onIntent(session, intent, extend ? 1 : 0);
    s_callbackDepth--;
}

void TJNTIReportEnded(int32_t session)
{
    if (!s_initialised)
        return;
    s_callbackDepth++;
    s_callbacks.onEnded(session);
    s_callbackDepth--;
}

void TJNTIReportKeyboard(const TJNTIKeyboardEvent* keyboard)
{
    if (!s_initialised)
        return;
    s_callbackDepth++;
    s_callbacks.onKeyboard(keyboard);
    s_callbackDepth--;
}

void TJNTIReportKeyboardDirection(BOOL rightToLeft)
{
    if (!s_initialised)
        return;
    s_callbackDepth++;
    s_callbacks.onKeyboardDirection(rightToLeft ? 1 : 0);
    s_callbackDepth--;
}

#pragma mark - Sessions

// `proxy`'s session is over: it stops reporting at once, and gives the keyboard up at the end of this run loop turn
// unless a new session has taken it by then.
static void TJNLeave(TJNTextInputView* proxy)
{
    [proxy deactivate];
    s_leaving = proxy;
    dispatch_async(dispatch_get_main_queue(), ^{
        if (s_leaving != proxy)
            return;
        s_leaving = nil;
        [proxy resignFirstResponder];
        [proxy removeFromSuperview];
    });
}

static void TJNAttach(int32_t session, const TJNTIConfig* config, NSString* text, int32_t selectionBase,
                     int32_t selectionExtent, int32_t composingStart, int32_t composingEnd, int32_t serial)
{
    UIView* host = TJNTIHostView();
    if (host == nil)
        return;
    if (s_proxy != nil)
    {
        TJNLeave(s_proxy);
        s_proxy = nil;
    }

    TJNTextInputView* proxy = [[TJNTextInputView alloc] initWithSession:session config:config];
    [proxy pushText:text
      selectionBase:selectionBase
    selectionExtent:selectionExtent
     composingStart:composingStart
       composingEnd:composingEnd
             serial:serial
             notify:NO];
    proxy.frame = host.bounds;
    [host addSubview:proxy];
    s_proxy = proxy;

    BOOL focused = [proxy becomeFirstResponder];
    // Taking first responder made the old proxy resign; only now can it go without the keyboard noticing.
    if (s_leaving != nil)
    {
        [s_leaving removeFromSuperview];
        s_leaving = nil;
    }
    if (!focused)
        TJNTIReportEnded(session);
}

#pragma mark - From C#

extern "C" int32_t TJNTI_Init(const TJNTICallbacks* callbacks, int32_t callbacksSize, int32_t configSize,
                             int32_t keyboardEventSize)
{
    if (callbacksSize != (int32_t)sizeof(TJNTICallbacks) || configSize != (int32_t)sizeof(TJNTIConfig)
        || keyboardEventSize != (int32_t)sizeof(TJNTIKeyboardEvent))
    {
        NSLog(@"TJNTextInput: the C# and native struct layouts differ (callbacks %d/%d, config %d/%d, keyboard event %d/%d).",
              callbacksSize, (int)sizeof(TJNTICallbacks), configSize, (int)sizeof(TJNTIConfig), keyboardEventSize,
              (int)sizeof(TJNTIKeyboardEvent));
        return 0;
    }
    s_callbacks = *callbacks;
    s_initialised = YES;
    if (s_tracker == nil)
        s_tracker = [[TJNKeyboardTracker alloc] init];
    else
        [s_tracker forget];
    return 1;
}

extern "C" void TJNTI_Attach(int32_t session, const TJNTIConfig* config, const uint16_t* text, int32_t length,
                            int32_t selectionBase, int32_t selectionExtent, int32_t composingStart, int32_t composingEnd,
                            int32_t serial)
{
    TJNTIConfig configCopy = *config;
    NSString* string = TJNString(text, length);
    TJNRunInOrder(^{
        if (s_initialised)
            TJNAttach(session, &configCopy, string, selectionBase, selectionExtent, composingStart, composingEnd, serial);
    });
}

extern "C" void TJNTI_Detach(int32_t session)
{
    TJNRunInOrder(^{
        TJNTextInputView* proxy = TJNViewFor(session);
        if (proxy == nil)
            return;
        s_proxy = nil;
        TJNLeave(proxy);
    });
}

extern "C" void TJNTI_SetConfig(int32_t session, const TJNTIConfig* config)
{
    TJNTIConfig configCopy = *config;
    TJNRunInOrder(^{
        TJNTextInputView* proxy = TJNViewFor(session);
        if (proxy == nil)
            return;
        [proxy applyConfig:&configCopy];
        // Traits are read when the keyboard comes up; a keyboard already up reads them again here.
        if (proxy.isFirstResponder)
            [proxy reloadInputViews];
    });
}

extern "C" void TJNTI_SetValue(int32_t session, int32_t serial, const uint16_t* text, int32_t length, int32_t selectionBase,
                              int32_t selectionExtent, int32_t composingStart, int32_t composingEnd)
{
    NSString* string = TJNString(text, length);
    TJNRunInOrder(^{
        [TJNViewFor(session) pushText:string
                       selectionBase:selectionBase
                     selectionExtent:selectionExtent
                      composingStart:composingStart
                        composingEnd:composingEnd
                              serial:serial
                              notify:YES];
    });
}

extern "C" void TJNTI_SetGeometry(int32_t session, TJNTIRect caret, TJNTIRect composing, int32_t hasComposing)
{
    TJNRunInOrder(^{
        [TJNViewFor(session) setCaret:TJNCGRect(caret) composing:TJNCGRect(composing) hasComposing:hasComposing != 0];
    });
}

extern "C" void TJNTI_SetDirection(int32_t session, int32_t rightToLeft, int32_t caretRightToLeft)
{
    TJNRunInOrder(^{
        [TJNViewFor(session) setRightToLeft:rightToLeft != 0];
    });
}

extern "C" void TJNTI_ShowEditMenu(int32_t session, TJNTIRect target, int32_t actions)
{
    TJNRunInOrder(^{
        [TJNViewFor(session) showEditMenuAt:TJNCGRect(target) actions:actions];
    });
}

extern "C" void TJNTI_HideEditMenu(void)
{
    TJNRunInOrder(^{
        [s_proxy hideEditMenu];
    });
}

// Core Animation draws the keyboard as its animation stands when each frame is shown, so the keyboard is evaluated for
// when the frame Unity is making now will be shown. Unity makes its frames in its display link's callback, which this is
// asked during, at the pace that link is set to (Application.targetFrameRate, or Unity's default), not the screen's
// fastest.
extern "C" double TJNTI_PresentationTime(void)
{
    UnityAppController* controller = GetAppController();
#if UNITY_USES_METAL_DISPLAY_LINK
    if (@available(iOS 17.0, *))
    {
        if (controller.unityUsesMetalDisplayLink)
        {
            // A CAMetalDisplayLink calls back preferredFrameLatency of its frames ahead of showing the frame it asks
            // for; its rate is the one Unity sets (UnityAppController+Rendering.mm callbackFramerateChange:), and a
            // preferred rate of 0 (CAFrameRateRangeDefault) is the screen's own.
            CAMetalDisplayLink* link = controller.unityMetalDisplayLink;
            double rate = link.preferredFrameRateRange.preferred;
            if (rate <= 0)
                rate = UnityGetGLView().window.windowScene.screen.maximumFramesPerSecond;
            return CACurrentMediaTime() + link.preferredFrameLatency / rate;
        }
    }
#endif
    // A CADisplayLink's target timestamp is when the frame made in its callback is shown.
    return controller.unityDisplayLink.targetTimestamp;
}

extern "C" void TJNTI_GetViewSize(float* width, float* height)
{
    CGSize size = UnityGetGLView().bounds.size;
    *width = (float)size.width;
    *height = (float)size.height;
}
