#pragma once
#ifdef SMARTPLAYOUT_DECKLINK_NATIVE_EXPORTS
#define SPDL_API __declspec(dllexport)
#else
#define SPDL_API __declspec(dllimport)
#endif
extern "C" {
SPDL_API int __cdecl SPDeckLink_GetApiVersion();
SPDL_API int __cdecl SPDeckLink_EnumerateOutputDevices(wchar_t* buffer, int charCapacity);
SPDL_API int __cdecl SPDeckLink_EnumerateOutputConnections(int deviceIndex, wchar_t* buffer, int charCapacity);
SPDL_API int __cdecl SPDeckLink_EnumerateOutputModes(int deviceIndex, unsigned int connection, wchar_t* buffer, int charCapacity);
SPDL_API int __cdecl SPDeckLink_StartOutput(int deviceIndex, unsigned int connection, unsigned int displayMode, int audioChannels);
SPDL_API int __cdecl SPDeckLink_PushVideoBGRA(const void* bytes, int byteCount, int stride, long long frameNumber);
SPDL_API int __cdecl SPDeckLink_PushAudioS16(const void* bytes, int byteCount, int sampleRate, int channels, long long startSampleFrame);
SPDL_API int __cdecl SPDeckLink_StopOutput();
SPDL_API int __cdecl SPDeckLink_GetDiagnostics(long long* videoScheduled, long long* audioScheduled, long long* videoQueueDrops, long long* audioQueueDrops, long long* bufferedVideo, long long* bufferedAudio);
SPDL_API int __cdecl SPDeckLink_GetOutputHealth(long long* completed, long long* late, long long* hardwareDropped, long long* flushed, long long* streamTime);
SPDL_API int __cdecl SPDeckLink_GetLastError(wchar_t* buffer, int charCapacity);
}
