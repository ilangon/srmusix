using System.Text.Json;

namespace SmartPlayout.Contracts;

public enum ModuleKind
{
    PlayoutEngine,
    CgStudio,
    ChromaStudio,
    OutputWorker
}

public enum ModuleState
{
    Starting,
    Ready,
    Busy,
    Reconnecting,
    Faulted,
    Stopped
}

public sealed record ModuleCommand(
    string RequestId,
    ModuleKind Target,
    string Operation,
    JsonElement Payload,
    DateTimeOffset CreatedUtc);

public sealed record ModuleReply(
    string RequestId,
    bool Success,
    string ErrorCode,
    string Message,
    JsonElement Payload,
    DateTimeOffset CompletedUtc);

public sealed record ModuleHeartbeat(
    ModuleKind Module,
    ModuleState State,
    int ProcessId,
    double CpuPercent,
    long WorkingSetBytes,
    long LastFrameNumber,
    long LastAudioPacketNumber,
    long AvDeltaTicks,
    DateTimeOffset TimestampUtc,
    string Detail);

public sealed record RenderRegistration(
    int Width,
    int Height,
    double FramesPerSecond,
    bool Interlaced,
    int AudioSampleRate,
    int AudioChannels,
    string PixelFormat,
    string SharedFrameName,
    string SharedAudioName);

public sealed record OutputRouteRegistration(
    string Id,
    string Kind,
    string FfmpegPath,
    string Destination,
    int Width,
    int Height,
    double FramesPerSecond,
    int SampleRate,
    int Channels,
    string VideoEncoder,
    string AudioEncoder,
    int VideoKbps,
    int AudioKbps,
    string PixelFormat,
    string SharedFrameName,
    string SharedAudioName,
    bool Interlaced,
    string FfmpegArgumentsTemplate);

public sealed record LayerCommand(
    string LayerId,
    string LayerType,
    int Z,
    bool Visible,
    JsonElement Properties);

public static class ModuleJson
{
    public static readonly JsonSerializerOptions Options=new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive=true,
        WriteIndented=false
    };

    public static string Serialize<T>(T value)=>JsonSerializer.Serialize(value,Options);
    public static T? Deserialize<T>(string json)=>JsonSerializer.Deserialize<T>(json,Options);
}
