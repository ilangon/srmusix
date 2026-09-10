using System;
using System.Collections.Generic;
using System.Linq;
using SmartPlayout.CodecsFFM;

namespace FFmpegNativePlayer;

public sealed class MediaCompatibilityProbe
{
    public sealed record Track(int StreamIndex, string Kind, string Codec, string Language, string Title, int Channels = 0, int SampleRate = 0, bool DecoderAvailable = true);
    public sealed record Result(
        string Container, string VideoCodec, string AudioCodec,
        int Width, int Height, double Fps, int SampleRate, int AudioChannels, long AudioBitrate,
        double DurationSeconds, bool VideoDecoderAvailable, bool AudioDecoderAvailable,
        IReadOnlyList<Track> AudioTracks, IReadOnlyList<Track> SubtitleTracks);

    public static Result Probe(string source)
    {
        var p = FfmpegMediaProbe.Probe(source, AppContext.BaseDirectory);
        var audio = p.AudioStreams.Select(a => new Track(a.StreamIndex,"audio",a.Codec,a.Language,a.Title,a.Channels,a.SampleRate,a.DecoderAvailable)).ToList();
        var subs = p.SubtitleStreams.Select(s => new Track(s.StreamIndex,"subtitle",s.Codec,s.Language,s.Title,0,0,s.DecoderAvailable)).ToList();
        var firstAudio = p.AudioStreams.Count > 0 ? p.AudioStreams[0] : null;
        return new Result(p.Container,p.VideoCodec,firstAudio?.Codec ?? "none",
            p.Width,p.Height,p.FramesPerSecond,firstAudio?.SampleRate ?? 0,firstAudio?.Channels ?? 0,firstAudio?.Bitrate ?? 0,
            p.DurationSeconds,p.VideoDecoderAvailable,firstAudio?.DecoderAvailable ?? true,audio,subs);
    }

    public static void RequirePlayableVideo(string source) =>
        FfmpegMediaProbe.RequirePlayableVideo(source, AppContext.BaseDirectory);
}
