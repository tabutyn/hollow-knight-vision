using System.ComponentModel;
using System.Runtime.InteropServices;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.Host;

public sealed class GdiWindowCapture
{
    private const uint SourceCopy = 0x00CC0020;
    private const uint CaptureLayeredWindows = 0x40000000;
    private const uint DibRgbColors = 0;
    private const uint PrintWindowClientOnly = 0x00000001;
    private const uint PrintWindowRenderFullContent = 0x00000002;

    public BgraFrame Capture(HollowKnightWindow window, long captureId)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Win32 GDI capture requires Windows.");
        }
        if (window.IsMinimized)
        {
            throw new InvalidOperationException("Hollow Knight is minimized; the GDI bootstrap cannot capture it.");
        }
        if (!GetClientRect(window.Handle, out var clientRect))
        {
            throw LastWin32("GetClientRect");
        }

        var width = clientRect.Right - clientRect.Left;
        var height = clientRect.Bottom - clientRect.Top;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("Hollow Knight has an empty client area.");
        }

        var clientOrigin = new NativePoint { X = 0, Y = 0 };
        if (!ClientToScreen(window.Handle, ref clientOrigin))
        {
            throw LastWin32("ClientToScreen");
        }

        var screen = GetDC(nint.Zero);
        if (screen == nint.Zero) throw LastWin32("GetDC");
        var memory = nint.Zero;
        var bitmap = nint.Zero;
        var priorObject = nint.Zero;
        try
        {
            memory = CreateCompatibleDC(screen);
            if (memory == nint.Zero) throw LastWin32("CreateCompatibleDC");
            var bitmapInfo = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = width,
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0,
                    SizeImage = (uint)checked(width * height * BgraFrame.BytesPerPixel)
                }
            };
            bitmap = CreateDIBSection(
                screen,
                ref bitmapInfo,
                DibRgbColors,
                out var pixels,
                nint.Zero,
                0);
            if (bitmap == nint.Zero || pixels == nint.Zero) throw LastWin32("CreateDIBSection");
            priorObject = SelectObject(memory, bitmap);
            if (priorObject == nint.Zero) throw LastWin32("SelectObject");
            var stride = checked(width * BgraFrame.BytesPerPixel);
            var managedPixels = new byte[checked(stride * height)];
            var printed = PrintWindow(
                window.Handle,
                memory,
                PrintWindowClientOnly | PrintWindowRenderFullContent);
            Marshal.Copy(pixels, managedPixels, 0, managedPixels.Length);
            if (!printed || !HasUsefulPixels(managedPixels))
            {
                if (!BitBlt(
                        memory,
                        0,
                        0,
                        width,
                        height,
                        screen,
                        clientOrigin.X,
                        clientOrigin.Y,
                        SourceCopy | CaptureLayeredWindows))
                {
                    throw LastWin32("PrintWindow and BitBlt");
                }
                Marshal.Copy(pixels, managedPixels, 0, managedPixels.Length);
            }
            return new BgraFrame(
                width,
                height,
                stride,
                managedPixels,
                captureId,
                DateTimeOffset.UtcNow);
        }
        finally
        {
            if (priorObject != nint.Zero && memory != nint.Zero)
            {
                _ = SelectObject(memory, priorObject);
            }
            if (bitmap != nint.Zero) _ = DeleteObject(bitmap);
            if (memory != nint.Zero) _ = DeleteDC(memory);
            _ = ReleaseDC(nint.Zero, screen);
        }
    }

    private static Win32Exception LastWin32(string operation) =>
        new(Marshal.GetLastWin32Error(), $"{operation} failed");

    private static bool HasUsefulPixels(ReadOnlySpan<byte> pixels)
    {
        var minimum = byte.MaxValue;
        var maximum = byte.MinValue;
        var samples = 0;
        var nonBlack = 0;
        for (var offset = 0; offset + 2 < pixels.Length; offset += 4096)
        {
            var blue = pixels[offset];
            var green = pixels[offset + 1];
            var red = pixels[offset + 2];
            minimum = Math.Min(minimum, Math.Min(red, Math.Min(green, blue)));
            maximum = Math.Max(maximum, Math.Max(red, Math.Max(green, blue)));
            if (red > 8 || green > 8 || blue > 8) nonBlack++;
            samples++;
        }
        return samples > 0 && nonBlack >= Math.Max(2, samples / 100) && maximum - minimum >= 8;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(nint window, nint deviceContext);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(nint window, nint deviceContext, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint window, ref NativePoint point);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateCompatibleDC(nint deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateDIBSection(
        nint deviceContext,
        ref BitmapInfo bitmapInfo,
        uint usage,
        out nint pixels,
        nint section,
        uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint SelectObject(nint deviceContext, nint graphicsObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(
        nint destination,
        int x,
        int y,
        int width,
        int height,
        nint source,
        int sourceX,
        int sourceY,
        uint rasterOperation);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint graphicsObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint deviceContext);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        internal uint Size;
        internal int Width;
        internal int Height;
        internal ushort Planes;
        internal ushort BitCount;
        internal uint Compression;
        internal uint SizeImage;
        internal int XPixelsPerMeter;
        internal int YPixelsPerMeter;
        internal uint ColorsUsed;
        internal uint ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        internal BitmapInfoHeader Header;
        internal uint Color;
    }
}
