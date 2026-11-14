using FFmpeg.AutoGen;
using Monitor.Core;
namespace Monitor.Engine;

sealed unsafe class SoftwareConverter : IDisposable
{
    SwsContext* context; string key = ""; byte[] pixels = [];
    public byte[] Convert(AVFrame* frame, VideoInfo info, bool highPrecision)
    {
        int width = frame->width, height = frame->height, bytes = highPrecision ? 8 : 4;
        bool fields = info.Interlaced && height % 2 == 0;
        int rows = fields ? height / 2 : height;
        AVPixelFormat output = highPrecision ? AVPixelFormat.AV_PIX_FMT_RGBA64LE : AVPixelFormat.AV_PIX_FMT_RGBA;
        string requested = $"{width}/{rows}/{frame->format}/{output}/{info.ChromaLocation}";
        if (context == null || key != requested)
        {
            if (context != null)
            {
                ffmpeg.sws_freeContext(context);
            }
            context = ffmpeg.sws_alloc_context();
            if (context == null)
            {
                throw new OutOfMemoryException();
            }
            void Set(string name, long value) => NativeRuntime.Check(ffmpeg.av_opt_set_int(context, name, value, 0));
            Set("srcw", width);
            Set("srch", rows);
            Set("src_format", frame->format);
            Set("dstw", width);
            Set("dsth", rows);
            Set("dst_format", (int)output);
            Set("sws_flags", 2);
            Set("src_h_chr_pos", info.ChromaLocation is 1 or 3 or 5 ? 0 : 128);
            Set("src_v_chr_pos", info.ChromaLocation is 3 or 4 ? 0 : info.ChromaLocation is 5 or 6 ? 256 : 128);
            NativeRuntime.Check(ffmpeg.sws_init_context(context, null, null));
            key = requested;
        }
        int size = checked(width * height * bytes);
        if (pixels.Length != size)
        {
            pixels = GC.AllocateUninitializedArray<byte>(size, pinned: true);
        }
        var descriptor = ffmpeg.av_pix_fmt_desc_get((AVPixelFormat)frame->format);
        int colorMatrix = info.Matrix.Contains("2020") ? 9 : info.Matrix.Contains("240") ? 7 : info.Matrix.Contains("FCC") ? 4 : info.Matrix.Contains("470") || info.Matrix.Contains("170") ? 5 : info.Matrix.Contains("709") ? 1 : info.Height <= 576 ? 5 : 1;
        int* coefficients = ffmpeg.sws_getCoefficients(colorMatrix);
        int_array4 matrix = default;
        for (uint i = 0; i < 4; i++)
        {
            matrix[i] = coefficients[i];
        }
        NativeRuntime.Check(ffmpeg.sws_setColorspaceDetails(context, matrix, info.Range.Contains("JPEG") || descriptor != null && (descriptor->flags & 32) != 0 ? 1 : 0, matrix, 1, 0, 1 << 16, 1 << 16));
        fixed (byte* outputData = pixels)
            for (int field = 0; field < (fields ? 2 : 1); field++)
            {
                byte_ptrArray8 source = frame->data;
                int_array8 strides = frame->linesize;
                if (fields)
                    for (uint i = 0; i < 8; i++)
                    {
                        if (source[i] != null)
                        {
                            source[i] += strides[i] * field;
                        }
                        strides[i] *= 2;
                    }
                byte_ptrArray4 target = default;
                int_array4 targetStride = default;
                target[0] = outputData + (fields ? width * bytes * field : 0);
                targetStride[0] = width * bytes * (fields ? 2 : 1);
                NativeRuntime.Check(ffmpeg.sws_scale(context, source, strides, 0, rows, target, targetStride));
            }
        return pixels;
    }
    public void Dispose()
    {
        if (context != null)
        {
            ffmpeg.sws_freeContext(context);
            context = null;
        }
        pixels = [];
    }
}
