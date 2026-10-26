using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using HollowKnightVision.Windows.Core;
using HollowKnightVision.Windows.Host;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace HollowKnightVision.Windows.App;

/// <summary>Window-owned GPU capture with a bounded pool; never captures the desktop.</summary>
internal sealed class WindowsGameCapture : IDisposable
{
    private readonly GdiWindowCapture fallback = new();
    private readonly SemaphoreSlim arrived = new(0, 1);
    private readonly object signalGate = new();
    private ID3D11Device? device;
    private ID3D11DeviceContext? context;
    private IDirect3DDevice? winrtDevice;
    private ID3D11Texture2D? staging;
    private Direct3D11CaptureFramePool? pool;
    private GraphicsCaptureSession? session;
    private GraphicsCaptureItem? item;
    private SizeInt32 poolSize;
    private nint windowHandle;
    private bool useFallback, disposed;
    private volatile bool closed;
    internal string Backend => useFallback ? "GDI" : "Windows Graphics Capture";
    internal string? FallbackReason { get; private set; }
    internal string? LastFrameIssue { get; private set; }
    internal double CaptureTimestamp { get; private set; }
    internal double ReadbackMilliseconds { get; private set; }

    internal async Task<BgraFrame?> CaptureAsync(HollowKnightWindow window, long captureId, CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellation.ThrowIfCancellationRequested();
        if (window.Handle != windowHandle)
        {
            DisposeSession();
            windowHandle = window.Handle;
            closed = false;
            useFallback = false;
            FallbackReason = null;
            try { Start(window); }
            catch (Exception error) when (error is COMException or NotSupportedException or SharpGen.Runtime.SharpGenException)
            {
                DisposeSession();
                useFallback = true;
                FallbackReason = error.Message;
            }
        }
        if (closed || window.IsMinimized)
        {
            await Task.Delay(100, cancellation).ConfigureAwait(false);
            return null;
        }
        if (useFallback)
        {
            var started = Stopwatch.GetTimestamp();
            var result = CpuBgraNormalizer.Normalize(fallback.Capture(window, captureId));
            CaptureTimestamp = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            ReadbackMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return result;
        }

        Direct3D11CaptureFrame? latest = pool!.TryGetNextFrame();
        if (latest is null)
        {
            // A static/minimized source need not publish. Return periodically so
            // the caller can observe game exit and cancellation without stale pixels.
            if (!await arrived.WaitAsync(TimeSpan.FromMilliseconds(250), cancellation).ConfigureAwait(false)) return null;
            latest = pool.TryGetNextFrame();
        }
        if (latest is null) return null;
        // At most two outstanding buffers, retaining only the newest available.
        var next = pool.TryGetNextFrame();
        if (next is not null) { latest.Dispose(); latest = next; }
        var size = latest.ContentSize;
        if (size.Width <= 0 || size.Height <= 0) { latest.Dispose(); return null; }
        if (size.Width != poolSize.Width || size.Height != poolSize.Height)
        {
            // The old texture can be clipped after a resize. Drop it before
            // resetting the pool; never scale a partial frame into tracking.
            latest.Dispose();
            pool.Recreate(winrtDevice!, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
            poolSize = size;
            return null;
        }
        using (latest)
        {
            var started = Stopwatch.GetTimestamp();
            // Some compositor frames report a presentation time ahead of receipt.
            // Never feed a future timestamp to tracking, receiver joins or latency.
            var receivedAt = started / (double)Stopwatch.Frequency;
            var sourceTime = latest.SystemRelativeTime.TotalSeconds;
            var timestamp = sourceTime > 0 ? Math.Min(sourceTime, receivedAt) : receivedAt;
            using var surface = latest.Surface;
            var access = surface.As<IDirect3DDxgiInterfaceAccess>();
            var textureId = typeof(ID3D11Texture2D).GUID;
            Marshal.ThrowExceptionForHR(access.GetInterface(ref textureId, out var texturePointer));
            using var texture = new ID3D11Texture2D(texturePointer);
            var description = texture.Description;
            if (staging is null || staging.Description.Width != description.Width || staging.Description.Height != description.Height)
            {
                staging?.Dispose();
                description.Usage = ResourceUsage.Staging;
                description.BindFlags = BindFlags.None;
                description.CPUAccessFlags = CpuAccessFlags.Read;
                description.MiscFlags = ResourceOptionFlags.None;
                staging = device!.CreateTexture2D(description);
            }
            var crop = ClientCrop(window.Handle, size.Width, size.Height);
            if (crop is null) return null;
            context!.CopyResource(staging, texture);
            var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            var stride = checked(crop.Value.Width * BgraFrame.BytesPerPixel);
            var pixels = new byte[checked(stride * crop.Value.Height)];
            try
            {
                for (var row = 0; row < crop.Value.Height; row++)
                    Marshal.Copy(mapped.DataPointer + checked((int)mapped.RowPitch * (crop.Value.Y + row) + crop.Value.X * 4),
                        pixels, row * stride, stride);
            }
            finally { context.Unmap(staging, 0); }
            var age = Math.Max(0, Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency - timestamp);
            var frame = new BgraFrame(crop.Value.Width, crop.Value.Height, stride, pixels, captureId,
                DateTimeOffset.UtcNow - TimeSpan.FromSeconds(age));
            var normalized = CpuBgraNormalizer.Normalize(frame);
            CaptureTimestamp = timestamp;
            ReadbackMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return normalized;
        }
    }

    private void Start(HollowKnightWindow window)
    {
        if (!GraphicsCaptureSession.IsSupported()) throw new NotSupportedException("Windows Graphics Capture is unavailable.");
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, null, out device).CheckError();
        context = device!.ImmediateContext;
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var inspectable));
        try { winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); }
        finally { Marshal.Release(inspectable); }
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var itemId = new Guid("79c3f95b-31f7-4ec2-a464-632ef5d30760");
        Marshal.ThrowExceptionForHR(interop.CreateForWindow(window.Handle, ref itemId, out var itemPointer));
        try { item = GraphicsCaptureItem.FromAbi(itemPointer); }
        finally { Marshal.Release(itemPointer); }
        item.Closed += OnClosed;
        poolSize = item.Size;
        pool = Direct3D11CaptureFramePool.CreateFreeThreaded(winrtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, poolSize);
        pool.FrameArrived += OnFrame;
        session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;
        session.StartCapture();
    }

    private void OnClosed(GraphicsCaptureItem sender, object args)
    {
        if (sender == item) closed = true;
    }
    private void OnFrame(Direct3D11CaptureFramePool sender, object args)
    {
        lock (signalGate)
        {
            if (!disposed && sender == pool && arrived.CurrentCount == 0) arrived.Release();
        }
    }

    private PixelRect? ClientCrop(nint handle, int width, int height)
    {
        if (!GetClientRect(handle, out var client)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var origin = new NativePoint();
        if (!ClientToScreen(handle, ref origin)) throw new Win32Exception(Marshal.GetLastWin32Error());
        Marshal.ThrowExceptionForHR(DwmGetWindowAttribute(handle, 9, out var bounds, Marshal.SizeOf<NativeRect>()));
        var x = origin.X - bounds.Left;
        var y = origin.Y - bounds.Top;
        var clientWidth = client.Right - client.Left;
        var clientHeight = client.Bottom - client.Top;
        if (x < 0 || y < 0 || clientWidth <= 0 || clientHeight <= 0
            || x + clientWidth > width || y + clientHeight > height)
        {
            LastFrameIssue = $"Client {x},{y} {clientWidth}x{clientHeight} outside capture {width}x{height}.";
            return null;
        }
        LastFrameIssue = null;
        return new PixelRect(x, y, clientWidth, clientHeight);
    }

    private void DisposeSession()
    {
        if (pool is not null) pool.FrameArrived -= OnFrame;
        if (item is not null) item.Closed -= OnClosed;
        session?.Dispose(); session = null;
        pool?.Dispose(); pool = null;
        staging?.Dispose(); staging = null;
        winrtDevice?.Dispose(); winrtDevice = null;
        context?.Dispose(); context = null;
        device?.Dispose(); device = null;
        item = null;
        while (arrived.Wait(0)) { }
    }
    public void Dispose()
    {
        lock (signalGate) { if (disposed) return; disposed = true; }
        DisposeSession();
        lock (signalGate) arrived.Dispose();
    }

    [ComImport, Guid("3628e81b-3cac-4c60-b7f4-23ce0e0c3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig] int CreateForWindow(nint window, ref Guid iid, out nint item);
        [PreserveSig] int CreateForMonitor(nint monitor, ref Guid iid, out nint item);
    }
    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        [PreserveSig] int GetInterface(ref Guid iid, out nint result);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint device, out nint graphicsDevice);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out NativeRect rect, int size);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(nint window, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(nint window, ref NativePoint point);
}
