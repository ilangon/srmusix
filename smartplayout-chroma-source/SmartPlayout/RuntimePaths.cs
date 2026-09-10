using SmartPlayout.CodecsFFM;
using SmartPlayout.EncoderFFM;
using System;
using System.IO;

namespace FFmpegNativePlayer;

/// <summary>
/// Central runtime ownership map. Decode, encode and capture share one version-locked
/// FFmpeg family. Hardware-vendor bridges remain isolated from the media runtime.
/// </summary>
public static class RuntimePaths
{
    public static string Root => Path.Combine(AppContext.BaseDirectory, "Runtime");
    public static string MediaCoreFfmpeg => Path.Combine(Root, "MediaCore", "FFmpeg");
    public static string DecoderFfmpeg => DecoderRuntimePolicy.RuntimeDirectory(AppContext.BaseDirectory);
    public static string EncoderFfmpeg => EncoderRuntimePolicy.RuntimeDirectory(AppContext.BaseDirectory);
    public static string DirectShowFfmpeg => MediaCoreFfmpeg;
    public static string DeckLinkFfmpeg => MediaCoreFfmpeg;
    public static string DeckLinkNative => Path.Combine(Root, "DeckLink", "Native");
    public static string Diagnostics => Path.Combine(AppContext.BaseDirectory, "RuntimeData", "Capabilities");

    public static string DecoderExe => Path.Combine(DecoderFfmpeg, "ffmpeg.exe");
    public static string DecoderProbe => Path.Combine(DecoderFfmpeg, "ffprobe.exe");
    public static string EncoderExe => EncoderRuntimePolicy.FfmpegExe(AppContext.BaseDirectory);
    public static string EncoderProbe => EncoderRuntimePolicy.FfprobeExe(AppContext.BaseDirectory);
    public static string DirectShowExe => Path.Combine(DirectShowFfmpeg, "ffmpeg.exe");
    public static string DeckLinkExe => Path.Combine(DeckLinkFfmpeg, "ffmpeg.exe");
    public static string DeckLinkNativeBridge => Path.Combine(DeckLinkNative, "SMARTPlayout.Device.BMD.x64.dll");
}
