using System.Text.Json;
using FFmpeg.AutoGen;
namespace Monitor.Engine;

static unsafe class FrameMetadata
{
    static double Q(AVRational value) => value.den == 0 ? 0 : ffmpeg.av_q2d(value);
    public static string Hdr(AVFrame* frame, string fallback = "")
    {
        var m = ffmpeg.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_MASTERING_DISPLAY_METADATA);
        var l = ffmpeg.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_CONTENT_LIGHT_LEVEL);
        if (m == null && l == null)
        {
            return fallback;
        }
        object? mastering = null, light = null;
        if (m != null)
        {
            var data = (AVMasteringDisplayMetadata*)m->data;
            var primaries = new double[3][];
            for (uint i = 0; i < 3; i++)
            {
                primaries[i] = [Q(data->display_primaries[i][0]), Q(data->display_primaries[i][1])];
            }
            mastering = new
            {
                Primaries = primaries,
                WhitePoint = new[] { Q(data->white_point[0]), Q(data->white_point[1]) },
                MinNits = Q(data->min_luminance),
                MaxNits = Q(data->max_luminance),
                HasPrimaries = data->has_primaries != 0,
                HasLuminance = data->has_luminance != 0
            };
        }
        if (l != null)
        {
            var data = (AVContentLightMetadata*)l->data;
            light = new
            {
                MaxCLL = data->MaxCLL,
                MaxFALL = data->MaxFALL
            };
        }
        return JsonSerializer.Serialize(new
        {
            Mastering = mastering,
            ContentLight = light
        });
    }
}
