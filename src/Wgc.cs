using System;
using System.Runtime.InteropServices;

namespace Flick;

/// <summary>
/// Windows.Graphics.Capture, called straight through its COM vtables.
/// The official C# projection is a 25 MB DLL; these seven interfaces are all Flick needs, so the app stays small.
/// Frames are polled, not evented, which fits the fixed-rate recording loop.
/// </summary>
internal sealed unsafe class WgcSource : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] public struct SizeInt32 { public int W, H; }

    private static readonly Guid IID_Interop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static Guid IID_Item = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static Guid IID_PoolStatics2 = new("589B103F-6BBC-5DF5-A991-02E28B3B66D5");
    private static Guid IID_Session2 = new("2C39AE40-7D2E-5044-804E-8B6799D4CF9E");
    private static Guid IID_Session3 = new("F2CDD966-22AE-5EA1-9596-3A289344C3BE");
    private static Guid IID_DxgiAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static Guid IID_Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
    private static Guid IID_Closable = new("30D5A829-7FA4-4026-83BB-D75BAE4EA99E");
    private const int FMT_BGRA8 = 87;

    [DllImport("combase", PreserveSig = true)] private static extern int RoGetActivationFactory(IntPtr hstr, ref Guid iid, out IntPtr factory);
    [DllImport("combase", PreserveSig = true, CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string s, int len, out IntPtr hstr);
    [DllImport("combase", PreserveSig = true)] private static extern int WindowsDeleteString(IntPtr hstr);
    [DllImport("combase", PreserveSig = true)] private static extern int RoInitialize(int type);
    [DllImport("d3d11", PreserveSig = true)] private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgi, out IntPtr inspectable);

    private IntPtr _item, _pool, _session, _rtDevice;

    public SizeInt32 ItemSize { get; private set; }

    public static void InitThread() { _ = RoInitialize(1); }

    private static IntPtr Factory(string cls, Guid iid)
    {
        Check(WindowsCreateString(cls, cls.Length, out IntPtr h));
        try { Check(RoGetActivationFactory(h, ref iid, out IntPtr f)); return f; }
        finally { WindowsDeleteString(h); }
    }

    private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
    private static void* Slot(IntPtr self, int i) => (*(void***)self)[i];

    public WgcSource(CaptureTarget target, IntPtr dxgiDevice, bool cursor, int buffers)
    {
        IntPtr interop = Factory("Windows.Graphics.Capture.GraphicsCaptureItem", IID_Interop);
        try
        {
            IntPtr handle = target.Kind == TargetKind.Window ? target.Window : target.Monitor;
            int slot = target.Kind == TargetKind.Window ? 3 : 4;
            IntPtr item;
            fixed (Guid* g = &IID_Item)
                Check(((delegate* unmanaged<IntPtr, IntPtr, Guid*, IntPtr*, int>)Slot(interop, slot))(interop, handle, g, &item));
            _item = item;
        }
        finally { Marshal.Release(interop); }

        SizeInt32 size;
        Check(((delegate* unmanaged<IntPtr, SizeInt32*, int>)Slot(_item, 7))(_item, &size));
        ItemSize = size;

        Check(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out _rtDevice));

        IntPtr statics = Factory("Windows.Graphics.Capture.Direct3D11CaptureFramePool", IID_PoolStatics2);
        try
        {
            IntPtr pool;
            Check(((delegate* unmanaged<IntPtr, IntPtr, int, int, SizeInt32, IntPtr*, int>)Slot(statics, 6))(statics, _rtDevice, FMT_BGRA8, buffers, size, &pool));
            _pool = pool;
        }
        finally { Marshal.Release(statics); }

        IntPtr session;
        Check(((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)Slot(_pool, 10))(_pool, _item, &session));
        _session = session;

        // Optional: older Windows builds lack these, and capture still works without them.
        if (Marshal.QueryInterface(_session, in IID_Session2, out IntPtr s2) >= 0)
        {
            ((delegate* unmanaged<IntPtr, byte, int>)Slot(s2, 7))(s2, (byte)(cursor ? 1 : 0));
            Marshal.Release(s2);
        }
        if (Marshal.QueryInterface(_session, in IID_Session3, out IntPtr s3) >= 0)
        {
            ((delegate* unmanaged<IntPtr, byte, int>)Slot(s3, 7))(s3, 0); // no yellow border
            Marshal.Release(s3);
        }
    }

    public void Start() => Check(((delegate* unmanaged<IntPtr, int>)Slot(_session, 6))(_session));

    public void Recreate(SizeInt32 size, int buffers)
    {
        Check(((delegate* unmanaged<IntPtr, IntPtr, int, int, SizeInt32, int>)Slot(_pool, 6))(_pool, _rtDevice, FMT_BGRA8, buffers, size));
    }

    /// <summary>Returns the next frame's texture (caller owns the pointer), or zero if none is waiting.</summary>
    public bool TryGetFrame(out IntPtr frame)
    {
        IntPtr f;
        int hr = ((delegate* unmanaged<IntPtr, IntPtr*, int>)Slot(_pool, 7))(_pool, &f);
        frame = hr >= 0 ? f : IntPtr.Zero;
        return frame != IntPtr.Zero;
    }

    public static SizeInt32 FrameSize(IntPtr frame)
    {
        SizeInt32 s;
        Check(((delegate* unmanaged<IntPtr, SizeInt32*, int>)Slot(frame, 8))(frame, &s));
        return s;
    }

    /// <summary>The frame's D3D11 texture. Release with Marshal.Release (or wrap in a Vortice object).</summary>
    public static IntPtr FrameTexture(IntPtr frame)
    {
        IntPtr surface;
        Check(((delegate* unmanaged<IntPtr, IntPtr*, int>)Slot(frame, 6))(frame, &surface));
        try
        {
            Check(Marshal.QueryInterface(surface, in IID_DxgiAccess, out IntPtr access));
            try
            {
                IntPtr tex;
                fixed (Guid* g = &IID_Texture2D)
                    Check(((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Slot(access, 3))(access, g, &tex));
                return tex;
            }
            finally { Marshal.Release(access); }
        }
        finally { Marshal.Release(surface); }
    }

    /// <summary>
    /// Frames, sessions and pools must be closed, not just released. Otherwise they keep the GPU device and its driver threads
    /// alive, which cost hundreds of MB per capture in testing.
    /// </summary>
    public static void Close(IntPtr obj)
    {
        if (obj == IntPtr.Zero) return;
        if (Marshal.QueryInterface(obj, in IID_Closable, out IntPtr closable) >= 0)
        {
            ((delegate* unmanaged<IntPtr, int>)Slot(closable, 6))(closable);
            Marshal.Release(closable);
        }
        Marshal.Release(obj);
    }

    public void Dispose()
    {
        Close(_session);
        Close(_pool);
        if (_item != IntPtr.Zero) Marshal.Release(_item);
        Close(_rtDevice);
        _session = _pool = _item = _rtDevice = IntPtr.Zero;
    }
}
