/*
 * proxy_test.c — throwaway host used to prove the winmm drop-in works.
 *
 * It statically imports winmm.dll (see winmm.def) exactly the way the editors
 * do, then calls a couple of entry points and writes what it got to
 * proxy_test_result.txt beside itself.
 *
 * Build it as "synthv-studio.exe" so the loader's host allow-list accepts it,
 * then run it twice: once in a folder with no winmm.dll (baseline — Windows
 * resolves the real System32 DLL) and once with the VocalPatch proxy dropped
 * in. The values must be identical both times, proving the forwarders are
 * transparent, while the second run additionally logs a loader attach.
 */

typedef unsigned long DWORD;
typedef unsigned int  UINT;
typedef int           BOOL;
typedef void         *H;

#if defined(_WIN64) || defined(__x86_64__) || defined(_M_X64) || defined(__amd64__)
#define VP_API
#else
#define VP_API __attribute__((stdcall))
#endif

extern DWORD VP_API timeGetTime(void);
extern UINT  VP_API timeBeginPeriod(UINT);
extern UINT  VP_API timeEndPeriod(UINT);
extern void  VP_API Sleep(DWORD);
extern H     VP_API CreateFileA(const char *name, DWORD access, DWORD share, void *security,
                                DWORD disposition, DWORD flags, H template_);
extern BOOL  VP_API WriteFile(H file, const void *buffer, DWORD want, DWORD *got, void *overlapped);
extern BOOL  VP_API CloseHandle(H object);
extern DWORD VP_API GetCurrentProcessId(void);

static char *append(char *at, const char *text)
{
    while (*text != 0) *at++ = *text++;
    return at;
}

static char *append_ulong(char *at, unsigned long value)
{
    char digits[24];
    int n = 0;
    if (value == 0) digits[n++] = '0';
    while (value != 0) { digits[n++] = (char)('0' + (value % 10)); value /= 10; }
    while (n > 0) *at++ = digits[--n];
    return at;
}

int main(void)
{
    char buffer[256];
    char *at = buffer;
    DWORD first, second;
    UINT period;
    H file;
    DWORD written = 0;

    first = timeGetTime();
    period = timeBeginPeriod(1);
    Sleep(60);
    second = timeGetTime();
    timeEndPeriod(1);

    at = append(at, "pid=");
    at = append_ulong(at, GetCurrentProcessId());
    at = append(at, " t1=");
    at = append_ulong(at, first);
    at = append(at, " t2=");
    at = append_ulong(at, second);
    at = append(at, " delta=");
    at = append_ulong(at, second - first);
    at = append(at, " beginPeriod=");
    at = append_ulong(at, period);
    at = append(at, "\r\n");

    file = CreateFileA("proxy_test_result.txt", 0x40000000u, 0, 0, 2, 0x80, 0);
    if (file != (H)-1 && file != 0)
    {
        WriteFile(file, buffer, (DWORD)(at - buffer), &written, 0);
        CloseHandle(file);
    }
    return 0;
}
