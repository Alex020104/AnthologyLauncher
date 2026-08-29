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
        L"A.N.T.H.O.L.O.G.Y Launcher",
        MB_OK | MB_ICONERROR | MB_SETFOREGROUND);
}
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
    const auto gameRoot = GetExecutableDirectory();
    if (gameRoot.empty())
    {
        ShowLaunchError(L"Не удалось определить корень A.N.T.H.O.L.O.G.Y.");
        return 1;
    }

    const auto launcherRoot = gameRoot + L"\\AnthologyLauncher";
    const auto scriptPath = launcherRoot + L"\\Start-AnthologyLauncherNext.ps1";
    if (GetFileAttributesW(scriptPath.c_str()) == INVALID_FILE_ATTRIBUTES)
    {
        ShowLaunchError(
            L"Компоненты нового лаунчера не найдены.\n"
            L"Ожидается папка AnthologyLauncher рядом с AnomalyLauncher.exe.");
        return 2;
    }

    const auto parameters =
        L"-NoLogo -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + scriptPath + L"\"";
    SHELLEXECUTEINFOW launch{};
    launch.cbSize = sizeof(launch);
    launch.fMask = SEE_MASK_FLAG_NO_UI | SEE_MASK_NOASYNC;
    launch.lpVerb = L"open";
    launch.lpFile = L"powershell.exe";
    launch.lpParameters = parameters.c_str();
    launch.lpDirectory = launcherRoot.c_str();
    launch.nShow = SW_HIDE;

    if (!ShellExecuteExW(&launch))
    {
        ShowLaunchError(L"Не удалось запустить новый лаунчер A.N.T.H.O.L.O.G.Y.");
        return 3;
    }

    return 0;
}
