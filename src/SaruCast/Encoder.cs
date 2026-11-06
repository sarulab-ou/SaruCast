namespace SaruCast;

public static class Encoder
{
    public static int VideoBitrateKbps(Settings settings)
    {
        int fullRate = settings.Height <= 720 ? 3000 : 6000;
        return Settings.NormalizeFrameRate(settings.FrameRate) switch { 5 => fullRate / 3, 15 => fullRate * 2 / 3, _ => fullRate };
    }
    public static (int Width, int Height) Size(int width, int height, int maxHeight)
    {
        double scale = Math.Min(1, Math.Min(1920.0 / width, Math.Clamp(maxHeight, 720, 1080) / (double)height));
        return (Math.Max(2, (int)(width * scale) / 2 * 2), Math.Max(2, (int)(height * scale) / 2 * 2));
    }
}
