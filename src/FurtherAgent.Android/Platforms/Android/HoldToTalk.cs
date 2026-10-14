using Android.Views;

namespace FurtherAgent.AndroidApp;

// Consume the native gesture so a release cannot also trigger a normal Clicked action.
internal sealed class HoldToTalk(Button button, Action begin, Action<bool> move, Action<bool> end)
{
    private global::Android.Views.View? native;
    private bool active;
    private float startY;
    private bool discard;
    public void Attach()
    {
        button.HandlerChanged += (_, _) => Bind();
        button.HandlerChanging += (_, _) => { if (active) Finish(true); if (native is not null) native.Touch -= Touch; native = null; };
        Bind();
    }
    private void Bind()
    {
        if (native is not null) native.Touch -= Touch;
        native = button.Handler?.PlatformView as global::Android.Views.View;
        if (native is not null) native.Touch += Touch;
    }
    private void Touch(object? sender, global::Android.Views.View.TouchEventArgs args)
    {
        var touch = args.Event;
        if (touch is null) return;
        if (touch.ActionMasked == MotionEventActions.Down && button.IsEnabled)
        {
            active = true; discard = false; startY = touch.RawY;
            native?.Parent?.RequestDisallowInterceptTouchEvent(true);
            Ui.PressFeedback(button, true); begin();
        }
        else if (active && touch.ActionMasked == MotionEventActions.Move)
        {
            bool next = touch.RawY - startY > 60 * (native?.Resources?.DisplayMetrics?.Density ?? 1);
            if (next != discard) { discard = next; move(discard); }
        }
        else if (active && touch.ActionMasked is MotionEventActions.Up or MotionEventActions.Cancel)
            Finish(discard || touch.ActionMasked == MotionEventActions.Cancel);
        args.Handled = true;
    }
    private void Finish(bool cancel)
    {
        active = false; native?.Parent?.RequestDisallowInterceptTouchEvent(false);
        Ui.PressFeedback(button, false); end(cancel);
    }
}
