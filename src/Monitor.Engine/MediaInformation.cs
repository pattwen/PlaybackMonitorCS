using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
namespace Monitor.Engine;

public static class MediaInformation
{
    [DllImport("MediaInfo", CallingConvention = CallingConvention.Cdecl)] static extern nint MediaInfo_New();
    [DllImport("MediaInfo", CallingConvention = CallingConvention.Cdecl)] static extern void MediaInfo_Delete(nint handle);
    [DllImport("MediaInfo", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] static extern nuint MediaInfo_Open(nint handle, string file);
    [DllImport("MediaInfo", CallingConvention = CallingConvention.Cdecl)] static extern void MediaInfo_Close(nint handle);
    [DllImport("MediaInfo", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] static extern nint MediaInfo_Option(nint handle, string option, string value);
    [DllImport("MediaInfo", CallingConvention = CallingConvention.Cdecl)] static extern nint MediaInfo_Inform(nint handle, nuint reserved);
    public static Task<string> InspectAsync(string path, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        if (path.Contains("://"))
        {
            return "直播信息由当前播放链路提供。";
        }
        nint handle = 0;
        try
        {
            NativeRuntime.Initialize();
            handle = MediaInfo_New();
            if (handle == 0)
            {
                throw new IOException("MediaInfo初始化失败");
            }
            MediaInfo_Option(handle, "ParseSpeed", "0.5");
            MediaInfo_Option(handle, "Inform", "JSON");
            if (MediaInfo_Open(handle, path) == 0)
            {
                return "MediaInfo无法分析该文件";
            }
            string json = Marshal.PtrToStringUni(MediaInfo_Inform(handle, 0)) ?? "";
            token.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (DllNotFoundException)
        {
            return "未检测到MediaInfo.dll，请放入x64 MediaInfo DLL。";
        }
        finally
        {
            if (handle != 0)
            {
                MediaInfo_Close(handle);
                MediaInfo_Delete(handle);
            }
        }
    }, token);
}
