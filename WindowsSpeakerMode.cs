using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace ChannelBridge;

// Windows exposes no public API equivalent to the speaker setup wizard. Keep the
// policy ABI isolated; verify read-back and restore the original formats on failure.
public sealed class WindowsSpeakerMode : IDisposable
{
    readonly IAudioPolicy policy;
    readonly string id;
    IntPtr originalDevice, originalMix;
    bool changed, committed;
    uint originalSpeakers;
    bool speakersChanged;
    public Endpoint Actual { get; private set; }
    public static int Mask(SpeakerLayout layout) => layout.Roles.Sum(r => SpeakerLayouts.Role(r).Bit);
    public WindowsSpeakerMode(string sourceId, SpeakerLayout layout)
    {
        id = sourceId;
        Actual = AudioEngine.Devices().FirstOrDefault(d => d.Id == id) ?? throw new IOException("所选音源设备未连接。");
        policy = (IAudioPolicy)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9"), true)!)!;
        try
        {
            Marshal.ThrowExceptionForHR(policy.GetDeviceFormat(id, 0, out originalDevice));
            Marshal.ThrowExceptionForHR(policy.GetMixFormat(id, out originalMix));
            originalSpeakers = ReadSpeakers(id);
            int mask = Mask(layout), channels = layout.Roles.Length;
            if (Actual.Channels == channels && Actual.Mask == mask && originalSpeakers == mask) return;
            // Open a writable property store before touching the format, so a denied
            // write can be retried elevated without partially changing Windows.
            using var enumerator = new MMDeviceEnumerator(); using var target = enumerator.GetDevice(id);
            target.GetPropertyInformation(StorageAccessMode.ReadWrite);
            int bits = Marshal.ReadInt16(originalDevice, 14);
            ushort tag = unchecked((ushort)Marshal.ReadInt16(originalDevice));
            var sub = tag == 0xfffe ? Marshal.PtrToStructure<Guid>(originalDevice + 24) : new Guid(tag == 3 ? "00000003-0000-0010-8000-00aa00389b71" : "00000001-0000-0010-8000-00aa00389b71");
            var endpoint = MakeFormat(Actual.Rate, bits, channels, mask, sub);
            if (tag == 0xfffe) endpoint.ValidBits = (ushort)Marshal.ReadInt16(originalDevice, 18);
            var mix = MakeFormat(Actual.Rate, 32, channels, mask, new Guid("00000003-0000-0010-8000-00aa00389b71"));
            IntPtr devicePtr = Marshal.AllocHGlobal(40), mixPtr = Marshal.AllocHGlobal(40);
            try
            {
                Marshal.StructureToPtr(endpoint, devicePtr, false); Marshal.StructureToPtr(mix, mixPtr, false);
                changed = true;
                Marshal.ThrowExceptionForHR(policy.SetDeviceFormat(id, devicePtr, mixPtr));
                speakersChanged = true; SetSpeakers(target, (uint)mask);
            }
            finally { Marshal.FreeHGlobal(devicePtr); Marshal.FreeHGlobal(mixPtr); }
            for (int i = 0; i < 30; i++)
            {
                var actual = AudioEngine.Devices().FirstOrDefault(d => d.Id == id);
                if (actual?.Channels == channels && actual.Mask == mask && ReadSpeakers(id) == mask) { Actual = actual; return; }
                Thread.Sleep(100);
            }
            throw new IOException("Windows 未接受所选声道模式：" + layout.Name);
        }
        catch (Exception ex)
        {
            string rollback = "";
            try { Restore(); } catch (Exception undo) { rollback = "；恢复旧格式失败：" + undo.Message; }
            Release();
            throw new InvalidOperationException("无法设置 Windows 音源声道模式：" + ex.Message + rollback, ex);
        }
    }
    public void Commit() => committed = true;
    void Restore()
    {
        var errors = new List<Exception>();
        try
        {
            if (changed && !committed && originalDevice != IntPtr.Zero && originalMix != IntPtr.Zero)
            { Marshal.ThrowExceptionForHR(policy.SetDeviceFormat(id, originalDevice, originalMix)); changed = false; }
        }
        catch (Exception ex) { errors.Add(ex); }
        try
        {
            if (speakersChanged && !committed)
            {
                using var e = new MMDeviceEnumerator(); using var d = e.GetDevice(id);
                d.GetPropertyInformation(StorageAccessMode.ReadWrite); SetSpeakers(d, originalSpeakers); speakersChanged = false;
            }
        }
        catch (Exception ex) { errors.Add(ex); }
        if (errors.Count > 0) throw new AggregateException("恢复 Windows 音频设置失败。", errors);
    }
    void Release()
    {
        if (originalDevice != IntPtr.Zero) { Marshal.FreeCoTaskMem(originalDevice); originalDevice = IntPtr.Zero; }
        if (originalMix != IntPtr.Zero) { Marshal.FreeCoTaskMem(originalMix); originalMix = IntPtr.Zero; }
        if (Marshal.IsComObject(policy)) Marshal.FinalReleaseComObject(policy);
    }
    public void Dispose() { try { Restore(); } finally { Release(); } }
    public static uint ReadSpeakers(string id)
    { using var e = new MMDeviceEnumerator(); using var d = e.GetDevice(id); return Convert.ToUInt32(d.Properties[PropertyKeys.PKEY_AudioEndpoint_PhysicalSpeakers].Value); }
    static void SetSpeakers(MMDevice device, uint mask)
    {
        IntPtr memory = Marshal.AllocHGlobal(24);
        try
        {
            for (int i = 0; i < 24; i++) Marshal.WriteByte(memory, i, 0);
            Marshal.WriteInt16(memory, 19); // VT_UI4
            Marshal.WriteInt32(memory, 8, unchecked((int)mask));
            device.Properties.SetValue(PropertyKeys.PKEY_AudioEndpoint_PhysicalSpeakers, Marshal.PtrToStructure<PropVariant>(memory));
            device.Properties.Commit();
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
    public static NativeFormat MakeFormat(int rate, int bits, int channels, int mask, Guid sub) => new()
    { Tag = 0xfffe, Channels = (ushort)channels, Rate = (uint)rate, BytesPerSecond = (uint)(rate * channels * bits / 8), Align = (ushort)(channels * bits / 8), Bits = (ushort)bits, Extra = 22, ValidBits = (ushort)bits, Mask = (uint)mask, SubFormat = sub };
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct NativeFormat
    {
        public ushort Tag, Channels;
        public uint Rate, BytesPerSecond;
        public ushort Align, Bits, Extra, ValidBits;
        public uint Mask;
        public Guid SubFormat;
    }
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioPolicy
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int useDefault, out IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr deviceFormat, IntPtr mixFormat);
    }
}
