using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using FFmpeg.AutoGen;
using Monitor.Core;
namespace Monitor.Engine;

public sealed unsafe class Evidence : IDisposable
{
    readonly Dictionary<Guid, (byte[] Data, byte[] Preview, string Json, double Pts, DateTimeOffset Wall, int Width, int Height)> cache = [];
    public void Capture(Guid id, FrameLease lease, VideoInfo info)
    {
        if (cache.ContainsKey(id))
        {
            return;
        }
        var cpu = ffmpeg.av_frame_alloc();

        try
        {
            AVFrame* frame = lease.Frame;
            if ((AVPixelFormat)frame->format == AVPixelFormat.AV_PIX_FMT_D3D11)
            {
                NativeRuntime.Check(ffmpeg.av_hwframe_transfer_data(cpu, frame, 0));
                frame = cpu;
            }
            var format = (AVPixelFormat)frame->format;
            int size = ffmpeg.av_image_get_buffer_size(format, frame->width, frame->height, 1);
            NativeRuntime.Check(size);
            byte_ptrArray4 source = default;
            int_array4 sourceStride = default;
            for (uint i = 0; i < 4; i++)
            {
                source[i] = frame->data[i];
                sourceStride[i] = frame->linesize[i];
            }
            byte[] raw = new byte[size];
            fixed (byte* data = raw)
                NativeRuntime.Check(ffmpeg.av_image_copy_to_buffer(data, size, source, sourceStride, format, frame->width, frame->height, 1));



            int w = frame->width, h = frame->height;
            using var converter = new SoftwareConverter();
            byte[] rgba = converter.Convert(frame, info, false);
            string json = JsonSerializer.Serialize(new
            {
                Video = info,
                FrameTime = lease.Pts,
                PixelFormat = ffmpeg.av_get_pix_fmt_name(format),
                Width = w,
                Height = h,
                Layout = "av_image_copy_to_buffer alignment=1",
                Preview = info.Hdr ? "HDR编码值预览" : "SDR RGBA预览",
                CapturedAt = DateTimeOffset.UtcNow
            }, Configuration.Json);
            cache[id] = (raw, rgba, json, lease.Pts, DateTimeOffset.UtcNow, w, h);
        }
        finally
        {
            ffmpeg.av_frame_free(&cpu);
        }
    }
    public string Save(Guid id, string root, VideoInfo info, VisualEvidence? visual = null)
    {
        if (!cache.TryGetValue(id, out var evidence))
        {
            return "";
        }
        string? scope = visual?.Regions.FirstOrDefault()?.Kind.ToString();
        string relative = $"snapshots/{Text.Timecode(evidence.Pts).Replace(':', '-')}_{id:N}" + (scope == null ? "" : "_" + scope);
        string prefix = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(prefix)!);
        File.WriteAllBytes(prefix + ".raw", evidence.Data);
        var json = JsonNode.Parse(evidence.Json)!.AsObject();
        if (visual != null)
        {
            json["Visual"] = JsonSerializer.SerializeToNode(visual, Configuration.Json);
        }
        File.WriteAllText(prefix + ".json", json.ToJsonString(Configuration.Json));
        SavePng(prefix + ".png", evidence.Preview, evidence.Width, evidence.Height);
        if (visual?.Regions.Length > 0)
        {
            using var marked = new System.Drawing.Bitmap(prefix + ".png");
            using var graphics = System.Drawing.Graphics.FromImage(marked);
            using var pen = new System.Drawing.Pen(System.Drawing.Color.OrangeRed, Math.Max(2, evidence.Width / 640f));
            foreach (var region in visual.Regions)
            {
                int x = Math.Clamp(region.X, 0, evidence.Width - 1), y = Math.Clamp(region.Y, 0, evidence.Height - 1);
                int w = Math.Min(region.Width, evidence.Width - x), h = Math.Min(region.Height, evidence.Height - y);
                if (w > 0 && h > 0)
                    graphics.DrawRectangle(pen, x, y, Math.Max(1, w - 1), Math.Max(1, h - 1));
            }
            marked.Save(prefix + ".marked.png", System.Drawing.Imaging.ImageFormat.Png);
        }
        return relative + ".png";
    }
    static void SavePng(string path, byte[] rgba, int width, int height)
    {
        // Bitmap png 编码仅用于事件存证
        rgba = (byte[])rgba.Clone();
        using var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var bits = bitmap.LockBits(new(0, 0, width, height), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            for (int i = 0; i < rgba.Length; i += 4)
            {
                (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]);
            }
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(rgba, y * width * 4, bits.Scan0 + y * bits.Stride, width * 4);
            }
        }
        finally
        {
            bitmap.UnlockBits(bits);
        }
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
    public DateTimeOffset? Wall(Guid? id) => id is { } key && cache.TryGetValue(key, out var e) ? e.Wall : null;
    public void Retain(IEnumerable<Guid> ids)
    {
        var keep = ids.ToHashSet();
        foreach (var id in cache.Keys.Where(k => !keep.Contains(k)).ToArray())
        {
            cache.Remove(id);
        }
    }
    public void Dispose() => cache.Clear();
}
