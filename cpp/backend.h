#ifndef FS_BACKEND_H
#define FS_BACKEND_H

#ifdef _WIN32
#define FS_API extern "C" __declspec(dllexport)
#define FS_CALL __stdcall
#else
#define FS_API extern "C"
#define FS_CALL
#endif

/* Log levels: 0 = info, 1 = warning, 2 = error. */
typedef void (FS_CALL *FSLogCallback)(int level, const char *message);
typedef void (FS_CALL *FSStateCallback)(int state, const char *detail);

enum {
    FS_STATE_IDLE = 0,
    FS_STATE_RUNNING_PROCESS = 1,
    FS_STATE_RUNNING_SERVICE = 2
};

enum {
    FS_TGWS_STATE_IDLE = 0,
    FS_TGWS_STATE_RUNNING = 1
};

enum {
    FS_OK = 0,
    FS_ERR_GENERIC = -1,
    FS_ERR_ALREADY_RUNNING = -2,
    FS_ERR_NEED_ADMIN = -3,
    FS_ERR_SERVICE_INSTALLED = -4,
    FS_ERR_INVALID_ARG = -5,
    FS_ERR_NOT_INITIALIZED = -6
};

/* Callbacks must stay alive until fs_shutdown() is called. */
FS_API int  FS_CALL fs_init(FSLogCallback log_cb, FSStateCallback state_cb);
FS_API void FS_CALL fs_shutdown(void);

/* Privileges / single instance. */
FS_API int  FS_CALL fs_is_admin(void);
FS_API int  FS_CALL fs_request_elevation(void);
FS_API int  FS_CALL fs_acquire_single_instance(void);
FS_API void FS_CALL fs_release_single_instance(void);

/* Real system state. */
FS_API int  FS_CALL fs_get_zapret_state(char *detail, int detail_len);
FS_API int  FS_CALL fs_get_active_service(char *out, int out_len);
FS_API int  FS_CALL fs_get_installed_service(char *out, int out_len);

/* Strategy / service control. */
FS_API int  FS_CALL fs_run_strategy(const wchar_t *dir, const wchar_t *bat);
FS_API int  FS_CALL fs_stop_strategy(void);
FS_API int  FS_CALL fs_install_service(const wchar_t *dir, const wchar_t *bat);
FS_API int  FS_CALL fs_remove_service(void);
FS_API int  FS_CALL fs_start_service(void);
FS_API int  FS_CALL fs_stop_service(void);

/* Diagnostics: parsed winws/winws2.exe command line for a strategy. */
FS_API int  FS_CALL fs_get_strategy_command(const wchar_t *dir,
                                            const wchar_t *bat,
                                            char *out, int out_len);

/* Human readable explanation of the last failed call. */
/* TG WS Proxy (Flowseal): manage the official Windows executable ourselves.
 * dir is the root with downloaded versions; exe is the selected executable.
 * The executable must already have TgWsProxy_data\config.json next to it. */
FS_API int  FS_CALL fs_tgws_start(const wchar_t *exe, const wchar_t *dir);
FS_API int  FS_CALL fs_tgws_stop(const wchar_t *dir);

/* Returns FS_TGWS_STATE_IDLE or FS_TGWS_STATE_RUNNING and writes a short
 * detail (e.g. "PID 1234") into out. */
FS_API int  FS_CALL fs_tgws_state(const wchar_t *dir, char *out, int out_len);

FS_API int  FS_CALL fs_last_error(char *out, int out_len);

#endif /* FS_BACKEND_H */
