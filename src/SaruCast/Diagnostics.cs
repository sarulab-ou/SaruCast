using System.IO;
using System.Text.Json;

namespace SaruCast;
public static class Diagnostics
{
    // Startup-only inspection never starts capture or sharing.
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2 || args[0] != "--diagnose") return 2;
        var output = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
        {
            version = typeof(Program).Assembly.GetName().Version?.ToString(),
            runtime = Environment.Version.ToString(), transport = "Cast Streaming",
            displays = Displays.List(), ffmpegBundled = File.Exists(Paths.Ffmpeg),
            defaultFrameRate = new Settings().FrameRate,
            captureStarted = false, televisionPlaybackVerified = false
        }, new JsonSerializerOptions { WriteIndented = true }));
        return File.Exists(Paths.Ffmpeg) ? 0 : 1;
    }
}
