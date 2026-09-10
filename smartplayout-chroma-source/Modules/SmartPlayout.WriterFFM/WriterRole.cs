namespace SmartPlayout.WriterFFM;

public enum WriterTransport { Rtmp, DvbUdp, DvbSrt, File }

public sealed record WriterRole(
    WriterTransport Transport,
    int Width,
    int Height,
    double FramesPerSecond,
    int SampleRate);
