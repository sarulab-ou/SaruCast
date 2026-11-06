using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace SaruCast;

public sealed class Encoder : IAsyncDisposable
{
    private Process? process;
    private IntPtr job;
    private readonly List<string> errors = [];
    private readonly TaskCompletionSource<Exception> failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<Exception> Failure => failure.Task;
    public string DirectoryPath { get; } = Path.Combine(Paths.Root, "sessions", Guid.NewGuid().ToString("N"));
    public int Width { get; private set; }
    public int Height { get; private set; }
    public (long WorkingSet, double CpuSeconds) Metrics()
    {
        if (process == null || process.HasExited) return (0, 0);
        process.Refresh(); return (process.WorkingSet64, process.TotalProcessorTime.TotalSeconds);
    }
    public static List<string> Arguments(DisplayInfo display, Settings options, string directory, string? pipe, bool testPattern = false)
    {
        var (w, h) = Size(display.Width, display.Height, options.Height);
        List<string> a = ["-hide_banner", "-loglevel", "warning", "-y", "-copyts", "-start_at_zero"];
        if (testPattern) a.AddRange(["-re", "-f", "lavfi", "-i", $"testsrc2=size={w}x{h}:rate=30"]);
        else if (options.Gdi)
            a.AddRange(["-f", "gdigrab", "-framerate", "30", "-draw_mouse", "1", "-offset_x", display.Left.ToString(), "-offset_y", display.Top.ToString(), "-video_size", $"{display.Width}x{display.Height}", "-i", "desktop"]);
        else
            a.AddRange(["-init_hw_device", $"d3d11va=cap:{display.Adapter}", "-filter_hw_device", "cap", "-f", "lavfi", "-i", $"ddagrab=output_idx={display.Output}:framerate=30:draw_mouse=1:output_fmt=bgra"]);
        if (pipe != null)
            a.AddRange(["-thread_queue_size", "128", "-f", "s16le", "-ar", "48000", "-ac", "2", "-i", @"\\.\pipe\" + pipe]);
        else if (testPattern) a.AddRange(["-re", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000"]);
        a.AddRange(["-map", "0:v:0"]);
        if (pipe != null || testPattern) a.AddRange(["-map", "1:a:0", "-c:a", "aac", "-b:a", "160k", "-ar", "48000", "-ac", "2", "-af", "aresample=async=1000:first_pts=0"]);
        else a.Add("-an");
        string filter = !testPattern && !options.Gdi ? "hwdownload,format=bgra," : "";
        a.AddRange(["-vf", filter + $"scale={w}:{h},format=nv12", "-c:v", "h264_mf", "-hw_encoding", options.Hardware ? "1" : "0", "-b:v", options.Height <= 720 ? "3000k" : "6000k", "-maxrate", options.Height <= 720 ? "4000k" : "8000k", "-bufsize", "8000k", "-g", "30", "-bf", "0", "-r", "30", "-f", "hls", "-hls_time", "1", "-hls_list_size", "12", "-hls_delete_threshold", "10", "-hls_flags", "delete_segments+independent_segments+temp_file+omit_endlist", "-master_pl_name", "master.m3u8", "-hls_segment_filename", Path.Combine(directory, "seg%06d.ts"), Path.Combine(directory, "index.m3u8")]);
        return a;
    }
    public static (int Width, int Height) Size(int width, int height, int maxHeight)
    {
        double scale = Math.Min(1, Math.Min(1920.0 / width, Math.Clamp(maxHeight, 720, 1080) / (double)height));
        return (Math.Max(2, (int)(width * scale) / 2 * 2), Math.Max(2, (int)(height * scale) / 2 * 2));
    }
    public async Task StartAsync(DisplayInfo display, Settings options, string? pipe, CancellationToken ct, bool testPattern = false)
    {
        if (!File.Exists(Paths.Ffmpeg)) throw new FileNotFoundException("同梱FFmpegが見つかりません。ZIP全体を展開してください。");
        Directory.CreateDirectory(DirectoryPath); (Width, Height) = Size(display.Width, display.Height, options.Height);
        var info = new ProcessStartInfo(Paths.Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var arg in Arguments(display, options, DirectoryPath, pipe, testPattern)) info.ArgumentList.Add(arg);
        process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errors) { if (errors.Count == 24) errors.RemoveAt(0); errors.Add(e.Data); } };
        process.Exited += (_, _) => failure.TrySetResult(new IOException("映像エンコーダーが停止しました。詳細設定でGPU使用を切り替えるか、診断ログを確認してください。"));
        process.Start(); process.BeginErrorReadLine();
        AttachKillJob(process);
        Log.Write($"Encoder start capture={(testPattern ? "pattern" : options.Gdi ? "GDI" : "DXGI")} adapter={display.Adapter} output={display.Output} resolution={Width}x{Height} fps=30 bitrate={(options.Height <= 720 ? 3000 : 6000)}kbps hardware={options.Hardware} audio={pipe != null || testPattern}");
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(25))
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited) { WriteErrors(); throw await failure.Task; }
            string index = Path.Combine(DirectoryPath, "index.m3u8");
            if (File.Exists(index))
            {
                try { if (File.ReadAllLines(index).Count(l => l.StartsWith("#EXTINF:")) >= 6) return; } catch (IOException) { }
            }
            await Task.Delay(100, ct);
        }
        WriteErrors(); throw new TimeoutException("映像を準備できません。詳細設定で画面取得方式やGPU使用を切り替えてください。");
    }
    private void WriteErrors()
    {
        lock (errors) foreach (string line in errors)
        {
            // FFmpeg only receives local paths, but redact all session paths and pipe names as well.
            string safe = line.Replace(DirectoryPath, "[session]");
            if (!safe.Contains("SaruCast-", StringComparison.Ordinal) && !safe.Contains("http", StringComparison.OrdinalIgnoreCase)) Log.Write("Encoder: " + safe);
        }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(IntPtr job, int cls, IntPtr info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    private void AttachKillJob(Process child)
    {
        job = CreateJobObject(IntPtr.Zero, null); if (job == IntPtr.Zero) throw new IOException("子プロセスの終了監視を作成できません。");
        int size = IntPtr.Size == 8 ? 144 : 112; IntPtr data = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, data, size); Marshal.WriteInt32(data, 16, 0x2000); // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(job, 9, data, (uint)size) || !AssignProcessToJobObject(job, child.Handle))
            { child.Kill(true); throw new IOException("子プロセスの終了監視を設定できません。"); }
        }
        finally { Marshal.FreeHGlobal(data); }
    }
    public async ValueTask DisposeAsync()
    {
        if (process != null)
        {
            try
            {
                if (!process.HasExited)
                {
                    await process.StandardInput.WriteLineAsync("q");
                    using var timeout = new CancellationTokenSource(1200);
                    try { await process.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); }
                }
            }
            catch (InvalidOperationException) { } catch (IOException) { if (!process.HasExited) process.Kill(true); }
            WriteErrors(); process.Dispose(); process = null;
        }
        if (job != IntPtr.Zero) { CloseHandle(job); job = IntPtr.Zero; }
        try { if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
