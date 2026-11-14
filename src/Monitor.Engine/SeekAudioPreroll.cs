namespace Monitor.Engine;


internal sealed class SeekAudioPreroll
{
    private readonly Queue<AudioBlock> blocks = new();
    private long bytes;
    internal int Count => blocks.Count;

    internal bool Add(AudioBlock block)
    {
        blocks.Enqueue(block);
        bytes += block.Samples.LongLength * sizeof(float);
        bool discarded = false;
        while (blocks.Count > 160 || bytes > 4L * 1024 * 1024)
        {
            AudioBlock old = blocks.Dequeue();
            bytes -= old.Samples.LongLength * sizeof(float);
            discarded = true;
        }

        return discarded;
    }

    internal AudioBlock Take()
    {
        AudioBlock block = blocks.Dequeue();
        bytes -= block.Samples.LongLength * sizeof(float);
        return block;
    }

    internal void Clear()
    {
        blocks.Clear();
        bytes = 0;
    }

    internal static AudioBlock? Trim(AudioBlock block, double firstVideoPts)
    {
        int count = block.Samples.Length / block.Channels;
        double end = block.Pts + count / (double)block.Rate;
        if (end <= firstVideoPts)
        {
            return null;
        }

        int skipped = (int)Math.Clamp(Math.Ceiling((firstVideoPts - block.Pts) * block.Rate - 1e-7), 0, count);
        if (skipped == 0)
        {
            return block;
        }

        return block with
        {
            Samples = block.Samples.AsSpan(skipped * block.Channels).ToArray(),
            Pts = block.Pts + skipped / (double)block.Rate
        };
    }
}
