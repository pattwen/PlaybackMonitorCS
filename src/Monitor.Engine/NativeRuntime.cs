using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
namespace Monitor.Engine;

public static class NativeRuntime
{
    public static string DirectoryPath => Path.GetFullPath(Environment.GetEnvironmentVariable("MONITOR_NATIVE") ?? Path.Combine(AppContext.BaseDirectory, "native"));
    static bool initialized;
    static readonly object Gate = new();
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern nint AddDllDirectory(string path);
    [DllImport("kernel32")] static extern bool SetDefaultDllDirectories(uint flags);
    public static unsafe void Initialize()
    {
        lock (Gate)
        {
            if (initialized)
            {
                return;
            }
            if (!Directory.Exists(DirectoryPath))
            {
                throw new DirectoryNotFoundException("缺少native目录！");
            }
            bool windows = OperatingSystem.IsWindows();
            // FFmpeg 8.x
            /*
            string[] libraries = windows
                ? ["avcodec-62.dll", "avformat-62.dll", "avutil-60.dll", "swscale-9.dll", "swresample-6.dll"]
                : ["libavcodec.so.62", "libavformat.so.62", "libavutil.so.60", "libswscale.so.9", "libswresample.so.6"];
            */
            // FFmpeg 9.x
            string[] libraries = windows
                ? ["avcodec-63.dll", "avformat-63.dll", "avutil-61.dll", "swscale-10.dll", "swresample-7.dll"]
                : ["libavcodec.so.63", "libavformat.so.63", "libavutil.so.61", "libswscale.so.10", "libswresample.so.7"];
            foreach (string file in libraries)
            {
                if (!File.Exists(Path.Combine(DirectoryPath, file)))
                {
                    throw new DirectoryNotFoundException($"缺少{file}。");
                }
            }
            if (windows)
            {
                SetDefaultDllDirectories(0x1400);
                AddDllDirectory(DirectoryPath);
            }
            ffmpeg.RootPath = DirectoryPath;
            // FFmpeg 8.x
            /* 
            if (ffmpeg.avcodec_version() >> 16 != 62)
            {
                throw new InvalidOperationException("要求FFmpeg 8.x shared x64（avcodec-62.dll）");
            }
            */
            // FFmpeg 9.x
            if (ffmpeg.avcodec_version() >> 16 != 63)
            {
                throw new InvalidOperationException("要求FFmpeg 9.x shared x64（avcodec-63.dll）");
            }
            ffmpeg.avformat_network_init();
            initialized = true;
        }
    }
    public static unsafe string Error(int code)
    {
        byte* b = stackalloc byte[1024];
        ffmpeg.av_strerror(code, b, 1024);
        return Marshal.PtrToStringUTF8((nint)b) ?? code.ToString();
    }
    public static void Check(int code)
    {
        if (code < 0)
        {
            throw new IOException(Error(code));
        }
    }
}
public sealed unsafe class FrameLease : IDisposable
{
    public AVFrame* Frame
    {
        get; private set;
    }
    public double Pts
    {
        get;
    }
    public double Duration
    {
        get;
    }
    public int Generation
    {
        get;
    }
    public Monitor.Core.VideoInfo? Info
    {
        get;
    }
    public FrameLease(AVFrame* f, double pts, double duration, int generation, Monitor.Core.VideoInfo? info = null)
    {
        Frame = ffmpeg.av_frame_clone(f);
        if (Frame == null)
        {
            throw new OutOfMemoryException();
        }
        Pts = pts;
        Duration = duration;
        Generation = generation;
        Info = info;
    }
    public void Dispose()
    {
        var f = Frame;
        if (f != null)
        {
            ffmpeg.av_frame_free(&f);
            Frame = null;
        }
    }
}
public sealed record AudioBlock(float[] Samples, int Channels, int Rate, string[] Names, double Pts, int Generation, int TrackIndex = -1);
