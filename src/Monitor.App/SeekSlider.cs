using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Monitor.App;

public enum SeekInputKind
{
    Track, Thumb, Keyboard
}
public sealed class SeekSliderEventArgs(double value, SeekInputKind kind) : EventArgs
{
    public double Value { get; } = value;
    public SeekInputKind Kind { get; } = kind;
}

public sealed class SeekSlider : Slider
{
    Track? track;
    bool interacting;
    SeekInputKind inputKind;
    public event EventHandler? UserInteractionStarted;
    public event EventHandler? UserInteractionCancelled;
    public event EventHandler<SeekSliderEventArgs>? SeekRequested;

    public SeekSlider()
    {
        IsMoveToPointEnabled = false;
        Unloaded += (_, _) => CancelInteraction();
        IsEnabledChanged += (_, _) => { if (!IsEnabled) CancelInteraction(); };
    }
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        track = GetTemplateChild("PART_Track") as Track;
    }
    void Begin(SeekInputKind kind)
    {
        if (interacting)
        {
            return;
        }
        interacting = true;
        inputKind = kind;
        UserInteractionStarted?.Invoke(this, EventArgs.Empty);
    }
    void Commit()
    {
        if (!interacting)
        {
            return;
        }
        interacting = false;
        SeekRequested?.Invoke(this, new(Value, inputKind));
    }
    public void CancelInteraction()
    {
        if (!interacting)
        {
            return;
        }
        interacting = false;
        UserInteractionCancelled?.Invoke(this, EventArgs.Empty);
    }
    bool FromThumb(object source)
    {
        for (DependencyObject? item = source as DependencyObject; item != null && !ReferenceEquals(item, this);)
        {
            if (ReferenceEquals(item, track?.Thumb))
            {
                return true;
            }
            item = item is Visual ? VisualTreeHelper.GetParent(item) : LogicalTreeHelper.GetParent(item);
        }
        return false;
    }
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!IsEnabled)
        {
            return;
        }
        if (FromThumb(e.OriginalSource))
        {
            Begin(SeekInputKind.Thumb);
            base.OnPreviewMouseLeftButtonDown(e);
            return;
        }
        Begin(SeekInputKind.Track);
        if (track == null)
        {
            CancelInteraction();
            return;
        }
        double target = track.ValueFromPoint(e.GetPosition(track));
        if (!double.IsFinite(target))
        {
            CancelInteraction();
            return;
        }
        SetCurrentValue(ValueProperty, Math.Clamp(target, Minimum, Maximum));
        e.Handled = true;
        Commit();
    }
    protected override void OnThumbDragStarted(DragStartedEventArgs e)
    {
        Begin(SeekInputKind.Thumb);
        base.OnThumbDragStarted(e);
    }
    protected override void OnThumbDragCompleted(DragCompletedEventArgs e)
    {
        base.OnThumbDragCompleted(e);
        if (e.Canceled)
        {
            CancelInteraction();
        }
        else
        {
            Commit();
        }
    }
    static bool SeekKey(Key key) => key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown;
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (IsEnabled && SeekKey(e.Key))
        {
            Begin(SeekInputKind.Keyboard);
        }
        base.OnPreviewKeyDown(e);
    }
    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        if (interacting && inputKind == SeekInputKind.Keyboard && SeekKey(e.Key))
        {
            Commit();
        }
    }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        if (interacting && inputKind == SeekInputKind.Keyboard)
        {
            CancelInteraction();
        }
        base.OnLostKeyboardFocus(e);
    }
}
