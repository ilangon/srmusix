#ifndef SMARTPLAYOUT_DECKLINK_NATIVE_EXPORTS
#define SMARTPLAYOUT_DECKLINK_NATIVE_EXPORTS
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include "SMARTPlayout.DeckLinkNative.h"
#include <windows.h>
#include <oleauto.h>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <cstring>
#include <deque>
#include <mutex>
#include <string>
#include <thread>
#include <vector>
#include <algorithm>
#include <cmath>
#include "DeckLinkAPI_h.h"

namespace {
struct VideoPacket { std::vector<unsigned char> bytes; int stride=0; long long frameNumber=0; };
struct AudioPacket { std::vector<unsigned char> bytes; int channels=2; long long startSampleFrame=0; };

std::mutex g_mutex;
std::condition_variable g_cv;
std::deque<VideoPacket> g_videoQueue;
std::deque<AudioPacket> g_audioQueue;
std::thread g_worker;
std::atomic<bool> g_stop{false}, g_running{false}, g_ready{false};
std::wstring g_lastError;
int g_outputOrdinal=0, g_width=0, g_height=0, g_channels=2;
BMDVideoConnection g_connection=bmdVideoConnectionUnspecified;
BMDDisplayMode g_displayMode=bmdModeUnknown;
std::atomic<long long> g_videoScheduled{0}, g_audioScheduled{0}, g_videoQueueDrops{0}, g_audioQueueDrops{0};
std::atomic<long long> g_bufferedVideo{0}, g_bufferedAudio{0};
std::atomic<long long> g_completed{0}, g_late{0}, g_hwDropped{0}, g_flushed{0};
std::atomic<long long> g_lastStreamTime{0};

void SetError(const std::wstring& text) { std::lock_guard<std::mutex> lock(g_mutex); g_lastError=text; }
std::wstring HrText(const wchar_t* where, HRESULT hr) { wchar_t b[256]; swprintf_s(b,L"%s failed (HRESULT 0x%08X)",where,(unsigned)hr); return b; }
unsigned char Clamp8(int v){ return (unsigned char)(v<0?0:(v>255?255:v)); }

void BGRAtoUYVY(const unsigned char* src, int srcStride, unsigned char* dst, int dstStride, int width, int height)
{
    for(int y=0;y<height;y++) {
        const unsigned char* s=src+y*srcStride;
        unsigned char* d=dst+y*dstStride;
        for(int x=0;x<width;x+=2) {
            int x1=std::min(x+1,width-1);
            int b0=s[x*4+0], g0=s[x*4+1], r0=s[x*4+2];
            int b1=s[x1*4+0],g1=s[x1*4+1],r1=s[x1*4+2];
            // Studio-range broadcast YUV. SD PAL/NTSC uses BT.601; HD/UHD uses BT.709.
            // Using BT.709 for 576-line SD was one cause of visibly wrong/flat DeckLink colour.
            bool sd601 = height <= 576;
            int y0 = sd601 ? (((66*r0+129*g0+25*b0+128)>>8)+16) : (((47*r0+157*g0+16*b0+128)>>8)+16);
            int y1 = sd601 ? (((66*r1+129*g1+25*b1+128)>>8)+16) : (((47*r1+157*g1+16*b1+128)>>8)+16);
            int u0 = sd601 ? (((-38*r0-74*g0+112*b0+128)>>8)+128) : (((-26*r0-87*g0+112*b0+128)>>8)+128);
            int v0 = sd601 ? (((112*r0-94*g0-18*b0+128)>>8)+128) : (((112*r0-102*g0-10*b0+128)>>8)+128);
            int u1 = sd601 ? (((-38*r1-74*g1+112*b1+128)>>8)+128) : (((-26*r1-87*g1+112*b1+128)>>8)+128);
            int v1 = sd601 ? (((112*r1-94*g1-18*b1+128)>>8)+128) : (((112*r1-102*g1-10*b1+128)>>8)+128);
            d[x*2+0]=Clamp8((u0+u1)/2); d[x*2+1]=Clamp8(y0);
            d[x*2+2]=Clamp8((v0+v1)/2); d[x*2+3]=Clamp8(y1);
        }
    }
}

class OutputCallback final : public IDeckLinkVideoOutputCallback
{
    std::atomic<ULONG> refs{1};
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, LPVOID* ppv) override {
        if(!ppv) return E_POINTER;
        if(iid==IID_IUnknown || iid==IID_IDeckLinkVideoOutputCallback) { *ppv=this; AddRef(); return S_OK; }
        *ppv=nullptr; return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs; }
    ULONG STDMETHODCALLTYPE Release() override { ULONG r=--refs; if(r==0) delete this; return r; }
    HRESULT STDMETHODCALLTYPE ScheduledFrameCompleted(IDeckLinkVideoFrame*, BMDOutputFrameCompletionResult result) override {
        switch(result) {
            case bmdOutputFrameCompleted: g_completed++; break;
            case bmdOutputFrameDisplayedLate: g_late++; break;
            case bmdOutputFrameDropped: g_hwDropped++; break;
            case bmdOutputFrameFlushed: g_flushed++; break;
            default: break;
        }
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE ScheduledPlaybackHasStopped() override { return S_OK; }
};

IDeckLink* GetOutputDeviceByOrdinal(IDeckLinkIterator* iterator, int wantedOrdinal)
{
    IDeckLink* device=nullptr; int outputOrdinal=0;
    while(iterator->Next(&device)==S_OK && device) {
        IDeckLinkOutput* output=nullptr;
        bool hasOutput=(device->QueryInterface(IID_IDeckLinkOutput,(void**)&output)==S_OK && output);
        if(output) output->Release();
        if(hasOutput) {
            if(outputOrdinal==wantedOrdinal) return device;
            outputOrdinal++;
        }
        device->Release(); device=nullptr;
    }
    return nullptr;
}

IDeckLinkDisplayMode* FindMode(IDeckLinkOutput* output)
{
    IDeckLinkDisplayModeIterator* it=nullptr;
    if(output->GetDisplayModeIterator(&it)!=S_OK || !it) return nullptr;
    IDeckLinkDisplayMode* mode=nullptr;
    while(it->Next(&mode)==S_OK && mode) {
        if(mode->GetDisplayMode()==g_displayMode) { it->Release(); return mode; }
        mode->Release(); mode=nullptr;
    }
    it->Release(); return nullptr;
}

const wchar_t* ConnectionName(BMDVideoConnection c)
{
    switch(c) {
        case bmdVideoConnectionSDI: return L"SDI";
        case bmdVideoConnectionHDMI: return L"HDMI";
        case bmdVideoConnectionOpticalSDI: return L"Optical SDI";
        case bmdVideoConnectionComponent: return L"Component / YUV";
        case bmdVideoConnectionComposite: return L"Composite";
        case bmdVideoConnectionSVideo: return L"S-Video";
        case bmdVideoConnectionEthernet: return L"Ethernet";
        case bmdVideoConnectionOpticalEthernet: return L"Optical Ethernet";
        case bmdVideoConnectionInternal: return L"Internal";
        default: return L"Unspecified / Auto";
    }
}

IDeckLink* OpenOutputDevice(int ordinal)
{
    IDeckLinkIterator* iterator=nullptr;
    HRESULT hr=CoCreateInstance(CLSID_CDeckLinkIterator,nullptr,CLSCTX_ALL,IID_IDeckLinkIterator,(void**)&iterator);
    if(FAILED(hr)||!iterator) return nullptr;
    IDeckLink* result=GetOutputDeviceByOrdinal(iterator,ordinal);
    iterator->Release();
    return result;
}

bool CopyUYVYIntoMutableFrame(IDeckLinkMutableVideoFrame* frame, const std::vector<unsigned char>& uyvy, int rowBytes)
{
    if(uyvy.size() < (size_t)rowBytes * (size_t)g_height) return false;
    IDeckLinkVideoBuffer* buffer=nullptr;
    HRESULT hr=frame->QueryInterface(IID_IDeckLinkVideoBuffer,(void**)&buffer);
    if(FAILED(hr)||!buffer) { SetError(HrText(L"IDeckLinkVideoBuffer",hr)); return false; }
    hr=buffer->StartAccess(bmdBufferAccessReadAndWrite);
    if(FAILED(hr)) { SetError(HrText(L"VideoBuffer StartAccess",hr)); buffer->Release(); return false; }
    void* dst=nullptr;
    hr=buffer->GetBytes(&dst);
    if(SUCCEEDED(hr) && dst) std::memcpy(dst,uyvy.data(),(size_t)rowBytes*(size_t)g_height);
    buffer->EndAccess(bmdBufferAccessReadAndWrite);
    buffer->Release();
    if(FAILED(hr)||!dst) { SetError(HrText(L"VideoBuffer GetBytes",hr)); return false; }
    return true;
}

void Worker()
{
    HRESULT com=CoInitializeEx(nullptr,COINIT_MULTITHREADED);
    IDeckLinkIterator* iterator=nullptr;
    IDeckLink* device=nullptr;
    IDeckLinkOutput* output=nullptr;
    IDeckLinkDisplayMode* mode=nullptr;
    OutputCallback* callback=nullptr;
    bool videoEnabled=false,audioEnabled=false,playbackStarted=false,audioPreroll=false;
    BMDTimeValue frameDuration=0; BMDTimeScale frameTimescale=0; int rowBytes=0;

    do {
        HRESULT hr=CoCreateInstance(CLSID_CDeckLinkIterator,nullptr,CLSCTX_ALL,IID_IDeckLinkIterator,(void**)&iterator);
        if(FAILED(hr)||!iterator){ SetError(HrText(L"DeckLink iterator",hr)); break; }
        device=GetOutputDeviceByOrdinal(iterator,g_outputOrdinal);
        if(!device){ SetError(L"Selected DeckLink output device was not found."); break; }
        hr=device->QueryInterface(IID_IDeckLinkOutput,(void**)&output);
        if(FAILED(hr)||!output){ SetError(HrText(L"IDeckLinkOutput",hr)); break; }
        mode=FindMode(output);
        if(!mode){ SetError(L"Selected DeckLink display mode is no longer exposed by this output."); break; }
        g_width=(int)mode->GetWidth(); g_height=(int)mode->GetHeight();

        if(g_connection!=bmdVideoConnectionUnspecified) {
            IDeckLinkConfiguration* cfg=nullptr;
            hr=device->QueryInterface(IID_IDeckLinkConfiguration,(void**)&cfg);
            if(FAILED(hr)||!cfg){ SetError(HrText(L"IDeckLinkConfiguration",hr)); break; }
            hr=cfg->SetInt(bmdDeckLinkConfigVideoOutputConnection,(LONGLONG)g_connection);
            cfg->Release();
            if(FAILED(hr)){ SetError(HrText(L"Set video output connection",hr)); break; }
        }

        BOOL supported=FALSE; BMDDisplayMode actualMode=bmdModeUnknown;
        hr=output->DoesSupportVideoMode(g_connection,mode->GetDisplayMode(),bmdFormat8BitYUV,
                                        bmdNoVideoOutputConversion,bmdSupportedVideoModeDefault,&actualMode,&supported);
        if(FAILED(hr)||!supported){ SetError(L"DeckLink device does not support requested mode with 8-bit YUV output."); break; }
        if(output->RowBytesForPixelFormat(bmdFormat8BitYUV,g_width,&rowBytes)!=S_OK || rowBytes<=0) {
            SetError(L"DeckLink RowBytesForPixelFormat failed for 8-bit YUV."); break;
        }
        if(mode->GetFrameRate(&frameDuration,&frameTimescale)!=S_OK || frameDuration<=0 || frameTimescale<=0) {
            SetError(L"DeckLink display mode frame-rate query failed."); break;
        }

        hr=output->EnableVideoOutput(mode->GetDisplayMode(),bmdVideoOutputFlagDefault);
        if(FAILED(hr)){ SetError(HrText(L"EnableVideoOutput",hr)); break; }
        videoEnabled=true;
        hr=output->EnableAudioOutput(bmdAudioSampleRate48kHz,bmdAudioSampleType16bitInteger,(unsigned)g_channels,bmdAudioOutputStreamTimestamped);
        if(FAILED(hr)){ SetError(HrText(L"EnableAudioOutput",hr)); break; }
        audioEnabled=true;

        callback=new OutputCallback();
        hr=output->SetScheduledFrameCompletionCallback(callback);
        if(FAILED(hr)){ SetError(HrText(L"SetScheduledFrameCompletionCallback",hr)); break; }
        hr=output->BeginAudioPreroll();
        if(FAILED(hr)){ SetError(HrText(L"BeginAudioPreroll",hr)); break; }
        audioPreroll=true;

        g_ready=true; g_running=true; g_cv.notify_all();
        const unsigned minVideoPreroll=std::max(2u,(unsigned)std::ceil((double)frameTimescale/frameDuration*0.20));
        const unsigned minAudioPreroll=9600; // 200 ms at 48 kHz.
        const unsigned silenceFrames=960; // 20 ms at 48 kHz
        std::vector<unsigned char> silence((size_t)silenceFrames*(size_t)g_channels*2u,0);

        VideoPacket latestVideo; std::vector<unsigned char> latestUyvy; bool haveLatestVideo=false; long long nextVideoFrame=0;
        long long nextAudioSampleFrame=0;
        const unsigned targetVideoBuffer=std::max(minVideoPreroll,4u);
        const unsigned targetAudioBuffer=14400; // 300 ms maximum scheduled lead at 48 kHz.
        while(!g_stop) {
            VideoPacket vp; AudioPacket ap; bool haveVideo=false,haveAudio=false;
            unsigned vbuf=0,abuf=0;
            if(SUCCEEDED(output->GetBufferedVideoFrameCount(&vbuf))) g_bufferedVideo=vbuf;
            if(SUCCEEDED(output->GetBufferedAudioSampleFrameCount(&abuf))) g_bufferedAudio=abuf;
            {
                std::unique_lock<std::mutex> lock(g_mutex);
                g_cv.wait_for(lock,std::chrono::milliseconds(2),[]{return g_stop||!g_videoQueue.empty()||!g_audioQueue.empty();});
                if(g_stop) break;
                // Decoder callback timing must never become DeckLink wire timing. Keep only the
                // newest complete Program picture; the DeckLink hardware clock repeats it as needed.
                if(!g_videoQueue.empty()){
                    vp=std::move(g_videoQueue.back());
                    if(g_videoQueue.size()>1) g_videoQueueDrops+=(long long)g_videoQueue.size()-1;
                    g_videoQueue.clear(); haveVideo=true;
                }
                // Do not schedule decoded PCM seconds ahead of the hardware video clock.
                // Keep a small bounded preroll and let DeckLink consume it at 48 kHz.
                if(!g_audioQueue.empty() && (!playbackStarted || abuf<targetAudioBuffer)){
                    ap=std::move(g_audioQueue.front()); g_audioQueue.pop_front(); haveAudio=true;
                }
            }
            if(haveVideo){
                latestVideo=std::move(vp);
                // Convert BGRA -> broadcast UYVY only when a NEW Final Program frame arrives.
                // Repeated hardware-clock frames reuse the cached 4:2:2 image instead of burning
                // CPU on the same conversion many times.
                latestUyvy.resize((size_t)rowBytes*(size_t)g_height);
                BGRAtoUYVY(latestVideo.bytes.data(),latestVideo.stride,latestUyvy.data(),rowBytes,g_width,g_height);
                haveLatestVideo=true;
            }

            if(haveAudio) {
                unsigned written=0;
                unsigned frames=(unsigned)(ap.bytes.size()/std::max(1,ap.channels*2));
                HRESULT ah=output->ScheduleAudioSamples(ap.bytes.data(),frames,(BMDTimeValue)nextAudioSampleFrame,bmdAudioSampleRate48kHz,&written);
                if(SUCCEEDED(ah)) { g_audioScheduled+=written; nextAudioSampleFrame+=written; if(written<frames) g_audioQueueDrops+=(frames-written); }
                else { g_audioQueueDrops+=frames; SetError(HrText(L"ScheduleAudioSamples",ah)); }
            }

            // PROGRAM STOP must not drain the hardware audio timeline while video/standby
            // remains on air. Schedule silence on the same continuous 48 kHz timeline;
            // resumed PCM then follows nextAudioSampleFrame without requiring an app/card restart.
            if(!haveAudio && abuf<targetAudioBuffer) {
                unsigned written=0;
                HRESULT ah=output->ScheduleAudioSamples(silence.data(),silenceFrames,(BMDTimeValue)nextAudioSampleFrame,bmdAudioSampleRate48kHz,&written);
                if(SUCCEEDED(ah)) { g_audioScheduled+=written; nextAudioSampleFrame+=written; }
                else { SetError(HrText(L"Schedule silent audio",ah)); }
            }

            if(SUCCEEDED(output->GetBufferedVideoFrameCount(&vbuf))) g_bufferedVideo=vbuf;

            // Maintain a stable DeckLink video queue at the SELECTED HARDWARE cadence. If decode,
            // CG or WPF processing is momentarily late, repeat the latest good Final Program frame
            // instead of starving the card and producing frame-by-frame/slow video.
            while(haveLatestVideo && vbuf<targetVideoBuffer && !g_stop) {
                IDeckLinkMutableVideoFrame* frame=nullptr;
                HRESULT fh=output->CreateVideoFrame(g_width,g_height,rowBytes,bmdFormat8BitYUV,bmdFrameFlagDefault,&frame);
                if(SUCCEEDED(fh)&&frame) {
                    if(CopyUYVYIntoMutableFrame(frame,latestUyvy,rowBytes)) {
                        fh=output->ScheduleVideoFrame(frame,(BMDTimeValue)nextVideoFrame*frameDuration,frameDuration,frameTimescale);
                        if(SUCCEEDED(fh)) { g_videoScheduled++; nextVideoFrame++; vbuf++; }
                        else { g_videoQueueDrops++; SetError(HrText(L"ScheduleVideoFrame",fh)); }
                    } else g_videoQueueDrops++;
                    frame->Release();
                } else { g_videoQueueDrops++; SetError(HrText(L"CreateVideoFrame",fh)); }
                if(FAILED(fh)) break;
            }
            g_bufferedVideo=vbuf;
            if(SUCCEEDED(output->GetBufferedAudioSampleFrameCount(&abuf))) g_bufferedAudio=abuf;

            if(!playbackStarted && vbuf>=minVideoPreroll && abuf>=minAudioPreroll) {
                if(audioPreroll) {
                    HRESULT eh=output->EndAudioPreroll();
                    if(FAILED(eh)){ SetError(HrText(L"EndAudioPreroll",eh)); break; }
                    audioPreroll=false;
                }
                HRESULT sh=output->StartScheduledPlayback(0,frameTimescale,1.0);
                if(FAILED(sh)){ SetError(HrText(L"StartScheduledPlayback",sh)); break; }
                playbackStarted=true;
            }

            if(playbackStarted) {
                BMDTimeValue st=0; double speed=0;
                if(SUCCEEDED(output->GetScheduledStreamTime(frameTimescale,&st,&speed))) g_lastStreamTime=st;
            }
        }

        if(playbackStarted) {
            BMDTimeValue actual=0;
            output->StopScheduledPlayback(0,&actual,frameTimescale);
        } else if(audioPreroll) {
            output->EndAudioPreroll();
        }
        output->SetScheduledFrameCompletionCallback(nullptr);
    } while(false);

    g_running=false; g_ready=false;
    if(output && audioEnabled) output->DisableAudioOutput();
    if(output && videoEnabled) output->DisableVideoOutput();
    if(callback) callback->Release();
    if(mode) mode->Release();
    if(output) output->Release();
    if(device) device->Release();
    if(iterator) iterator->Release();
    if(SUCCEEDED(com)) CoUninitialize();
}

void StopWorker()
{
    g_stop=true; g_cv.notify_all();
    if(g_worker.joinable()) g_worker.join();
    std::lock_guard<std::mutex> lock(g_mutex); g_videoQueue.clear(); g_audioQueue.clear();
}
}

extern "C" {
SPDL_API int __cdecl SPDeckLink_GetApiVersion(){ return 7; }

SPDL_API int __cdecl SPDeckLink_EnumerateOutputDevices(wchar_t* buffer,int capacity)
{
    if(!buffer||capacity<=0) return -1; buffer[0]=0;
    HRESULT com=CoInitializeEx(nullptr,COINIT_MULTITHREADED);
    IDeckLinkIterator* iterator=nullptr;
    HRESULT hr=CoCreateInstance(CLSID_CDeckLinkIterator,nullptr,CLSCTX_ALL,IID_IDeckLinkIterator,(void**)&iterator);
    if(FAILED(hr)||!iterator){ SetError(HrText(L"DeckLink iterator",hr)); if(SUCCEEDED(com))CoUninitialize(); return 0; }
    std::wstring all; int count=0; IDeckLink* device=nullptr;
    while(iterator->Next(&device)==S_OK && device) {
        IDeckLinkOutput* output=nullptr;
        if(device->QueryInterface(IID_IDeckLinkOutput,(void**)&output)==S_OK && output) {
            BSTR name=nullptr; device->GetDisplayName(&name);
            if(count++) all+=L"\r\n";
            all += name ? std::wstring(name,SysStringLen(name)) : L"DeckLink Output";
            if(name) SysFreeString(name);
            output->Release();
        }
        device->Release(); device=nullptr;
    }
    iterator->Release(); if(SUCCEEDED(com)) CoUninitialize();
    wcsncpy_s(buffer,capacity,all.c_str(),_TRUNCATE); return count;
}

SPDL_API int __cdecl SPDeckLink_EnumerateOutputConnections(int outputOrdinal,wchar_t* buffer,int capacity)
{
    if(!buffer||capacity<=0) return -1; buffer[0]=0;
    HRESULT com=CoInitializeEx(nullptr,COINIT_MULTITHREADED);
    IDeckLink* device=OpenOutputDevice(outputOrdinal);
    if(!device){ SetError(L"Selected DeckLink output device was not found."); if(SUCCEEDED(com))CoUninitialize(); return 0; }
    LONGLONG mask=0; IDeckLinkProfileAttributes* attrs=nullptr;
    HRESULT hr=device->QueryInterface(IID_IDeckLinkProfileAttributes,(void**)&attrs);
    if(SUCCEEDED(hr)&&attrs) { hr=attrs->GetInt(BMDDeckLinkVideoOutputConnections,&mask); attrs->Release(); }
    std::wstring all; int count=0;
    const BMDVideoConnection values[]={bmdVideoConnectionSDI,bmdVideoConnectionHDMI,bmdVideoConnectionOpticalSDI,bmdVideoConnectionComponent,bmdVideoConnectionComposite,bmdVideoConnectionSVideo,bmdVideoConnectionEthernet,bmdVideoConnectionOpticalEthernet,bmdVideoConnectionInternal};
    if(SUCCEEDED(hr)) {
        for(BMDVideoConnection c:values) if((mask & (LONGLONG)c)!=0) {
            if(count++) all+=L"\r\n"; all+=std::to_wstring((unsigned)c); all+=L"|"; all+=ConnectionName(c);
        }
    }
    if(count==0) { all=L"0|Unspecified / Auto"; count=1; }
    wcsncpy_s(buffer,capacity,all.c_str(),_TRUNCATE); device->Release(); if(SUCCEEDED(com))CoUninitialize(); return count;
}

SPDL_API int __cdecl SPDeckLink_EnumerateOutputModes(int outputOrdinal,unsigned int connection,wchar_t* buffer,int capacity)
{
    if(!buffer||capacity<=0) return -1; buffer[0]=0;
    HRESULT com=CoInitializeEx(nullptr,COINIT_MULTITHREADED);
    IDeckLink* device=OpenOutputDevice(outputOrdinal);
    if(!device){ SetError(L"Selected DeckLink output device was not found."); if(SUCCEEDED(com))CoUninitialize(); return 0; }
    IDeckLinkOutput* output=nullptr; HRESULT hr=device->QueryInterface(IID_IDeckLinkOutput,(void**)&output);
    if(FAILED(hr)||!output){ SetError(HrText(L"IDeckLinkOutput",hr)); device->Release(); if(SUCCEEDED(com))CoUninitialize(); return 0; }
    IDeckLinkDisplayModeIterator* it=nullptr; hr=output->GetDisplayModeIterator(&it);
    std::wstring all; int count=0; IDeckLinkDisplayMode* mode=nullptr;
    if(SUCCEEDED(hr)&&it) while(it->Next(&mode)==S_OK && mode) {
        BOOL supported=FALSE; BMDDisplayMode actual=bmdModeUnknown;
        HRESULT sh=output->DoesSupportVideoMode((BMDVideoConnection)connection,mode->GetDisplayMode(),bmdFormat8BitYUV,bmdNoVideoOutputConversion,bmdSupportedVideoModeDefault,&actual,&supported);
        if(SUCCEEDED(sh)&&supported) {
            BMDTimeValue dur=0; BMDTimeScale scale=0; mode->GetFrameRate(&dur,&scale);
            BSTR name=nullptr; mode->GetName(&name);
            if(count++) all+=L"\r\n";
            all+=std::to_wstring((unsigned)mode->GetDisplayMode()); all+=L"|";
            all+=(name?std::wstring(name,SysStringLen(name)):L"DeckLink Mode"); all+=L"|";
            all+=std::to_wstring((int)mode->GetWidth()); all+=L"|"; all+=std::to_wstring((int)mode->GetHeight()); all+=L"|";
            all+=std::to_wstring((long long)scale); all+=L"|"; all+=std::to_wstring((long long)dur); all+=L"|";
            auto fd=mode->GetFieldDominance(); all+=((fd==bmdLowerFieldFirst||fd==bmdUpperFieldFirst)?L"1":L"0");
            if(name) SysFreeString(name);
        }
        mode->Release(); mode=nullptr;
    }
    if(it)it->Release(); output->Release(); device->Release(); if(SUCCEEDED(com))CoUninitialize();
    wcsncpy_s(buffer,capacity,all.c_str(),_TRUNCATE); return count;
}

SPDL_API int __cdecl SPDeckLink_StartOutput(int outputOrdinal,unsigned int connection,unsigned int displayMode,int audioChannels)
{
    StopWorker();
    { std::lock_guard<std::mutex> lock(g_mutex); g_lastError.clear(); g_videoQueue.clear(); g_audioQueue.clear(); }
    g_outputOrdinal=outputOrdinal; g_connection=(BMDVideoConnection)connection; g_displayMode=(BMDDisplayMode)displayMode; g_channels=audioChannels;
    g_videoScheduled=0; g_audioScheduled=0; g_videoQueueDrops=0; g_audioQueueDrops=0; g_bufferedVideo=0; g_bufferedAudio=0;
    g_completed=0; g_late=0; g_hwDropped=0; g_flushed=0; g_lastStreamTime=0;
    g_stop=false; g_ready=false; g_running=false; g_worker=std::thread(Worker);
    for(int i=0;i<200 && !g_ready && g_worker.joinable();i++) Sleep(20);
    if(!g_ready){ StopWorker(); return -2; }
    return 0;
}

SPDL_API int __cdecl SPDeckLink_PushVideoBGRA(const void* bytes,int byteCount,int stride,long long frameNumber)
{
    if(!g_running||!bytes||byteCount<=0||stride<=0) return -1;
    VideoPacket p; p.stride=stride; p.frameNumber=frameNumber; p.bytes.resize(byteCount); memcpy(p.bytes.data(),bytes,byteCount);
    { std::lock_guard<std::mutex> lock(g_mutex); if(g_videoQueue.size()>=8){ g_videoQueue.pop_front(); g_videoQueueDrops++; } g_videoQueue.push_back(std::move(p)); }
    g_cv.notify_one(); return 0;
}

SPDL_API int __cdecl SPDeckLink_PushAudioS16(const void* bytes,int byteCount,int sampleRate,int channels,long long startSampleFrame)
{
    if(!g_running||!bytes||byteCount<=0) return -1;
    if(sampleRate!=48000){ SetError(L"Native DeckLink bridge requires 48 kHz PCM audio."); return -3; }
    if(channels!=g_channels){ SetError(L"PCM channel count does not match the enabled DeckLink audio output."); return -4; }
    AudioPacket p; p.channels=channels; p.startSampleFrame=startSampleFrame; p.bytes.resize(byteCount); memcpy(p.bytes.data(),bytes,byteCount);
    { std::lock_guard<std::mutex> lock(g_mutex); if(g_audioQueue.size()>=32){ g_audioQueue.pop_front(); g_audioQueueDrops++; } g_audioQueue.push_back(std::move(p)); }
    g_cv.notify_one(); return 0;
}

SPDL_API int __cdecl SPDeckLink_StopOutput(){ StopWorker(); return 0; }

SPDL_API int __cdecl SPDeckLink_GetDiagnostics(long long* videoScheduled,long long* audioScheduled,long long* videoQueueDrops,long long* audioQueueDrops,long long* bufferedVideo,long long* bufferedAudio)
{
    if(videoScheduled)*videoScheduled=g_videoScheduled.load(); if(audioScheduled)*audioScheduled=g_audioScheduled.load();
    if(videoQueueDrops)*videoQueueDrops=g_videoQueueDrops.load(); if(audioQueueDrops)*audioQueueDrops=g_audioQueueDrops.load();
    if(bufferedVideo)*bufferedVideo=g_bufferedVideo.load(); if(bufferedAudio)*bufferedAudio=g_bufferedAudio.load();
    return g_running?1:0;
}

SPDL_API int __cdecl SPDeckLink_GetOutputHealth(long long* completed,long long* late,long long* hardwareDropped,long long* flushed,long long* streamTime)
{
    if(completed)*completed=g_completed.load(); if(late)*late=g_late.load(); if(hardwareDropped)*hardwareDropped=g_hwDropped.load();
    if(flushed)*flushed=g_flushed.load(); if(streamTime)*streamTime=g_lastStreamTime.load(); return g_running?1:0;
}

SPDL_API int __cdecl SPDeckLink_GetLastError(wchar_t* buffer,int capacity)
{
    if(!buffer||capacity<=0) return -1; std::wstring s; { std::lock_guard<std::mutex> lock(g_mutex); s=g_lastError; }
    wcsncpy_s(buffer,capacity,s.empty()?L"No native DeckLink error reported.":s.c_str(),_TRUNCATE); return 0;
}
}
