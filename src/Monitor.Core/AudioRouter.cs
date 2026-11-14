namespace Monitor.Core;

public static class AudioRouter
{
    public static float[] Route(ReadOnlySpan<float> source, int channels, string[] names, string[] speakers, float volume, bool muted, int solo)
    {
        int frames = source.Length / channels, outputs = speakers.Length;
        var result = new float[frames * outputs];
        float gain = muted ? 0 : volume;
        for (int i = 0; i < frames; i++)
        {
            if (solo >= 0 && solo < channels)
            {
                for (int o = 0; o < Math.Min(outputs, 2); o++)
                {
                    result[i * outputs + o] = source[i * channels + solo] * gain;
                }
                continue;
            }
            if (outputs <= 2)
            {
                float left = 0, right = 0;
                if (channels == 1)
                {
                    left = right = source[i];
                }

                else
                    for (int c = 0; c < channels; c++)
                    {
                        float v = source[i * channels + c];
                        string name = c < names.Length ? names[c] : "";
                        switch (name)
                        {
                            case "FL":
                                left += v;
                                break;
                            case "FR":
                                right += v;
                                break;
                            case "FC":
                                left += v * .7071f;
                                right += v * .7071f;
                                break;
                            case "BL":
                            case "SL":
                                left += v * .7071f;
                                break;
                            case "BR":
                            case "SR":
                                right += v * .7071f;
                                break;
                            case "LFE":
                                break;
                            default:
                                left += v * .5f;
                                right += v * .5f;
                                break;
                        }
                    }
                float headroom = channels > 6 ? .3f : channels > 2 ? .4f : 1;
                left = Math.Clamp(left * gain * headroom, -1, 1);
                right = Math.Clamp(right * gain * headroom, -1, 1);
                result[i * outputs] = outputs == 1 ? (left + right) * .5f : left;
                if (outputs == 2)
                {
                    result[i * outputs + 1] = right;
                }
            }
            else
            {
                for (int o = 0; o < outputs; o++)
                {
                    float sum = 0;
                    int count = 0;
                    for (int c = 0; c < channels; c++)
                    {
                        string name = c < names.Length ? names[c] : $"CH{c + 1}";
                        if (!speakers.Contains(name))
                            name = name switch
                            {
                                "SL" => "BL",
                                "SR" => "BR",
                                "BL" => "SL",
                                "BR" => "SR",
                                _ => name
                            };
                        if (name == speakers[o] || name.StartsWith("CH") && c == o)
                        {
                            sum += source[i * channels + c];
                            count++;
                        }
                    }
                    result[i * outputs + o] = Math.Clamp(sum * gain / Math.Max(1, count), -1, 1);
                }
            }
        }
        return result;
    }
}
