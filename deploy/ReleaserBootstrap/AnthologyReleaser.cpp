#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>

#include <string>

namespace
{
std::wstring GetExecutableDirectory()
{
    std::wstring path(32768, L'\0');
    const auto length = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
    if (length == 0 || length >= path.size())
    {
        return {};
    }

    path.resize(length);
    const auto separator = path.find_last_of(L"\\/");
    return separator == std::wstring::npos ? std::wstring{} : path.substr(0, separator);
}

void ShowLaunchError(const wchar_t* message)
{
    MessageBoxW(
        nullptr,
        message,
        L"A.N.T.H.O.L.O.G.Y Releaser",
        MB_OK | MB_ICONERROR | MB_SETFOREGROUND);
}
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
    const auto root = GetExecutableDirectory();
    if (root.empty())
    {
        ShowLaunchError(L"Не удалось определить папку релизера.");
        return 1;
    }

    const auto appRoot = root + L"\\App";
    const auto application = appRoot + L"\\AnthologyReleaser.Next.exe";
    if (GetFileAttributesW(application.c_str()) == INVALID_FILE_ATTRIBUTES)
    {
        ShowLaunchError(
            L"Компоненты релизера не найдены.\n"
            L"Ожидается App\\AnthologyReleaser.Next.exe рядом с этим файлом.");
        return 2;
    }

    SHELLEXECUTEINFOW launch{};
    launch.cbSize = sizeof(launch);
    launch.fMask = SEE_MASK_FLAG_NO_UI | SEE_MASK_NOASYNC;
    launch.lpVerb = L"open";
    launch.lpFile = application.c_str();
    launch.lpDirectory = appRoot.c_str();
    launch.nShow = SW_SHOWNORMAL;
    if (!ShellExecuteExW(&launch))
    {
        ShowLaunchError(L"Не удалось запустить A.N.T.H.O.L.O.G.Y Releaser Next.");
        return 3;
    }

    return 0;
}
