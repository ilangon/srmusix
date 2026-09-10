#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <iostream>
#include <string>
#include "DeckLinkAPI_h.h"

static std::wstring BstrToW(BSTR s){ return s ? std::wstring(s, SysStringLen(s)) : L""; }
static const wchar_t* ConnectionName(BMDVideoConnection c)
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
        default: return L"Unknown";
    }
}


int wmain()
{
    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(hr) && hr != RPC_E_CHANGED_MODE) {
        std::wcerr << L"COM INIT FAILED 0x" << std::hex << hr << L"\n";
        return 10;
    }

    IDeckLinkIterator* iterator = nullptr;
    hr = CoCreateInstance(CLSID_CDeckLinkIterator, nullptr, CLSCTX_ALL, IID_IDeckLinkIterator, reinterpret_cast<void**>(&iterator));
    if (FAILED(hr) || !iterator) {
        std::wcerr << L"DECKLINK ITERATOR FAILED 0x" << std::hex << hr << L"\n";
        std::wcerr << L"Install/repair Blackmagic Desktop Video and verify the DeckLink SDK/driver architecture matches x64.\n";
        if (SUCCEEDED(hr)) CoUninitialize();
        return 20;
    }

    int count = 0;
    IDeckLink* device = nullptr;
    while (iterator->Next(&device) == S_OK && device)
    {
        ++count;
        BSTR model = nullptr;
        BSTR display = nullptr;
        device->GetModelName(&model);
        device->GetDisplayName(&display);
        std::wcout << L"DEVICE " << count << L"\n";
        std::wcout << L"  MODEL   : " << BstrToW(model) << L"\n";
        std::wcout << L"  DISPLAY : " << BstrToW(display) << L"\n";
        if (model) SysFreeString(model);
        if (display) SysFreeString(display);

        IDeckLinkOutput* output = nullptr;
        if (device->QueryInterface(IID_IDeckLinkOutput, reinterpret_cast<void**>(&output)) == S_OK && output)
        {
            std::wcout << L"  OUTPUT  : YES\n";
            IDeckLinkProfileAttributes* attrs=nullptr;
            if(device->QueryInterface(IID_IDeckLinkProfileAttributes,reinterpret_cast<void**>(&attrs))==S_OK && attrs)
            {
                LONGLONG mask=0;
                if(attrs->GetInt(BMDDeckLinkVideoOutputConnections,&mask)==S_OK)
                {
                    std::wcout << L"  PHYSICAL OUTPUTS : "; bool first=true;
                    const BMDVideoConnection values[]={bmdVideoConnectionSDI,bmdVideoConnectionHDMI,bmdVideoConnectionOpticalSDI,bmdVideoConnectionComponent,bmdVideoConnectionComposite,bmdVideoConnectionSVideo,bmdVideoConnectionEthernet,bmdVideoConnectionOpticalEthernet,bmdVideoConnectionInternal};
                    for(BMDVideoConnection c:values) if((mask & (LONGLONG)c)!=0){ if(!first)std::wcout<<L", "; std::wcout<<ConnectionName(c); first=false; }
                    std::wcout << L"\n";
                }
                attrs->Release();
            }
            IDeckLinkDisplayModeIterator* modes = nullptr;
            if (output->GetDisplayModeIterator(&modes) == S_OK && modes)
            {
                int modeCount = 0;
                IDeckLinkDisplayMode* mode = nullptr;
                while (modes->Next(&mode) == S_OK && mode)
                {
                    ++modeCount;
                    BSTR name = nullptr;
                    mode->GetName(&name);
                    BMDTimeValue dur = 0; BMDTimeScale scale = 0;
                    mode->GetFrameRate(&dur, &scale);
                    std::wcout << L"    MODE " << modeCount << L" : " << BstrToW(name)
                               << L" • " << mode->GetWidth() << L"x" << mode->GetHeight()
                               << L" • duration=" << dur << L" scale=" << scale << L"\n";
                    if (name) SysFreeString(name);
                    mode->Release();
                }
                modes->Release();
            }
            output->Release();
        }
        else std::wcout << L"  OUTPUT  : NO\n";

        IDeckLinkInput* input = nullptr;
        if (device->QueryInterface(IID_IDeckLinkInput, reinterpret_cast<void**>(&input)) == S_OK && input)
        {
            std::wcout << L"  INPUT   : YES\n";
            input->Release();
        }
        else std::wcout << L"  INPUT   : NO\n";

        device->Release();
        device = nullptr;
    }
    iterator->Release();
    std::wcout << L"TOTAL DEVICES: " << count << L"\n";
    if (hr != RPC_E_CHANGED_MODE) CoUninitialize();
    return count > 0 ? 0 : 30;
}
