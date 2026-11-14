using System;
using System.IO;
namespace Monitor.Core;


public sealed class LevelWindow
{
    readonly object gate = new();
    string[] names = [];
    double[] power = [];
    double[] peaks = [];
    long samples;
    ChannelLevel[] latest = [];
    public void Add(ChannelLevel[] levels, int sampleFrames)
    {
        if (sampleFrames <= 0)
        {
            return;
        }
        lock (gate)
        {
            if (!names.SequenceEqual(levels.Select(l => l.Name)))
            {
                names = levels.Select(l => l.Name).ToArray();
                power = new double[levels.Length];
                peaks = Enumerable.Repeat(-120d, levels.Length).ToArray();
                samples = 0;
            }
            latest = levels;
            for (int i = 0; i < levels.Length; i++)
            {
                power[i] += Math.Pow(10, levels[i].RmsDb / 10) * sampleFrames;
                peaks[i] = Math.Max(peaks[i], levels[i].PeakDb);
            }
            samples += sampleFrames;
        }
    }
    public ChannelLevel[] ReadAndReset()
    {
        lock (gate)
        {
            if (samples == 0)
            {
                return latest.Select(l => l with { PeakDb = -120 }).ToArray();
            }
            var result = names.Select((name, i) => new ChannelLevel(name, Math.Max(-120, 10 * Math.Log10(Math.Max(1e-12, power[i] / samples))), peaks[i])).ToArray();
            Array.Clear(power);
            Array.Fill(peaks, -120);
            samples = 0;
            return result;
        }
    }
    public void Clear()
    {
        lock (gate)
        {
            names = [];
            power = [];
            peaks = [];
            latest = [];
            samples = 0;
        }
    }
}
