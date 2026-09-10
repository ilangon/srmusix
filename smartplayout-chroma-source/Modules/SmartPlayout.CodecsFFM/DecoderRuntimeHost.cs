using System;
using FFmpeg.AutoGen.Abstractions;
using FFmpeg.AutoGen.Bindings.DynamicallyLoaded;

namespace SmartPlayout.CodecsFFM;

public static class DecoderRuntimeHost
{
    private static readonly object Gate = new();
    private static bool _initialized;

    public static string EnsureInitialized(string applicationRoot)
    {
        lock (Gate)
        {
            string path = DecoderRuntimePolicy.RequireSmartFfmpeg(applicationRoot);
            if (_initialized) return path;

            // FFmpeg.AutoGen 9.x split-package API:
            //   API types/functions          -> FFmpeg.AutoGen.Abstractions
            //   dynamic native DLL resolver -> FFmpeg.AutoGen.Bindings.DynamicallyLoaded
            DynamicallyLoadedBindings.LibrariesPath = path;
            DynamicallyLoadedBindings.ThrowErrorIfFunctionNotFound = true;
            DynamicallyLoadedBindings.Initialize();

            // Force a native call now so runtime/ABI mismatches fail at decoder startup.
            _ = ffmpeg.av_version_info();

            _initialized = true;
            return path;
        }
    }
}
