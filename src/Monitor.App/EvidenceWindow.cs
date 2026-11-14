using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Monitor.Core;
using Monitor.Engine;
namespace Monitor.App;

public sealed class EvidenceWindow : Window
{
    readonly VideoHost host = new(); readonly TextBlock state = new() { TextWrapping = TextWrapping.Wrap };
    readonly CancellationTokenSource stop = new(); readonly string raw;
    Task rendering = Task.CompletedTask; int mode, field; bool allowed, closing;
    readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public EvidenceWindow(string raw, Window owner)
    {
        this.raw = raw;
        SetResourceReference(StyleProperty, typeof(Window));
        Owner = owner;
        Title = "原始异常帧查看";
        Width = 1050;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        using var json = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(raw, ".json")));
        var root = new DockPanel { Margin = new Thickness(10) };
        Content = root;
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(tools, Dock.Top);
        root.Children.Add(tools);
        tools.Children.Add(new TextBlock { Text = "原始帧时间 " + Text.Timecode(json.RootElement.GetProperty("FrameTime").GetDouble()), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        var display = new ComboBox { ItemsSource = new[] { "原生显示", "SDR兼容预览" }, SelectedIndex = 0 };
        display.SelectionChanged += (_, _) => Volatile.Write(ref mode, display.SelectedIndex);
        tools.Children.Add(display);
        var fields = new ComboBox { ItemsSource = new[] { "原始织帧", "上场观察", "下场观察" }, SelectedIndex = 0 };
        fields.SelectionChanged += (_, _) => Volatile.Write(ref field, fields.SelectedIndex);
        tools.Children.Add(fields);
        DockPanel.SetDock(state, Dock.Bottom);
        root.Children.Add(state);
        root.Children.Add(host);
        Loaded += (_, _) => { if (!closing) rendering = Task.Run(Render); };
        Closing += (_, e) =>
        {
            if (allowed)
            {
                return;
            }
            e.Cancel = true;
            if (closing)
            {
                return;
            }
            closing = true;
            stop.Cancel();
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => _ = FinishCloseAsync()));
        };
        Closed += (_, _) => closed.TrySetResult();
    }
    internal Task RequestCloseAsync()
    {
        Dispatcher.VerifyAccess();
        if (!closed.Task.IsCompleted)
        {
            Close();
        }
        return closed.Task;
    }
    async Task FinishCloseAsync()
    {
        try
        {
            await rendering;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
        finally
        {
            stop.Dispose();
            allowed = true;
            _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(Close));
        }
    }
    async Task Render()
    {
        FfmpegDecoder? decoder = null;
        GpuRenderer? renderer = null;
        FrameLease? frame = null;
        try
        {
            decoder = new(new()
            {
                Name = "原始截图",
                Location = raw,
                HardwareDecode = false
            });
            string failure = "";
            decoder.Fatal += message => failure = message;
            decoder.Start();
            await foreach (var next in decoder.Video.Reader.ReadAllAsync(stop.Token))
            {
                frame = next;
                break;
            }
            if (frame == null)
            {
                throw new IOException(failure == "" ? "无法读取截图源像素" : failure);
            }
            renderer = new(host.VideoHandle, 0);
            var settings = new DetectionSettings();
            while (!stop.IsCancellationRequested)
            {
                renderer.DisplayMode = (DisplayMode)Volatile.Read(ref mode);
                int currentField = Volatile.Read(ref field);
                var info = frame.Info ?? decoder.Info!;
                renderer.Process(frame, info, settings, currentField == 0);
                if (currentField > 0)
                {
                    renderer.PresentField(info, settings, currentField);
                }
                string text = renderer.Status + " · RAW源像素 / " + info.BitDepth + "bit / " + info.PixelFormat;
                await Dispatcher.InvokeAsync(() => state.Text = text);
                await Task.Delay(250, stop.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!stop.IsCancellationRequested)
            {
                await Dispatcher.InvokeAsync(() => state.Text = "查看失败：" + ex.Message);
            }
        }
        finally
        {
            renderer?.Dispose();
            frame?.Dispose();
            decoder?.Dispose();
        }
    }
}
