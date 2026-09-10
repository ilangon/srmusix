using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using SmartPlayout.CodecsFFM;

namespace FFmpegNativePlayer;

internal static class AudioLanguageHelper
{
    public const string DefaultLanguage = "Tamil";
    private static readonly ConcurrentDictionary<string, MediaCompatibilityProbe.Result> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static MediaCompatibilityProbe.Result Probe(string file) =>
        Cache.GetOrAdd(file, MediaCompatibilityProbe.Probe);

    public static List<string> GetLanguages(string file)
    {
        try
        {
            var tracks = Probe(file).AudioTracks;
            var langs = tracks.Select(t => string.IsNullOrWhiteSpace(t.Language) ? "Unknown" : t.Language.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            // Tamil is always the preferred default. If the file has no Tamil track,
            // playback safely falls back to its first available audio stream.
            if (!langs.Any(x => string.Equals(x, DefaultLanguage, StringComparison.OrdinalIgnoreCase)))
                langs.Insert(0, DefaultLanguage);
            else
            {
                langs.RemoveAll(x => string.Equals(x, DefaultLanguage, StringComparison.OrdinalIgnoreCase));
                langs.Insert(0, DefaultLanguage);
            }
            return langs;
        }
        catch { return new List<string> { DefaultLanguage }; }
    }

    public static int ResolveStreamIndex(string file, string? preferredLanguage)
    {
        try
        {
            var tracks = Probe(file).AudioTracks;
            if (tracks.Count == 0) return -1;
            string wanted = string.IsNullOrWhiteSpace(preferredLanguage) ? DefaultLanguage : preferredLanguage.Trim();
            var exact = tracks.FirstOrDefault(t => string.Equals(t.Language, wanted, StringComparison.OrdinalIgnoreCase));
            var selected = exact ?? tracks[0];
            if (!selected.DecoderAvailable)
                throw new NotSupportedException($"Selected audio stream #{selected.StreamIndex} codec '{selected.Codec}' has no decoder in SMART PLAYOUT Decoder runtime.");
            return selected.StreamIndex;
        }
        catch (NotSupportedException) { throw; }
        catch { return -1; }
    }
}
