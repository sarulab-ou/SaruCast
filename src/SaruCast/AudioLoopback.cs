using System.Diagnostics;
using System.IO.Pipes;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SaruCast;

public sealed class AudioLoopback : IAsyncDisposable
{
    public string PipeName { get; } = "SaruCast-" + Guid.NewGuid().ToString("N");
    private readonly NamedPipeServerStream pipe;
    private readonly WasapiLoopbackCapture capture;
    private readonly MMDeviceEnumerator devices = new();
    private readonly string deviceId;
    private readonly BufferedWaveProvider buffer;
    private readonly IWaveProvider pcm;
    private readonly CancellationTokenSource stop = new();
    private Task? writer;
    private Exception? error;
    public int PeakPcm16 { get; private set; }
    private readonly TaskCompletionSource<Exception> failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<Exception> Failure => failure.Task;
    public AudioLoopback()
    {
        using var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        deviceId = device.ID;
        capture = new WasapiLoopbackCapture(device);
        buffer = new BufferedWaveProvider(capture.WaveFormat) { BufferDuration = TimeSpan.FromMilliseconds(250), DiscardOnBufferOverflow = true, ReadFully = true };
        ISampleProvider samples = buffer.ToSampleProvider();
        if (samples.WaveFormat.Channels == 1) samples = new MonoToStereoSampleProvider(samples);
        else if (samples.WaveFormat.Channels != 2) samples = new MultiplexingSampleProvider([samples], 2);
        if (samples.WaveFormat.SampleRate != 48000) samples = new WdlResamplingSampleProvider(samples, 48000);
        pcm = new SampleToWaveProvider16(samples);
        capture.DataAvailable += (_, e) => buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        capture.RecordingStopped += (_, e) => { if (e.Exception != null && !stop.IsCancellationRequested) { error = e.Exception; failure.TrySetResult(new InvalidOperationException("PC音声の取得が止まりました。", e.Exception)); } };
        pipe = new NamedPipeServerStream(PipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 0, 65536);
    }
    public void Start() => writer = WriteAsync();
    private async Task WriteAsync()
    {
        try
        {
            await pipe.WaitForConnectionAsync(stop.Token); capture.StartRecording();
            var clock = Stopwatch.StartNew(); long sent = 0, nextDeviceCheck = 1000;
            byte[] data = new byte[1920]; // 10ms, 48kHz stereo signed 16-bit
            while (!stop.IsCancellationRequested)
            {
                if (error != null) throw new InvalidOperationException("PC音声の取得が止まりました。", error);
                if (clock.ElapsedMilliseconds >= nextDeviceCheck)
                {
                    using var current = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    if (current.ID != deviceId) throw new InvalidOperationException("再生デバイスが変わりました。もう一度キャストを開始してください。");
                    nextDeviceCheck += 1000;
                }
                long targetMs = sent * 1000 / 192000;
                long ahead = targetMs - clock.ElapsedMilliseconds;
                if (ahead > 0) await Task.Delay((int)ahead, stop.Token);
                if (clock.ElapsedMilliseconds - targetMs > 500) throw new InvalidOperationException("音声送信が追いつきません。画質を下げて再開始してください。");
                pcm.Read(data, 0, data.Length); // ReadFully supplies silence when no applications play sound.
                for (int i = 0; i < data.Length; i += 2) PeakPcm16 = Math.Max(PeakPcm16, Math.Abs((int)BitConverter.ToInt16(data, i)));
                await pipe.WriteAsync(data, stop.Token); sent += data.Length;
            }
        }
        catch (Exception e) when (e is System.IO.IOException or OperationCanceledException or InvalidOperationException or System.Runtime.InteropServices.COMException or ObjectDisposedException)
        { if (!stop.IsCancellationRequested) failure.TrySetResult(e); }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); pipe.Dispose(); capture.StopRecording();
        if (writer != null) await writer;
        capture.Dispose(); devices.Dispose(); stop.Dispose();
    }
}
