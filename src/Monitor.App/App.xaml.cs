using System.DirectoryServices.ActiveDirectory;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
namespace Monitor.App;

public partial class App : Application
{
    Mutex? mutex; bool owns;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string? Value(string key)
        {
            int i = Array.IndexOf(e.Args, key);
            return i >= 0 && i + 1 < e.Args.Length ? e.Args[i + 1] : null;
        }
        // string root = Path.GetFullPath(Value("--data-dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlaybackMonitorCS"));

        string root = Path.GetFullPath(Value("--data-dir") ?? Path.Combine(AppContext.BaseDirectory, "settings"));
        try
        {
            Directory.CreateDirectory(root);
            string name = "Local\\PlaybackMonitorCS-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..24];
            mutex = new(false, name);
            try
            {
                owns = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                owns = true;
            }
            if (!owns)
            {
                MessageBox.Show("该记录目录正在被另一个实例使用。可通过 --data-dir 指定不同目录。");
                Shutdown();
                return;
            }
            DispatcherUnhandledException += (_, args) =>
            {
                args.Handled = true;
                try
                {
                    File.AppendAllText(Path.Combine(root, "application-errors.log"), DateTimeOffset.UtcNow + " " + args.Exception + Environment.NewLine);
                }
                catch { }
                if (Dispatcher.HasShutdownStarted || MainWindow is MainWindow { IsShuttingDown: true })
                    return;
                MessageBox.Show("程序发生错误，详情已写入记录目录。\n" + args.Exception.Message, "MultiPlay");
            };
            var window = new MainWindow(root, !e.Args.Contains("--no-audio-output"));
            MainWindow = window;
            if (Value("--open") is { } source)
                window.Loaded += async (_, _) => await window.OpenAsync(new() { Name = Path.GetFileName(source), Location = source, IsLive = source.Contains("://") });
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show("无法启动：" + ex.Message);
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if (owns)
        {
            mutex?.ReleaseMutex();
        }
        mutex?.Dispose();
        base.OnExit(e);
    }
}
