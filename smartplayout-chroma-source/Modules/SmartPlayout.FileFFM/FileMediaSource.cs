using System;
using System.IO;

namespace SmartPlayout.FileFFM;

public enum MediaSourceKind { File, NetworkUrl, LiveInput }

public sealed record FileMediaSource(string Source, MediaSourceKind Kind)
{
    public static FileMediaSource From(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("Media source is empty.", nameof(source));
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && !uri.IsFile)
            return new FileMediaSource(source, MediaSourceKind.NetworkUrl);
        return new FileMediaSource(Path.GetFullPath(source), MediaSourceKind.File);
    }

    public bool Exists => Kind != MediaSourceKind.File || File.Exists(Source);
}
