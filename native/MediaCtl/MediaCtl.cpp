#include <windows.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Media.Control.h>
#include <iostream>
#include <string>

using namespace winrt::Windows::Media::Control;

int wmain()
{
    try { winrt::init_apartment(winrt::apartment_type::multi_threaded); }
    catch (...) { return 1; }

    auto manager = GlobalSystemMediaTransportControlsSessionManager::RequestAsync().get();
    auto session = manager.GetCurrentSession();
    if (!session)
    {
        std::wcout << L"no session\n";
        return 2;
    }

    auto playback = session.GetPlaybackInfo();
    auto media = session.TryGetMediaPropertiesAsync().get();
    int status = (int)playback.PlaybackStatus();
    std::wstring title(media.Title().c_str());
    std::wstring artist(media.Artist().c_str());
    std::wcout << L"status=" << status << L" title=" << title << L" artist=" << artist << L"\n";

    if (playback.PlaybackStatus() != GlobalSystemMediaTransportControlsSessionPlaybackStatus::Playing)
    {
        session.TryPlayAsync().get();
        Sleep(1200);
        playback = session.GetPlaybackInfo();
        std::wcout << L"after play status=" << (int)playback.PlaybackStatus() << L"\n";
    }
    return 0;
}
