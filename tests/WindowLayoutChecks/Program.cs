using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using WindowsToolbelt;

internal static class Program
{
    private static int _checks;

    [STAThread]
    private static void Main()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WindowsToolbeltChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "layout.json");
        var service = new WindowLayoutService(path);
        var windows = new List<Window>();
        var nativeWindows = new List<IntPtr>();
        try
        {
            Check(!service.HasSavedLayout, "restore unavailable before first save");
            Throws<FileNotFoundException>(() => service.Restore(), "missing file");
            File.WriteAllText(path, "broken json");
            Throws<JsonException>(() => service.Restore(), "malformed JSON");
            File.WriteAllText(path, "{\"Version\":99}");
            Throws<InvalidDataException>(() => service.Restore(), "unsupported version");
            File.WriteAllText(path, "{\"Windows\":[null]}");
            Throws<InvalidDataException>(() => service.Restore(), "invalid window entry");

            var oldWork = Rect(-1920, 40, 0, 1080);
            var bounds = Rect(-1820, 140, -1020, 740);
            Check(WindowLayoutService.FitToWorkArea(bounds, oldWork, oldWork).Equals(bounds), "unchanged negative-coordinate monitor");
            var fitted = WindowLayoutService.FitToWorkArea(bounds, oldWork, Rect(0, 0, 1280, 720));
            Check(fitted.Equals(Rect(100, 100, 900, 700)), "disconnected monitor maps to available display");
            fitted = WindowLayoutService.FitToWorkArea(Rect(100, 100, 2000, 1500), Rect(0, 0, 2560, 1600), Rect(0, 40, 800, 600));
            Check(fitted.Equals(Rect(0, 40, 800, 600)), "oversized window fits smaller work area");

            var wideWork = Rect(0, 0, 7680, 2112);
            var savedWide = new SavedWindow
            {
                MonitorId = "wide", MonitorDevice = @"\\.\DISPLAY1", WorkArea = wideWork,
                NormalBounds = Rect(5746, 0, 7680, 1063)
            };
            var small = new DisplayMonitor("small", @"\\.\DISPLAY1", Rect(-1920, 0, 0, 1080), Rect(-1920, 0, 0, 1040));
            var wide = new DisplayMonitor("wide", @"\\.\DISPLAY3", Rect(0, 0, 7680, 2160), wideWork);
            Check(WindowLayoutService.ChooseMonitor(savedWide, [small, wide]) == wide, "renamed display matched by monitor id");
            savedWide.MonitorId = string.Empty;
            Check(WindowLayoutService.ChooseMonitor(savedWide, [small, wide]) == wide, "legacy layout ignores reassigned display name");
            var movedWide = wide with { Bounds = Rect(1920, 0, 9600, 2160), Work = Rect(1920, 0, 9600, 2112) };
            var primarySmall = small with { Bounds = Rect(0, 0, 1920, 1080), Work = Rect(0, 0, 1920, 1040) };
            Check(WindowLayoutService.ChooseMonitor(savedWide, [primarySmall, movedWide]) == movedWide,
                "legacy layout follows same-size display moved in arrangement");
            Check(WindowLayoutService.ChooseMonitor(savedWide, [small]) == small, "disconnected display falls back to remaining one");

            var normal = MakeWindow("normal", windows);
            var minimized = MakeWindow("minimized", windows);
            var maximized = MakeWindow("maximized", windows);
            var tool = MakeWindow("tool", windows, WindowStyle.ToolWindow);
            var hidden = MakeWindow("hidden", windows);
            hidden.Hide();
            minimized.WindowState = WindowState.Minimized;
            maximized.WindowState = WindowState.Maximized;
            Pump();

            var layout = service.Save();
            Check(service.HasSavedLayout, "layout persists to disk");
            var handles = new[] { Handle(normal), Handle(minimized), Handle(maximized) };
            var captured = layout.Windows.Where(w => handles.Contains(w.Handle)).ToList();
            Check(captured.Count == 3, "normal, minimized and maximized windows enumerated");
            Check(!layout.Windows.Any(w => w.Handle == Handle(tool) || w.Handle == Handle(hidden)), "tool and hidden windows excluded");
            Check(captured.Single(w => w.Handle == Handle(minimized)).ShowCommand == 2, "minimized state captured");
            Check(captured.Single(w => w.Handle == Handle(maximized)).ShowCommand == 3, "maximized state captured");

            // Only test-created windows are ever passed to Restore.
            layout.Windows = captured;
            Write(path, layout);
            normal.Title += " (changed title)";
            foreach (var window in new[] { normal, minimized, maximized })
            {
                window.WindowState = WindowState.Normal;
                window.Left += 80;
                window.Top += 60;
                window.Width += 30;
            }
            Pump();
            var result = new WindowLayoutService(path).Restore();
            Pump();
            Check(result == new RestoreResult(3, 0, 0), "fresh service restores saved windows, including changed title");
            foreach (var saved in captured)
            {
                var actual = Placement(new IntPtr(saved.Handle));
                Check(actual.ShowCommand == saved.ShowCommand, $"show state restored: {saved.Title}");
                if (saved.ShowCommand == 1)
                {
                    // Capture through the service again to compare in the same physical-pixel coordinate system.
                    var recaptured = service.Save().Windows.Single(w => w.Handle == saved.Handle);
                    Check(recaptured.NormalBounds.Equals(saved.NormalBounds), "normal size and position restored exactly");
                }
            }

            var minimizedMax = Placement(new IntPtr(Handle(maximized)));
            minimizedMax.ShowCommand = 2;
            minimizedMax.Flags = 2;
            SetWindowPlacement(new IntPtr(Handle(maximized)), ref minimizedMax);
            Pump();
            var minimizedMaxSaved = service.Save().Windows.Single(w => w.Handle == Handle(maximized));
            Check(minimizedMaxSaved.RestoreToMaximized && minimizedMaxSaved.ShowCommand == 2, "minimized-from-maximized flag captured");
            layout.Windows = [minimizedMaxSaved];
            Write(path, layout);
            maximized.WindowState = WindowState.Normal;
            Pump();
            result = service.Restore();
            Pump();
            var actualMinimizedMax = Placement(new IntPtr(Handle(maximized)));
            Check(result.Restored == 1 && actualMinimizedMax.ShowCommand == 2 && (actualMinimizedMax.Flags & 2) != 0,
                "minimized-from-maximized placement restored");

            var savedNormal = captured.Single(w => w.Handle == Handle(normal));
            savedNormal.Title = normal.Title;
            savedNormal.Handle = 0;
            layout.Windows = [savedNormal];
            Write(path, layout);
            Check(service.Restore().Restored == 1, "unique recreated window matched by title, process and class");

            var duplicateA = MakeNativeDuplicate(nativeWindows);
            var duplicateB = MakeNativeDuplicate(nativeWindows);
            var duplicateSaved = service.Save().Windows.Where(w =>
                w.Handle == duplicateA.ToInt64() || w.Handle == duplicateB.ToInt64()).ToList();
            Check(duplicateSaved.Count == 2, "same-class duplicate windows captured");
            foreach (var saved in duplicateSaved) saved.Handle = 0;
            layout.Windows = duplicateSaved;
            Write(path, layout);
            Check(service.Restore() == new RestoreResult(0, 2, 0), "ambiguous duplicate windows skipped");

            layout.Windows = [captured.Single(w => w.Handle == Handle(minimized))];
            Write(path, layout);
            minimized.Close();
            Pump();
            Check(service.Restore() == new RestoreResult(0, 1, 0), "closed window skipped without affecting others");
            Check(Directory.GetFiles(directory, "*.tmp").Length == 0, "atomic saves leave no temporary files");
            Console.WriteLine($"PASS: {_checks} window layout checks.");
        }
        finally
        {
            foreach (var window in windows)
                window.Close();
            foreach (var hwnd in nativeWindows)
                DestroyWindow(hwnd);
            File.Delete(path);
            Directory.Delete(directory); // Nonrecursive; only the test's own empty directory.
        }
    }

    private static Window MakeWindow(string title, List<Window> windows, WindowStyle style = WindowStyle.SingleBorderWindow)
    {
        var window = new Window
        {
            Title = "Windows Toolbelt check " + title,
            Width = 400, Height = 250, Left = 120, Top = 140,
            WindowStyle = style, ShowActivated = false
        };
        windows.Add(window);
        window.Show();
        Pump();
        return window;
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static IntPtr MakeNativeDuplicate(List<IntPtr> windows)
    {
        var hwnd = CreateWindowEx(0, "STATIC", "Windows Toolbelt duplicate check", 0x10CF0000,
            180, 180, 400, 250, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Check(hwnd != IntPtr.Zero, "create same-class disposable window");
        windows.Add(hwnd);
        Pump();
        return hwnd;
    }

    private static void Write(string path, WindowLayout layout) => File.WriteAllText(path, JsonSerializer.Serialize(layout));
    private static long Handle(Window window) => new WindowInteropHelper(window).Handle.ToInt64();
    private static NativeRect Rect(int left, int top, int right, int bottom) => new() { Left = left, Top = top, Right = right, Bottom = bottom };
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL: " + label);
        _checks++;
        Console.WriteLine("PASS: " + label);
    }
    private static void Throws<T>(Action action, string label) where T : Exception
    {
        try { action(); }
        catch (T) { Check(true, label); return; }
        throw new Exception("FAIL: expected " + typeof(T).Name + " for " + label);
    }

    private static WindowPlacement Placement(IntPtr hwnd)
    {
        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        Check(GetWindowPlacement(hwnd, ref placement), "read native placement");
        return placement;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct WindowPlacement
    {
        public int Length, Flags, ShowCommand;
        public Point Min, Max;
        public NativeRect Normal;
    }
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hwnd, ref WindowPlacement placement);
    [DllImport("user32.dll")] private static extern bool SetWindowPlacement(IntPtr hwnd, ref WindowPlacement placement);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(int extendedStyle,
        string className, string title, uint style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
}
