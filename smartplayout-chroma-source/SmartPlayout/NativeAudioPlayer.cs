
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen.Abstractions;
using NAudio.Wave;
using NAudio.Dsp;

namespace FFmpegNativePlayer;

public unsafe sealed class NativeAudioPlayer : IDisposable
{
    // Per-instance software gain wrapper.
    // IMPORTANT: WaveOutEvent.Volume can map to the shared Windows waveOut device volume
    // on some drivers. Preview and On-Air therefore MUST NOT use WaveOutEvent.Volume.
    // This wrapper applies mute/volume to each player's PCM stream at Read() time.
    private sealed class InstanceGainWaveProvider : IWaveProvider
    {
        private readonly IWaveProvider _source;
        private readonly Func<float> _gainProvider;

        public InstanceGainWaveProvider(IWaveProvider source, Func<float> gainProvider)
        {
            _source = source;
            _gainProvider = gainProvider;
        }

        public WaveFormat WaveFormat => _source.WaveFormat;

        public int Read(byte[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            float gain = Math.Clamp(_gainProvider(), 0f, 1f);

            if (gain >= 0.9999f)
                return read;

            // NativeAudioPlayer outputs S16 PCM. Apply gain sample-by-sample.
            int end = offset + read - 1;
            if (gain <= 0.0001f)
            {
                Array.Clear(buffer, offset, read);
                return read;
            }

            for (int i = offset; i < end; i += 2)
            {
                short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
                int scaled = (int)Math.Round(sample * gain);
                scaled = Math.Clamp(scaled, short.MinValue, short.MaxValue);
                buffer[i] = (byte)(scaled & 0xFF);
                buffer[i + 1] = (byte)((scaled >> 8) & 0xFF);
            }
            return read;
        }
    }
    public event Action<string>? Diagnostic;
    public event Action? Ended;
    public event Action<float, float>? LevelChanged;
    public event Action<byte[], int>? ProcessedPcmReady;
    public event Action<double>? MediaTimestampReady;

    private AVFormatContext* _fmt;
    private AVCodecContext* _codec;
    private SwrContext* _swr;
    private int _streamIndex = -1;

    private BufferedWaveProvider? _buffer;
    private WaveOutEvent? _waveOut;

    private CancellationTokenSource? _cts;
    private Task? _decodeTask;

    private string? _source;
    private AVRational _timeBase;
    // Same zero-based container timeline used by video; important for MPEG-PS/TS/VOB/DAT.
    private double _timelineOriginSeconds;
    private int _outRate = 48000;
    public int OutputSampleRate => _outRate > 0 ? _outRate : 48000;
    private int _outChannels = 2;
    private AVSampleFormat _outFmt = AVSampleFormat.AV_SAMPLE_FMT_S16;

    private volatile bool _paused;
    private float _volume = 1.0f;
    private bool _muted;

    // MAIN OUTPUT AUDIO PROCESSING. Neutral defaults preserve the existing stable path.
    public float MasterGainDb { get; set; } = 0f;
    public float BassDb { get; set; } = 0f;
    public float TrebleDb { get; set; } = 0f;
    public float Balance { get; set; } = 0f; // -1 left .. +1 right
    public bool NormalizeEnabled { get; set; } = true;
    public bool LoudnessControlEnabled { get; set; } = false;
    public float[] EqualizerDb { get; set; } = new float[10];
    private readonly object _dspGate = new();
    private BiQuadFilter[]? _eqL, _eqR;
    private BiQuadFilter? _bassL, _bassR, _trebleL, _trebleR;
    private int _dspSignature;

    // FINAL STANDALONE AUTO AUDIO NORMALIZER.
    // Conservative real-time peak normalization: reduces hot/clipping files quickly,
    // raises genuinely quiet material slowly, and applies a hard safety ceiling.
    // No button is exposed in the standalone player.
    private float _autoNormalizeGain = 1.0f;
    private const float AutoNormalizeTargetPeak = 0.88f;
    private const float AutoNormalizeCeiling = 0.98f;
    private const float AutoNormalizeMinGain = 0.45f;
    private const float AutoNormalizeMaxGain = 1.75f;

    private readonly object _gate = new();
    // Seek generation prevents a packet decoded before a seek from being queued after it.
    private int _seekGeneration;
    private double _seekDiscardUntilSeconds = -1;

    public bool IsOpen => _fmt != null && _codec != null;
    public bool IsPaused => _paused;
    public TimeSpan Position { get; private set; }

    // Approximate sound-card playback clock: decoded audio PTS minus queued PCM.
    // This is used as the master clock for video sync.
    public double PlaybackPositionSeconds
    {
        get
        {
            double decoded = Position.TotalSeconds;
            double queued = _buffer?.BufferedDuration.TotalSeconds ?? 0;
            return Math.Max(0, decoded - queued);
        }
    }

    public Task OpenAndPlayAsync(string source, double seekSeconds = 0, bool startPaused = false, int preferredStreamIndex = -1, bool requireExactStream = false)
        => Task.Run(() => OpenAndPlay(source, seekSeconds, startPaused, preferredStreamIndex, requireExactStream));

    public int ActiveStreamIndex => _streamIndex;
    public double BufferedSeconds => _buffer?.BufferedDuration.TotalSeconds ?? 0;

    private void OpenAndPlay(string source, double seekSeconds, bool startPaused, int preferredStreamIndex, bool requireExactStream)
    {
        Stop();
        _autoNormalizeGain = 1.0f;
        _source = source;

        AVFormatContext* fmt = null;
        int r = ffmpeg.avformat_open_input(&fmt, source, null, null);
        ThrowIfError(r, "AUDIO open input");
        _fmt = fmt;

        ThrowIfError(ffmpeg.avformat_find_stream_info(_fmt, null), "AUDIO find stream info");
        _timelineOriginSeconds = ComputeCommonTimelineOriginSeconds(_fmt);
        Diagnostic?.Invoke($"AUDIO timeline origin ✓ {_timelineOriginSeconds:0.###} sec");

        AVCodec* dec = null;
        int streamArrayIndex = -1;
        int packetStreamIndex = -1;

        // Track selection uses AVStream.index (the same value carried by AVPacket.stream_index),
        // not an assumed array position. This matters for files whose stream numbering is not a
        // simple 0..N layout. When the user explicitly selects a track we NEVER silently fall
        // back to the default track; failure is surfaced instead of pretending the switch worked.
        if (preferredStreamIndex >= 0)
        {
            for (int i = 0; i < (int)_fmt->nb_streams; i++)
            {
                AVStream* candidate = _fmt->streams[i];
                if (candidate == null || candidate->codecpar == null) continue;
                if (candidate->index != preferredStreamIndex) continue;
                if (candidate->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_AUDIO) break;

                dec = ffmpeg.avcodec_find_decoder(candidate->codecpar->codec_id);
                if (dec != null)
                {
                    streamArrayIndex = i;
                    packetStreamIndex = candidate->index;
                }
                break;
            }

            if (requireExactStream && (streamArrayIndex < 0 || dec == null))
                throw new InvalidOperationException($"Requested audio stream {preferredStreamIndex} could not be opened exactly.");
        }

        if (streamArrayIndex < 0)
        {
            int best = ffmpeg.av_find_best_stream(_fmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, &dec, 0);
            if (best < 0 || dec == null)
                throw new InvalidOperationException("No audio stream/decoder found.");
            streamArrayIndex = best;
            AVStream* bestStream = _fmt->streams[streamArrayIndex];
            packetStreamIndex = bestStream != null ? bestStream->index : best;
        }

        AVStream* st = _fmt->streams[streamArrayIndex];
        _streamIndex = packetStreamIndex;
        _timeBase = st->time_base;

        _codec = ffmpeg.avcodec_alloc_context3(dec);
        if (_codec == null) throw new InvalidOperationException("avcodec_alloc_context3(audio) failed.");
        ThrowIfError(ffmpeg.avcodec_parameters_to_context(_codec, st->codecpar), "AUDIO parameters_to_context");
        ThrowIfError(ffmpeg.avcodec_open2(_codec, dec, null), "AUDIO decoder open");

        Diagnostic?.Invoke($"AUDIO stream ✓ index={_streamIndex} codec_id={_codec->codec_id} rate={_codec->sample_rate}");

        _outRate = _codec->sample_rate > 0 ? _codec->sample_rate : 48000;

        // Configure stereo signed 16-bit PCM output.
        AVChannelLayout outLayout = default;
        ffmpeg.av_channel_layout_default(&outLayout, _outChannels);

        _swr = ffmpeg.swr_alloc();
        if (_swr == null) throw new InvalidOperationException("swr_alloc failed.");

        ThrowIfError(ffmpeg.av_opt_set_chlayout(_swr, "in_chlayout", &_codec->ch_layout, 0), "swr in layout");
        ThrowIfError(ffmpeg.av_opt_set_int(_swr, "in_sample_rate", _codec->sample_rate, 0), "swr in rate");
        ThrowIfError(ffmpeg.av_opt_set_sample_fmt(_swr, "in_sample_fmt", _codec->sample_fmt, 0), "swr in fmt");

        ThrowIfError(ffmpeg.av_opt_set_chlayout(_swr, "out_chlayout", &outLayout, 0), "swr out layout");
        ThrowIfError(ffmpeg.av_opt_set_int(_swr, "out_sample_rate", _outRate, 0), "swr out rate");
        ThrowIfError(ffmpeg.av_opt_set_sample_fmt(_swr, "out_sample_fmt", _outFmt, 0), "swr out fmt");
        ThrowIfError(ffmpeg.swr_init(_swr), "swr_init");
        ffmpeg.av_channel_layout_uninit(&outLayout);

        _buffer = new BufferedWaveProvider(new WaveFormat(_outRate, 16, _outChannels))
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = false,
            ReadFully = true
        };
        _waveOut = new WaveOutEvent { DesiredLatency = 120 };
        _waveOut.Init(new InstanceGainWaveProvider(_buffer, () => _muted ? 0f : _volume));
        // Keep the Windows waveOut device at unity. Never use WaveOutEvent.Volume for
        // Preview/On-Air control because some audio drivers expose it as shared device gain.
        _waveOut.Volume = 1.0f;

        if (seekSeconds > 0)
        {
            double absoluteSeekSeconds = seekSeconds + _timelineOriginSeconds;
            long ts = (long)(absoluteSeekSeconds / ffmpeg.av_q2d(_timeBase));
            ThrowIfError(ffmpeg.av_seek_frame(_fmt, _streamIndex, ts, ffmpeg.AVSEEK_FLAG_BACKWARD), "AUDIO initial seek");
            ffmpeg.avcodec_flush_buffers(_codec);
            ffmpeg.swr_close(_swr);
            ThrowIfError(ffmpeg.swr_init(_swr), "AUDIO resampler reset after initial seek");
            _seekDiscardUntilSeconds = seekSeconds;
            Position = TimeSpan.FromSeconds(seekSeconds);
        }

        _paused = startPaused;
        _cts = new CancellationTokenSource();
        _decodeTask = Task.Run(() => DecodeLoop(_cts.Token));
        if (startPaused)
        {
            _waveOut.Pause();
            Diagnostic?.Invoke("AUDIO prepared ✓ (held for video first-frame lock)");
        }
        else
        {
            _waveOut.Play();
            Diagnostic?.Invoke("AUDIO playback started ✓");
        }
    }

    private static double ComputeCommonTimelineOriginSeconds(AVFormatContext* format)
    {
        if (format != null && format->start_time != ffmpeg.AV_NOPTS_VALUE)
            return format->start_time / (double)ffmpeg.AV_TIME_BASE;

        double earliest = double.PositiveInfinity;
        if (format != null)
        {
            for (int i = 0; i < (int)format->nb_streams; i++)
            {
                AVStream* st = format->streams[i];
                if (st == null || st->start_time == ffmpeg.AV_NOPTS_VALUE) continue;
                double value = st->start_time * ffmpeg.av_q2d(st->time_base);
                if (double.IsFinite(value) && value < earliest) earliest = value;
            }
        }
        return double.IsFinite(earliest) ? earliest : 0.0;
    }

    private void DecodeLoop(CancellationToken ct)
    {
        AVPacket* pkt = ffmpeg.av_packet_alloc();
        AVFrame* frame = ffmpeg.av_frame_alloc();
        double lastAudioPts = double.NaN;
        double timestampContinuityOffset = 0.0;
        int continuityGeneration = Volatile.Read(ref _seekGeneration);
        if (pkt == null || frame == null) return;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_paused) { Thread.Sleep(10); continue; }

                // IMPORTANT:
                // FFmpeg decodes much faster than real-time. Without backpressure
                // BufferedWaveProvider fills in a moment, later audio gets dropped,
                // the demuxer reaches EOF, and only the first few seconds are heard.
                // Keep a compact ~1 second queue. This lowers memory/latency while retaining backpressure.
                while (!ct.IsCancellationRequested && !_paused &&
                       _buffer != null && _buffer.BufferedDuration > TimeSpan.FromSeconds(1.0))
                {
                    Thread.Sleep(10);
                }

                if (ct.IsCancellationRequested) break;
                if (_paused) continue;

                int rr;
                int packetSeekGeneration;
                lock (_gate)
                {
                    packetSeekGeneration = _seekGeneration;
                    rr = ffmpeg.av_read_frame(_fmt, pkt);
                }
                if (rr < 0)
                {
                    Diagnostic?.Invoke("AUDIO demux reached end of file");
                    while (!ct.IsCancellationRequested &&
                           _buffer != null &&
                           _buffer.BufferedBytes > 0)
                    {
                        Thread.Sleep(20);
                    }
                    if (!ct.IsCancellationRequested)
                    {
                        Diagnostic?.Invoke("AUDIO playback end ✓");
                        Ended?.Invoke();
                    }
                    break;
                }

                if (pkt->stream_index == _streamIndex)
                {
                    int sr = ffmpeg.avcodec_send_packet(_codec, pkt);
                    if (sr >= 0)
                    {
                        while (!ct.IsCancellationRequested)
                        {
                            int fr = ffmpeg.avcodec_receive_frame(_codec, frame);
                            if (fr == ffmpeg.AVERROR(ffmpeg.EAGAIN) || fr == ffmpeg.AVERROR_EOF) break;
                            if (fr < 0) break;

                            long pts = frame->best_effort_timestamp;
                            int currentGeneration = Volatile.Read(ref _seekGeneration);
                            if (currentGeneration != continuityGeneration)
                            {
                                continuityGeneration = currentGeneration;
                                lastAudioPts = double.NaN;
                                timestampContinuityOffset = 0.0;
                            }

                            double frameDuration = (frame->nb_samples > 0 && _outRate > 0)
                                ? frame->nb_samples / (double)_outRate : 0.020;
                            bool hasTimestamp = pts != ffmpeg.AV_NOPTS_VALUE;
                            double rawPtsSeconds = hasTimestamp
                                ? pts * ffmpeg.av_q2d(_timeBase) - _timelineOriginSeconds
                                : (double.IsFinite(lastAudioPts) ? lastAudioPts + frameDuration : Position.TotalSeconds);
                            double ptsSeconds = rawPtsSeconds + timestampContinuityOffset;

                            // If Seek() occurred after this packet was read, this frame belongs to
                            // the old timeline and must never reach the sound-card buffer.
                            if (packetSeekGeneration != Volatile.Read(ref _seekGeneration))
                            {
                                ffmpeg.av_frame_unref(frame);
                                break;
                            }

                            // BACKWARD seek may decode audio preroll before the exact target.
                            // Decode it for codec state, but keep it out of the PCM queue.
                            double seekFloor = _seekDiscardUntilSeconds;
                            if (seekFloor >= 0 && ptsSeconds + 0.002 < seekFloor)
                            {
                                ffmpeg.av_frame_unref(frame);
                                continue;
                            }

                            if (seekFloor >= 0)
                            {
                                _seekDiscardUntilSeconds = -1;
                                lastAudioPts = double.NaN;
                                timestampContinuityOffset = 0.0;
                                Diagnostic?.Invoke($"AUDIO seek lock ✓ audio={ptsSeconds:0.###}s target={seekFloor:0.###}s");
                            }

                            if (double.IsFinite(lastAudioPts))
                            {
                                double expected = lastAudioPts + frameDuration;
                                double jump = ptsSeconds - expected;
                                if (jump < -0.350 || jump > 2.000)
                                {
                                    timestampContinuityOffset += expected - ptsSeconds;
                                    ptsSeconds = expected;
                                    Diagnostic?.Invoke($"AUDIO TIMESTAMP DISCONTINUITY repaired ✓ jump={jump:0.###}s");
                                }
                            }
                            if (!hasTimestamp && double.IsFinite(lastAudioPts))
                                Diagnostic?.Invoke("AUDIO PTS MISSING — monotonic fallback used");

                            // Publish the normalized media timestamp before PCM delivery. Video and
                            // audio both use the same zero-based container timeline; callers may use
                            // this value as the authoritative media clock without subtracting device
                            // buffering (which can jump independently of media time).
                            MediaTimestampReady?.Invoke(Math.Max(0, ptsSeconds));
                            WriteFrame(frame);
                            lastAudioPts = ptsSeconds;
                            Position = TimeSpan.FromSeconds(Math.Max(0, ptsSeconds));
                        }
                    }
                }
                ffmpeg.av_packet_unref(pkt);
            }
        }
        finally
        {
            ffmpeg.av_packet_free(&pkt);
            ffmpeg.av_frame_free(&frame);
        }
    }

    private void WriteFrame(AVFrame* frame)
    {
        int maxOut = (int)ffmpeg.av_rescale_rnd(
            ffmpeg.swr_get_delay(_swr, _codec->sample_rate) + frame->nb_samples,
            _outRate,
            _codec->sample_rate,
            AVRounding.AV_ROUND_UP);

        int bps = ffmpeg.av_get_bytes_per_sample(_outFmt);
        int needed = maxOut * _outChannels * bps;
        if (needed <= 0) return;

        byte[] managed = new byte[needed];
        fixed (byte* dst = managed)
        {
            byte** outData = stackalloc byte*[1];
            outData[0] = dst;
            int converted = ffmpeg.swr_convert(_swr, outData, maxOut, frame->extended_data, frame->nb_samples);
            if (converted <= 0) return;

            int bytes = converted * _outChannels * bps;

            // AUTOMATIC AUDIO QUALITY / NORMALIZATION
            // Measure this decoded PCM block first. Gain reduction reacts quickly to prevent
            // clipping; gain increase is intentionally slow to avoid audible pumping.
            if (bps == 2 && bytes >= 2)
            {
                int sampleCount = bytes / 2;
                int blockPeak = 0;
                for (int i = 0; i < sampleCount; i++)
                {
                    int o = i * 2;
                    short v = (short)(managed[o] | (managed[o + 1] << 8));
                    int a = v == short.MinValue ? 32768 : Math.Abs((int)v);
                    if (a > blockPeak) blockPeak = a;
                }

                if (NormalizeEnabled && blockPeak > 256) // ignore near-silence when deciding gain
                {
                    float peak01 = blockPeak / 32768f;
                    float targetPeak = LoudnessControlEnabled ? 0.78f : AutoNormalizeTargetPeak;
                    float wanted = Math.Clamp(
                        targetPeak / Math.Max(0.001f, peak01),
                        AutoNormalizeMinGain,
                        AutoNormalizeMaxGain);

                    // Fast attack when turning down; slow release/boost when turning up.
                    float coefficient = wanted < _autoNormalizeGain ? 0.35f : 0.015f;
                    _autoNormalizeGain += (wanted - _autoNormalizeGain) * coefficient;
                }
                else if (!NormalizeEnabled) _autoNormalizeGain = 1.0f;

                float ceiling = AutoNormalizeCeiling * 32767f;
                for (int i = 0; i < sampleCount; i++)
                {
                    int o = i * 2;
                    short v = (short)(managed[o] | (managed[o + 1] << 8));
                    float scaled = v * _autoNormalizeGain;
                    if (scaled > ceiling) scaled = ceiling;
                    else if (scaled < -ceiling) scaled = -ceiling;
                    short nv = (short)Math.Round(scaled);
                    managed[o] = (byte)(nv & 0xFF);
                    managed[o + 1] = (byte)((nv >> 8) & 0xFF);
                }
            }

            // Master gain, balance, bass/treble and 10-band broadcast EQ.
            if (bps == 2 && bytes >= 4) ApplyMasterAudioProcessing(managed, bytes);

            // Measure the processed S16 PCM for the lightweight stereo VU meter.
            // No extra decoder/filter is created, so this does not alter playback or A/V sync.
            if (bps == 2 && bytes >= 2)
            {
                int samples = bytes / 2;
                int leftPeak = 0, rightPeak = 0;
                for (int i = 0; i < samples; i += Math.Max(1, _outChannels))
                {
                    int o = i * 2;
                    short l = (short)(managed[o] | (managed[o + 1] << 8));
                    int la = l == short.MinValue ? 32768 : Math.Abs((int)l);
                    if (la > leftPeak) leftPeak = la;
                    if (_outChannels > 1 && i + 1 < samples)
                    {
                        int ro = (i + 1) * 2;
                        short r = (short)(managed[ro] | (managed[ro + 1] << 8));
                        int ra = r == short.MinValue ? 32768 : Math.Abs((int)r);
                        if (ra > rightPeak) rightPeak = ra;
                    }
                    else rightPeak = leftPeak;
                }
                LevelChanged?.Invoke(leftPeak / 32768f, rightPeak / 32768f);
            }

            ProcessedPcmReady?.Invoke(managed, bytes);

            if (_buffer != null)
            {
                // Normally the decode-loop backpressure keeps enough room.
                // For unusually large audio frames, wait briefly for WaveOut.
                while (_buffer.BufferLength - _buffer.BufferedBytes < bytes &&
                       _cts != null && !_cts.IsCancellationRequested)
                {
                    Thread.Sleep(5);
                }

                if (_cts == null || !_cts.IsCancellationRequested)
                    _buffer.AddSamples(managed, 0, bytes);
            }
        }
    }

    private void ApplyMasterAudioProcessing(byte[] data, int bytes)
    {
        float[] eq = EqualizerDb ?? Array.Empty<float>();
        int sig = HashCode.Combine(_outRate, MathF.Round(BassDb,2), MathF.Round(TrebleDb,2), eq.Length);
        for(int i=0;i<Math.Min(10,eq.Length);i++) sig=HashCode.Combine(sig,MathF.Round(eq[i],2));
        if (_eqL == null || sig != _dspSignature)
        {
            lock (_dspGate)
            {
                if (_eqL == null || sig != _dspSignature)
                {
                    float[] hz = { 31f, 62f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f };
                    _eqL = new BiQuadFilter[10]; _eqR = new BiQuadFilter[10];
                    for(int i=0;i<10;i++)
                    {
                        float gain = i < eq.Length ? Math.Clamp(eq[i], -12f, 12f) : 0f;
                        _eqL[i]=BiQuadFilter.PeakingEQ(_outRate,hz[i],1.0f,gain);
                        _eqR[i]=BiQuadFilter.PeakingEQ(_outRate,hz[i],1.0f,gain);
                    }
                    _bassL=BiQuadFilter.LowShelf(_outRate,120f,1.0f,Math.Clamp(BassDb,-12f,12f));
                    _bassR=BiQuadFilter.LowShelf(_outRate,120f,1.0f,Math.Clamp(BassDb,-12f,12f));
                    _trebleL=BiQuadFilter.HighShelf(_outRate,8000f,1.0f,Math.Clamp(TrebleDb,-12f,12f));
                    _trebleR=BiQuadFilter.HighShelf(_outRate,8000f,1.0f,Math.Clamp(TrebleDb,-12f,12f));
                    _dspSignature=sig;
                }
            }
        }
        float gainLin=MathF.Pow(10f,Math.Clamp(MasterGainDb,-24f,12f)/20f);
        float bal=Math.Clamp(Balance,-1f,1f);
        float lg=gainLin*(bal>0?1f-bal:1f), rg=gainLin*(bal<0?1f+bal:1f);
        for(int o=0;o+3<bytes;o+=4)
        {
            short ls=(short)(data[o]|(data[o+1]<<8)); short rs=(short)(data[o+2]|(data[o+3]<<8));
            float l=ls/32768f, r=rs/32768f;
            if(_bassL!=null) l=_bassL.Transform(l); if(_bassR!=null) r=_bassR.Transform(r);
            if(_eqL!=null && _eqR!=null) for(int i=0;i<10;i++){l=_eqL[i].Transform(l);r=_eqR[i].Transform(r);}
            if(_trebleL!=null) l=_trebleL.Transform(l); if(_trebleR!=null) r=_trebleR.Transform(r);
            l=Math.Clamp(l*lg,-0.98f,0.98f); r=Math.Clamp(r*rg,-0.98f,0.98f);
            short lo=(short)Math.Round(l*32767f), ro=(short)Math.Round(r*32767f);
            data[o]=(byte)(lo&0xff);data[o+1]=(byte)((lo>>8)&0xff);data[o+2]=(byte)(ro&0xff);data[o+3]=(byte)((ro>>8)&0xff);
        }
    }

    public void Pause()
    {
        _paused = true;
        _waveOut?.Pause();
        Diagnostic?.Invoke("AUDIO pause");
    }

    public void Resume()
    {
        _paused = false;
        _waveOut?.Play();
        Diagnostic?.Invoke("AUDIO resume");
    }

    public void Seek(double seconds)
    {
        if (_fmt == null || _codec == null || _streamIndex < 0) return;
        double target = Math.Max(0, seconds);
        lock (_gate)
        {
            _waveOut?.Pause();

            // Invalidate any packet that was read on the old timeline before this lock.
            Interlocked.Increment(ref _seekGeneration);

            // Remove already queued PCM and reset both decoder and resampler state.
            _buffer?.ClearBuffer();
            double absoluteTarget = target + _timelineOriginSeconds;
            long ts = (long)(absoluteTarget / ffmpeg.av_q2d(_timeBase));
            int seekResult = ffmpeg.av_seek_frame(_fmt, _streamIndex, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);
            if (seekResult < 0)
            {
                Diagnostic?.Invoke($"AUDIO seek failed: {seekResult}");
                if (!_paused) _waveOut?.Play();
                return;
            }

            ffmpeg.avcodec_flush_buffers(_codec);
            if (_swr != null)
            {
                ffmpeg.swr_close(_swr);
                int swrResult = ffmpeg.swr_init(_swr);
                if (swrResult < 0)
                    Diagnostic?.Invoke($"AUDIO resampler reset failed: {swrResult}");
            }

            _seekDiscardUntilSeconds = target;
            Position = TimeSpan.FromSeconds(target);
            // Reacquire level gently after a transport jump so a hot/quiet section
            // does not inherit stale gain from the previous timeline position.
            _autoNormalizeGain = Math.Clamp(_autoNormalizeGain, 0.75f, 1.25f);
            if (!_paused) _waveOut?.Play();
        }
        Diagnostic?.Invoke($"AUDIO seek request ✓ {target:0.000}s — buffer/decoder/resampler flushed");
    }

    public void SetVolume(float volume01)
    {
        // Per-player software gain only. Do not touch WaveOutEvent.Volume.
        _volume = Math.Clamp(volume01, 0f, 1f);
    }

    public void SetMuted(bool muted)
    {
        // Per-player software mute only. Do not touch WaveOutEvent.Volume.
        _muted = muted;
        Diagnostic?.Invoke(muted ? "AUDIO mute (instance only)" : "AUDIO unmute (instance only)");
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try
        {
            if (_decodeTask != null && !_decodeTask.IsCompleted)
                _decodeTask.Wait(1500);
        }
        catch { }
        try { _waveOut?.Stop(); } catch { }

        _waveOut?.Dispose();
        _waveOut = null;
        _buffer = null;

        if (_swr != null)
        {
            SwrContext* swr = _swr;
            ffmpeg.swr_free(&swr);
            _swr = null;
        }

        if (_codec != null)
        {
            AVCodecContext* codec = _codec;
            ffmpeg.avcodec_free_context(&codec);
            _codec = null;
        }

        if (_fmt != null)
        {
            AVFormatContext* fmt = _fmt;
            ffmpeg.avformat_close_input(&fmt);
            _fmt = null;
        }

        _cts?.Dispose();
        _cts = null;
        _decodeTask = null;
        _streamIndex = -1;
        Position = TimeSpan.Zero;
        _seekDiscardUntilSeconds = -1;
        _autoNormalizeGain = 1.0f;
        Interlocked.Increment(ref _seekGeneration);
        _paused = false;
    }

    private static void ThrowIfError(int code, string stage)
    {
        if (code >= 0) return;
        byte* buf = stackalloc byte[1024];
        ffmpeg.av_strerror(code, buf, 1024);
        string msg = Marshal.PtrToStringAnsi((IntPtr)buf) ?? $"FFmpeg error {code}";
        throw new InvalidOperationException($"{stage}: {msg} ({code})");
    }

    public void Dispose() => Stop();
}
