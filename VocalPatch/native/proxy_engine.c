/*
 * proxy_engine.c — the shared engine behind VocalPatch's drop-in proxy DLLs.
 *
 * WHY THIS EXISTS
 * ---------------
 * winmm.dll is in the static import table of SynthV Studio 2, SynthV Studio
 * (v1), VoiSona and Piapro Studio NT, and it is not a KnownDLL. Windows
 * resolves a statically imported DLL through the application directory first,
 * so dropping a file named winmm.dll next to the editor's exe makes the editor
 * load *that* DLL at startup. Its DllMain then pulls in the VocalPatch runtime.
 * That is the whole "copy the DLL in and it just works" trick: no registry, no
 * installer, no launcher stub, one file per editor folder.
 *
 * The same engine builds other shims (mscoree.dll for pure-managed hosts,
 * hostfxr.dll for self-contained .NET apps); build_proxy.ps1 picks the target
 * name and the export list, and the editor decides which one it wants to load.
 *
 * WHAT IT DOES
 * ------------
 *   1. DllMain (PROCESS_ATTACH) spawns one worker thread — nothing else. Doing
 *      real work under the loader lock is how you deadlock a host.
 *   2. The worker resolves the real DLL in C:\Windows\System32 by full path
 *      (a full path cannot resolve back to us) and LoadLibrary()s the
 *      VocalPatch loader, which hosts the CLR and runs
 *      VocalPatch.Editor.Bootstrap.
 *   3. Every export below forwards to the real function. Each one is resolved
 *      lazily on first call, so even a call made before the worker thread runs
 *      still lands on the genuine implementation.
 *
 * Every prototype in native\exports\*.h is integer/pointer only, so forwarding
 * is exact — no floating-point or struct-by-value ABI to get wrong.
 *
 * WHY NO #include
 * ---------------
 * Built with TinyCC, whose bundled headers are too thin for this
 * (GetSystemDirectoryW, GetEnvironmentVariableW, ...). The handful of kernel32
 * imports are declared below instead.
 */

typedef void          *vp_handle;
typedef unsigned int   vp_dword;
typedef unsigned short vp_word;
typedef int            vp_bool;
typedef unsigned long long vp_ptr64;

/* x64 ignores calling conventions; 32-bit needs stdcall for the Win32 ABI.
   TinyCC rejects the __stdcall keyword but accepts the attribute spelling. */
#if defined(_WIN64) || defined(__x86_64__) || defined(_M_X64) || defined(__amd64__)
#define VP_API
#else
#define VP_API __attribute__((stdcall))
#endif

#define VP_MAX_PATH 1024

/* A marker tooling can look for to tell this shim apart from the genuine DLL
   (deploy_dropin.ps1 refuses to overwrite a real hostfxr with a shim, and
   refuses to "uninstall" a file that is not ours). Not exported, so it does not
   show up in the export table. */
const char vp_shim_marker[] = "VocalPatchDropInShim";

/* Optional build-time configuration: the build script writes
   vocalpatch_proxy_config.h with VP_LOADER_DEFAULT (an absolute path to
   vocalpatch_loader.dll) and proxy_target.h with VP_TARGET_FILE (the name of
   the DLL we are standing in for). Compile with -DVP_HAVE_CONFIG -I<dir>. */
#if defined(VP_HAVE_CONFIG)
#include "vocalpatch_proxy_config.h"
#include "proxy_target.h"
#endif

#ifndef VP_TARGET_FILE
#error build_proxy.ps1 must generate proxy_target.h (or define VP_TARGET_FILE)
#endif

/* kernel32 ------------------------------------------------------------- */
extern vp_handle VP_API LoadLibraryW(const vp_word *name);
extern void     *VP_API GetProcAddress(vp_handle module, const char *name);
extern vp_dword  VP_API GetSystemDirectoryW(vp_word *buffer, vp_dword size);
extern vp_dword  VP_API GetModuleFileNameW(vp_handle module, vp_word *buffer, vp_dword size);
extern vp_dword  VP_API GetEnvironmentVariableW(const vp_word *name, vp_word *buffer, vp_dword size);
extern vp_handle VP_API CreateFileW(const vp_word *name, vp_dword access, vp_dword share,
                                    void *security, vp_dword disposition, vp_dword flags, vp_handle template_);
extern vp_bool   VP_API ReadFile(vp_handle file, void *buffer, vp_dword want, vp_dword *got, void *overlapped);
extern vp_bool   VP_API CloseHandle(vp_handle object);
extern vp_handle VP_API CreateThread(void *attributes, vp_dword stack, vp_dword (VP_API *start)(void *),
                                     void *parameter, vp_dword flags, vp_dword *id);
extern vp_bool   VP_API DisableThreadLibraryCalls(vp_handle module);

#define VP_GENERIC_READ  0x80000000u
#define VP_OPEN_EXISTING 3u

/* ------------------------------------------------------------------ */
/* Small string helpers (ASCII <-> UTF-16, append, compare)            */
/* ------------------------------------------------------------------ */

static int vp_wlen(const vp_word *text)
{
    int n = 0;
    while (text[n] != 0) n++;
    return n;
}

static void vp_widen(const char *text, vp_word *out, int capacity)
{
    int i = 0;
    while (text[i] != 0 && i < capacity - 1) { out[i] = (vp_word)(unsigned char)text[i]; i++; }
    out[i] = 0;
}

static void vp_narrow(const vp_word *text, char *out, int capacity)
{
    int i = 0;
    while (text[i] != 0 && i < capacity - 1) { out[i] = (char)(text[i] & 0xFF); i++; }
    out[i] = 0;
}

static void vp_wappend(const vp_word *text, vp_word *out, int capacity)
{
    int at = vp_wlen(out);
    int i = 0;
    while (text[i] != 0 && at < capacity - 1) { out[at++] = text[i++]; }
    out[at] = 0;
}

/* Appends an ASCII literal to a wide buffer (paths are all ASCII here). */
static void vp_wappend_ascii(vp_word *out, int capacity, const char *text)
{
    int at = vp_wlen(out);
    int i = 0;
    while (text[i] != 0 && at < capacity - 1) { out[at++] = (vp_word)(unsigned char)text[i++]; }
    out[at] = 0;
}

static void vp_dir_of(const vp_handle module, vp_word *dir, int capacity)
{
    if (!module) { dir[0] = 0; return; }
    GetModuleFileNameW(module, dir, capacity);
    {
        int at = vp_wlen(dir);
        while (at > 0 && dir[at] != '\\' && dir[at] != '/') at--;
        dir[at] = 0;
    }
}

static int vp_w_equal_ci(const vp_word *a, const vp_word *b)
{
    int i = 0;
    for (;;)
    {
        vp_word x = a[i], y = b[i];
        if (x >= 'A' && x <= 'Z') x += 32;
        if (y >= 'A' && y <= 'Z') y += 32;
        if (x != y) return 0;
        if (x == 0) return 1;
        i++;
    }
}

/* ------------------------------------------------------------------ */
/* Resolving the genuine target DLL                                    */
/* ------------------------------------------------------------------ */

static vp_handle g_self;
static vp_handle g_real;
static int        g_real_tried;

static int vp_file_exists(const vp_word *path);

static void vp_ensure_real(void)
{
    vp_word path[VP_MAX_PATH];
    vp_word dir[VP_MAX_PATH];
    if (g_real || g_real_tried) return;
    g_real_tried = 1;

    /* A sibling "<name>.orig.dll" is the genuine article when the host's own
       copy had to be renamed to make room for us. A self-contained .NET app
       ships a version-locked hostfxr and must not be handed the machine-wide
       copy, so this is the preferred source when it exists. */
    vp_dir_of(g_self, dir, VP_MAX_PATH);
    if (dir[0])
    {
        path[0] = 0;
        vp_wappend(dir, path, VP_MAX_PATH);
        vp_wappend_ascii(path, VP_MAX_PATH, "\\" VP_TARGET_BASE ".orig.dll");
        if (vp_file_exists(path))
        {
            g_real = LoadLibraryW(path);
            if (g_real) return;
        }
    }

    /* Otherwise the real DLL is the one in the system directory. Resolved by
       full path, so it can never bounce back to us. */
    {
        vp_dword n = GetSystemDirectoryW(path, VP_MAX_PATH - 16);
        if (n == 0 || n >= VP_MAX_PATH - 16) return;
        path[n] = 0;
        vp_wappend_ascii(path, VP_MAX_PATH, "\\" VP_TARGET_FILE);
        g_real = LoadLibraryW(path);
    }
}

static void *vp_resolve(const char *name)
{
    vp_ensure_real();
    if (!g_real) return 0;
    return GetProcAddress(g_real, name);
}

/* ------------------------------------------------------------------ */
/* Finding and loading the VocalPatch loader                           */
/* ------------------------------------------------------------------ */

/* Order: VOCALPATCH_LOADER, then a vocalpatch.ini line "loader=<path>",
   then vocalpatch_loader.dll beside this proxy, then the per-user and
   per-machine install folders, then a path baked in at build time. */
static int vp_file_exists(const vp_word *path)
{
    vp_handle h = CreateFileW(path, VP_GENERIC_READ, 1, 0, VP_OPEN_EXISTING, 0x80, 0);
    if (h == (vp_handle)-1 || h == 0) return 0;
    CloseHandle(h);
    return 1;
}

static int vp_read_ini_loader(vp_word *out, int capacity)
{
    vp_word path[VP_MAX_PATH];
    char buffer[2048];
    vp_dword got = 0;
    vp_handle file;
    int i, n;
    const char *key = "loader=";

    out[0] = 0;
    vp_dir_of(g_self, path, VP_MAX_PATH);
    vp_wappend_ascii(path, VP_MAX_PATH, "\\vocalpatch.ini");

    file = CreateFileW(path, VP_GENERIC_READ, 1, 0, VP_OPEN_EXISTING, 0x80, 0);
    if (file == (vp_handle)-1 || file == 0) return 0;
    ReadFile(file, buffer, sizeof(buffer) - 1, &got, 0);
    CloseHandle(file);
    buffer[got] = 0;

    for (i = 0; i + 7 < (int)got; i++)
    {
        if (i > 0 && buffer[i - 1] != '\n') continue;
        for (n = 0; n < 7; n++) if (buffer[i + n] != key[n]) break;
        if (n != 7) continue;
        i += 7;
        n = 0;
        while (i < (int)got && buffer[i] != '\r' && buffer[i] != '\n' && n < capacity - 1)
            out[n++] = (vp_word)(unsigned char)buffer[i++];
        out[n] = 0;
        return n > 0;
    }
    return 0;
}

/* Order: VOCALPATCH_LOADER, a "loader=" line in vocalpatch.ini beside this
   proxy, vocalpatch_loader.dll beside this proxy, then the per-user and
   per-machine install folders, then the path baked in at build time. */
static vp_handle vp_load_payload(void)
{
    vp_word path[VP_MAX_PATH];
    vp_word dir[VP_MAX_PATH];
    vp_word variable[32];
    vp_word appdata[VP_MAX_PATH];

    /* 1. explicit environment override */
    vp_widen("VOCALPATCH_LOADER", variable, 32);
    path[0] = 0;
    if (GetEnvironmentVariableW(variable, path, VP_MAX_PATH) > 0 && vp_file_exists(path))
        return LoadLibraryW(path);

    /* 2. vocalpatch.ini beside this proxy */
    if (vp_read_ini_loader(path, VP_MAX_PATH) && vp_file_exists(path))
        return LoadLibraryW(path);

    /* 3. the loader sitting next to this proxy */
    vp_dir_of(g_self, dir, VP_MAX_PATH);
    path[0] = 0;
    vp_wappend(dir, path, VP_MAX_PATH);
    vp_wappend_ascii(path, VP_MAX_PATH, "\\vocalpatch_loader.dll");
    if (vp_file_exists(path)) return LoadLibraryW(path);

    /* 4. the per-user install folder used by install-appinit.ps1 */
    vp_widen("LOCALAPPDATA", variable, 32);
    if (GetEnvironmentVariableW(variable, appdata, VP_MAX_PATH) > 0)
    {
        path[0] = 0;
        vp_wappend(appdata, path, VP_MAX_PATH);
        vp_wappend_ascii(path, VP_MAX_PATH, "\\VocalPatch\\payload\\vocalpatch_loader.dll");
        if (vp_file_exists(path)) return LoadLibraryW(path);
    }

    /* 5. the per-machine install folder */
    vp_widen("ProgramData", variable, 32);
    if (GetEnvironmentVariableW(variable, appdata, VP_MAX_PATH) > 0)
    {
        path[0] = 0;
        vp_wappend(appdata, path, VP_MAX_PATH);
        vp_wappend_ascii(path, VP_MAX_PATH, "\\VocalPatch\\payload\\vocalpatch_loader.dll");
        if (vp_file_exists(path)) return LoadLibraryW(path);
    }

    /* 6. the path baked in at build time */
#if defined(VP_LOADER_DEFAULT)
    vp_widen(VP_LOADER_DEFAULT, path, VP_MAX_PATH);
    if (vp_file_exists(path)) return LoadLibraryW(path);
#endif

    return 0;
}

/* ------------------------------------------------------------------ */
/* Worker                                                              */
/* ------------------------------------------------------------------ */

static vp_dword VP_API vp_worker(void *unused)
{
    (void)unused;
    vp_ensure_real();
    vp_load_payload();
    return 0;
}

/* ------------------------------------------------------------------ */
/* Exports: each forwards to the genuine target DLL                    */
/* ------------------------------------------------------------------ */

#define VP_TYPE(ret)  ret VP_API

#define VP_FWD(ret, name, params, args, failret)                          \
    typedef VP_TYPE(ret) (*vp_fn_##name) params;                          \
    static vp_fn_##name vp_p_##name;                                      \
    __attribute__((dllexport)) VP_TYPE(ret) name params                   \
    {                                                                     \
        if (!vp_p_##name) vp_p_##name = (vp_fn_##name)vp_resolve(#name);   \
        if (vp_p_##name) { return vp_p_##name args; }                      \
        failret;                                                          \
    }

/* Signature-free forwarder for a function whose prototype is not written out.
   Eight pointer-sized arguments in, pointer-sized result out.

   Safe here for two reasons. Every winmm/hostfxr entry point takes scalar
   arguments only (integers and pointers, no floats and nothing by value), so
   there is no vector-register or hidden-pointer ABI to get wrong. And on x64 a
   callee simply ignores arguments past the ones it declares, so passing eight
   when the real function takes three is harmless — while declaring fewer than
   the real function takes would corrupt the stack arguments it does read.

   The whole winmm export table is forwarded this way so that a shim can never
   be missing an entry point some module in the host imports: an unresolved
   static import fails the DLL load, which would take the editor down with it. */
#define VP_FWD_ANY(name)                                                  \
    VP_FWD(vp_ptr64, name,                                                \
        (vp_ptr64 a1, vp_ptr64 a2, vp_ptr64 a3, vp_ptr64 a4,              \
         vp_ptr64 a5, vp_ptr64 a6, vp_ptr64 a7, vp_ptr64 a8),             \
        (a1, a2, a3, a4, a5, a6, a7, a8), return 0)

/* The entry points this shim re-exports: one file per target DLL under
   native\exports\. build_proxy.ps1 copies the chosen one into the build
   directory as proxy_exports.h. */
#include "proxy_exports.h"

/* ------------------------------------------------------------------ */
/* DllMain                                                             */
/* ------------------------------------------------------------------ */

vp_bool VP_API DllMain(vp_handle instance, vp_dword reason, void *reserved)
{
    (void)reserved;
    if (reason == 1) /* DLL_PROCESS_ATTACH */
    {
        g_self = instance;
        DisableThreadLibraryCalls(instance);
        CreateThread(0, 0, vp_worker, 0, 0, 0);
    }
    return 1;
}
