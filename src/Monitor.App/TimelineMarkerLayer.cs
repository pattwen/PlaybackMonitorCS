using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using Monitor.Core;

namespace Monitor.App;


public sealed class TimelineMarkerLayer : IDisposable
{
    readonly Slider slider;
    readonly MarksAdorner adorner;
    AdornerLayer? layer;
    bool disposed;
    public TimelineMarkerLayer(Slider slider)
    {
        this.slider = slider;
        adorner = new(slider);
        slider.Loaded += Loaded;
        slider.Unloaded += Unloaded;
        slider.SizeChanged += Changed;
        if (slider.IsLoaded)
        {
            Attach();
        }
    }
    public void Update(IEnumerable<AlarmEvent> events, string sessionId, double duration, bool isLive)
    {
        slider.Dispatcher.VerifyAccess();
        adorner.Markers = TimelineMarkers.Create(events, sessionId, duration, isLive);
        adorner.InvalidateVisual();
    }
    public void UpdateEntries(IEnumerable<TimelineEntry> entries, double duration, bool isLive)
    {
        slider.Dispatcher.VerifyAccess();
        adorner.Markers = isLive || !double.IsFinite(duration) || duration <= 0 ? [] : entries
            .Where(e => double.IsFinite(e.Start) && e.Start >= 0 && e.Start <= duration)
            .Select(e => new TimelineMarker(e.Id, e.Kind, e.Start, e.Start / duration, Text.Label(e.Kind) + " · " + Text.Timecode(e.Start))).ToArray();
        adorner.InvalidateVisual();
    }
    void Attach()
    {
        if (disposed || layer != null)
        {
            return;
        }
        layer = AdornerLayer.GetAdornerLayer(slider);
        layer?.Add(adorner);
        adorner.InvalidateVisual();
    }
    void Loaded(object sender, RoutedEventArgs e) => Attach();
    void Unloaded(object sender, RoutedEventArgs e)
    {
        layer?.Remove(adorner);
        layer = null;
    }
    void Changed(object sender, SizeChangedEventArgs e) => adorner.InvalidateVisual();
    public void Dispose()
    {
        slider.Dispatcher.VerifyAccess();
        if (disposed)
        {
            return;
        }
        disposed = true;
        slider.Loaded -= Loaded;
        slider.Unloaded -= Unloaded;
        slider.SizeChanged -= Changed;
        layer?.Remove(adorner);
        layer = null;
    }
    sealed class MarksAdorner : Adorner
    {
        readonly Slider slider;
        readonly Pen pen = new(new SolidColorBrush(Color.FromRgb(255, 105, 120)), 2);
        public TimelineMarker[] Markers { get; set; } = [];
        public MarksAdorner(Slider slider) : base(slider) { this.slider = slider; IsHitTestVisible = false; pen.Freeze(); }
        protected override void OnRender(DrawingContext drawing)
        {
            if (Markers.Length == 0 || ActualWidth <= 0 || slider.Orientation != Orientation.Horizontal)
            {
                return;
            }
            double left = 7, usable = Math.Max(0, slider.ActualWidth - 14);
            if (slider.Template?.FindName("PART_Track", slider) is Track track && track.ActualWidth > 0)
            {
                double thumb = track.Thumb?.ActualWidth ?? 0;
                left = track.TransformToAncestor(slider).Transform(new Point()).X + thumb / 2;
                usable = Math.Max(0, track.ActualWidth - thumb);
            }
            bool reverse = slider.IsDirectionReversed ^ (slider.FlowDirection == FlowDirection.RightToLeft);
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            var painted = new HashSet<long>();
            foreach (var marker in Markers)
            {
                double x = left + usable * (reverse ? 1 - marker.Fraction : marker.Fraction);
                if (painted.Add((long)Math.Round(x * dpi)))
                {
                    drawing.DrawLine(pen, new Point(x, 0), new Point(x, Math.Min(6, slider.ActualHeight)));
                }
            }
        }
    }
}
