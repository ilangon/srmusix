using SmartPlayout.Contracts;

namespace SmartPlayout.WorkerShared;

public sealed class AudioJitterQueue
{
    readonly Queue<SharedAudioPacket> _packets=new();
    readonly object _sync=new();
    readonly double _targetSeconds;
    readonly double _maximumSeconds;
    double _bufferedSeconds;
    bool _primed;

    public AudioJitterQueue(double targetMilliseconds=120,double maximumMilliseconds=200)
    {
        if(targetMilliseconds<=0||maximumMilliseconds<targetMilliseconds)throw new ArgumentOutOfRangeException(nameof(targetMilliseconds));
        _targetSeconds=targetMilliseconds/1000.0;
        _maximumSeconds=maximumMilliseconds/1000.0;
    }

    public double BufferedMilliseconds { get { lock(_sync)return _bufferedSeconds*1000; } }

    public long Enqueue(SharedAudioPacket packet)
    {
        int bytesPerSecond=checked(packet.SampleRate*packet.Channels*2);
        double duration=(double)packet.DataLength/bytesPerSecond;
        var owned=new SharedAudioPacket(packet.Sequence,packet.PtsTicks,packet.SampleRate,packet.Channels,packet.DataLength,packet.Pcm.AsSpan(0,packet.DataLength).ToArray());
        long droppedBytes=0;
        lock(_sync)
        {
            _packets.Enqueue(owned);_bufferedSeconds+=duration;
            while(_bufferedSeconds>_maximumSeconds&&_packets.Count>1)
            {
                var dropped=_packets.Dequeue();
                droppedBytes+=dropped.DataLength;
                _bufferedSeconds-=(double)dropped.DataLength/(dropped.SampleRate*dropped.Channels*2);
            }
            if(_bufferedSeconds>=_targetSeconds)_primed=true;
        }
        return droppedBytes;
    }

    public bool TryDequeue(out SharedAudioPacket? packet)
    {
        lock(_sync)
        {
            packet=null;
            if(!_primed||_packets.Count==0)return false;
            packet=_packets.Dequeue();
            _bufferedSeconds=Math.Max(0,_bufferedSeconds-(double)packet.DataLength/(packet.SampleRate*packet.Channels*2));
            if(_packets.Count==0){_bufferedSeconds=0;_primed=false;}
            return true;
        }
    }

    public void Reset(){lock(_sync){_packets.Clear();_bufferedSeconds=0;_primed=false;}}
}
