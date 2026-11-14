using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Monitor.Core;

namespace Monitor.Engine;
internal static unsafe class ProgramCatalog
{
    internal static PlaybackProgram[] Read(AVFormatContext* format)
    {
        var result = new List<PlaybackProgram>();
        for (uint index = 0;
            index < format->nb_programs;
            index++)
        {
            AVProgram* program = format->programs[index];
            if (program == null || program->stream_index == null)
            {
                continue;
            }

            var indexes = new List<int>();
            bool hasVideo = false;
            for (uint item = 0;
                item < program->nb_stream_indexes;
                item++)
            {
                uint streamIndex = program->stream_index[item];
                if (streamIndex >= format->nb_streams)
                {
                    continue;
                }

                indexes.Add(checked((int)streamIndex));
                AVStream* stream = format->streams[streamIndex];
                hasVideo |= stream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO;
            }

            result.Add(new PlaybackProgram(program->id, program->program_num, Metadata(program->metadata, "service_name"), Metadata(program->metadata, "service_provider"), indexes.Distinct().ToArray(), hasVideo));
        }

        return result.ToArray();
    }

    private static string Metadata(AVDictionary* dictionary, string key)
    {
        if (dictionary == null)
        {
            return "";
        }

        AVDictionaryEntry* entry = ffmpeg.av_dict_get(dictionary, key, null, 0);
        string text = entry == null ? "" : Marshal.PtrToStringUTF8((nint)entry->value) ?? "";
        return new string (text.Where(character => !char.IsControl(character)).Take(160).ToArray());
    }

    internal static PlaybackStream[] Streams(AVFormatContext* format)
    {
        var result = new List<PlaybackStream>();
        for (uint index = 0;
            index < format->nb_streams;
            index++)
        {
            AVStream* stream = format->streams[index];
            AVCodecParameters* parameters = stream->codecpar;
            PlaybackStreamKind kind = parameters->codec_type switch
            {
                AVMediaType.AVMEDIA_TYPE_VIDEO => PlaybackStreamKind.Video,
                AVMediaType.AVMEDIA_TYPE_AUDIO => PlaybackStreamKind.Audio,
                _ => PlaybackStreamKind.Other
            };
            bool decodable = kind != PlaybackStreamKind.Other && ffmpeg.avcodec_find_decoder(parameters->codec_id) != null;
            result.Add(new PlaybackStream(checked((int)index), kind, (stream->disposition & ffmpeg.AV_DISPOSITION_DEFAULT) != 0, decodable, parameters->width, parameters->height));
        }

        return result.ToArray();
    }
}
