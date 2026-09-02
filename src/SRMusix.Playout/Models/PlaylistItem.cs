namespace SRMusix.Playout.Models;

public sealed class PlaylistItem
{
    public required string FilePath { get; init; }
    public string Title => Path.GetFileNameWithoutExtension(FilePath);
    public TimeSpan Duration { get; set; }
    public DateTime? ScheduledAt { get; set; }
    public string Status { get; set; } = "Ready";
}

