using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SaruCast;

public sealed class StreamingAudioSource : IDisposable
{
    private readonly MMDeviceEnumerator devices = new();
    private readonly WasapiLoopbackCapture capture;
    private readonly BufferedWaveProvider buffer;
    private readonly IWaveProvider pcm;
    private readonly string deviceId;
    private readonly byte[] bytes = new byte[3840];
    private readonly short[] samples = new short[1920];
    private Exception? error;
    private bool disposed;
    public int PeakPcm16 { get; private set; }
    public StreamingAudioSource()
    {
        using var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        deviceId = device.ID; capture = new WasapiLoopbackCapture(device);
        buffer = new BufferedWaveProvider(capture.WaveFormat) { BufferDuration = TimeSpan.FromMilliseconds(120), ReadFully = true, DiscardOnBufferOverflow = true };
        ISampleProvider provider = buffer.ToSampleProvider();
        if (provider.WaveFormat.Channels == 1) provider = new MonoToStereoSampleProvider(provider);
        else if (provider.WaveFormat.Channels != 2) provider = new MultiplexingSampleProvider([provider], 2);
        if (provider.WaveFormat.SampleRate != 48000) provider = new WdlResamplingSampleProvider(provider, 48000);
        pcm = new SampleToWaveProvider16(provider);
        capture.DataAvailable += (_, e) => { if (!disposed && e.BytesRecorded > 0) buffer.AddSamples(e.Buffer, 0, e.BytesRecorded); };
        capture.RecordingStopped += (_, e) => { if (!disposed) error = e.Exception ?? new IOException("PC音声の取得が停止しました。"); };
    }
    public void Start() => capture.StartRecording();
    public void ValidateDevice()
    {
        if (error != null) throw new IOException("PC音声の取得を続行できません。", error);
        using var current = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        if (current.ID != deviceId) throw new IOException("再生デバイスが変わりました。もう一度キャストを開始してください。");
    }
    public short[] ReadFrame()
    {
        if (error != null) throw new IOException("PC音声の取得が停止しました。", error);
        pcm.Read(bytes, 0, bytes.Length); Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
        foreach (short value in samples) PeakPcm16 = Math.Max(PeakPcm16, Math.Abs((int)value));
        return samples;
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        try { capture.StopRecording(); } finally { capture.Dispose(); devices.Dispose(); }
    }
}
