using System;
using System.IO;
using System.Linq;
using System.Text;

namespace FFmpegNativePlayer;

internal static class DataStorage
{
    public static readonly string Root = @"D:\Smart Playout";
    public static readonly string DataRoot = Path.Combine(Root, "Data");
    public static readonly string BackupRoot = Path.Combine(Root, "Backup");
    public static readonly string Programs = Path.Combine(DataRoot, "Programs");
    public static readonly string Playlists = Path.Combine(DataRoot, "Playlists");
    public static readonly string Schedules = Path.Combine(DataRoot, "Schedules");
    public static readonly string Settings = Path.Combine(DataRoot, "Settings");
    public static readonly string AutoState = Path.Combine(DataRoot, "AutoState");

    public static readonly string CurrentPlaylist = Path.Combine(AutoState, "CurrentPlaylist.splaylist");
    public static readonly string CurrentSchedule = Path.Combine(AutoState, "CurrentSchedule.sschedule");
    public static readonly string CurrentFiller = Path.Combine(AutoState, "CurrentFiller.splaylist");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(BackupRoot);
        Directory.CreateDirectory(Programs);
        Directory.CreateDirectory(Playlists);
        Directory.CreateDirectory(Schedules);
        Directory.CreateDirectory(Settings);
        Directory.CreateDirectory(AutoState);
        Directory.CreateDirectory(Path.Combine(BackupRoot, "Programs"));
        Directory.CreateDirectory(Path.Combine(BackupRoot, "Playlists"));
        Directory.CreateDirectory(Path.Combine(BackupRoot, "Schedules"));
        Directory.CreateDirectory(Path.Combine(BackupRoot, "AutoState"));
        Directory.CreateDirectory(Path.Combine(BackupRoot, "Settings"));
    }

    public static string SafeFileName(string name, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();
        foreach (char c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    public static string ProgramPath(string name) => Path.Combine(Programs, SafeFileName(name, "Program") + ".sprogram");
    public static string PlaylistPath(string name) => Path.Combine(Playlists, SafeFileName(name, "Playlist") + ".splaylist");
    public static string SchedulePath(string name) => Path.Combine(Schedules, SafeFileName(name, "Schedule") + ".sschedule");

    public static void WritePersistentText(string targetPath, string text, string backupCategory)
    {
        EnsureCreated();
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        BackupExisting(targetPath, backupCategory);

        string temp = targetPath + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, targetPath, true);
    }

    public static void MirrorToData(string sourcePath, string dataTargetPath, string backupCategory)
    {
        if (!File.Exists(sourcePath)) return;
        string text = File.ReadAllText(sourcePath);
        WritePersistentText(dataTargetPath, text, backupCategory);
    }

    private static void BackupExisting(string targetPath, string category)
    {
        if (!File.Exists(targetPath)) return;
        string dir = Path.Combine(BackupRoot, category);
        Directory.CreateDirectory(dir);
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        string backupName = $"{Path.GetFileNameWithoutExtension(targetPath)}_{stamp}{Path.GetExtension(targetPath)}.bak";
        File.Copy(targetPath, Path.Combine(dir, backupName), true);

        // Keep the backup area bounded while preserving recent recovery points.
        var old = new DirectoryInfo(dir).GetFiles(Path.GetFileNameWithoutExtension(targetPath) + "_*.bak")
            .OrderByDescending(f => f.CreationTimeUtc).Skip(30).ToList();
        foreach (var f in old) { try { f.Delete(); } catch { } }
    }
}
