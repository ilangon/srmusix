using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FFmpegNativePlayer;

public sealed class SubtitleController
{
    private sealed record Cue(double Start, double End, string Text);
    private readonly List<Cue> _cues = new();
    public bool Enabled { get; set; } = true;
    public string ActiveName { get; private set; } = "Off";

    public void Clear() { _cues.Clear(); ActiveName = "Off"; }

    public async Task LoadExternalAsync(string path)
    {
        string text = await File.ReadAllTextAsync(path, Encoding.UTF8);
        LoadSrtText(text);
        ActiveName = Path.GetFileName(path);
        Enabled = true;
    }

    public async Task LoadEmbeddedAsync(string source, int streamIndex, string displayName)
    {
        string ffmpeg = RuntimePaths.DecoderExe;
        if (!File.Exists(ffmpeg)) throw new FileNotFoundException("ffmpeg.exe not found for subtitle extraction", ffmpeg);
        string temp = Path.Combine(Path.GetTempPath(), $"smartplayer_sub_{Environment.ProcessId}_{streamIndex}.srt");
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
            Arguments = $"-hide_banner -loglevel error -y -i \"{source}\" -map 0:{streamIndex} -f srt \"{temp}\""
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffmpeg subtitle extractor");
        string err = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0 || !File.Exists(temp)) throw new InvalidOperationException("Subtitle track is not text-convertible. " + err.Trim());
        string text = await File.ReadAllTextAsync(temp, Encoding.UTF8);
        try { File.Delete(temp); } catch { }
        LoadSrtText(text);
        ActiveName = displayName;
        Enabled = true;
    }

    public string GetText(double seconds)
    {
        if (!Enabled || _cues.Count == 0) return "";
        // subtitle cue count is normally small enough for linear scan around current time; binary search start times.
        int lo = 0, hi = _cues.Count - 1, best = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_cues[mid].Start <= seconds) { best = mid; lo = mid + 1; } else hi = mid - 1;
        }
        if (best >= 0 && seconds <= _cues[best].End) return _cues[best].Text;
        return "";
    }

    private void LoadSrtText(string input)
    {
        _cues.Clear();
        string normalized = input.Replace("\r\n", "\n").Replace('\r','\n');
        foreach (string block in Regex.Split(normalized.Trim(), "\\n\\s*\\n"))
        {
            var lines = block.Split('\n');
            int timeLine = Array.FindIndex(lines, l => l.Contains(" --> "));
            if (timeLine < 0) continue;
            var parts = lines[timeLine].Split(" --> ", StringSplitOptions.None);
            if (parts.Length != 2 || !TryTime(parts[0], out double start) || !TryTime(parts[1], out double end)) continue;
            string text = string.Join("\n", lines[(timeLine + 1)..]).Trim();
            text = Regex.Replace(text, "<[^>]+>", "");
            if (!string.IsNullOrWhiteSpace(text)) _cues.Add(new Cue(start, end, text));
        }
        _cues.Sort((a,b) => a.Start.CompareTo(b.Start));
    }

    private static bool TryTime(string value, out double seconds)
    {
        seconds = 0;
        var m = Regex.Match(value.Trim(), @"(?<h>\d+):(?<m>\d+):(?<s>\d+)[,.](?<ms>\d+)");
        if (!m.Success) return false;
        seconds = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture) * 3600 +
                  int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) * 60 +
                  int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture) +
                  int.Parse(m.Groups["ms"].Value, CultureInfo.InvariantCulture) / Math.Pow(10, m.Groups["ms"].Value.Length);
        return true;
    }
}
