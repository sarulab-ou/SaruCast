using System.Diagnostics;
using System.IO;
using System.Threading.Channels;
using RikRealization.ClassicCast.Protocol.Media;

namespace SaruCast;

public sealed class StreamingVideoEncoder : IAsyncDisposable
{
    private Process? process;
    private KillOnCloseJob? job;
    private readonly CancellationTokenSource life = new();
    private readonly Channel<H264AccessUnit> frames = Channel.CreateBounded<H264AccessUnit>(new BoundedChannelOptions(4) { SingleReader = true, SingleWriter = true });
    private readonly Queue<string> errors = new();
    private Task? reader;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int FrameRate { get; private set; }
    public ChannelReader<H264AccessUnit> Frames => frames.Reader;
    public void Start(DisplayInfo display, Settings settings, bool testPattern = false)
    {
        if (!File.Exists(Paths.Ffmpeg)) throw new FileNotFoundException("同梱FFmpegがありません。ZIP全体を展開してください。");
        (Width, Height) = Encoder.Size(display.Width, display.Height, settings.Height);
        FrameRate = Settings.NormalizeFrameRate(settings.FrameRate);
        string rate = FrameRate.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var args = new List<string> { "-hide_banner", "-loglevel", "warning", "-y", "-flags", "low_delay" };
        if (testPattern) args.AddRange(["-re", "-f", "lavfi", "-i", $"testsrc2=size={Width}x{Height}:rate={rate}"]);
        else if (settings.Gdi) args.AddRange(["-f", "gdigrab", "-framerate", rate, "-draw_mouse", "1", "-offset_x", display.Left.ToString(), "-offset_y", display.Top.ToString(), "-video_size", $"{display.Width}x{display.Height}", "-i", "desktop"]);
        else args.AddRange(["-init_hw_device", $"d3d11va=cap:{display.Adapter}", "-filter_hw_device", "cap", "-f", "lavfi", "-i", $"ddagrab=output_idx={display.Output}:framerate={rate}:draw_mouse=1:output_fmt=bgra"]);
        string filter = !testPattern && !settings.Gdi ? "hwdownload,format=bgra," : "";
        int bitrate = Encoder.VideoBitrateKbps(settings);
        args.AddRange(["-an", "-vf", filter + $"scale={Width}:{Height},format=nv12", "-c:v", "h264_mf", "-hw_encoding", settings.Hardware ? "1" : "0", "-profile:v", "66", "-level:v", "41", "-scenario", "display_remoting", "-rate_control", "cbr", "-b:v", $"{bitrate}k", "-maxrate", $"{bitrate * 4 / 3}k", "-bufsize", $"{bitrate}k", "-g", rate, "-bf", "0", "-r", rate, "-bsf:v", "h264_metadata=aud=insert", "-f", "h264", "-flush_packets", "1", "pipe:1"]);
        var info = new ProcessStartInfo(Paths.Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        process = new() { StartInfo = info };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errors) { if (errors.Count >= 16) errors.Dequeue(); errors.Enqueue(e.Data); } };
        process.Start(); process.BeginErrorReadLine(); job = new(process);
        reader = Task.Run(ReadAsync);
        Log.Write($"Native encoder start resolution={Width}x{Height} fps={FrameRate} bitrate={bitrate}kbps hardware={settings.Hardware} source={(testPattern ? "pattern" : settings.Gdi ? "GDI" : "DXGI")}");
    }
    private async Task ReadAsync()
    {
        var splitter = new H264StreamSplitter(); byte[] buffer = new byte[65536];
        try
        {
            while (!life.IsCancellationRequested)
            {
                int count = await process!.StandardOutput.BaseStream.ReadAsync(buffer, life.Token);
                if (count == 0) break;
                foreach (var unit in splitter.Push(buffer.AsSpan(0, count))) await frames.Writer.WriteAsync(unit, life.Token);
                if (splitter.PendingBytes > 8 * 1024 * 1024) throw new IOException("映像フレームの上限を超えました。");
            }
            if (!life.IsCancellationRequested) throw new IOException("映像エンコーダーが停止しました。GPU設定を切り替えて再開始してください。");
            frames.Writer.TryComplete();
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        { frames.Writer.TryComplete(life.IsCancellationRequested ? null : e); }
    }
    public (long WorkingSet, double CpuSeconds) Metrics()
    { if (process == null || process.HasExited) return (0, 0); process.Refresh(); return (process.WorkingSet64, process.TotalProcessorTime.TotalSeconds); }
    public async ValueTask DisposeAsync()
    {
        life.Cancel(); job?.Dispose();
        if (process != null)
        {
            try { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); } catch (InvalidOperationException) { }
            if (reader != null) await reader;
            lock (errors) foreach (string error in errors) if (!error.Contains("http", StringComparison.OrdinalIgnoreCase)) Log.Write("Native encoder: " + error);
            process.Dispose(); process = null;
        }
        life.Dispose();
    }
}
