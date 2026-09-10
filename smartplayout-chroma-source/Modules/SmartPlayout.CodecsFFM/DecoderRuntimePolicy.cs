using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SmartPlayout.CodecsFFM;

public enum DecoderBackend { SmartFfmpeg, MicrosoftMediaFoundation }

public static class DecoderRuntimePolicy
{
    public static readonly string[] RequiredLibraries =
    {
        "avcodec-63.dll","avformat-63.dll","avutil-61.dll",
        "avfilter-12.dll","avdevice-63.dll","swscale-10.dll","swresample-7.dll"
    };

    public static string RuntimeDirectory(string applicationRoot) =>
        Path.Combine(applicationRoot, "Runtime", "MediaCore", "FFmpeg");

    public static IReadOnlyList<string> MissingFiles(string applicationRoot)
    {
        string dir=RuntimeDirectory(applicationRoot);
        return RequiredLibraries.Where(f=>!File.Exists(Path.Combine(dir,f))).ToArray();
    }

    public static string RequireSmartFfmpeg(string applicationRoot)
    {
        string dir=RuntimeDirectory(applicationRoot);
        var missing=MissingFiles(applicationRoot);
        if(missing.Count>0)
            throw new FileNotFoundException(
                "SMART PLAYOUT decoder runtime is incomplete. Missing: "+string.Join(", ",missing)+
                ". Shared media runtime is locked to Runtime\\MediaCore\\FFmpeg; PATH and unrelated DLL folders are never borrowed.");
        return dir;
    }

    public static DecoderBackend FallbackBackend => DecoderBackend.MicrosoftMediaFoundation;
}
