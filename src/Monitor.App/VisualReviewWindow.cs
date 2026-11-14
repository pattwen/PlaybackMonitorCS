using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Monitor.Core;

namespace Monitor.App;

public sealed class VisualReviewWindow : Window
{
    public string Review
    {
        get; private set;
    }
    public string Note
    {
        get; private set;
    }
    public VisualReviewWindow(AlarmEvent row, Window owner)
    {
        SetResourceReference(StyleProperty, typeof(Window));
        Owner = owner;
        Title = "人工复核";
        Width = 590;
        Height = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Review = row.Review;
        Note = row.Note;
        var root = new StackPanel { Margin = new Thickness(18) };
        Content = root;
        root.Children.Add(new TextBlock { Text = $"{row.Label} · {row.StartText}", FontWeight = FontWeights.Bold });
        root.Children.Add(new TextBlock
        {
            Text = "节目效果与真实异常可能效果相同，请结合截图判断。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 12)
        });
        string[] options = ["待复核", "确认异常", "节目特效", "正常/误报"];
        var choice = new ComboBox { ItemsSource = options, SelectedItem = options.Contains(row.Review) ? row.Review : "待复核" };
        root.Children.Add(choice);
        root.Children.Add(new TextBlock { Text = "备注", Margin = new Thickness(0, 12, 0, 5) });
        var note = new TextBox
        {
            Text = row.Note,
            Height = 100,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        root.Children.Add(note);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var save = new Button { Content = "保存", IsDefault = true };
        var cancel = new Button { Content = "取消", IsCancel = true };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        save.Click += (_, _) => { Review = (string)choice.SelectedItem; Note = note.Text; DialogResult = true; };
    }
}

public sealed class VisualSnapshotWindow : Window
{
    public VisualSnapshotWindow(string rootPath, AlarmEvent row, Window owner)
    {
        SetResourceReference(StyleProperty, typeof(Window));
        Owner = owner;
        Title = "画面异常截图 · " + row.Label;
        Width = 1120;
        Height = 780;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var tabs = new TabControl { Margin = new Thickness(10) };
        Content = tabs;
        if (!string.IsNullOrEmpty(row.Snapshot))
            Add("首次可保留候选帧", row.Snapshot, row.Visual);
        if (!string.IsNullOrEmpty(row.PeakSnapshot) && row.PeakSnapshot != row.Snapshot)
            Add("最明显候选帧", row.PeakSnapshot, row.PeakVisual);

        void Add(string title, string relative, VisualEvidence? visual)
        {
            string root = Path.GetFullPath(rootPath) + Path.DirectorySeparatorChar;
            string png = Path.GetFullPath(Path.Combine(root, relative));
            if (!png.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("截图路径超出记录目录。");
            }
            var panel = new DockPanel { Margin = new Thickness(8) };
            var text = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
                Text = visual == null ? row.Detail : $"截图时间 {Text.Timecode(visual.FrameTime)} · 局部分数 {visual.Score:F1} · 需人工复核\n" +
                    string.Join("\n", visual.Regions.Select(r => $"{Text.Label(r.Kind)} ({r.X},{r.Y},{r.Width}×{r.Height})：{r.Detail}"))
            };
            DockPanel.SetDock(text, Dock.Top);
            panel.Children.Add(text);
            var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(tools, Dock.Top);
            panel.Children.Add(tools);
            var original = new Button { Content = "原始帧查看（HDR / 场序）", IsEnabled = File.Exists(Path.ChangeExtension(png, ".raw")) };
            tools.Children.Add(original);
            tools.Children.Add(new TextBlock { Text = "红框为候选区域；原图和原始像素文件独立保留。", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
            original.Click += (_, _) =>
            {
                try
                {
                    new EvidenceWindow(Path.ChangeExtension(png, ".raw"), owner).Show();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "无法查看原始帧");
                }
            };
            string marked = Path.Combine(Path.GetDirectoryName(png)!, Path.GetFileNameWithoutExtension(png) + ".marked.png");
            string display = File.Exists(marked) ? marked : png;
            if (File.Exists(display))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(display);
                bitmap.EndInit();
                bitmap.Freeze();
                panel.Children.Add(new Image { Source = bitmap, Stretch = Stretch.Uniform });
            }
            else
            {
                panel.Children.Add(new TextBlock { Text = "截图文件不存在或尚未写入。" });
            }
            tabs.Items.Add(new TabItem { Header = title, Content = panel });
        }
    }
}
