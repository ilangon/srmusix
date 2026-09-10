using System.IO;
namespace FFmpegNativePlayer;
public sealed class PlaylistItem
{
    public string FilePath { get; set; } = "";
    public string Name => Path.GetFileName(FilePath);
    public string Type => Path.GetExtension(FilePath).TrimStart('.').ToUpperInvariant();
}
