using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;

namespace MudClient.App.Services;

/// <summary>Independent notification signals shown as a small colored badge on the taskbar icon
/// (see <see cref="TaskbarOverlayIconService"/>) — combinable, since more than one can be active
/// at once (e.g. a fight breaks out while a chat message is also waiting).</summary>
[Flags]
public enum TaskbarNotificationColor
{
    None = 0,
    Red = 1,
    Green = 2,
    Blue = 4,
}

/// <summary>
/// Shows a small colored overlay badge on the taskbar icon via the Win32 <c>ITaskbarList3</c> COM
/// API. Unlike <c>FlashWindowEx</c> (no color parameter — see the now-removed TaskbarFlashService),
/// this can render an arbitrary color, which is what lets combat (red), chat (green) and automation
/// (blue) each mean something different and blend additively when more than one is active at once
/// — red+green makes yellow, red+blue magenta, green+blue cyan, all three white, the same way
/// combining colored light does. A no-op on any non-Windows platform — the app is otherwise
/// cross-platform, and there is no equivalent Avalonia API.
/// </summary>
public static class TaskbarOverlayIconService
{
    private const int IconSize = 16;

    private static ITaskbarList3? _taskbarList;
    private static bool _initAttempted;
    private static readonly Dictionary<TaskbarNotificationColor, IntPtr> IconCache = new();

    /// <summary>Sets the overlay badge to the additive blend of every flag in
    /// <paramref name="color"/>, or clears it entirely for <see cref="TaskbarNotificationColor.None"/>.</summary>
    public static void SetState(Window window, TaskbarNotificationColor color)
    {
        if (OperatingSystem.IsWindows())
        {
            SetStateCore(window, color);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void SetStateCore(Window window, TaskbarNotificationColor color)
    {
        var taskbarList = GetTaskbarList();
        if (taskbarList is null || GetHandle(window) is not { } handle)
        {
            return;
        }

        if (color == TaskbarNotificationColor.None)
        {
            taskbarList.SetOverlayIcon(handle, IntPtr.Zero, string.Empty);
            return;
        }

        var icon = GetOrCreateIcon(color);
        if (icon != IntPtr.Zero)
        {
            taskbarList.SetOverlayIcon(handle, icon, DescribeColor(color));
        }
    }

    /// <summary>Lazily creates and caches the one <c>ITaskbarList3</c> COM instance for the
    /// process. Returns null (rather than throwing) when no Explorer taskbar is available to talk
    /// to — the badge silently stays off in that case instead of taking the app down.</summary>
    [SupportedOSPlatform("windows")]
    private static ITaskbarList3? GetTaskbarList()
    {
        if (_taskbarList is not null || _initAttempted)
        {
            return _taskbarList;
        }

        _initAttempted = true;
        try
        {
            var instance = (ITaskbarList3)new TaskbarListCoClass();
            instance.HrInit();
            _taskbarList = instance;
        }
        catch (COMException)
        {
            // e.g. running under a shell with no taskbar to register against.
        }

        return _taskbarList;
    }

    private static string DescribeColor(TaskbarNotificationColor color) => color switch
    {
        TaskbarNotificationColor.Red => "Walka",
        TaskbarNotificationColor.Green => "Wiadomość na czacie",
        TaskbarNotificationColor.Blue => "Zadziałał timer lub trigger",
        _ => "Powiadomienie",
    };

    [SupportedOSPlatform("windows")]
    private static IntPtr GetOrCreateIcon(TaskbarNotificationColor color)
    {
        if (IconCache.TryGetValue(color, out var cached))
        {
            return cached;
        }

        var icon = CreateSolidCircleIcon(ResolveColor(color));
        IconCache[color] = icon;
        return icon;
    }

    /// <summary>Additive RGB blend of every active flag's pure primary.</summary>
    private static (byte R, byte G, byte B) ResolveColor(TaskbarNotificationColor color) => (
        color.HasFlag(TaskbarNotificationColor.Red) ? (byte)255 : (byte)0,
        color.HasFlag(TaskbarNotificationColor.Green) ? (byte)255 : (byte)0,
        color.HasFlag(TaskbarNotificationColor.Blue) ? (byte)255 : (byte)0);

    /// <summary>Builds a small solid-color circle as a 32bpp ARGB icon via raw GDI32 — a plain
    /// filled circle is all a taskbar overlay badge needs, so this writes pixels directly instead
    /// of pulling in an imaging library for one shape.</summary>
    [SupportedOSPlatform("windows")]
    private static IntPtr CreateSolidCircleIcon((byte R, byte G, byte B) color)
    {
        var bitmapInfo = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = IconSize,
                biHeight = -IconSize, // negative = top-down row order
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0, // BI_RGB
            },
        };

        var colorBitmap = CreateDIBSection(IntPtr.Zero, ref bitmapInfo, 0, out var bitsPtr, IntPtr.Zero, 0);
        if (colorBitmap == IntPtr.Zero || bitsPtr == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var pixels = new byte[IconSize * IconSize * 4];
        var center = (IconSize - 1) / 2.0;
        var radius = IconSize / 2.0 - 1.0;
        for (var y = 0; y < IconSize; y++)
        {
            for (var x = 0; x < IconSize; x++)
            {
                var dx = x - center;
                var dy = y - center;
                if (dx * dx + dy * dy > radius * radius)
                {
                    continue; // stays zeroed — fully transparent outside the circle
                }

                var offset = (y * IconSize + x) * 4;
                pixels[offset] = color.B;
                pixels[offset + 1] = color.G;
                pixels[offset + 2] = color.R;
                pixels[offset + 3] = 255;
            }
        }

        Marshal.Copy(pixels, 0, bitsPtr, pixels.Length);

        // A 1bpp mask is still required by CreateIconIndirect even though the real transparency
        // comes from the color bitmap's own alpha channel — an all-zero mask ("opaque everywhere")
        // is the standard pairing for a 32bpp-with-alpha icon.
        var maskStrideBytes = (IconSize + 15) / 16 * 2;
        var maskBitmap = CreateBitmap(IconSize, IconSize, 1, 1, new byte[maskStrideBytes * IconSize]);

        var iconInfo = new ICONINFO
        {
            fIcon = true,
            xHotspot = 0,
            yHotspot = 0,
            hbmMask = maskBitmap,
            hbmColor = colorBitmap,
        };

        // CreateIconIndirect copies the bitmaps into the icon it returns, so the originals can
        // (and should) be freed right away.
        var icon = CreateIconIndirect(ref iconInfo);
        DeleteObject(colorBitmap);
        DeleteObject(maskBitmap);

        return icon;
    }

    private static IntPtr? GetHandle(Window window)
    {
        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        return handle == IntPtr.Zero ? null : handle;
    }

    // ========================================================================
    // Win32/COM interop
    // ========================================================================

    [ComImport]
    [Guid("56FDF344-FD6D-11D0-958A-006097C9A090")]
    private class TaskbarListCoClass;

    // ITaskbarList3's vtable, in order — every member up to and including SetOverlayIcon must be
    // declared (even the ones this service never calls) so the runtime lands SetOverlayIcon on the
    // correct slot; nothing after it is needed.
    [ComImport]
    [Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);

        // ITaskbarList2
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);

        // ITaskbarList3
        void SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
        void SetProgressState(IntPtr hwnd, int tbpFlags);
        void RegisterTab(IntPtr hwndTab, IntPtr hwndMdi);
        void UnregisterTab(IntPtr hwndTab);
        void SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
        void SetTabActive(IntPtr hwndTab, IntPtr hwndMdi, int tbatFlags);
        void ThumbBarAddButtons(IntPtr hwnd, uint cButtons, IntPtr pButtons);
        void ThumbBarUpdateButtons(IntPtr hwnd, uint cButtons, IntPtr pButtons);
        void ThumbBarSetImageList(IntPtr hwnd, IntPtr himl);
        void SetOverlayIcon(IntPtr hwnd, IntPtr hIcon, [MarshalAs(UnmanagedType.LPWStr)] string pszDescription);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors; // unused for 32bpp BI_RGB, present to match the native layout
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool fIcon;
        public uint xHotspot;
        public uint yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [SupportedOSPlatform("windows")]
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(
        IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [SupportedOSPlatform("windows")]
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitCount, byte[] bits);

    [SupportedOSPlatform("windows")]
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [SupportedOSPlatform("windows")]
    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO icon);
}
