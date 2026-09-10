using System;
using System.IO;

namespace SmartPlayout.EncoderFFM;

public static class EncoderRuntimePolicy
{
    public static string RuntimeDirectory(string applicationRoot) =>
        Path.Combine(applicationRoot, "Runtime", "MediaCore", "FFmpeg");

    public static string FfmpegExe(string applicationRoot) =>
        Path.Combine(RuntimeDirectory(applicationRoot), "ffmpeg.exe");

    public static string FfprobeExe(string applicationRoot) =>
        Path.Combine(RuntimeDirectory(applicationRoot), "ffprobe.exe");

    public static string RequireFfmpegExe(string applicationRoot)
    {
        string exe=FfmpegExe(applicationRoot);
        if(!File.Exists(exe))
            throw new FileNotFoundException(
                "SMART PLAYOUT shared media runtime is missing ffmpeg.exe at Runtime\\MediaCore\\FFmpeg. "+
                "Encoder ownership never falls back to PATH or an unrelated FFmpeg DLL family.", exe);
        return exe;
    }
}
