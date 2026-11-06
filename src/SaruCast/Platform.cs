using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SaruCast;

public sealed record CastDevice(string Name, string Address, int Port = 8009)
{
    public override string ToString() => $"{Name}  ({Address})";
}
public sealed record DisplayInfo(string DeviceName, int Adapter, int Output, int Left, int Top, int Width, int Height)
{
    public override string ToString() => $"{DeviceName.Replace(@"\\.\DISPLAY", "画面 ", StringComparison.OrdinalIgnoreCase)}  ·  {Width} × {Height}";
}
public sealed class Settings
{
    public string Television { get; set; } = "";
    public string ManualTelevision { get; set; } = "";
    public string Display { get; set; } = "";
    public bool Audio { get; set; } = true;
    public bool Hardware { get; set; } = true;
    public bool Gdi { get; set; }
    public int Height { get; set; } = 1080;
    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(Paths.Root, "settings.json"))) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save()
    {
        string temp = Path.Combine(Paths.Root, $"settings-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, JsonSerializer.Serialize(this));
        File.Move(temp, Path.Combine(Paths.Root, "settings.json"), true);
    }
}
public static class Paths
{
    public static string Root { get; } = Init();
    public static string Ffmpeg => Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe");
    private static string Init()
    {
        string root = Environment.GetEnvironmentVariable("SARUCAST_DATA_ROOT") is { Length: > 0 } isolated
            ? Path.GetFullPath(isolated)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SaruCast");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "sessions"));
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        // Only stale session directories owned by this app. Never touch recent/concurrent sessions.
        foreach (string dir in Directory.GetDirectories(Path.Combine(root, "sessions")))
            if (Guid.TryParseExact(Path.GetFileName(dir), "N", out _) && Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow.AddDays(-2))
                try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        foreach (string file in Directory.GetFiles(Path.Combine(root, "logs"), "*.log"))
            if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-14))
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return root;
    }
}
public static class Log
{
    private static readonly object Gate = new();
    private static readonly string FileName = Path.Combine(Paths.Root, "logs", $"{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
    public static void Write(string message)
    {
        // Call sites must never pass media URLs, tokens, Cast payloads or command lines.
        lock (Gate) try { File.AppendAllText(FileName, $"{DateTimeOffset.Now:O} {message}\n"); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    public static void Error(string context, Exception error)
    {
        // Method names only: exception messages can contain URLs or user paths.
        var frames = new System.Diagnostics.StackTrace(error, false).GetFrames().Take(12);
        Write(context + " " + error.GetType().Name + " " + string.Join(" <- ", frames.Select(f => f.GetMethod()?.DeclaringType?.Name + "." + f.GetMethod()?.Name)));
    }
}
public static class Displays
{
    [DllImport("dxgi.dll", ExactSpelling = true)] private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumDelegate(IntPtr self, uint index, out IntPtr item);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DescDelegate(IntPtr self, out OutputDesc desc);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct OutputDesc
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public Rect DesktopCoordinates;
        [MarshalAs(UnmanagedType.Bool)] public bool AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
    }
    private static T Method<T>(IntPtr obj, int slot) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size));
    public static IReadOnlyList<DisplayInfo> List()
    {
        Guid iid = new("770aae78-f26f-4dba-a829-253c83d1b387");
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref iid, out IntPtr factory));
        var result = new List<DisplayInfo>();
        try
        {
            for (uint a = 0; Method<EnumDelegate>(factory, 12)(factory, a, out IntPtr adapter) == 0; a++)
            {
                try
                {
                    for (uint o = 0; Method<EnumDelegate>(adapter, 7)(adapter, o, out IntPtr output) == 0; o++)
                    {
                        try
                        {
                            Marshal.ThrowExceptionForHR(Method<DescDelegate>(output, 7)(output, out var d));
                            var r = d.DesktopCoordinates;
                            if (d.AttachedToDesktop && r.Right > r.Left && r.Bottom > r.Top)
                                result.Add(new(d.DeviceName, (int)a, (int)o, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top));
                        }
                        finally { Marshal.Release(output); }
                    }
                }
                finally { Marshal.Release(adapter); }
            }
        }
        finally { Marshal.Release(factory); }
        return result;
    }
}
