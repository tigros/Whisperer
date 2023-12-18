#include "framework.h"
#include "qlaunch.h"
#include <tlhelp32.h>

#define BUFFER_SIZE 1024

DWORD Run(char *cmd)
{
    STARTUPINFO StartupInfo;
    PROCESS_INFORMATION ProcessInfo;

    memset(&StartupInfo, 0, sizeof(StartupInfo));
    StartupInfo.cb = sizeof(STARTUPINFO);
    StartupInfo.dwFlags = STARTF_USESHOWWINDOW;
    StartupInfo.wShowWindow = SW_SHOW;

    if (!CreateProcess(NULL, cmd, NULL, NULL, FALSE,
        CREATE_NO_WINDOW,
        NULL,
        NULL,
        &StartupInfo,
        &ProcessInfo))
    {
        return GetLastError();
    }

    CloseHandle(ProcessInfo.hThread);
    CloseHandle(ProcessInfo.hProcess);

    return 0;
}

int APIENTRY wWinMain(_In_ HINSTANCE hInstance,
                     _In_opt_ HINSTANCE hPrevInstance,
                     _In_ LPWSTR    lpCmdLine,
                     _In_ int       nCmdShow)
{
    UNREFERENCED_PARAMETER(hPrevInstance);
    UNREFERENCED_PARAMETER(lpCmdLine);

    char cmd[BUFFER_SIZE];
    size_t   i;    
    wcstombs_s(&i, cmd, (size_t)BUFFER_SIZE, lpCmdLine, (size_t)BUFFER_SIZE);

    Run(cmd);

    return 0;
}

