using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Flick;

/// <summary>
/// Records what the computer is playing (WASAPI loopback) as 48 kHz 16-bit stereo.
/// Windows sends nothing while the computer is silent, so silence is filled in by the clock; sound stays in sync with the video.
/// </summary>
internal sealed unsafe class Loopback : IDisposable
{
    public const int Rate = 48000, Channels = 2, BytesPerFrame = 4;
    private const int ChunkFrames = 960; // 20 ms

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints_(int flow, int mask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int ctx, IntPtr prop, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int share, uint flags, long buffer, long period, WaveFormat* format, IntPtr session);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int share, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long def, out long min);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr h);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devPos, out ulong qpc);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat { public ushort Tag, Channels; public uint Rate, AvgBytes; public ushort Block, Bits, Extra; }

    private readonly IAudioClient _client;
    private readonly IAudioCaptureClient _capture;
    private readonly Action<byte[], int, long> _sink; // pcm, byte count, start position in frames
    private Thread? _thread;
    private volatile bool _stop;
    private long _startTicks;
    private long _written;
    public long SilenceFrames { get; private set; }
    public long LiveFrames { get; private set; }

    private Loopback(IAudioClient client, IAudioCaptureClient capture, Action<byte[], int, long> sink)
    {
        _client = client; _capture = capture; _sink = sink;
    }

    /// <summary>Returns null if this computer has no usable output device; recording then carries on without sound.</summary>
    public static Loopback? TryOpen(Action<byte[], int, long> sink)
    {
        try
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (en.GetDefaultAudioEndpoint(0 /*render*/, 1 /*multimedia*/, out IMMDevice dev) < 0) return null;
            var iidClient = typeof(IAudioClient).GUID;
            if (dev.Activate(ref iidClient, 1 /*inproc*/, IntPtr.Zero, out object c) < 0) return null;
            var client = (IAudioClient)c;

            var fmt = new WaveFormat { Tag = 1, Channels = Channels, Rate = Rate, AvgBytes = Rate * BytesPerFrame, Block = BytesPerFrame, Bits = 16, Extra = 0 };
            const uint LOOPBACK = 0x00020000, AUTOCONVERT = 0x80000000, SRC_QUALITY = 0x08000000;
            int hr = client.Initialize(0, LOOPBACK | AUTOCONVERT | SRC_QUALITY, 10_000_000 /*1 s*/, 0, &fmt, IntPtr.Zero);
            if (hr < 0) { Log.Write($"audio init failed 0x{hr:X8}"); return null; }

            var iidCap = typeof(IAudioCaptureClient).GUID;
            if (client.GetService(ref iidCap, out object s) < 0) return null;
            return new Loopback(client, (IAudioCaptureClient)s, sink);
        }
        catch (Exception e) { Log.Write("audio open failed: " + e.Message); return null; }
    }

    /// <summary>Starts delivering audio. Position 0 lines up with the given Stopwatch tick (the video's first frame).</summary>
    public void Start(long startTicks)
    {
        _startTicks = startTicks;
        _client.Start();
        _thread = new Thread(Run) { IsBackground = true, Name = "Flick audio", Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    private long Expected() => (long)((Stopwatch.GetTimestamp() - _startTicks) * (double)Rate / Stopwatch.Frequency);

    private void Run()
    {
        var pad = new byte[ChunkFrames * BytesPerFrame];
        bool deviceOk = true;
        while (!_stop)
        {
            try
            {
                if (deviceOk) Drain();
            }
            catch (Exception e)
            {
                // The output device went away mid-recording. Keep the video going with silence.
                Log.Write("audio device lost: " + e.Message);
                deviceOk = false;
            }

            FillSilenceTo(Expected() - ChunkFrames, pad);
            Thread.Sleep(8);
        }
        try { if (deviceOk) Drain(); } catch { }
        FillSilenceTo(Expected(), pad);
    }

    private void Drain()
    {
        while (true)
        {
            int hr = _capture.GetNextPacketSize(out uint next);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            if (next == 0) return;
            hr = _capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            try
            {
                if (frames == 0) continue;
                int bytes = (int)frames * BytesPerFrame;
                var pcm = new byte[bytes];
                if ((flags & 2 /*SILENT*/) == 0) Marshal.Copy(data, pcm, 0, bytes);

                long expected = Expected();
                // Running ahead of the video clock by more than 50 ms (rare clock drift): drop this packet.
                if (_written + frames > expected + Rate / 20) continue;
                // A gap before this packet: fill it so the sound stays lined up.
                var filler = new byte[ChunkFrames * BytesPerFrame];
                FillSilenceTo(expected - frames, filler);

                _sink(pcm, bytes, _written);
                _written += frames;
                LiveFrames += frames;
            }
            finally { _capture.ReleaseBuffer(frames); }
        }
    }

    private void FillSilenceTo(long target, byte[] pad)
    {
        while (_written + ChunkFrames <= target)
        {
            _sink(pad, pad.Length, _written);
            _written += ChunkFrames;
            SilenceFrames += ChunkFrames;
        }
    }

    public void StopAndJoin()
    {
        _stop = true;
        _thread?.Join(2000);
        try { _client.Stop(); } catch { }
    }

    public void Dispose()
    {
        try { Marshal.ReleaseComObject(_capture); Marshal.ReleaseComObject(_client); } catch { }
    }
}
