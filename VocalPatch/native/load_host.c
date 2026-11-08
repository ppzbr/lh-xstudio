/*
 * load_host.c - a throwaway host process used to prove the loader works.
 *
 * It does nothing but LoadLibrary() the loader named on the command line and then
 * stay alive, which is exactly what an injected editor does from the loader's
 * point of view: DllMain runs, the loader starts the CLR on its own thread, and
 * the managed runtime logs to %TEMP%\vocalpatch-editor.log.
 *
 * Build it with the same bitness as the loader you want to test:
 *   tcc -o load_host_x64.exe load_host.c            (64-bit TinyCC)
 *   tcc -o load_host_x86.exe load_host.c            (32-bit TinyCC)
 */

typedef void         *vp_handle;
typedef unsigned int  vp_dword;

/* x64 ignores calling conventions; 32-bit needs stdcall for the Win32 ABI.
   TinyCC rejects the __stdcall keyword but accepts the attribute spelling. */
#if defined(_WIN64) || defined(__x86_64__) || defined(_M_X64) || defined(__amd64__)
#define VP_API
#elif defined(_MSC_VER)
#define VP_API __stdcall
#else
#define VP_API __attribute__((stdcall))
#endif

extern vp_handle VP_API LoadLibraryA(const char *name);
extern void      VP_API Sleep(vp_dword milliseconds);

int main(int argc, char **argv)
{
    vp_handle module;
    vp_dword  hold = 6000;

    if (argc < 2) return 2;

    if (argc >= 3) {
        vp_dword parsed = 0;
        char     *p     = argv[2];
        while (*p >= '0' && *p <= '9') parsed = parsed * 10 + (vp_dword)(*p++ - '0');
        if (parsed != 0) hold = parsed;
    }

    module = LoadLibraryA(argv[1]);
    if (module == 0) return 3;

    /* DllMain already spawned the loader thread; give the CLR time to start. */
    Sleep(hold);
    return 0;
}
