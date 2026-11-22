using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace WindowsToolbelt;

// Uses physical pixels on the worker thread, independently of the WPF window's DPI.
internal sealed class WindowLayoutService(string filePath)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    internal bool HasSavedLayout => File.Exists(filePath);

    internal WindowLayout Save()
    {
        using var dpi = new DpiScope();
        var layout = new WindowLayout { SavedAt = DateTimeOffset.Now, Windows = EnumerateWindows(EnumerateMonitors()) };
        if (layout.Windows.Count == 0)
            throw new InvalidDataException("No windows could be saved. The previous layout was kept.");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        var temporaryPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(layout, JsonOptions));
            File.Move(temporaryPath, filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
        return layout;
    }

    internal RestoreResult Restore()
    {
        var layout = JsonSerializer.Deserialize<WindowLayout>(File.ReadAllText(filePath), JsonOptions);
        if (layout is null || layout.Version != 1 || layout.Windows is null ||
            layout.Windows.Any(w => w is null || !w.NormalBounds.IsValid || !w.WorkArea.IsValid ||
                w.ProcessId <= 0 || string.IsNullOrEmpty(w.ProcessName) || string.IsNullOrEmpty(w.ClassName) ||
                w.ShowCommand is not (1 or 2 or 3)))
            throw new InvalidDataException("The saved layout is invalid. Save a new layout to replace it.");

        using var dpi = new DpiScope();
        var monitors = EnumerateMonitors();
        var available = EnumerateWindows(monitors);
        var used = new HashSet<long>();
        var restored = 0;
        var missing = 0;
        var failed = 0;
        foreach (var saved in layout.Windows)
        {
            // HWNDs and PIDs can be recycled: verify process creation time and window class too.
            var current = available.FirstOrDefault(w => !used.Contains(w.Handle) &&
                w.Handle == saved.Handle && w.ProcessId == saved.ProcessId &&
                w.ProcessStarted == saved.ProcessStarted && w.ClassName == saved.ClassName);
            if (current is null)
            {
                // A recreated window is safe to match only when its identity is unambiguous.
                var matches = available.Where(w => !used.Contains(w.Handle) &&
                    w.ProcessName == saved.ProcessName && w.ClassName == saved.ClassName &&
                    w.Title == saved.Title).ToList();
                if (matches.Count == 1 && layout.Windows.Count(w =>
                    w.ProcessName == saved.ProcessName && w.ClassName == saved.ClassName &&
                    w.Title == saved.Title) == 1)
                    current = matches[0];
            }
            if (current is null)
            {
                missing++;
                continue;
            }
            used.Add(current.Handle);
            var hwnd = new IntPtr(current.Handle);
            // Recheck immediately before applying, in case the app closed during enumeration.
            GetWindowThreadProcessId(hwnd, out var pid);
            var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
            if (pid != current.ProcessId || !GetWindowPlacement(hwnd, ref placement))
            {
                failed++;
                continue;
            }

            var monitor = ChooseMonitor(saved, monitors);
            var bounds = FitToWorkArea(saved.NormalBounds, saved.WorkArea, monitor.Work);
            // WINDOWPLACEMENT uses workspace coordinates, not screen coordinates.
            bounds = bounds.Offset(monitor.Bounds.Left - monitor.Work.Left, monitor.Bounds.Top - monitor.Work.Top);
            placement.NormalPosition = bounds;
            placement.MinPosition = new NativePoint { X = -1, Y = -1 };
            placement.MaxPosition = new NativePoint { X = -1, Y = -1 };
            placement.Flags = saved.RestoreToMaximized ? 2 : 0; // WPF_RESTORETOMAXIMIZED
            placement.ShowCommand = saved.ShowCommand;
            if (SetWindowPlacement(hwnd, ref placement))
                restored++;
            else
                failed++;
        }
        return new RestoreResult(restored, missing, failed);
    }

    private static List<SavedWindow> EnumerateWindows(IReadOnlyList<DisplayMonitor> monitors)
    {
        var windows = new List<SavedWindow>();
        if (!EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || hwnd == GetShellWindow() ||
                (GetWindowLong(hwnd, -20) & 0x80) != 0) // WS_EX_TOOLWINDOW
                return true;
            if (DwmGetWindowAttribute(hwnd, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;
            var title = new StringBuilder(1024);
            if (GetWindowText(hwnd, title, title.Capacity) == 0)
                return true;
            var className = new StringBuilder(256);
            GetClassName(hwnd, className, className.Capacity);
            var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
            if (!GetWindowPlacement(hwnd, ref placement) || !placement.NormalPosition.IsValid)
                return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            try
            {
                using var process = Process.GetProcessById((int)pid);
                var info = GetMonitor(MonitorFromWindow(hwnd, 2));
                var monitor = monitors.FirstOrDefault(m => m.Device == info.Device) ??
                    new DisplayMonitor(string.Empty, info.Device, info.Bounds, info.Work);
                windows.Add(new SavedWindow
                {
                    Handle = hwnd.ToInt64(), ProcessId = (int)pid,
                    ProcessStarted = process.StartTime.ToUniversalTime().Ticks,
                    ProcessName = process.ProcessName, ClassName = className.ToString(), Title = title.ToString(),
                    NormalBounds = placement.NormalPosition.Offset(
                        monitor.Work.Left - monitor.Bounds.Left, monitor.Work.Top - monitor.Bounds.Top),
                    ShowCommand = placement.ShowCommand is 2 or 3 ? placement.ShowCommand : 1,
                    RestoreToMaximized = (placement.Flags & 2) != 0,
                    MonitorId = monitor.Id, MonitorDevice = monitor.Device, WorkArea = monitor.Work
                });
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
            {
                // A closing or inaccessible window must not prevent saving the rest of the desktop.
            }
            return true;
        }, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return windows;
    }

    private static List<DisplayMonitor> EnumerateMonitors()
    {
        var monitors = new List<DisplayMonitor>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr handle, IntPtr dc, ref NativeRect rect, IntPtr data) =>
        {
            var info = GetMonitor(handle);
            monitors.Add(new DisplayMonitor(GetMonitorId(info.Device), info.Device, info.Bounds, info.Work));
            return true;
        }, IntPtr.Zero);
        if (monitors.Count == 0)
            throw new InvalidOperationException("No displays were found.");
        return monitors;
    }

    // DISPLAYn device names are reassigned after sleep, power cycles and reconnects, so identify the
    // physical monitor by its device interface path (stable per monitor and connector) instead.
    private static string GetMonitorId(string device)
    {
        var display = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
        for (uint i = 0; EnumDisplayDevices(device, i, ref display, 1); i++) // EDD_GET_DEVICE_INTERFACE_NAME
        {
            if ((display.StateFlags & 1) != 0 && !string.IsNullOrEmpty(display.DeviceId)) // DISPLAY_DEVICE_ACTIVE
                return display.DeviceId;
            display = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
        }
        return string.Empty;
    }

    internal static DisplayMonitor ChooseMonitor(SavedWindow saved, IReadOnlyList<DisplayMonitor> monitors)
    {
        static DisplayMonitor? Single(IEnumerable<DisplayMonitor> candidates)
        {
            var list = candidates.Take(2).ToList();
            return list.Count == 1 ? list[0] : null;
        }
        static int Width(NativeRect r) => r.Right - r.Left;
        static int Height(NativeRect r) => r.Bottom - r.Top;

        // Prefer the same physical monitor, then an unchanged display geometry, then a display of the
        // same size that moved in the arrangement. The volatile device name is only a last hint.
        return (string.IsNullOrEmpty(saved.MonitorId) ? null : Single(monitors.Where(m => m.Id == saved.MonitorId)))
            ?? Single(monitors.Where(m => m.Work.Equals(saved.WorkArea)))
            ?? Single(monitors.Where(m => Width(m.Work) == Width(saved.WorkArea) && Height(m.Work) == Height(saved.WorkArea)))
            ?? Single(monitors.Where(m => m.Device == saved.MonitorDevice &&
                Width(m.Work) == Width(saved.WorkArea) && Height(m.Work) == Height(saved.WorkArea)))
            ?? monitors.MaxBy(m => Overlap(m.Bounds, saved.NormalBounds))!;
    }

    private static long Overlap(NativeRect a, NativeRect b) =>
        (long)Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) *
        Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));

    private static MonitorInfo GetMonitor(IntPtr handle)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = string.Empty };
        if (!GetMonitorInfo(handle, ref info))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return info;
    }

    internal static NativeRect FitToWorkArea(NativeRect bounds, NativeRect oldWork, NativeRect newWork)
    {
        // Keep exact coordinates on unchanged displays. On changed/disconnected displays,
        // retain the relative position and fit the whole window onto an available monitor.
        if (oldWork.Equals(newWork))
            return bounds;
        var width = Math.Min(bounds.Right - bounds.Left, newWork.Right - newWork.Left);
        var height = Math.Min(bounds.Bottom - bounds.Top, newWork.Bottom - newWork.Top);
        var left = Math.Clamp(newWork.Left + bounds.Left - oldWork.Left, newWork.Left, newWork.Right - width);
        var top = Math.Clamp(newWork.Top + bounds.Top - oldWork.Top, newWork.Top, newWork.Bottom - height);
        return new NativeRect { Left = left, Top = top, Right = left + width, Bottom = top + height };
    }

    private sealed class DpiScope : IDisposable
    {
        private readonly IntPtr _previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        public void Dispose()
        {
            if (_previous != IntPtr.Zero)
                SetThreadDpiAwarenessContext(_previous);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length, Flags, ShowCommand;
        public NativePoint MinPosition, MaxPosition;
        public NativeRect NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Bounds, Work;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    private delegate bool EnumWindowCallback(IntPtr hwnd, IntPtr data);
    private delegate bool EnumMonitorCallback(IntPtr monitor, IntPtr dc, ref NativeRect rect, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hwnd, ref WindowPlacement placement);
    [DllImport("user32.dll")] private static extern bool SetWindowPlacement(IntPtr hwnd, ref WindowPlacement placement);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevices(string device, uint index, ref DisplayDevice display, uint flags);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, EnumMonitorCallback callback, IntPtr data);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}

internal sealed class WindowLayout
{
    public int Version { get; set; } = 1;
    public DateTimeOffset SavedAt { get; set; }
    public List<SavedWindow> Windows { get; set; } = [];
}

internal sealed class SavedWindow
{
    public long Handle { get; set; }
    public int ProcessId { get; set; }
    public long ProcessStarted { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public NativeRect NormalBounds { get; set; }
    public int ShowCommand { get; set; }
    public bool RestoreToMaximized { get; set; }
    public string MonitorId { get; set; } = string.Empty;
    public string MonitorDevice { get; set; } = string.Empty;
    public NativeRect WorkArea { get; set; }
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect
{
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }
    public readonly bool IsValid => Right > Left && Bottom > Top;
    public readonly NativeRect Offset(int x, int y) => new()
        { Left = Left + x, Top = Top + y, Right = Right + x, Bottom = Bottom + y };
}

internal sealed record DisplayMonitor(string Id, string Device, NativeRect Bounds, NativeRect Work);

internal sealed record RestoreResult(int Restored, int Missing, int Failed);
