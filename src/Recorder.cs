using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;
using Vortice.Mathematics;

namespace Flick;

public sealed record RecordOptions(CaptureTarget Target, Quality Quality, bool Sound, string Path);

public sealed record RecordResult(string Path, int Width, int Height, double Seconds, long FileBytes, int Frames, int SkippedFrames, bool HardwareEncoder, bool SoundRequested, bool SoundCaptured, string? EndedEarlyBecause);

/// <summary>
/// Capture â†’ GPU scale and colour convert (D3D11 video processor) â†’ H.264 (hardware when possible) + AAC â†’ MP4.
/// Frames are written on a fixed clock, repeating the last picture when the screen hasn't changed, so the file plays
/// at a steady rate everywhere and sound can't drift.
/// </summary>
public sealed class Recorder
{
    private const int RingSize = 8;
    private readonly RecordOptions _o;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<RecordResult> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _stop;
    private Thread? _thread;

    private ID3D11Device _device = null!;
    private ID3D11DeviceContext _ctx = null!;
    private ID3D11VideoDevice _vdev = null!;
    private ID3D11VideoContext _vctx = null!;
    private ID3D11VideoProcessorEnumerator? _vpEnum;
    private ID3D11VideoProcessor? _vp;
    private ID3D11Texture2D? _latest;
    private ID3D11VideoProcessorInputView? _inputView;
    private readonly ID3D11Texture2D[] _ring = new ID3D11Texture2D[RingSize];
    private readonly ID3D11VideoProcessorOutputView[] _outViews = new ID3D11VideoProcessorOutputView[RingSize];
    private ID3D11Texture2D? _staging;
    private IMFDXGIDeviceManager? _manager;
    private IMFSinkWriter? _writer;
    private int _videoStream, _audioStream = -1;
    private bool _hardware;
    private int _outW, _outH, _fps, _bitrate;
    private int _latestW, _latestH;

    public Recorder(RecordOptions options) { _o = options; }

    /// <summary>Completes once the first frame is in hand and recording has really begun, or throws if it can't.</summary>
    public Task Started => _started.Task;
    public Task<RecordResult> Completed => _done.Task;

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Flick recorder", Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void Stop() => _stop = true;

    private void Run()
    {
        string? path = _o.Path;
        try
        {
            WgcSource.InitThread();
            MediaFactory.MFStartup(true).CheckError();
            try { _done.TrySetResult(Record()); }
            finally { Cleanup(); MediaFactory.MFShutdown(); }
        }
        catch (Exception e)
        {
            Log.Write("record failed: " + e);
            _started.TrySetException(e);
            _done.TrySetException(e);
            try { if (File.Exists(path) && new FileInfo(path).Length == 0) File.Delete(path); } catch { }
        }
    }

    private RecordResult Record()
    {
        CreateDevice();
        using var dxgi = _device.QueryInterface<IDXGIDevice>();
        using var source = new WgcSource(_o.Target, dxgi.NativePointer, cursor: true, buffers: 3);
        var srcSize = source.ItemSize;

        var crop = _o.Target.Crop ?? new PxRect(0, 0, srcSize.W, srcSize.H);
        crop = ClampCrop(crop, srcSize.W, srcSize.H);

        (_outW, _outH) = _o.Quality.OutputSize(crop.W, crop.H);
        _fps = _o.Quality.Fps;
        _bitrate = _o.Quality.VideoBitrate(crop.W, crop.H);

        CreateOutputSurfaces();
        BuildProcessor(crop.W, crop.H);

        Loopback? audio = null;
        bool soundWanted = _o.Sound;
        if (soundWanted)
            audio = Loopback.TryOpen((pcm, bytes, pos) =>
            {
                try { WriteAudio(pcm, bytes, pos); }
                catch (Exception e) { Log.Write("audio write failed: " + e.Message); }
            });

        CreateWriterWithFallback(audio != null);

        source.Start();

        long tick = 10_000_000L / _fps;
        var timer = Native.CreateWaitableTimerExW(IntPtr.Zero, null, 2 /*HIGH_RESOLUTION*/, 0x1F0003);
        long startTicks = 0;
        long lastIndex = -1;
        int frames = 0, skipped = 0, ringPos = 0;
        var content = srcSize;
        bool haveFrame = false, started = false;
        var firstFrameDeadline = Stopwatch.StartNew();
        string? endedEarly = null;
        double seconds = 0;

        try
        {
            while (true)
            {
                // Take the newest frame; older ones are returned to the pool straight away.
                IntPtr newest = IntPtr.Zero;
                while (source.TryGetFrame(out IntPtr f))
                {
                    WgcSource.Close(newest);
                    newest = f;
                }
                if (newest != IntPtr.Zero)
                {
                    try
                    {
                        var size = WgcSource.FrameSize(newest);
                        if (size.W != content.W || size.H != content.H)
                        {
                            // The window was resized. Follow it; the output stays the same size.
                            content = size;
                            source.Recreate(size, 3);
                            crop = ClampCrop(_o.Target.Crop ?? new PxRect(0, 0, size.W, size.H), size.W, size.H);
                            BuildProcessor(crop.W, crop.H);
                        }
                        using var tex = new ID3D11Texture2D(WgcSource.FrameTexture(newest));
                        CopyToLatest(tex, crop, size);
                        haveFrame = true;
                    }
                    finally { WgcSource.Close(newest); }
                }

                if (haveFrame && !started)
                {
                    started = true;
                    startTicks = Stopwatch.GetTimestamp();
                    audio?.Start(startTicks);
                    _started.TrySetResult();
                }

                if (!started)
                {
                    if (_stop) throw new OperationCanceledException();
                    if (firstFrameDeadline.ElapsedMilliseconds > 5000) throw new InvalidOperationException("Windows didn't deliver a picture from that screen.");
                    Thread.Sleep(5);
                    continue;
                }

                if (_stop) break;
                if (!_o.Target.StillValid()) { endedEarly = _o.Target.Kind == TargetKind.Window ? "The window was closed." : "The screen was disconnected."; break; }

                long elapsed = (long)((Stopwatch.GetTimestamp() - startTicks) * (10_000_000.0 / Stopwatch.Frequency));
                long index = elapsed / tick;
                if (index > lastIndex)
                {
                    if (lastIndex >= 0) skipped += (int)(index - lastIndex - 1);
                    long dur = (lastIndex < 0 ? 1 : index - lastIndex) * tick;
                    Blt(_outViews[ringPos]);
                    try { WriteVideo(ringPos, index * tick, dur); }
                    catch (Exception e) when (_hardware && frames < 5)
                    {
                        // Some drivers accept the hardware encoder, then refuse the first frames. Start over in software.
                        Log.Write("hardware encoder failed on first frames, switching to software: " + e.Message);
                        _writer?.Dispose(); _writer = null;
                        _manager?.Dispose(); _manager = null;
                        _hardware = false;
                        CreateWriter(false, audio != null);
                        WriteVideo(ringPos, index * tick, dur);
                    }
                    ringPos = (ringPos + 1) % RingSize;
                    lastIndex = index;
                    frames++;
                }

                // Sleep until just before the next tick.
                long nextDue = (lastIndex + 1) * tick - ((long)((Stopwatch.GetTimestamp() - startTicks) * (10_000_000.0 / Stopwatch.Frequency)));
                if (nextDue > 5_000)
                {
                    long due = -(nextDue - 2_000);
                    if (timer != IntPtr.Zero && Native.SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                        Native.WaitForSingleObject(timer, 100);
                    else Thread.Sleep(1);
                }
            }
            seconds = (lastIndex + 1) * tick / 10_000_000.0;
        }
        catch (Exception e) when (e is not OperationCanceledException && started)
        {
            // Disk full, GPU reset and the like. Save what we have rather than lose it.
            Log.Write("recording cut short: " + e);
            endedEarly = "Recording stopped early: " + e.Message;
            seconds = Math.Max(0, lastIndex + 1) * tick / 10_000_000.0;
        }
        finally
        {
            if (timer != IntPtr.Zero) Native.CloseHandle(timer);
        }

        bool soundCaptured = audio != null;
        long live = 0, silent = 0;
        if (audio != null)
        {
            audio.StopAndJoin();
            live = audio.LiveFrames; silent = audio.SilenceFrames;
            audio.Dispose();
        }

        _writer!.Finalize();
        _writer.Dispose();
        _writer = null;

        long bytes = new FileInfo(_o.Path).Length;
        Log.Write($"recorded {_o.Quality.Name} {_outW}x{_outH}@{_fps} {seconds:0.0}s {bytes} bytes, frames {frames}, skipped {skipped}, hw {_hardware}, audio live {live} silent {silent}");
        return new RecordResult(_o.Path, _outW, _outH, seconds, bytes, frames, skipped, _hardware, soundWanted, soundCaptured, endedEarly);
    }

    private static PxRect ClampCrop(PxRect c, int w, int h)
    {
        int x = Math.Clamp(c.X, 0, Math.Max(0, w - 2)), y = Math.Clamp(c.Y, 0, Math.Max(0, h - 2));
        // Even sizes, so "no scaling" really means no scaling and text stays crisp.
        return new PxRect(x, y, Math.Max(2, Math.Clamp(c.W, 2, w - x) & ~1), Math.Max(2, Math.Clamp(c.H, 2, h - y) & ~1));
    }

    private void CreateDevice()
    {
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
        D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport, levels, out ID3D11Device? dev, out ID3D11DeviceContext? ctx).CheckError();
        _device = dev!; _ctx = ctx!;
        using var mt = _device.QueryInterface<ID3D11Multithread>();
        mt.SetMultithreadProtected(true);
        _vdev = _device.QueryInterface<ID3D11VideoDevice>();
        _vctx = _ctx.QueryInterface<ID3D11VideoContext>();
    }

    private void CreateOutputSurfaces()
    {
        var desc = new Texture2DDescription
        {
            Width = (uint)_outW, Height = (uint)_outH, MipLevels = 1, ArraySize = 1, Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.VideoEncoder,
        };
        for (int i = 0; i < RingSize; i++)
        {
            try { _ring[i] = _device.CreateTexture2D(desc); }
            catch { desc.BindFlags = BindFlags.RenderTarget; _ring[i] = _device.CreateTexture2D(desc); }
        }
    }

    private void BuildProcessor(int inW, int inH)
    {
        // Rebuilt only when the picture's size changes.
        if (_vp != null && inW == _latestW && inH == _latestH) { SetRects(inW, inH); return; }
        _inputView?.Dispose(); _latest?.Dispose(); _vp?.Dispose(); _vpEnum?.Dispose();
        foreach (var v in _outViews) v?.Dispose();

        _latest = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)inW, Height = (uint)inH, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        _latestW = inW; _latestH = inH;

        _vpEnum = _vdev.CreateVideoProcessorEnumerator(new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational((uint)_fps, 1), InputWidth = (uint)inW, InputHeight = (uint)inH,
            OutputFrameRate = new Rational((uint)_fps, 1), OutputWidth = (uint)_outW, OutputHeight = (uint)_outH,
            Usage = VideoUsage.PlaybackNormal,
        });
        _vp = _vdev.CreateVideoProcessor(_vpEnum, 0);

        _inputView = _vdev.CreateVideoProcessorInputView(_latest, _vpEnum, new VideoProcessorInputViewDescription
        {
            FourCC = 0, ViewDimension = VideoProcessorInputViewDimension.Texture2D, Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
        });
        for (int i = 0; i < RingSize; i++)
            _outViews[i] = _vdev.CreateVideoProcessorOutputView(_ring[i], _vpEnum, new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D, Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
            });

        var inCs = new VideoProcessorColorSpace { Usage = 0, RGB_Range = 0 /*full*/, YCbCr_Matrix = 1, Nominal_Range = 2 /*0-255*/ };
        var outCs = new VideoProcessorColorSpace { Usage = 0, RGB_Range = 0, YCbCr_Matrix = 1 /*BT.709*/, Nominal_Range = 1 /*16-235*/ };
        _vctx.VideoProcessorSetStreamColorSpace(_vp, 0, inCs);
        _vctx.VideoProcessorSetOutputColorSpace(_vp, outCs);
        _vctx.VideoProcessorSetStreamFrameFormat(_vp, 0, VideoFrameFormat.Progressive);
        _vctx.VideoProcessorSetStreamAutoProcessingMode(_vp, 0, false);
        SetRects(inW, inH);
    }

    private void SetRects(int inW, int inH)
    {
        // Keep the picture's shape if a window was resized: fit it, centred, on black.
        double s = Math.Min(_outW / (double)inW, _outH / (double)inH);
        int dw = Math.Min(_outW, (int)Math.Round(inW * s)), dh = Math.Min(_outH, (int)Math.Round(inH * s));
        int dx = (_outW - dw) / 2, dy = (_outH - dh) / 2;
        _vctx.VideoProcessorSetStreamSourceRect(_vp!, 0, true, new Vortice.RawRect(0, 0, inW, inH));
        _vctx.VideoProcessorSetStreamDestRect(_vp!, 0, true, new Vortice.RawRect(dx, dy, dx + dw, dy + dh));
    }

    private void CopyToLatest(ID3D11Texture2D frame, PxRect crop, WgcSource.SizeInt32 size)
    {
        int right = Math.Min(crop.Right, size.W), bottom = Math.Min(crop.Bottom, size.H);
        if (right <= crop.X || bottom <= crop.Y) return;
        var box = new Box(crop.X, crop.Y, 0, right, bottom, 1);
        _ctx.CopySubresourceRegion(_latest!, 0, 0, 0, 0, frame, 0, box);
    }

    private void Blt(ID3D11VideoProcessorOutputView view)
    {
        var streams = new[] { new VideoProcessorStream { Enable = true, OutputIndex = 0, InputFrameOrField = 0, InputSurface = _inputView! } };
        _vctx.VideoProcessorBlt(_vp!, view, 0, 1, streams).CheckError();
    }

    // ---- Writer ----

    private static ulong Pack(int hi, int lo) => ((ulong)(uint)hi << 32) | (uint)lo;

    private void CreateWriterWithFallback(bool withAudio)
    {
        if (Environment.GetEnvironmentVariable("FLICK_SOFTWARE") == "1")
        {
            CreateWriter(false, withAudio);
            _hardware = false;
            return;
        }
        try
        {
            CreateWriter(true, withAudio);
            _hardware = true;
        }
        catch (Exception e)
        {
            Log.Write("hardware encoder unavailable, using software: " + e.Message);
            _writer?.Dispose(); _writer = null;
            _manager?.Dispose(); _manager = null;
            CreateWriter(false, withAudio);
            _hardware = false;
        }
    }

    private void CreateWriter(bool hardware, bool withAudio)
    {
        using var attrs = MediaFactory.MFCreateAttributes(6);
        attrs.Set(SinkWriterAttributeKeys.LowLatency, 1u);
        if (hardware)
        {
            attrs.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1u);
            _manager = MediaFactory.MFCreateDXGIDeviceManager();
            _manager.ResetDevice(_device).CheckError();
            attrs.Set(SinkWriterAttributeKeys.D3DManager, _manager);
        }

        var writer = MediaFactory.MFCreateSinkWriterFromURL(_o.Path, null!, attrs);
        _writer = writer;

        using (var outType = MediaFactory.MFCreateMediaType())
        {
            outType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            outType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            outType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)_bitrate);
            outType.Set(MediaTypeAttributeKeys.Mpeg2Profile, 100u /*High*/);
            VideoCommon(outType);
            _videoStream = writer.AddStream(outType);
        }
        using (var inType = MediaFactory.MFCreateMediaType())
        {
            inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            inType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            VideoCommon(inType);
            inType.Set(MediaTypeAttributeKeys.DefaultStride, (uint)_outW);
            writer.SetInputMediaType(_videoStream, inType, null);
        }

        if (withAudio)
        {
            using var aOut = MediaFactory.MFCreateMediaType();
            aOut.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            aOut.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
            aOut.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
            aOut.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)Loopback.Rate);
            aOut.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)Loopback.Channels);
            aOut.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(Quality.AudioBitsPerSecond / 8));
            _audioStream = writer.AddStream(aOut);

            using var aIn = MediaFactory.MFCreateMediaType();
            aIn.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            aIn.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
            aIn.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
            aIn.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)Loopback.Rate);
            aIn.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)Loopback.Channels);
            aIn.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)Loopback.BytesPerFrame);
            aIn.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(Loopback.Rate * Loopback.BytesPerFrame));
            writer.SetInputMediaType(_audioStream, aIn, null);
        }

        writer.BeginWriting();
    }

    private void VideoCommon(IMFMediaType t)
    {
        t.Set(MediaTypeAttributeKeys.FrameSize, Pack(_outW, _outH));
        t.Set(MediaTypeAttributeKeys.FrameRate, Pack(_fps, 1));
        t.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
        t.Set(MediaTypeAttributeKeys.InterlaceMode, 2u /*progressive*/);
        t.Set(MediaTypeAttributeKeys.YuvMatrix, 1u /*BT.709*/);
        t.Set(MediaTypeAttributeKeys.VideoPrimaries, 2u /*BT.709*/);
        t.Set(MediaTypeAttributeKeys.TransferFunction, 5u /*BT.709*/);
        t.Set(MediaTypeAttributeKeys.VideoNominalRange, 2u /*16-235*/);
    }

    private void WriteVideo(int ring, long time, long duration)
    {
        using var sample = MediaFactory.MFCreateSample();
        IMFMediaBuffer buffer;
        if (_hardware)
        {
            buffer = MediaFactory.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, _ring[ring], 0, false);
            using var b2 = buffer.QueryInterface<IMF2DBuffer>();
            buffer.CurrentLength = b2.ContiguousLength;
        }
        else buffer = ReadBack(ring);
        using (buffer)
        {
            sample.AddBuffer(buffer);
            sample.SampleTime = time;
            sample.SampleDuration = duration;
            _writer!.WriteSample(_videoStream, sample);
        }
    }

    /// <summary>Software path: copy the converted frame to CPU memory for the software encoder.</summary>
    private unsafe IMFMediaBuffer ReadBack(int ring)
    {
        _staging ??= _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_outW, Height = (uint)_outH, MipLevels = 1, ArraySize = 1, Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read,
        });
        _ctx.CopyResource(_staging, _ring[ring]);
        int size = _outW * _outH * 3 / 2;
        var buffer = MediaFactory.MFCreateMemoryBuffer(size);
        var map = _ctx.Map(_staging, 0, MapMode.Read);
        try
        {
            IntPtr dst;
            buffer.Lock(out dst, out _, out _);
            byte* d = (byte*)dst, s = (byte*)map.DataPointer;
            int pitch = (int)map.RowPitch;
            for (int y = 0; y < _outH; y++) Buffer.MemoryCopy(s + y * pitch, d + y * _outW, _outW, _outW);
            // The UV plane sits below the Y plane, whose height the driver may align; staging NV12 maps as one block.
            byte* uvSrc = s + pitch * _outH; byte* uvDst = d + _outW * _outH;
            for (int y = 0; y < _outH / 2; y++) Buffer.MemoryCopy(uvSrc + y * pitch, uvDst + y * _outW, _outW, _outW);
            buffer.Unlock();
            buffer.CurrentLength = size;
        }
        finally { _ctx.Unmap(_staging, 0); }
        return buffer;
    }

    private void WriteAudio(byte[] pcm, int bytes, long positionFrames)
    {
        var writer = _writer;
        if (writer == null) return;
        using var buffer = MediaFactory.MFCreateMemoryBuffer(bytes);
        IntPtr dst;
        buffer.Lock(out dst, out _, out _);
        Marshal.Copy(pcm, 0, dst, bytes);
        buffer.Unlock();
        buffer.CurrentLength = bytes;
        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = positionFrames * 10_000_000L / Loopback.Rate;
        sample.SampleDuration = (bytes / Loopback.BytesPerFrame) * 10_000_000L / Loopback.Rate;
        writer.WriteSample(_audioStream, sample);
    }

    private void Cleanup()
    {
        try { _writer?.Dispose(); } catch { }
        _writer = null;
        foreach (var v in _outViews) v?.Dispose();
        foreach (var t in _ring) t?.Dispose();
        _staging?.Dispose();
        _inputView?.Dispose(); _latest?.Dispose(); _vp?.Dispose(); _vpEnum?.Dispose();
        _manager?.Dispose();
        _vctx?.Dispose(); _vdev?.Dispose();
        try { _ctx?.ClearState(); _ctx?.Flush(); } catch { }
        _ctx?.Dispose(); _device?.Dispose();
    }
}
