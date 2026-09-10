using System.IO.MemoryMappedFiles;

namespace SmartPlayout.Contracts;

public sealed record SharedVideoFrame(
    int Width,int Height,int Stride,int DataLength,long FrameNumber,long PtsTicks,byte[] Pixels);

/// <summary>
/// One-writer/many-reader newest-frame transport. Odd sequence values mean a
/// write is in progress; readers only accept two matching even sequence reads.
/// </summary>
public sealed class SharedVideoFrameBuffer : IDisposable
{
    const long HeaderBytes=64;
    const long DefaultCapacity=64L*1024*1024;
    const int Magic=0x53504642; // SPFB
    readonly MemoryMappedFile _map;
    readonly MemoryMappedViewAccessor _view;
    readonly long _pixelCapacity;
    long _sequence;

    SharedVideoFrameBuffer(MemoryMappedFile map,long capacity)
    {
        _map=map;
        _view=map.CreateViewAccessor(0,capacity,MemoryMappedFileAccess.ReadWrite);
        _pixelCapacity=capacity-HeaderBytes;
    }

    public static SharedVideoFrameBuffer CreateOrOpen(string name,long capacity=DefaultCapacity)
    {
        if(string.IsNullOrWhiteSpace(name))throw new ArgumentException("Shared frame name is required.",nameof(name));
        if(capacity<HeaderBytes+1024)throw new ArgumentOutOfRangeException(nameof(capacity));
        return new(MemoryMappedFile.CreateOrOpen(name,capacity,MemoryMappedFileAccess.ReadWrite),capacity);
    }

    public void Write(byte[] pixels,int width,int height,int stride,long frameNumber,long ptsTicks)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        int length=checked(stride*height);
        if(width<=0||height<=0||stride<width*4||length>pixels.Length)throw new ArgumentOutOfRangeException(nameof(pixels),"Invalid BGRA frame dimensions.");
        if(length>_pixelCapacity)throw new InvalidOperationException($"Frame requires {length} bytes; shared capacity is {_pixelCapacity} bytes.");
        long writing=Interlocked.Add(ref _sequence,2)-1;
        _view.Write(0,writing);
        _view.Write(8,Magic);
        _view.Write(12,1);
        _view.Write(16,width);
        _view.Write(20,height);
        _view.Write(24,stride);
        _view.Write(28,length);
        _view.Write(32,frameNumber);
        _view.Write(40,ptsTicks);
        _view.WriteArray(HeaderBytes,pixels,0,length);
        Thread.MemoryBarrier();
        _view.Write(0,writing+1);
    }

    public bool TryRead(ref byte[]? pixels,long lastFrameNumber,out SharedVideoFrame? frame)
    {
        frame=null;
        long before=_view.ReadInt64(0);
        if(before==0||(before&1)!=0||_view.ReadInt32(8)!=Magic)return false;
        int width=_view.ReadInt32(16),height=_view.ReadInt32(20),stride=_view.ReadInt32(24),length=_view.ReadInt32(28);
        long number=_view.ReadInt64(32),pts=_view.ReadInt64(40);
        if(number<=lastFrameNumber)return false;
        if(width<=0||height<=0||stride<width*4||length<=0||length>_pixelCapacity||length!=stride*height)return false;
        if(pixels is null||pixels.Length<length)pixels=new byte[length];
        _view.ReadArray(HeaderBytes,pixels,0,length);
        Thread.MemoryBarrier();
        long after=_view.ReadInt64(0);
        if(before!=after||(after&1)!=0)return false;
        frame=new(width,height,stride,length,number,pts,pixels);
        return true;
    }

    public void Dispose(){_view.Dispose();_map.Dispose();}
}
