/*
 * vocalpatch_loader.c
 *
 * Minimal CLR-hosting loader for VocalPatch. Inject this DLL into a running
 * singing-synthesis editor (CeVIO / Synthesizer V Studio / VoiSona / Piapro /
 * OpenUtau) and it starts the .NET Framework CLR in-process, then calls
 *
 *     VocalPatch.Editor.Bootstrap.Run(string)      // VocalPatch.Editor.dll
 *
 * The assembly is looked up beside this DLL, or in the directory named by the
 * environment variable VOCALPATCH_EDITOR_DIR. Pass VOCALPATCH_PROFILE to force a
 * profile id (e.g. "synthv"); otherwise the runtime detects the host by process
 * name.
 *
 * WHY THIS FILE HAS NO #include
 * ----------------------------
 * It is deliberately header-free so that it builds with any C compiler that can
 * produce a Windows DLL - including TinyCC, whose bundled headers are too thin to
 * declare WideCharToMultiByte/CP_ACP. Everything it needs is declared below:
 * the handful of kernel32 imports, the CLR hosting GUIDs and vtable slots (copied
 * from metahost.h), and the <windows.h> types it would otherwise use. It also uses
 * no wide string literals, because wchar_t width varies between compilers; wide
 * strings are built at runtime from narrow ones.
 *
 * Consequences: no mscoree.lib, no Windows SDK metahost.h, no C runtime.
 *
 * Build (pick any):
 *   TinyCC : tcc -shared -O2 -o vocalpatch_loader.dll vocalpatch_loader.c -lkernel32
 *   MSVC   : cl /nologo /LD /O2 /Fe:vocalpatch_loader.dll vocalpatch_loader.c
 *   Zig    : zig cc -target x86_64-windows-gnu -shared -O2 -o vocalpatch_loader.dll vocalpatch_loader.c
 *   MinGW  : gcc -shared -O2 -o vocalpatch_loader.dll vocalpatch_loader.c -lkernel32
 *
 *   For 32-bit hosts (older VOCALOID4 / Piapro builds) build a 32-bit loader and
 *   inject it with a 32-bit vpatch-inject: bitness must match the host exactly.
 *
 * Diagnostics: see %TEMP%\vocalpatch-loader.log.
 */

/* ------------------------------------------------------------------ */
/* <windows.h> replacement: just what this file uses                   */
/* ------------------------------------------------------------------ */

typedef void          *vp_handle;
typedef unsigned int   vp_dword;
typedef int            vp_bool;
typedef long           vp_long;
typedef long           vp_hresult;
typedef unsigned short vp_wchar;
typedef unsigned char  vp_u8;

#define VP_TRUE 1
#define VP_FALSE 0
#define VP_OK2(hr) (((vp_hresult)(hr)) >= 0)
#define VP_INVALID_HANDLE ((vp_handle)0)
#define VP_MAX_PATH 260
#define VP_DLL_PROCESS_ATTACH 1

#define VP_FILE_APPEND_DATA 0x00000004u
#define VP_FILE_SHARE_READ 0x00000001u
#define VP_FILE_SHARE_WRITE 0x00000002u
#define VP_OPEN_ALWAYS 4u
#define VP_FILE_ATTRIBUTE_NORMAL 0x00000080u
#define VP_FILE_BEGIN 0u
#define VP_FILE_END 2u
#define VP_INVALID_FILE_ATTRIBUTES 0xFFFFFFFFu
#define VP_DEFAULT_STACK 0u

/* x64 ignores calling conventions; 32-bit needs stdcall to match the Win32 ABI.
   MSVC spells it __stdcall; GCC, clang and TinyCC all accept the attribute form
   (TinyCC 0.9.27 rejects the __stdcall keyword, so the attribute is the portable
   spelling here). */
#if defined(_WIN64) || defined(__x86_64__) || defined(_M_X64) || defined(__amd64__)
#define VP_API
#elif defined(_MSC_VER)
#define VP_API __stdcall
#else
#define VP_API __attribute__((stdcall))
#endif

extern vp_handle VP_API LoadLibraryW(const vp_wchar *name);
extern void     *VP_API GetProcAddress(vp_handle module, const char *name);
extern vp_dword  VP_API GetModuleFileNameW(vp_handle module, vp_wchar *buffer, vp_dword size);
extern vp_dword  VP_API GetFileAttributesW(const vp_wchar *path);
extern vp_dword  VP_API GetEnvironmentVariableW(const vp_wchar *name, vp_wchar *buffer, vp_dword size);
extern vp_handle VP_API CreateFileA(const char *path, vp_dword access, vp_dword share,
                                    void *attributes, vp_dword disposition,
                                    vp_dword flags, vp_handle template_file);
extern vp_bool   VP_API WriteFile(vp_handle file, const void *buffer, vp_dword size,
                                  vp_dword *written, void *overlapped);
extern vp_dword  VP_API SetFilePointer(vp_handle file, vp_long distance, vp_long *high, vp_dword method);
extern vp_bool   VP_API CloseHandle(vp_handle handle);
extern vp_handle VP_API CreateThread(void *attributes, vp_dword stack_size,
                                     void *start, void *parameter,
                                     vp_dword flags, vp_dword *thread_id);
extern vp_bool   VP_API DisableThreadLibraryCalls(vp_handle module);
extern vp_dword  VP_API GetTempPathA(vp_dword size, char *buffer);

/* ------------------------------------------------------------------ */
/* CLR hosting contracts (values straight out of metahost.h)           */
/* ------------------------------------------------------------------ */

typedef struct vp_guid { vp_dword d1; vp_wchar d2; vp_wchar d3; vp_u8 d4[8]; } vp_guid;
typedef const vp_guid *vp_refiid;

static const vp_guid k_clsid_clr_meta_host = {
    0x9280188du, 0x0e8eu, 0x4867u, { 0xb3, 0x0c, 0x7f, 0xa8, 0x38, 0x84, 0xe8, 0xde } };
static const vp_guid k_iid_iclr_meta_host = {
    0xd332db9eu, 0xb9b3u, 0x4125u, { 0x82, 0x07, 0xa1, 0x48, 0x84, 0xf5, 0x32, 0x16 } };
static const vp_guid k_iid_iclr_runtime_info = {
    0xbd39d1d2u, 0xba2fu, 0x486au, { 0x89, 0xb0, 0xb4, 0xb0, 0xcb, 0x46, 0x68, 0x91 } };
static const vp_guid k_clsid_clr_runtime_host = {
    0x90f1a06eu, 0x7712u, 0x4762u, { 0x86, 0xb5, 0x7a, 0x5e, 0xba, 0x6b, 0xdb, 0x02 } };
static const vp_guid k_iid_iclr_runtime_host = {
    0x90f1a06cu, 0x7712u, 0x4762u, { 0x86, 0xb5, 0x7a, 0x5e, 0xba, 0x6b, 0xdb, 0x02 } };

/* IUnknown:        0 QueryInterface, 1 AddRef, 2 Release
 *
 * ICLRMetaHost (metahost.h):
 *   3 GetRuntime  4 GetVersionFromFile  5 EnumerateInstalledRuntimes
 *   6 EnumerateLoadedRuntimes  7 RequestRuntimeLoadedNotification
 *   8 QueryLegacyV2RuntimeBinding  9 ExitProcess
 *
 * ICLRRuntimeInfo (metahost.h):
 *   3 GetVersionString  4 GetRuntimeDirectory  5 IsLoaded  6 LoadErrorString
 *   7 LoadLibrary  8 GetProcAddress  9 GetInterface  10 IsLoadable
 *   11 SetDefaultStartupFlags  12 GetDefaultStartupFlags
 *   13 BindAsLegacyV2Runtime  14 IsStarted
 *
 * ICLRRuntimeHost (mscoree.h):
 *   3 Start  4 Stop  5 SetHostControl  6 GetCLRControl  7 UnloadAppDomain
 *   8 ExecuteInAppDomain  9 GetCurrentAppDomainId  10 ExecuteApplication
 *   11 ExecuteInDefaultAppDomain
 *
 * IEnumUnknown:    3 Next, 4 Skip, 5 Reset, 6 Clone
 *
 * Slot numbers verified against the real header text, not recalled:
 * microsoft/win32metadata generation/WinSDK/RecompiledIdlHeaders/um/{metahost,mscoree}.h
 */
#define VP_SLOT_METAHOST_GET_RUNTIME 3
#define VP_SLOT_METAHOST_ENUMERATE 5
#define VP_SLOT_RUNTIMEINFO_GET_INTERFACE 9
#define VP_SLOT_RUNTIMEHOST_START 3
#define VP_SLOT_RUNTIMEHOST_EXECUTE_DEFAULT_APPDOMAIN 11
#define VP_SLOT_ENUM_NEXT 3
typedef vp_hresult (VP_API *vp_query_interface_fn)(void *self, vp_refiid iid, void **out);
typedef vp_dword   (VP_API *vp_release_fn)(void *self);
typedef vp_hresult (VP_API *vp_get_runtime_fn)(void *self, const vp_wchar *version,
                                              vp_refiid iid, void **out);
typedef vp_hresult (VP_API *vp_enumerate_runtimes_fn)(void *self, void **enumerator);
typedef vp_hresult (VP_API *vp_get_interface_fn)(void *self, const vp_guid *clsid,
                                                vp_refiid iid, void **out);
typedef vp_hresult (VP_API *vp_start_fn)(void *self);
typedef vp_hresult (VP_API *vp_execute_fn)(void *self, const vp_wchar *assembly,
                                          const vp_wchar *type_name, const vp_wchar *method_name,
                                          const vp_wchar *argument, vp_dword *return_value);
typedef vp_hresult (VP_API *vp_enum_next_fn)(void *self, vp_dword count, void **items,
                                            vp_dword *fetched);
typedef vp_hresult (VP_API *vp_clr_create_instance_fn)(const vp_guid *clsid, vp_refiid iid,
                                                      void **out);

typedef struct vp_iface { void **vtable; } vp_iface;

static void *vp_slot(void *object, int index)
{
    return ((vp_iface *)object)->vtable[index];
}

static vp_dword vp_release(void *object)
{
    return ((vp_release_fn)vp_slot(object, 2))(object);
}

static vp_hresult vp_query_interface(void *object, vp_refiid iid, void **out)
{
    return ((vp_query_interface_fn)vp_slot(object, 0))(object, iid, out);
}

/* ------------------------------------------------------------------ */
/* Tiny string helpers (no CRT, no wide literals)                      */
/* ------------------------------------------------------------------ */

static vp_dword vp_strlen(const char *text)
{
    vp_dword n = 0;
    while (text[n] != '\0') n++;
    return n;
}

static vp_dword vp_wlen(const vp_wchar *text)
{
    vp_dword n = 0;
    while (text[n] != 0) n++;
    return n;
}

/* ASCII/ANSI -> UTF-16. Non-ASCII bytes become '?'; loader paths are ASCII. */
static void vp_widen(const char *source, vp_wchar *destination, vp_dword capacity)
{
    vp_dword i = 0;
    if (capacity == 0) return;
    while (source[i] != '\0' && i + 1 < capacity)
    {
        destination[i] = (vp_wchar)(vp_u8)source[i];
        i++;
    }
    destination[i] = 0;
}

static void vp_wappend(const vp_wchar *suffix, vp_wchar *buffer, vp_dword capacity)
{
    vp_dword length = vp_wlen(buffer);
    vp_dword i = 0;
    while (suffix[i] != 0 && length + i + 1 < capacity)
    {
        buffer[length + i] = suffix[i];
        i++;
    }
    buffer[length + i] = 0;
}

static void vp_wappend_ascii(const char *suffix, vp_wchar *buffer, vp_dword capacity)
{
    vp_dword length = vp_wlen(buffer);
    vp_dword i = 0;
    while (suffix[i] != '\0' && length + i + 1 < capacity)
    {
        buffer[length + i] = (vp_wchar)(vp_u8)suffix[i];
        i++;
    }
    buffer[length + i] = 0;
}

static void vp_narrow(const vp_wchar *source, char *destination, vp_dword capacity)
{
    vp_dword i = 0;
    if (capacity == 0) return;
    while (source[i] != 0 && i + 1 < capacity)
    {
        destination[i] = (source[i] < 0x80) ? (char)source[i] : '?';
        i++;
    }
    destination[i] = '\0';
}

/* ------------------------------------------------------------------ */
/* Host allow-list                                                     */
/*                                                                     */
/* AppInit_DLLs loads this DLL into every process that links user32, so */
/* the loader has to be able to say "not my host" and leave before it   */
/* touches the CLR. The list mirrors EditorProfile's process names:     */
/* prefix match, case-insensitive, extension stripped.                  */
/*                                                                     */
/* Each entry has to be a prefix of the real *process* name, which is   */
/* not always the product name - "NetEase Cloud Music XStudio" is why   */
/* this says "netease" and not "xstudio". A wrong entry fails silently  */
/* on purpose (a non-host leaves before it opens the log), so check any */
/* new host with a real launch rather than by reading this list.        */
/* ------------------------------------------------------------------ */

static const char *k_host_prefixes[] = {
    "cevio", "synthv", "voisona", "openutau", "piapro", "vocaloid",
    "synthutau", "utauv", "ace studio", "netease", "hotkey_host"
};
#define VP_HOST_COUNT ((int)(sizeof(k_host_prefixes) / sizeof(k_host_prefixes[0])))

static int vp_lower(int c)
{
    return (c >= 'A' && c <= 'Z') ? c + 32 : c;
}

static int vp_starts_with(const char *text, const char *prefix)
{
    int i = 0;
    while (prefix[i] != '\0')
    {
        if (text[i] == '\0') return 0;
        if (vp_lower((unsigned char)text[i]) != vp_lower((unsigned char)prefix[i])) return 0;
        i++;
    }
    return 1;
}

/* Bypass for probes and the load_host self-test: set VOCALPATCH_FORCE_HOST=1.
   Extra names without a rebuild: VOCALPATCH_HOSTS=a;b (or comma separated). */
static int host_is_allowed(void)
{
    vp_wchar path[VP_MAX_PATH + 1];
    vp_wchar variable[24];
    vp_wchar value[8];
    char ansi[VP_MAX_PATH + 1];
    const char *name;
    int i, start = 0, dot = -1;
    vp_dword length;

    vp_widen("VOCALPATCH_FORCE_HOST", variable, sizeof(variable) / sizeof(vp_wchar));
    if (GetEnvironmentVariableW(variable, value, 8) > 0 && value[0] != 0) return 1;

    length = GetModuleFileNameW(0, path, VP_MAX_PATH);
    if (length == 0 || length >= VP_MAX_PATH) return 0;

    vp_narrow(path, ansi, sizeof(ansi));
    for (i = 0; ansi[i] != '\0'; i++)
    {
        if (ansi[i] == '\\' || ansi[i] == '/') { start = i + 1; dot = -1; }
        else if (ansi[i] == '.') dot = i;
    }
    name = ansi + start;
    if (dot > start) ansi[dot] = '\0';

    for (i = 0; i < VP_HOST_COUNT; i++)
        if (vp_starts_with(name, k_host_prefixes[i])) return 1;

    vp_widen("VOCALPATCH_HOSTS", variable, sizeof(variable) / sizeof(vp_wchar));
    {
        vp_wchar hosts[VP_MAX_PATH + 1];
        if (GetEnvironmentVariableW(variable, hosts, VP_MAX_PATH) > 0)
        {
            char list[VP_MAX_PATH + 1];
            char item[64];
            int n = 0;
            vp_narrow(hosts, list, sizeof(list));
            for (i = 0; ; i++)
            {
                char ch = list[i];
                if (ch == ';' || ch == ',' || ch == '\0')
                {
                    item[n] = '\0';
                    if (n > 0 && vp_starts_with(name, item)) return 1;
                    n = 0;
                    if (ch == '\0') break;
                }
                else if (n < (int)sizeof(item) - 1) item[n++] = ch;
            }
        }
    }
    return 0;
}

/* ------------------------------------------------------------------ */
/* Logging: %TEMP%\vocalpatch-loader.log                              */
/* ------------------------------------------------------------------ */

static vp_handle g_log = VP_INVALID_HANDLE;

static void log_write(const char *text)
{
    vp_dword written = 0;
    if (g_log == VP_INVALID_HANDLE || text == 0) return;
    WriteFile(g_log, text, vp_strlen(text), &written, 0);
}

static void log_line(const char *text)
{
    log_write(text);
    log_write("\r\n");
}

static void log_hex(const char *label, vp_dword value)
{
    static const char digits[] = "0123456789abcdef";
    char line[16];
    int i = 0;
    int shift;
    log_write(label);
    log_write(" = 0x");
    for (shift = 28; shift >= 0; shift -= 4)
    {
        line[i++] = digits[(value >> shift) & 0xFu];
    }
    line[i] = '\0';
    log_line(line);
}

static void log_path(const char *label, const vp_wchar *path)
{
    char ansi[VP_MAX_PATH + 1];
    vp_narrow(path, ansi, sizeof(ansi));
    log_write(label);
    log_line(ansi);
}

static void log_open(void)
{
    char path[VP_MAX_PATH + 1];
    vp_dword length = GetTempPathA(VP_MAX_PATH, path);
    const char *name = "vocalpatch-loader.log";
    vp_dword i = 0;

    if (length == 0 || length > VP_MAX_PATH - 24) return;
    while (name[i] != '\0' && length + i < VP_MAX_PATH)
    {
        path[length + i] = name[i];
        i++;
    }
    path[length + i] = '\0';

    g_log = CreateFileA(path, VP_FILE_APPEND_DATA, VP_FILE_SHARE_READ | VP_FILE_SHARE_WRITE,
                        0, VP_OPEN_ALWAYS, VP_FILE_ATTRIBUTE_NORMAL, VP_INVALID_HANDLE);
    if (g_log == VP_INVALID_HANDLE) return;
    SetFilePointer(g_log, 0, 0, VP_FILE_END);
}

/* ------------------------------------------------------------------ */
/* Hosting                                                             */
/* ------------------------------------------------------------------ */

static int file_exists(const vp_wchar *path)
{
    return GetFileAttributesW(path) != VP_INVALID_FILE_ATTRIBUTES;
}

/* Directory containing the module we were loaded as. */
static void module_directory(vp_handle module, vp_wchar *out)
{
    vp_dword length = GetModuleFileNameW(module, out, VP_MAX_PATH);
    while (length > 0)
    {
        if (out[length - 1] == L'\\' || out[length - 1] == L'/')
        {
            out[length - 1] = 0;
            return;
        }
        length--;
    }
    out[0] = 0;
}

/* VOCALPATCH_EDITOR_DIR wins; otherwise the assembly must sit beside this DLL. */
static int resolve_assembly(vp_handle module, vp_wchar *out)
{
    /* Long enough for "VOCALPATCH_EDITOR_DIR" plus its terminator. */
    vp_wchar variable[40];
    vp_wchar directory[VP_MAX_PATH + 1];

    vp_widen("VOCALPATCH_EDITOR_DIR", variable, sizeof(variable) / sizeof(vp_wchar));
    if (GetEnvironmentVariableW(variable, directory, VP_MAX_PATH) > 0)
    {
        vp_wappend_ascii("\\VocalPatch.Editor.dll", directory, VP_MAX_PATH);
        if (file_exists(directory))
        {
            vp_widen("", out, VP_MAX_PATH);
            vp_wappend(directory, out, VP_MAX_PATH);
            return 1;
        }
        log_path("VOCALPATCH_EDITOR_DIR has no assembly: ", directory);
    }

    module_directory(module, directory);
    vp_wappend_ascii("\\VocalPatch.Editor.dll", directory, VP_MAX_PATH);
    {
        vp_dword i = 0;
        while (directory[i] != 0 && i < VP_MAX_PATH) { out[i] = directory[i]; i++; }
        out[i] = 0;
    }
    return file_exists(out);
}

/* Last resort: walk the installed runtimes and take the first ICLRRuntimeInfo. */
static void *runtime_by_enumeration(void *meta_host)
{
    void *enumerator = 0;
    vp_enumerate_runtimes_fn enumerate =
        (vp_enumerate_runtimes_fn)vp_slot(meta_host, VP_SLOT_METAHOST_ENUMERATE);

    if (!VP_OK2(enumerate(meta_host, &enumerator)) || enumerator == 0) return 0;

    for (;;)
    {
        void *item = 0;
        vp_dword fetched = 0;
        vp_enum_next_fn next = (vp_enum_next_fn)vp_slot(enumerator, VP_SLOT_ENUM_NEXT);

        if (!VP_OK2(next(enumerator, 1, &item, &fetched)) || fetched == 0 || item == 0) break;

        {
            void *info = 0;
            if (VP_OK2(vp_query_interface(item, &k_iid_iclr_runtime_info, &info)) && info != 0)
            {
                vp_release(enumerator);
                return info;
            }
            /* Some enumerators hand back the interface itself without a working QI. */
            vp_release(enumerator);
            return item;
        }
    }

    vp_release(enumerator);
    return 0;
}

static int host_and_start(vp_handle module)
{
    vp_wchar assembly[VP_MAX_PATH + 1];
    vp_wchar argument[VP_MAX_PATH + 1];
    vp_wchar mscoree_name[16];
    vp_wchar version[16];
    vp_wchar type_name[64];
    vp_wchar method_name[8];
    vp_wchar profile_variable[24];
    vp_handle mscoree;
    vp_clr_create_instance_fn create_instance;
    void *meta_host = 0;
    vp_hresult hr;

    if (!resolve_assembly(module, assembly))
    {
        log_line("VocalPatch.Editor.dll not found beside the loader");
        return 1;
    }
    log_path("assembly: ", assembly);

    argument[0] = 0;
    vp_widen("VOCALPATCH_PROFILE", profile_variable, sizeof(profile_variable) / sizeof(vp_wchar));
    if (GetEnvironmentVariableW(profile_variable, argument, VP_MAX_PATH) == 0)
    {
        argument[0] = 0;
    }

    vp_widen("mscoree.dll", mscoree_name, sizeof(mscoree_name) / sizeof(vp_wchar));
    mscoree = LoadLibraryW(mscoree_name);
    if (mscoree == 0)
    {
        log_line("LoadLibraryW(mscoree.dll) failed");
        return 1;
    }

    create_instance = (vp_clr_create_instance_fn)(void *)GetProcAddress(mscoree, "CLRCreateInstance");
    if (create_instance == 0)
    {
        log_line("mscoree!CLRCreateInstance missing (no .NET Framework 4+)");
        return 1;
    }

    hr = create_instance(&k_clsid_clr_meta_host, &k_iid_iclr_meta_host, &meta_host);
    log_hex("CLRCreateInstance", (vp_dword)hr);
    if (!VP_OK2(hr) || meta_host == 0) return 1;

    {
        void *runtime_info = 0;
        vp_get_runtime_fn get_runtime = (vp_get_runtime_fn)vp_slot(meta_host, VP_SLOT_METAHOST_GET_RUNTIME);
        const char *versions[2];
        int i;

        versions[0] = "v4.0.30319";
        versions[1] = "v2.0.50727";
        for (i = 0; i < 2; i++)
        {
            vp_widen(versions[i], version, sizeof(version) / sizeof(vp_wchar));
            hr = get_runtime(meta_host, version, &k_iid_iclr_runtime_info, &runtime_info);
            log_hex("GetRuntime", (vp_dword)hr);
            if (VP_OK2(hr) && runtime_info != 0) break;
            runtime_info = 0;
        }

        if (runtime_info == 0)
        {
            log_line("falling back to EnumerateInstalledRuntimes");
            runtime_info = runtime_by_enumeration(meta_host);
        }

        vp_release(meta_host);

        if (runtime_info == 0)
        {
            log_line("no CLR runtime available in this process");
            return 1;
        }

        {
            void *runtime_host = 0;
            vp_get_interface_fn get_interface = (vp_get_interface_fn)vp_slot(runtime_info, VP_SLOT_RUNTIMEINFO_GET_INTERFACE);

            hr = get_interface(runtime_info, &k_clsid_clr_runtime_host,
                               &k_iid_iclr_runtime_host, &runtime_host);
            log_hex("GetInterface(ICLRRuntimeHost)", (vp_dword)hr);
            vp_release(runtime_info);

            if (!VP_OK2(hr) || runtime_host == 0)
            {
                log_line("could not obtain ICLRRuntimeHost");
                return 1;
            }

            {
                vp_dword managed_result = 0;
                vp_execute_fn execute = (vp_execute_fn)vp_slot(runtime_host, VP_SLOT_RUNTIMEHOST_EXECUTE_DEFAULT_APPDOMAIN);
                vp_start_fn start = (vp_start_fn)vp_slot(runtime_host, VP_SLOT_RUNTIMEHOST_START);

                /* Hosting through metahost requires an explicit start; S_FALSE
                   just means the runtime was already up (e.g. the host is .NET). */
                hr = start(runtime_host);
                log_hex("Start", (vp_dword)hr);

                vp_widen("VocalPatch.Editor.Bootstrap", type_name, sizeof(type_name) / sizeof(vp_wchar));
                vp_widen("Run", method_name, sizeof(method_name) / sizeof(vp_wchar));

                /* ExecuteInDefaultAppDomain starts the runtime if it is not running. */
                hr = execute(runtime_host, assembly, type_name, method_name, argument, &managed_result);
                log_hex("ExecuteInDefaultAppDomain", (vp_dword)hr);
                log_hex("managed return value", managed_result);
                vp_release(runtime_host);
                return VP_OK2(hr) ? 0 : 1;
            }
        }
    }
}

/* ------------------------------------------------------------------ */
/* Entry points                                                        */
/* ------------------------------------------------------------------ */

static vp_dword VP_API bootstrap_thread(void *parameter)
{
    int code;
    /* Loaded into every process by AppInit_DLLs: leave immediately, and log
       nothing, unless this process is one of the target editors. */
    if (!host_is_allowed()) return 0;
    log_open();
    log_line("--- vocalpatch_loader attached ---");
    code = host_and_start((vp_handle)parameter);
    log_line(code == 0 ? "loader ok" : "loader failed");
    if (g_log != VP_INVALID_HANDLE) CloseHandle(g_log);
    return 0;
}

int VP_API DllMain(vp_handle self, vp_dword reason, void *reserved)
{
    (void)reserved;
    if (reason == VP_DLL_PROCESS_ATTACH)
    {
        vp_handle thread;
        DisableThreadLibraryCalls(self);
        /* Never start the CLR inside DllMain: the loader lock is held here. */
        thread = CreateThread(0, VP_DEFAULT_STACK, (void *)&bootstrap_thread, (void *)self, 0, 0);
        if (thread != 0) CloseHandle(thread);
    }
    return VP_TRUE;
}
