using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.Wave;
namespace ChannelBridge;

public static class MultichannelChecks
{
    public static string Run()
    {
        void Check(bool pass, string message) { if (!pass) throw new Exception(message); }
        var lines = new List<string>();
        var source = new Endpoint("source", "7.1", 8, 96000, 0x63f);
        foreach (int mask in new[] { 0x3f, 0x60f })
        {
            var native = new Endpoint("native", "5.1", 6, 48000, mask);
            var stereo = new Endpoint("stereo", "Stereo", 2, 96000, 3);
            var settings = SpeakerLayouts.Create(SpeakerLayouts.Get("7.1"), source);
            var roles = SpeakerLayouts.SourceRoles(native);
            int stereoSide = 0;
            foreach (var speaker in settings)
            {
                int channel = Array.IndexOf(roles, speaker.Role);
                speaker.DeviceId = channel >= 0 ? native.Id : stereo.Id;
                speaker.NativeOutput = channel >= 0; speaker.Side = channel >= 0 ? channel : stereoSide++;
                speaker.DeviceChannelMask = channel >= 0 ? native.Mask : 3;
                speaker.DeviceChannels = channel >= 0 ? 6 : 2;
                speaker.GainDb = 0;
            }
            var routes = SpeakerLayouts.BuildRoutes(settings, source, new[] { native, stereo });
            var quick = JsonSerializer.Deserialize<List<SpeakerSetting>>(JsonSerializer.Serialize(settings))!;
            foreach (var s in quick.Where(s => s.NativeOutput)) { s.DeviceId = "previous"; s.NativeOutput = false; }
            string beforePlan = JsonSerializer.Serialize(quick);
            Check(SpeakerLayouts.QuickMapTargets(quick, native).Count == 6 && JsonSerializer.Serialize(quick) == beforePlan, "Quick map preview/cancel must not modify the profile");
            SpeakerLayouts.ApplyQuickMap(quick, native);
            Check(quick.Count(s => s.DeviceId == stereo.Id) == 2 && quick.Count(s => s.DeviceId == native.Id && s.NativeOutput) == 6, "Quick map must preserve the additional stereo pair");
            Check(SpeakerLayouts.BuildRoutes(quick, source, new[] { native, stereo }).Count == 2, "Quick map produces valid 5.1 + stereo streams");
            Check(SpeakerLayouts.QuickMapTargets(quick, native with { Mask = 0 }).Count == 0, "Unknown driver roles require manual mapping");
            Check(routes.Count == 2 && routes.Sum(r => r.Outputs!.Length) == 8, "5.1 + stereo must create two streams with eight outputs");
            Check(routes.SelectMany(r => r.Outputs!).Select(c => c.Source).Order().SequenceEqual(Enumerable.Range(0, 8)), "Every 7.1 source channel must appear once");
            foreach (var route in routes)
            {
                var endpoint = route.DeviceId == native.Id ? native : stereo;
                var format = OutputFormats.Float(endpoint.Rate, route.Outputs!.Length, route.OutputMask, route.NativeOutput);
                if (route.NativeOutput)
                {
                    var ptr = WaveFormat.MarshalToPtr(format);
                    try { Check(Marshal.ReadInt32(ptr, 20) == mask, "Native channel mask must survive serialization"); }
                    finally { Marshal.FreeHGlobal(ptr); }
                }
                var fifo = new AdaptiveOutput(source.Rate, format, route.Outputs);
                var input = new float[9600 * 8];
                for (int frame = 0; frame < 9600; frame++) for (int c = 0; c < 8; c++) input[frame * 8 + c] = (c + 1) * .05f;
                fifo.Push(input, 9600, 8);
                var provider = new FloatWaveProvider(fifo);
                var bytes = new byte[format.BlockAlign * 64 + 16]; Array.Fill(bytes, (byte)0x55);
                int read = provider.Read(bytes, 8, format.BlockAlign * 64);
                Check(read == format.BlockAlign * 64 && bytes[0] == 0x55 && bytes[^1] == 0x55, "Provider must preserve byte offsets and frame alignment");
                for (int f = 0; f < 64; f++) for (int c = 0; c < format.Channels; c++)
                    Check(Math.Abs(BitConverter.ToSingle(bytes, 8 + (f * format.Channels + c) * 4) - (route.Outputs[c].Source + 1) * .05f) < .00001, "Native 5.1 + stereo channel isolation / 96-to-48k conversion");
            }
            var profile = new SurroundProfile { SourceId = source.Id, LayoutId = "7.1", Speakers = settings };
            var restored = SpeakerLayouts.ReadProfile(JsonSerializer.Serialize(profile), new[] { source, native, stereo });
            Check(restored.Version == 3 && restored.Speakers.Count(s => s.NativeOutput) == 6, "Multichannel profile round-trip");
            bool changed = false;
            try { SpeakerLayouts.BuildRoutes(settings, source, new[] { native with { Mask = mask == 0x3f ? 0x60f : 0x3f }, stereo }); }
            catch (InvalidOperationException) { changed = true; }
            Check(changed, "Changed output mask must not silently reorder channels");
            var first = settings.First(s => s.NativeOutput); int previous = first.Side;
            first.Side = settings.First(s => s.NativeOutput && s != first).Side;
            bool duplicate = false;
            try { SpeakerLayouts.BuildRoutes(settings, source, new[] { native, stereo }); } catch (InvalidOperationException) { duplicate = true; }
            Check(duplicate, "Duplicate native output rejected"); first.Side = previous;
            first.NativeOutput = false;
            bool mixed = false;
            try { SpeakerLayouts.BuildRoutes(settings, source, new[] { native, stereo }); } catch (InvalidOperationException) { mixed = true; }
            Check(mixed, "One device cannot mix native and stereo modes");
            lines.Add($"PASS: 7.1 to 5.1 mask {mask:X} + stereo; mapping, PCM isolation, resampling, persistence and layout-change guards");
        }
        foreach (int target in Enumerable.Range(0, 8))
        {
            var speaker = new SpeakerSetting { NativeOutput = true, Side = target, GainDb = 0, DelayMs = 20 };
            var signal = new TestSignal(48000, speaker, 8, 0x63f);
            byte[] bytes = new byte[48000 * 8 * 4]; int count = signal.Read(bytes, 0, bytes.Length);
            bool sound = false;
            for (int frame = 0; frame < count / 32; frame++) for (int c = 0; c < 8; c++)
            {
                float value = BitConverter.ToSingle(bytes, (frame * 8 + c) * 4);
                if (c != target || frame < 960) Check(value == 0, "Test tone leaked or ignored speaker delay");
                else sound |= Math.Abs(value) > .01;
            }
            Check(sound, "Selected native test channel must receive tone");
        }
        var empty = Enumerable.Range(0, 8).Select(c => new OutputChannel(c == 6 ? 0 : -1, c == 6 ? .5f : 0, c == 6 ? 25 : 0)).ToArray();
        var partial = new AdaptiveOutput(48000, OutputFormats.Float(96000, 8, 0x63f, true), empty);
        partial.Push(Enumerable.Repeat(.4f, 9600).ToArray(), 9600, 1);
        float[] output = new float[8 * 128]; partial.Read(output, 0, output.Length);
        Check(output.Where((_, i) => i % 8 != 6).All(v => v == 0) && Math.Abs(output[6] - .2f) < .00001, "Unassigned native channels must stay silent with per-channel gain / 48-to-96k conversion");
        partial.MasterGain = 0; partial.Read(output, 0, output.Length); Check(output.All(v => v == 0), "Native buffered output must obey system mute");
        lines.Add("PASS: all eight test channels isolated; gain, compensation, unassigned-channel silence and system mute");
        return string.Join("\n", lines) + "\n";
    }
}
