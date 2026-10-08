using NAudio.Wave;
using System.Runtime.InteropServices;
namespace ChannelBridge;

public sealed record OutputChannel(int Source, float Gain, int DelayMs);
public static class OutputFormats
{
    public static WaveFormat Float(int rate, int channels, int mask, bool native)
    {
        if (channels is < 1 or > 32) throw new InvalidOperationException("设备输出声道数必须为 1–32。");
        if (!native) return WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
        var pointer = WaveFormat.MarshalToPtr(new WaveFormatExtensible(rate, 32, channels));
        try { Marshal.WriteInt32(pointer, 20, mask); return WaveFormat.MarshalFromPtr(pointer); }
        finally { Marshal.FreeHGlobal(pointer); }
    }
}
// NAudio's SampleToWaveProvider rejects extensible float formats. Preserve the
// channel mask when adapting the float DSP stream to WASAPI's byte provider.
public sealed class FloatWaveProvider(ISampleProvider source) : IWaveProvider
{
    float[] samples = Array.Empty<float>();
    public WaveFormat WaveFormat => source.WaveFormat;
    public int Read(byte[] buffer, int offset, int count)
    {
        int needed = count / WaveFormat.BlockAlign * WaveFormat.Channels;
        if (samples.Length < needed) samples = new float[needed];
        int read = source.Read(samples, 0, needed);
        Buffer.BlockCopy(samples, 0, buffer, offset, read * 4);
        return read * 4;
    }
}
// Retain the old stereo constructor for legacy routes and regression fixtures.
public sealed class AdaptiveStereo : AdaptiveOutput
{
    public AdaptiveStereo(int inputRate, int outputRate, int l, int r, float volume, int delayMs, float? rightVolume = null, int? rightDelayMs = null, int bufferMs = 80)
        : base(inputRate, WaveFormat.CreateIeeeFloatWaveFormat(outputRate, 2), new[] { new OutputChannel(l, volume, delayMs), new OutputChannel(r, rightVolume ?? volume, rightDelayMs ?? delayMs) }, bufferMs) { }
}
