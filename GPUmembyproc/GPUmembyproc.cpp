// gets GPU memory usage by process.
// Can pass exe name on command line, otherwise prints all that use GPU memory.
// Based on Mozilla's gfxWindowsPlatform.cpp and wj32's Process Hacker.
// Won't handle multiple GPU cards properly.

#include <windows.h>
#include <stdio.h>
#include <tuple>
#include <Tlhelp32.h>
#include "d3dkmtQueryStatistics.h"
#include "dxgi.h"

#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "dxgi.lib")

#define NT_SUCCESS(x) ((x)>=0)

typedef std::tuple<DWORD, DWORD> Version;

HMODULE gdi32Handle;
PFND3DKMTQS queryD3DKMTStatistics;

bool GetDXGIAdapter(IDXGIAdapter **aDXGIAdapter)
{
    *aDXGIAdapter = NULL;
    IDXGIFactory *factory = NULL;
    CreateDXGIFactory(__uuidof(IDXGIFactory), (void**)&factory);
    IDXGIAdapter *a1;
    UINT i;
    for (i = 0; factory->EnumAdapters(i, &a1) != DXGI_ERROR_NOT_FOUND; ++i)
    {
        IDXGIAdapter *a2;
        a1->QueryInterface(__uuidof(IDXGIAdapter), (void **)&a2);
        DXGI_ADAPTER_DESC desc;
        a2->GetDesc(&desc);
        if (wcsstr(desc.Description, L"NVIDIA") || wcsstr(desc.Description, L"ATI") || wcsstr(desc.Description, L"AMD"))
            *aDXGIAdapter = a2;
    }

    factory->Release();
    return *aDXGIAdapter != NULL;
}

OSVERSIONINFOEX getWindowsVersionInfo()
{
    OSVERSIONINFOEX info = {};
    info.dwOSVersionInfoSize = sizeof(info);
    const auto success = GetVersionEx(reinterpret_cast<OSVERSIONINFO*>(&info));
    return info;
}

Version getWindowsVersion() 
{
    const auto info = getWindowsVersionInfo();
    return Version(info.dwMajorVersion, info.dwMinorVersion);
}

bool IsWin8OrLater() 
{
    return getWindowsVersion() >= Version(6, 2);
}

void printcomma(long long n)
{
    if (n < 1000) 
    {
        printf("%lld", n);
        return;
    }
    printcomma(n / 1000);
    printf(",%03lld", n % 1000);
}

void getgpustats(PROCESSENTRY32 *ppe, HANDLE ProcessHandle)
{
    long long dedicatedBytesUsed = 0;
    long long sharedBytesUsed = 0;
    long long committedBytesUsed = 0;
    IDXGIAdapter *DXGIAdapter;

    if (GetDXGIAdapter(&DXGIAdapter)) 
    {
        // Most of this block is understood thanks to wj32's work on Process Hacker

        DXGI_ADAPTER_DESC adapterDesc;
        D3DKMTQS queryStatistics;

        DXGIAdapter->GetDesc(&adapterDesc);
        DXGIAdapter->Release();

        memset(&queryStatistics, 0, sizeof(D3DKMTQS));
        queryStatistics.Type = D3DKMTQS_PROCESS;
        queryStatistics.AdapterLuid = adapterDesc.AdapterLuid;
        queryStatistics.hProcess = ProcessHandle;
        if (NT_SUCCESS(queryD3DKMTStatistics(&queryStatistics))) 
            committedBytesUsed = queryStatistics.QueryResult.ProcessInfo.SystemMemory.BytesAllocated;

        memset(&queryStatistics, 0, sizeof(D3DKMTQS));
        queryStatistics.Type = D3DKMTQS_ADAPTER;
        queryStatistics.AdapterLuid = adapterDesc.AdapterLuid;
        if (NT_SUCCESS(queryD3DKMTStatistics(&queryStatistics))) 
        {
            ULONG i;
            ULONG segmentCount = queryStatistics.QueryResult.AdapterInfo.NbSegments;

            for (i = 0; i < segmentCount; i++) 
            {
                memset(&queryStatistics, 0, sizeof(D3DKMTQS));
                queryStatistics.Type = D3DKMTQS_SEGMENT;
                queryStatistics.AdapterLuid = adapterDesc.AdapterLuid;
                queryStatistics.QuerySegment.SegmentId = i;

                if (NT_SUCCESS(queryD3DKMTStatistics(&queryStatistics))) 
                {
                    BOOL aperture;

                    // SegmentInformation has a different definition in Win7 than later versions
                    if (!IsWin8OrLater())
                        aperture = queryStatistics.QueryResult.SegmentInfoWin7.Aperture;
                    else
                        aperture = queryStatistics.QueryResult.SegmentInfoWin8.Aperture;

                    memset(&queryStatistics, 0, sizeof(D3DKMTQS));
                    queryStatistics.Type = D3DKMTQS_PROCESS_SEGMENT;
                    queryStatistics.AdapterLuid = adapterDesc.AdapterLuid;
                    queryStatistics.hProcess = ProcessHandle;
                    queryStatistics.QueryProcessSegment.SegmentId = i;
                    if (NT_SUCCESS(queryD3DKMTStatistics(&queryStatistics))) 
                    {
                        ULONGLONG bytesCommitted;
                        if (!IsWin8OrLater())
                            bytesCommitted = queryStatistics.QueryResult.ProcessSegmentInfo.Win7.BytesCommitted;
                        else
                            bytesCommitted = queryStatistics.QueryResult.ProcessSegmentInfo.Win8.BytesCommitted;
                        if (aperture)
                            sharedBytesUsed += bytesCommitted;
                        else
                            dedicatedBytesUsed += bytesCommitted;
                    }
                }
            }
        }
    }

    if (dedicatedBytesUsed > 0)
    {
        printf("%s  %d  ", ppe->szExeFile, ppe->th32ProcessID);
        printcomma(committedBytesUsed);
        printf("  ");
        printcomma(dedicatedBytesUsed);
        printf("\n");
    }

    CloseHandle(ProcessHandle);
}

void printgpustats(char *name)
{
    PROCESSENTRY32 ppe = { 0 };
    ppe.dwSize = sizeof(PROCESSENTRY32);
    HANDLE h = NULL;

    HANDLE hSnapShot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (Process32First(hSnapShot, &ppe))
    {
        do
        {
            if (!strlen(name) || !stricmp(name, ppe.szExeFile))
            {
                HANDLE hProcess = OpenProcess(PROCESS_QUERY_INFORMATION, FALSE, ppe.th32ProcessID);
                if (hProcess)
                    getgpustats(&ppe, hProcess);
            }
        } while (Process32Next(hSnapShot, &ppe));
    }
    CloseHandle(hSnapShot);
}

int main(int argc, char *argv[])
{
    if ((gdi32Handle = LoadLibrary(TEXT("gdi32.dll"))))
    {
        queryD3DKMTStatistics = (PFND3DKMTQS)GetProcAddress(gdi32Handle, "D3DKMTQueryStatistics");
        if (queryD3DKMTStatistics)
            printgpustats(argc == 1 ? "" : argv[1]);
        FreeLibrary(gdi32Handle);
    }
}