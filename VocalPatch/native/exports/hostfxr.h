/*
 * exports/hostfxr.h — the hostfxr.dll entry points the VocalPatch shim exports.
 *
 * OpenUtau is a self-contained .NET app. Its apphost (OpenUtau.exe) computes an
 * absolute path to hostfxr.dll inside its own folder and calls LoadLibraryEx on
 * that exact file, so replacing that file with this shim gets our DllMain run
 * before the CLR starts. Because the real hostfxr is renamed to hostfxr.orig.dll
 * beside it, the engine forwards there rather than to the machine-wide copy —
 * a self-contained app must keep the host it shipped with.
 *
 * char_t is wchar_t on Windows. Every parameter is an int or a pointer, so
 * forwarding is exact.
 */

typedef int  vp_i32;
typedef long long vp_i64;

/* --- classic hosting entry points --- */
VP_FWD(vp_i32, hostfxr_main, (const vp_i32 argc, const vp_word **argv), (argc, argv), return 0)
VP_FWD(vp_i32, hostfxr_main_startupinfo,
       (const vp_i32 argc, const vp_word **argv, const vp_word *host_path,
        const vp_word *dotnet_root, const vp_word *app_path),
       (argc, argv, host_path, dotnet_root, app_path), return 0)
VP_FWD(vp_i32, hostfxr_main_bundle_startupinfo,
       (const vp_i32 argc, const vp_word **argv, const vp_word *host_path,
        const vp_word *dotnet_root, const vp_word *app_path, vp_i64 bundle_header_offset),
       (argc, argv, host_path, dotnet_root, app_path, bundle_header_offset), return 0)

/* --- component / runtime-config hosting --- */
VP_FWD(vp_i32, hostfxr_initialize_for_runtime_config,
       (const vp_word *runtime_config_path, const void *parameters, void **host_context_handle),
       (runtime_config_path, parameters, host_context_handle), return 0)
VP_FWD(vp_i32, hostfxr_initialize_for_dotnet_command_line,
       (const vp_i32 argc, const vp_word **argv, const void *parameters, void **host_context_handle),
       (argc, argv, parameters, host_context_handle), return 0)
VP_FWD(vp_i32, hostfxr_run_app, (const void *host_context_handle), (host_context_handle), return 0)
VP_FWD(vp_i32, hostfxr_get_runtime_delegate,
       (const void *host_context_handle, vp_i32 type, void **delegate),
       (host_context_handle, type, delegate), return 0)
VP_FWD(vp_i32, hostfxr_get_runtime_property_value,
       (const void *host_context_handle, const vp_word *name, const vp_word **value),
       (host_context_handle, name, value), return 0)
VP_FWD(vp_i32, hostfxr_set_runtime_property_value,
       (const void *host_context_handle, const vp_word *name, const vp_word *value),
       (host_context_handle, name, value), return 0)
VP_FWD(vp_i32, hostfxr_get_runtime_properties,
       (const void *host_context_handle, vp_ptr64 *count, const vp_word **keys, const vp_word **values),
       (host_context_handle, count, keys, values), return 0)
VP_FWD(vp_i32, hostfxr_close, (const void *host_context_handle), (host_context_handle), return 0)

/* --- diagnostics / SDK resolution --- */
VP_FWD(vp_i32, hostfxr_set_error_writer, (void *error_writer), (error_writer), return 0)
VP_FWD(vp_i32, hostfxr_resolve_sdk,
       (const vp_word *exe_dir, const vp_word *working_dir, vp_word *result),
       (exe_dir, working_dir, result), return 0)
VP_FWD(vp_i32, hostfxr_resolve_sdk2,
       (const vp_word *exe_dir, const vp_word *working_dir, vp_i32 flags, void *result),
       (exe_dir, working_dir, flags, result), return 0)
VP_FWD(vp_i32, hostfxr_get_available_sdks, (const vp_i32 argc, const vp_word **argv), (argc, argv), return 0)
VP_FWD(vp_i32, hostfxr_get_native_search_directories,
       (const vp_i32 argc, const vp_word **argv, vp_word *buffer, vp_i32 buffer_size, vp_i32 *required),
       (argc, argv, buffer, buffer_size, required), return 0)
VP_FWD(vp_i32, hostfxr_get_dotnet_environment_info,
       (const vp_word *dotnet_root, void *reserved, void *info),
       (dotnet_root, reserved, info), return 0)
VP_FWD(vp_i32, hostfxr_resolve_frameworks_for_runtime_config,
       (const vp_word *runtime_config_path, const void *parameters, void *result_callback, void *user_data),
       (runtime_config_path, parameters, result_callback, user_data), return 0)
