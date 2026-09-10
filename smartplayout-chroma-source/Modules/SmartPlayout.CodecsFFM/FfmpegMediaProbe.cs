using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using SmartPlayout.FileFFM;

namespace SmartPlayout.CodecsFFM;

public unsafe static class FfmpegMediaProbe
{
    public sealed record StreamInfo(
        int StreamIndex, string Kind, string Codec, bool DecoderAvailable,
        string Language, string Title, int Width = 0, int Height = 0,
        int Channels = 0, int SampleRate = 0, long Bitrate = 0, double FramesPerSecond = 0);

    public sealed record Result(
        string Source, string Container, double DurationSeconds,
        int VideoStreamIndex, string VideoCodec, bool VideoDecoderAvailable,
        int Width, int Height, double FramesPerSecond,
        IReadOnlyList<StreamInfo> VideoStreams,
        IReadOnlyList<StreamInfo> AudioStreams,
        IReadOnlyList<StreamInfo> SubtitleStreams)
    {
        public bool HasVideo => VideoStreams.Count > 0;
        public bool HasAudio => AudioStreams.Count > 0;
    }

    public static Result Probe(string source, string applicationRoot)
    {
        var media = FileMediaSource.From(source);
        if (!media.Exists) throw new System.IO.FileNotFoundException("Media source was not found.", media.Source);
        DecoderRuntimeHost.EnsureInitialized(applicationRoot);

        AVFormatContext* fmt = null;
        try
        {
            Throw(ffmpeg.avformat_open_input(&fmt, media.Source, null, null), "demux open");
            Throw(ffmpeg.avformat_find_stream_info(fmt, null), "stream info");

            string container = fmt->iformat != null && fmt->iformat->name != null
                ? Marshal.PtrToStringAnsi((IntPtr)fmt->iformat->name) ?? "unknown" : "unknown";

            var videos = new List<StreamInfo>();
            var audios = new List<StreamInfo>();
            var subtitles = new List<StreamInfo>();

            for (int i = 0; i < (int)fmt->nb_streams; i++)
            {
                AVStream* st = fmt->streams[i];
                AVCodecParameters* cp = st == null ? null : st->codecpar;
                if (cp == null) continue;

                string codec = CodecName(cp->codec_id);
                bool decoder = ffmpeg.avcodec_find_decoder(cp->codec_id) != null;
                string lang = NormalizeLanguage(Metadata(st->metadata, "language"), Metadata(st->metadata, "title"));
                string title = Metadata(st->metadata, "title");

                if (cp->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    AVRational fr = st->avg_frame_rate.num != 0 ? st->avg_frame_rate : st->r_frame_rate;
                    double fps = fr.den != 0 ? ffmpeg.av_q2d(fr) : 0;
                    videos.Add(new StreamInfo(st->index, "video", codec, decoder, lang, title,
                        cp->width, cp->height, 0, 0, cp->bit_rate, fps));
                }
                else if (cp->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                {
                    audios.Add(new StreamInfo(st->index, "audio", codec, decoder, lang, title,
                        0, 0, cp->ch_layout.nb_channels, cp->sample_rate, cp->bit_rate, 0));
                }
                else if (cp->codec_type == AVMediaType.AVMEDIA_TYPE_SUBTITLE)
                {
                    subtitles.Add(new StreamInfo(st->index, "subtitle", codec, decoder, lang, title));
                }
            }

            AVCodec* bestDecoder = null;
            int bestVideoArray = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &bestDecoder, 0);
            int bestVideoStreamIndex = -1;
            StreamInfo? bestVideo = null;
            if (bestVideoArray >= 0 && bestVideoArray < (int)fmt->nb_streams)
            {
                int streamIndex = fmt->streams[bestVideoArray]->index;
                bestVideo = videos.Find(x => x.StreamIndex == streamIndex);
                bestVideoStreamIndex = streamIndex;
            }
            bestVideo ??= videos.Count > 0 ? videos[0] : null;
            if (bestVideo != null && bestVideoStreamIndex < 0) bestVideoStreamIndex = bestVideo.StreamIndex;

            double duration = fmt->duration > 0 ? fmt->duration / (double)ffmpeg.AV_TIME_BASE : 0;
            return new Result(media.Source, container, duration,
                bestVideoStreamIndex, bestVideo?.Codec ?? "none", bestVideo?.DecoderAvailable ?? false,
                bestVideo?.Width ?? 0, bestVideo?.Height ?? 0, bestVideo?.FramesPerSecond ?? 0,
                videos, audios, subtitles);
        }
        finally
        {
            if (fmt != null) { AVFormatContext* local = fmt; ffmpeg.avformat_close_input(&local); }
        }
    }

    public static Result RequirePlayableVideo(string source, string applicationRoot)
    {
        var r = Probe(source, applicationRoot);
        if (!r.HasVideo)
            throw new InvalidOperationException($"No video stream was found. Container={r.Container}.");
        if (!r.VideoDecoderAvailable)
            throw new NotSupportedException($"No SMART FFmpeg decoder is available for video codec '{r.VideoCodec}' in container '{r.Container}'.");
        return r;
    }

    public static StreamInfo? ResolveAudioStream(Result result, string? preferredLanguage)
    {
        if (result.AudioStreams.Count == 0) return null;
        string wanted = string.IsNullOrWhiteSpace(preferredLanguage) ? "Tamil" : preferredLanguage.Trim();
        foreach (var a in result.AudioStreams)
            if (string.Equals(a.Language, wanted, StringComparison.OrdinalIgnoreCase)) return a;
        return result.AudioStreams[0];
    }

    public static void RequireAudioDecoder(StreamInfo? stream)
    {
        if (stream == null) return; // valid video-only file
        if (!stream.DecoderAvailable)
            throw new NotSupportedException($"No SMART FFmpeg decoder is available for selected audio stream #{stream.StreamIndex} codec '{stream.Codec}'.");
    }

    private static string Metadata(AVDictionary* dict, string key)
    {
        if (dict == null) return "";
        AVDictionaryEntry* e = ffmpeg.av_dict_get(dict, key, null, 0);
        return e != null && e->value != null ? Marshal.PtrToStringUTF8((IntPtr)e->value) ?? "" : "";
    }

    private static string NormalizeLanguage(string s, string title)
    {
        static string C(string v) => (v ?? "").Trim().ToLowerInvariant().Replace('_','-');
        string lang=C(s), t=C(title);
        if (!string.IsNullOrWhiteSpace(lang) && lang is not "und" and not "unknown" and not "none")
        {
            if (lang is "tam" or "ta" || lang.StartsWith("ta-") || lang.Contains("tamil")) return "Tamil";
            if (lang is "tel" or "te" || lang.StartsWith("te-") || lang.Contains("telugu")) return "Telugu";
            if (lang is "mal" or "ml" || lang.StartsWith("ml-") || lang.Contains("malayalam")) return "Malayalam";
            if (lang is "hin" or "hi" || lang.StartsWith("hi-") || lang.Contains("hindi")) return "Hindi";
            if (lang is "kan" or "kn" || lang.StartsWith("kn-") || lang.Contains("kannada")) return "Kannada";
            if (lang is "eng" or "en" || lang.StartsWith("en-") || lang.Contains("english")) return "English";
            return s.Trim();
        }
        if (t.Contains("tamil") || t.Contains("தமிழ")) return "Tamil";
        if (t.Contains("telugu") || t.Contains("తెలుగు")) return "Telugu";
        if (t.Contains("malayalam") || t.Contains("മലയാള")) return "Malayalam";
        if (t.Contains("hindi") || t.Contains("हिन्द") || t.Contains("हिंदी")) return "Hindi";
        if (t.Contains("kannada") || t.Contains("ಕನ್ನಡ")) return "Kannada";
        if (t.Contains("english")) return "English";
        return "Unknown";
    }

    private static string CodecName(AVCodecID id)
    {
        string? n = ffmpeg.avcodec_get_name(id);
        return string.IsNullOrWhiteSpace(n) ? id.ToString() : n;
    }

    private static void Throw(int code, string stage)
    {
        if (code >= 0) return;
        byte* buf = stackalloc byte[1024]; ffmpeg.av_strerror(code, buf, 1024);
        throw new InvalidOperationException(stage + ": " + (Marshal.PtrToStringAnsi((IntPtr)buf) ?? code.ToString()));
    }
}
