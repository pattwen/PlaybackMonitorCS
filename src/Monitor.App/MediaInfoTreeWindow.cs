using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Monitor.Core;
using Monitor.Engine;

namespace Monitor.App;

public sealed class MediaInfoTreeWindow : Window
{
    readonly TreeView tree = new()
    {
        Background = new SolidColorBrush(Color.FromRgb(19, 29, 39)),
        Foreground = Brushes.Gainsboro,
        BorderBrush = new SolidColorBrush(Color.FromRgb(53, 72, 91))
    };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    readonly Button copy = new() { Content = "复制信息树", IsEnabled = false };
    readonly Button export = new() { Content = "导出原始JSON", IsEnabled = false };
    readonly CancellationTokenSource stop = new();
    Task loading = Task.CompletedTask;
    MediaInfoReport? report;
    public MediaInfoTreeWindow(Window owner, string location, string? existingJson = null)
    {
        SetResourceReference(StyleProperty, typeof(Window));
        Owner = owner;
        Title = "MediaInfo 媒体信息";
        Width = 1020;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(14) };
        Content = root;
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(tools, Dock.Top);
        root.Children.Add(tools);
        var expand = new Button { Content = "全部节点展开" };
        var collapse = new Button { Content = "全部节点折叠" };
        tools.Children.Add(expand);
        tools.Children.Add(collapse);
        tools.Children.Add(copy);
        tools.Children.Add(export);
        expand.Click += (_, _) => SetExpanded(true);
        collapse.Click += (_, _) => SetExpanded(false);
        copy.Click += (_, _) => { if (report != null) Clipboard.SetText(MediaInfoTree.ToText(report.Nodes)); };
        export.Click += (_, _) => Export();
        status.Text = location;
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);
        root.Children.Add(tree);
        tree.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.C && System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control) && tree.SelectedItem is TreeViewItem { Tag: MediaInfoNode node })
            {
                Clipboard.SetText(MediaInfoTree.ToText([node]));
                e.Handled = true;
            }
        };
        Closed += async (_, _) => { stop.Cancel(); await loading; stop.Dispose(); };
        Loaded += (_, _) => loading = LoadAsync(location, existingJson);
    }
    async Task LoadAsync(string location, string? existingJson)
    {
        try
        {
            status.Text = "正在分析：" + location;
            string json = existingJson ?? await MediaInformation.InspectAsync(location, stop.Token);
            if (stop.IsCancellationRequested)
                return;
            if (!json.TrimStart().StartsWith('{'))
            {
                status.Text = json;
                return;
            }
            report = MediaInfoTree.Parse(json);
            foreach (var node in report.Nodes)
            {
                tree.Items.Add(Item(node, true));
            }
            status.Text = report.Source.Length == 0 ? location : report.Source;
            copy.IsEnabled = export.IsEnabled = true;
        }
        catch (OperationCanceledException ex)
        {
        }
        catch (Exception ex2)
        {
            if (!stop.IsCancellationRequested)
                status.Text = "媒体信息读取失败：" + ex2.Message;
        }
    }
    static TreeViewItem Item(MediaInfoNode node, bool expanded = false)
    {
        var header = new TextBlock { Text = node.DisplayText, TextWrapping = TextWrapping.Wrap, MaxWidth = 880, Margin = new Thickness(0, 2, 0, 2) };
        var item = new TreeViewItem { Header = header, IsExpanded = expanded, Tag = node, Foreground = Brushes.Gainsboro };
        foreach (var child in node.Children)
        {
            item.Items.Add(Item(child));
        }
        return item;
    }
    void SetExpanded(bool expanded)
    {
        void Set(TreeViewItem item)
        {
            item.IsExpanded = expanded;
            foreach (TreeViewItem child in item.Items)
            {
                Set(child);
            }
        }
        foreach (TreeViewItem item in tree.Items)
        {
            Set(item);
        }
    }
    void Export()
    {
        if (report is null)
        {
            return;
        }
        var dialog = new SaveFileDialog { Filter = "MediaInfo JSON|*.json", FileName = "mediainfo.json" };
        if (dialog.ShowDialog(this) == true)
            try
            {
                File.WriteAllText(dialog.FileName, report.RawJson);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "导出失败");
            }
    }
}
