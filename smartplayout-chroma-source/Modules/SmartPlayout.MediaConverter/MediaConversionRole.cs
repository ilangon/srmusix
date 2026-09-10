namespace SmartPlayout.MediaConverter;

public sealed record MediaConversionRole(
    string InputPixelFormat,
    string OutputPixelFormat,
    int OutputSampleRate,
    int OutputChannels)
{
    public static MediaConversionRole BroadcastDefault => new("AUTO","BGRA/S16",48000,2);
}
