using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FFmpegNativePlayer;

public sealed record DeckLinkOutputConnection(uint Value, string Name)
{
    public override string ToString() => Name;
}

public sealed record DeckLinkOutputMode(uint Id, string Name, int Width, int Height, long TimeScale, long FrameDuration, bool Interlaced)
{
    public double Fps => FrameDuration > 0 ? (double)TimeScale / FrameDuration : 0;
    public override string ToString() => $"{Name} • {Width}x{Height} • {Fps:0.###} {(Interlaced ? "i" : "p")}";
}

/// <summary>
/// Native Blackmagic DeckLink SDK output path.
/// Device, physical output connection and display mode are all queried from the card.
/// The bridge consumes Final Program BGRA video + PCM audio without H.264/H.265 encoding.
/// </summary>
public sealed class DeckLinkNativeOutputEngine : IDisposable
{
    const string BridgeName = "SMARTPlayout.Device.BMD.x64.dll";

    static DeckLinkNativeOutputEngine()
    {
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(DeckLinkNativeOutputEngine).Assembly, (name, assembly, path) =>
            {
                if (!string.Equals(name, BridgeName, StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
                string exact = RuntimePaths.DeckLinkNativeBridge;
                return File.Exists(exact) ? NativeLibrary.Load(exact) : IntPtr.Zero;
            });
        }
        catch (InvalidOperationException) { }
    }

    readonly object _sync = new();
    bool _running;
    long _videoFrameNumber;
    long _audioSampleFrame;
    byte[] _videoScratch = Array.Empty<byte>();
    long _lastHealthFrame;
    int _targetWidth, _targetHeight;
    int _scaleMode;
    double _displayAspect = 16.0/9.0;
    BitmapSource? _pendingVideo;
    int _videoPumpActive;

    public bool Running { get { lock (_sync) return _running; } }
    public string Status { get; private set; } = "STOPPED";
    public string? ActiveDevice { get; private set; }
    public string? ActiveConnection { get; private set; }
    public string? ActiveMode { get; private set; }
    public string BridgePath => RuntimePaths.DeckLinkNativeBridge;
    public bool BridgePresent => File.Exists(BridgePath);
    public event Action<string>? StatusChanged;

    [DllImport(BridgeName, CallingConvention = CallingConvention.Cdecl)]
    static extern int SPDeckLink_GetApiVersion();
    [DllImport(BridgeName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    static extern int SPDeckLink_EnumerateOutputDevices(IntPtr buffer, int charCapacity);
    [DllImport(BridgeName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    static extern int SPDeckLink_EnumerateOutputConnections(int deviceIndex, IntPtr buffer, int charCapacity);
    [DllImport(BridgeName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    static extern int SPDeckLink_EnumerateOutputModes(int deviceIndex, uint connection, IntPtr buffer, int charCapacity);
    [DllImport(BridgeName, CallingConvention = CallingConvention.Cdecl)]
    static extern int SPDeckLink_StartOutput(int deviceIndex, uint connection, uint displayMode, int audioChannels);
    [DllImport(BridgeName, CallingConvention = CallingConvention.Cdecl)]
    static extern int SPDeckLink_PushVideoBGRA(IntPtr bytes, int byteCount, int stride, long frameNumber);
    [DllImport(BridgeName, CallingConvention = CallingConvention.Cdecl)]
    static extern int SPDeckLink_PushAudioS16(IntPtr bytes, int byteCount, int sampleRate, int channels, long startSampleFrame);
    [DllImport(BridgeName, CallingConvention = CallingConvention.Cdecl)]
    static extern int SPDeckLink_StopOutput();
    [DllImport(BridgeName, CallingConvention = CallingConvention.Cdecl)]
    static extern int SPDeckLink_GetDiagnostics(out long videoScheduled, out long audioScheduled, out long videoQueueDrops, out long audioQueueDrops, out long bufferedVideo, out long bufferedAudio);
    [DllImport(BridgeName, CallingConvention = CallingConvention.Cdecl)]
    static extern int SPDeckLink_GetOutputHealth(out long completed, out long late, out long hardwareDropped, out long flushed, out long streamTime);
    [DllImport(BridgeName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    static extern int SPDeckLink_GetLastError(IntPtr buffer, int charCapacity);

    public (bool Available, List<string> Devices, string Message) Detect()
    {
        var devices = new List<string>();
        if (!BridgePresent)
            return (false, devices, "Native DeckLink bridge is not built/installed. START is locked to prevent a UI freeze or native crash.");
        try
        {
            NativeLibrary.Load(BridgePath);
            int api = SPDeckLink_GetApiVersion();
            IntPtr p = Marshal.AllocHGlobal(64 * 1024);
            try
            {
                int count = SPDeckLink_EnumerateOutputDevices(p, 32768);
                string text = Marshal.PtrToStringUni(p) ?? "";
                foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    if (!devices.Contains(line, StringComparer.OrdinalIgnoreCase)) devices.Add(line.Trim());
                string msg = count > 0
                    ? $"Native DeckLink SDK bridge READY • API {api} • {count} output device(s) • physical connection + mode capability query available."
                    : "Native DeckLink SDK bridge loaded, but no output device was enumerated. " + ReadLastError();
                return (count > 0, devices, msg);
            }
            finally { Marshal.FreeHGlobal(p); }
        }
        catch (Exception ex)
        {
            return (false, devices, "Native DeckLink SDK bridge load failed • START LOCKED • " + ex.Message);
        }
    }

    public List<DeckLinkOutputConnection> GetConnections(int deviceIndex)
    {
        var result = new List<DeckLinkOutputConnection>();
        if (!BridgePresent) return result;
        IntPtr p = Marshal.AllocHGlobal(64 * 1024);
        try
        {
            int count = SPDeckLink_EnumerateOutputConnections(deviceIndex, p, 32768);
            if (count <= 0) return result;
            string text = Marshal.PtrToStringUni(p) ?? "";
            foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var a = line.Split(new[] { '|' }, 2);
                if (a.Length == 2 && uint.TryParse(a[0], out uint value)) result.Add(new(value, a[1].Trim()));
            }
        }
        catch { }
        finally { Marshal.FreeHGlobal(p); }
        return result;
    }

    public List<DeckLinkOutputMode> GetModes(int deviceIndex, uint connection)
    {
        var result = new List<DeckLinkOutputMode>();
        if (!BridgePresent) return result;
        IntPtr p = Marshal.AllocHGlobal(256 * 1024);
        try
        {
            int count = SPDeckLink_EnumerateOutputModes(deviceIndex, connection, p, 131072);
            if (count <= 0) return result;
            string text = Marshal.PtrToStringUni(p) ?? "";
            foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var a = line.Split('|');
                if (a.Length < 7) continue;
                if (uint.TryParse(a[0], out uint id) && int.TryParse(a[2], out int w) && int.TryParse(a[3], out int h) &&
                    long.TryParse(a[4], out long scale) && long.TryParse(a[5], out long duration))
                    result.Add(new(id, a[1].Trim(), w, h, scale, duration, a[6].Trim() == "1"));
            }
        }
        catch { }
        finally { Marshal.FreeHGlobal(p); }
        return result;
    }

    public void Start(int deviceIndex, string deviceName, DeckLinkOutputConnection connection, DeckLinkOutputMode mode)
    {
        if (!BridgePresent) throw new InvalidOperationException("Native DeckLink SDK bridge is not installed. START is locked for safety.");
        if (mode.Width <= 0 || mode.Height <= 0 || mode.FrameDuration <= 0 || mode.TimeScale <= 0)
            throw new InvalidOperationException("Selected DeckLink mode is invalid. Refresh device capabilities.");
        Stop("RESTARTING");
        int rc = SPDeckLink_StartOutput(deviceIndex, connection.Value, mode.Id, 2);
        if (rc != 0) throw new InvalidOperationException("Native DeckLink start failed • " + ReadLastError());
        lock (_sync)
        {
            _running = true; _videoFrameNumber = 0; _audioSampleFrame = 0; _lastHealthFrame = 0;
            _targetWidth = mode.Width; _targetHeight = mode.Height;
            ActiveDevice = deviceName; ActiveConnection = connection.Name; ActiveMode = mode.ToString();
        }
        SetStatus($"RUNNING • {deviceName} • {connection.Name} • {mode}");
    }

    public void UpdateScaleMode(int scaleMode,double displayAspect)
    {
        lock(_sync)
        {
            _scaleMode=Math.Clamp(scaleMode,0,3);
            if(displayAspect>0) _displayAspect=displayAspect;
        }
    }

    static BitmapSource FitAndPadToDeckLinkRaster(BitmapSource source, int targetWidth, int targetHeight)
    {
        if (source.PixelWidth == targetWidth && source.PixelHeight == targetHeight) return source;
        if (targetWidth <= 0 || targetHeight <= 0) return source;

        double sx = (double)targetWidth / Math.Max(1, source.PixelWidth);
        double sy = (double)targetHeight / Math.Max(1, source.PixelHeight);
        double scale = Math.Min(sx, sy);
        double drawWidth = Math.Max(1, source.PixelWidth * scale);
        double drawHeight = Math.Max(1, source.PixelHeight * scale);
        double x = (targetWidth - drawWidth) * 0.5;
        double y = (targetHeight - drawHeight) * 0.5;

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual,BitmapScalingMode.HighQuality);
        RenderOptions.SetEdgeMode(visual,EdgeMode.Unspecified);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(System.Windows.Media.Brushes.Black, null, new System.Windows.Rect(0, 0, targetWidth, targetHeight));
            dc.DrawImage(source, new System.Windows.Rect(x, y, drawWidth, drawHeight));
        }
        var raster = new RenderTargetBitmap(targetWidth, targetHeight, 96, 96, PixelFormats.Pbgra32);
        raster.Render(visual); raster.Freeze();
        return raster;
    }

    public void PushVideo(BitmapSource frame)
    {
        if (!Running || frame == null) return;
        // DeckLink copy/conversion/native scheduling must not block the Program decoder.
        // Coalesce to the newest immutable frame on one dedicated worker, especially when
        // 50/60 fps media feeds a 25/29.97 fps hardware output.
        lock (_sync) _pendingVideo = frame;
        if (Interlocked.Exchange(ref _videoPumpActive, 1) != 0) return;
        _ = Task.Run(PumpLatestVideo);
    }

    void PumpLatestVideo()
    {
        try
        {
            while (Running)
            {
                BitmapSource? frame;
                lock (_sync) { frame = _pendingVideo; _pendingVideo = null; }
                if (frame == null) break;
                PushVideoCore(frame);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _videoPumpActive, 0);
            bool restart;
            lock (_sync) restart = Running && _pendingVideo != null;
            if (restart && Interlocked.Exchange(ref _videoPumpActive, 1) == 0)
                _ = Task.Run(PumpLatestVideo);
        }
    }

    void PushVideoCore(BitmapSource frame)
    {
        try
        {
            BitmapSource src = frame;
            int liveScale; double liveAspect;
            lock(_sync) { liveScale=_scaleMode; liveAspect=_displayAspect; }
            // Native DeckLink consumes the SAME live framing rule as RTMP/UDP/DVB/SRT.
            // The WPF confidence-preview dimensions are never used as hardware raster state.
            // FINAL PROGRAM normally arrives already framed to the hardware raster.
            // Only scale at the DeckLink edge for an explicit manual raster mismatch.
            if (src.PixelWidth != _targetWidth || src.PixelHeight != _targetHeight)
                src = MasterAvBus.ScaleFrameToRaster(src,_targetWidth,_targetHeight,liveScale,liveAspect);
            if (src.Format != PixelFormats.Bgra32)
            {
                var converted = new FormatConvertedBitmap();
                converted.BeginInit(); converted.Source = src; converted.DestinationFormat = PixelFormats.Bgra32; converted.EndInit(); converted.Freeze(); src = converted;
            }
            int stride = src.PixelWidth * 4;
            int bytes = stride * src.PixelHeight;
            if (_videoScratch.Length != bytes) _videoScratch = new byte[bytes];
            src.CopyPixels(_videoScratch, stride, 0);
            var h = GCHandle.Alloc(_videoScratch, GCHandleType.Pinned);
            try
            {
                long n; lock (_sync) n = _videoFrameNumber++;
                int rc = SPDeckLink_PushVideoBGRA(h.AddrOfPinnedObject(), bytes, stride, n);
                if (rc != 0) SetStatus("ERROR • NATIVE DECKLINK VIDEO • " + ReadLastError());
                else if (n - _lastHealthFrame >= 50)
                {
                    _lastHealthFrame = n; string health = ReadHealth();
                    if (!string.IsNullOrWhiteSpace(health)) SetStatus($"RUNNING • {ActiveDevice} • {ActiveConnection} • {health}");
                }
            }
            finally { h.Free(); }
        }
        catch (Exception ex) { SetStatus("ERROR • NATIVE DECKLINK VIDEO • " + ex.Message); }
    }

    public void PushAudio(byte[] pcm, int count, int sampleRate, int channels = 2)
    {
        if (!Running || pcm == null || count <= 0) return;
        try
        {
            byte[] send = sampleRate == 48000 ? (count == pcm.Length ? pcm : pcm[..count]) : ResampleS16(pcm, count, sampleRate, 48000, channels);
            var h = GCHandle.Alloc(send, GCHandleType.Pinned);
            try
            {
                long start; lock (_sync) start = _audioSampleFrame;
                int rc = SPDeckLink_PushAudioS16(h.AddrOfPinnedObject(), send.Length, 48000, channels, start);
                if (rc != 0) SetStatus("ERROR • NATIVE DECKLINK AUDIO • " + ReadLastError());
                else lock (_sync) _audioSampleFrame += send.Length / Math.Max(1, channels * 2);
            }
            finally { h.Free(); }
        }
        catch (Exception ex) { SetStatus("ERROR • NATIVE DECKLINK AUDIO • " + ex.Message); }
    }

    static byte[] ResampleS16(byte[] src, int count, int inRate, int outRate, int channels)
    {
        if (inRate <= 0 || outRate <= 0 || channels <= 0) return Array.Empty<byte>();
        int inFrames = count / (channels * 2); if (inFrames <= 1) return src[..Math.Min(count, src.Length)];
        int outFrames = Math.Max(1, (int)Math.Round(inFrames * (double)outRate / inRate)); var dst = new byte[outFrames * channels * 2];
        for (int of = 0; of < outFrames; of++)
        {
            double pos = of * (double)inRate / outRate; int i0 = Math.Min(inFrames - 1, (int)pos), i1 = Math.Min(inFrames - 1, i0 + 1); double t = pos - i0;
            for (int ch = 0; ch < channels; ch++)
            {
                int a = (i0 * channels + ch) * 2, b = (i1 * channels + ch) * 2; short s0 = (short)(src[a] | (src[a + 1] << 8)), s1 = (short)(src[b] | (src[b + 1] << 8));
                short v = (short)Math.Clamp((int)Math.Round(s0 + (s1 - s0) * t), short.MinValue, short.MaxValue); int d = (of * channels + ch) * 2; dst[d] = (byte)v; dst[d + 1] = (byte)(v >> 8);
            }
        }
        return dst;
    }

    public void Stop(string message = "STOPPED BY OPERATOR")
    {
        bool wasRunning; lock (_sync) { wasRunning = _running; _running = false; _pendingVideo = null; ActiveDevice = null; ActiveConnection = null; ActiveMode = null; _targetWidth = _targetHeight = 0; }
        if (wasRunning && BridgePresent) { try { SPDeckLink_StopOutput(); } catch { } }
        SetStatus(message);
    }

    static string ReadHealth()
    {
        try
        {
            SPDeckLink_GetDiagnostics(out _, out _, out long vqd, out long aqd, out long bv, out long ba);
            SPDeckLink_GetOutputHealth(out long completed, out long late, out long dropped, out _, out _);
            return $"BUF V{bv}/A{ba} • DONE {completed} • LATE {late} • DROP {dropped + vqd} • AQDROP {aqd}";
        }
        catch { return ""; }
    }

    static string ReadLastError()
    {
        try
        {
            IntPtr p = Marshal.AllocHGlobal(8192);
            try { SPDeckLink_GetLastError(p, 4096); return Marshal.PtrToStringUni(p) ?? "Unknown DeckLink SDK error."; }
            finally { Marshal.FreeHGlobal(p); }
        }
        catch { return "DeckLink SDK bridge error details unavailable."; }
    }

    void SetStatus(string text) { Status = text; try { StatusChanged?.Invoke(text); } catch { } }
    public void Dispose() => Stop("STOPPED");
}
