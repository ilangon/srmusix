using System.IO.MemoryMappedFiles;

namespace SmartPlayout.Contracts;

public sealed record SharedAudioPacket(long Sequence,long PtsTicks,int SampleRate,int Channels,int DataLength,byte[] Pcm);

/// <summary>
/// Single-writer/single-reader PCM ring. 256 x 32 KiB slots provide bounded
/// buffering without allocations or back-pressure on the Program audio callback.
/// </summary>
public sealed class SharedAudioRingBuffer : IDisposable
{
    const int Magic=0x53504142; // SPAB
    const int Slots=256;
    const int PayloadBytes=32768;
    const long HeaderBytes=64;
    const long SlotHeaderBytes=40;
    const long SlotBytes=SlotHeaderBytes+PayloadBytes;
    const long Capacity=HeaderBytes+Slots*SlotBytes;
    readonly MemoryMappedFile _map;
    readonly MemoryMappedViewAccessor _view;
    long _writerSequence;

    SharedAudioRingBuffer(MemoryMappedFile map)
    {
        _map=map;_view=map.CreateViewAccessor(0,Capacity,MemoryMappedFileAccess.ReadWrite);
        _view.Write(8,Magic);_view.Write(12,1);_view.Write(16,Slots);_view.Write(20,PayloadBytes);
    }

    public static SharedAudioRingBuffer CreateOrOpen(string name)=>
        new(MemoryMappedFile.CreateOrOpen(name,Capacity,MemoryMappedFileAccess.ReadWrite));

    public long Write(byte[] pcm,int sourceOffset,int count,int sampleRate,int channels,long ptsTicks)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        if(sourceOffset<0||count<=0||sourceOffset+count>pcm.Length||count>PayloadBytes)throw new ArgumentOutOfRangeException(nameof(count));
        if(sampleRate<=0||channels<=0)throw new ArgumentOutOfRangeException(nameof(sampleRate));
        long sequence=Interlocked.Increment(ref _writerSequence);
        long slotOffset=HeaderBytes+(sequence%Slots)*SlotBytes;
        long writing=sequence*2+1;
        _view.Write(slotOffset,writing);
        _view.Write(slotOffset+8,sequence);
        _view.Write(slotOffset+16,ptsTicks);
        _view.Write(slotOffset+24,sampleRate);
        _view.Write(slotOffset+28,channels);
        _view.Write(slotOffset+32,count);
        _view.WriteArray(slotOffset+SlotHeaderBytes,pcm,sourceOffset,count);
        Thread.MemoryBarrier();
        _view.Write(slotOffset,writing+1);
        _view.Write(0,sequence);
        return sequence;
    }

    public bool TryReadNext(ref byte[]? pcm,ref long lastSequence,out SharedAudioPacket? packet,out long dropped)
    {
        packet=null;dropped=0;
        if(_view.ReadInt32(8)!=Magic)return false;
        long newest=_view.ReadInt64(0);
        if(newest<=lastSequence)return false;
        long wanted=lastSequence+1;
        long oldest=Math.Max(1,newest-Slots+1);
        if(wanted<oldest){dropped=oldest-wanted;wanted=oldest;}
        long offset=HeaderBytes+(wanted%Slots)*SlotBytes;
        long before=_view.ReadInt64(offset);
        if((before&1)!=0)return false;
        long sequence=_view.ReadInt64(offset+8),pts=_view.ReadInt64(offset+16);
        int sampleRate=_view.ReadInt32(offset+24),channels=_view.ReadInt32(offset+28),count=_view.ReadInt32(offset+32);
        if(sequence!=wanted||count<=0||count>PayloadBytes||sampleRate<=0||channels<=0)return false;
        if(pcm is null||pcm.Length<count)pcm=new byte[count];
        _view.ReadArray(offset+SlotHeaderBytes,pcm,0,count);
        Thread.MemoryBarrier();
        if(_view.ReadInt64(offset)!=before)return false;
        lastSequence=sequence;
        packet=new(sequence,pts,sampleRate,channels,count,pcm);
        return true;
    }

    public void Dispose(){_view.Dispose();_map.Dispose();}
}
