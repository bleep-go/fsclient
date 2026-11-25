# -*- coding: utf-8 -*-
"""Qt-compatible wrapper around cpp/fs_backend.dll.

Drop-in replacement for backend_mock.Backend: keeps every signal and method
used by ui_main.py, but delegates process/service control to native code.
"""
import ctypes
import json
import os
import shutil
import subprocess
import sys
import threading
import urllib.request
import weakref
import zipfile

from PyQt6.QtCore import QObject, pyqtSignal

HERE = os.path.dirname(os.path.abspath(__file__))
APP_DIR = (
    os.path.dirname(sys.executable)
    if getattr(sys, "frozen", False)
    else HERE
)
DLL_PATH = os.path.join(HERE, "cpp", "fs_backend.dll")

RELEASES_API = "https://api.github.com/repos/Flowseal/zapret-discord-youtube/releases"
RELEASES_DIR = os.path.join(APP_DIR, "releases")
PLUGINS_DIR = os.path.join(APP_DIR, "plugins")
PLUGIN_EXTS = ('.py', '.bat', '.cmd', '.ps1', '.exe')

FALLBACK_RELEASES = [
    {"tag": "v1.10.4", "url": None, "is_latest": True},
    {"tag": "v1.10.3", "url": None, "is_latest": False},
    {"tag": "v1.10.2", "url": None, "is_latest": False},
    {"tag": "v1.10.1", "url": None, "is_latest": False},
    {"tag": "v1.10.0", "url": None, "is_latest": False},
]

FS_OK = 0
FS_ERR_NEED_ADMIN = -3
FS_ERR_ALREADY_RUNNING = -2
FS_ERR_SERVICE_INSTALLED = -4

FS_STATE_IDLE = 0
FS_STATE_RUNNING_PROCESS = 1
FS_STATE_RUNNING_SERVICE = 2

LOG_CB = ctypes.WINFUNCTYPE(None, ctypes.c_int, ctypes.c_char_p)
STATE_CB = ctypes.WINFUNCTYPE(None, ctypes.c_int, ctypes.c_char_p)

_instances = weakref.WeakSet()
_log_cb_holder = None
_state_cb_holder = None


def _decode(value):
    if not value:
        return ""
    if isinstance(value, bytes):
        return value.decode("utf-8", errors="replace")
    return str(value)


if not os.path.isfile(DLL_PATH):
    raise RuntimeError(
        "Не найден cpp/fs_backend.dll. Запустите build.bat перед запуском лаунчера."
    )

_dll = ctypes.WinDLL(DLL_PATH)

_dll.fs_init.argtypes = [LOG_CB, STATE_CB]
_dll.fs_init.restype = ctypes.c_int
_dll.fs_shutdown.argtypes = []
_dll.fs_shutdown.restype = None
_dll.fs_is_admin.argtypes = []
_dll.fs_is_admin.restype = ctypes.c_int
_dll.fs_request_elevation.argtypes = []
_dll.fs_request_elevation.restype = ctypes.c_int
_dll.fs_acquire_single_instance.argtypes = []
_dll.fs_acquire_single_instance.restype = ctypes.c_int
_dll.fs_release_single_instance.argtypes = []
_dll.fs_release_single_instance.restype = None
_dll.fs_get_zapret_state.argtypes = [ctypes.c_char_p, ctypes.c_int]
_dll.fs_get_zapret_state.restype = ctypes.c_int
_dll.fs_get_active_service.argtypes = [ctypes.c_char_p, ctypes.c_int]
_dll.fs_get_active_service.restype = ctypes.c_int
_dll.fs_get_installed_service.argtypes = [ctypes.c_char_p, ctypes.c_int]
_dll.fs_get_installed_service.restype = ctypes.c_int
_dll.fs_run_strategy.argtypes = [ctypes.c_wchar_p, ctypes.c_wchar_p]
_dll.fs_run_strategy.restype = ctypes.c_int
_dll.fs_get_strategy_command.argtypes = [
    ctypes.c_wchar_p, ctypes.c_wchar_p, ctypes.c_char_p, ctypes.c_int
]
_dll.fs_get_strategy_command.restype = ctypes.c_int
_dll.fs_stop_strategy.argtypes = []
_dll.fs_stop_strategy.restype = ctypes.c_int
_dll.fs_install_service.argtypes = [ctypes.c_wchar_p, ctypes.c_wchar_p]
_dll.fs_install_service.restype = ctypes.c_int
_dll.fs_remove_service.argtypes = []
_dll.fs_remove_service.restype = ctypes.c_int
_dll.fs_start_service.argtypes = []
_dll.fs_start_service.restype = ctypes.c_int
_dll.fs_stop_service.argtypes = []
_dll.fs_stop_service.restype = ctypes.c_int
_dll.fs_last_error.argtypes = [ctypes.c_char_p, ctypes.c_int]
_dll.fs_last_error.restype = ctypes.c_int


def _native_log(level, message):
    text = _decode(message)
    prefix = {1: "⚠ ", 2: "✖ "}.get(level, "")
    for instance in list(_instances):
        try:
            instance.log.emit(prefix + text)
        except RuntimeError:
            pass


def _native_state(state, detail):
    text = _decode(detail)
    for instance in list(_instances):
        try:
            instance.native_state = int(state)
            instance.native_detail = text
            instance.state_changed.emit(int(state), text)
        except RuntimeError:
            pass


_log_cb_holder = LOG_CB(_native_log)
_state_cb_holder = STATE_CB(_native_state)
_dll.fs_init(_log_cb_holder, _state_cb_holder)


def last_error():
    buffer = ctypes.create_string_buffer(1024)
    _dll.fs_last_error(buffer, len(buffer))
    return _decode(buffer.value)


def is_admin():
    return bool(_dll.fs_is_admin())


def request_elevation():
    release_single_instance()
    ok = _dll.fs_request_elevation() == FS_OK
    if not ok:
        # UAC was cancelled: keep protecting this instance.
        acquire_single_instance()
    return ok, ("" if ok else last_error())


def acquire_single_instance():
    return _dll.fs_acquire_single_instance() == FS_OK, last_error()


def release_single_instance():
    _dll.fs_release_single_instance()


def shutdown_native():
    _dll.fs_shutdown()


class DownloadWorker(threading.Thread):
    """Kept as a plain thread; Qt signals are connected from the GUI thread."""

    def __init__(self, tag, url, backend):
        super().__init__(daemon=True)
        self.tag = tag
        self.url = url
        self.backend = backend

    def run(self):
        try:
            os.makedirs(RELEASES_DIR, exist_ok=True)
            target_dir = os.path.join(RELEASES_DIR, self.tag)
            zip_path = os.path.join(RELEASES_DIR, f"{self.tag}.zip")

            self.backend.download_progress.emit(0, "Скачивание архива...")
            req = urllib.request.Request(
                self.url, headers={"User-Agent": "fsclient/1.0"}
            )
            with urllib.request.urlopen(req, timeout=60) as resp:
                total = int(resp.headers.get("Content-Length", "0") or "0")
                downloaded = 0
                with open(zip_path, "wb") as handle:
                    while True:
                        chunk = resp.read(65536)
                        if not chunk:
                            break
                        handle.write(chunk)
                        downloaded += len(chunk)
                        if total > 0:
                            pct = min(90, int(downloaded * 90 / total))
                            self.backend.download_progress.emit(
                                pct, "Скачивание архива..."
                            )

            self.backend.download_progress.emit(92, "Распаковка...")
            if os.path.exists(target_dir):
                shutil.rmtree(target_dir)
            os.makedirs(target_dir, exist_ok=True)
            with zipfile.ZipFile(zip_path, "r") as archive:
                archive.extractall(target_dir)

            entries = [
                e for e in os.listdir(target_dir) if not e.startswith(".")
            ]
            if len(entries) == 1 and os.path.isdir(
                os.path.join(target_dir, entries[0])
            ):
                sub = os.path.join(target_dir, entries[0])
                for name in os.listdir(sub):
                    shutil.move(
                        os.path.join(sub, name),
                        os.path.join(target_dir, name),
                    )
                os.rmdir(sub)

            try:
                os.remove(zip_path)
            except Exception:
                pass

            self.backend.download_progress.emit(100, "Готово")
            self.backend.download_done.emit(self.tag)
        except Exception as exc:
            self.backend.download_failed.emit(str(exc))


class Backend(QObject):
    log = pyqtSignal(str)
    release_list_ready = pyqtSignal(list)
    download_progress = pyqtSignal(int, str)
    download_done = pyqtSignal(str)
    download_failed = pyqtSignal(str)
    service_changed = pyqtSignal(str)
    strategy_started = pyqtSignal(str)
    state_changed = pyqtSignal(int, str)

    def __init__(self, parent=None):
        super().__init__(parent)
        _instances.add(self)
        self.releases = []
        self.installed_service = ""
        self.running_strategy = ""
        self.native_state = FS_STATE_IDLE
        self.native_detail = ""
        self._closed = False
        self._worker = None

        try:
            os.makedirs(PLUGINS_DIR, exist_ok=True)
        except Exception:
            pass

        self.installed_service = self.get_installed_service()
        state, detail = self.get_state()
        self.native_state = state
        self.native_detail = detail

    # ---------- Native state ----------
    def get_state(self):
        buffer = ctypes.create_string_buffer(1024)
        state = int(_dll.fs_get_zapret_state(buffer, len(buffer)))
        detail = _decode(buffer.value)
        return state, detail

    def get_active_service(self):
        buffer = ctypes.create_string_buffer(512)
        if _dll.fs_get_active_service(buffer, len(buffer)) == FS_OK:
            return _decode(buffer.value)
        return ""

    def get_installed_service(self):
        buffer = ctypes.create_string_buffer(512)
        if _dll.fs_get_installed_service(buffer, len(buffer)) == FS_OK:
            return _decode(buffer.value)
        return ""

    def get_strategy_command(self, tag, bat):
        buffer = ctypes.create_string_buffer(8192)
        rc = _dll.fs_get_strategy_command(
            self._release_dir(tag), bat, buffer, len(buffer)
        )
        return _decode(buffer.value) if rc == FS_OK else ""

    def poll_state(self):
        previous = (self.native_state, self.native_detail)
        state, detail = self.get_state()
        installed = self.get_installed_service()
        self.native_state = state
        self.native_detail = detail
        if installed != self.installed_service:
            self.installed_service = installed
            self.service_changed.emit(installed)
        if (state, detail) != previous:
            self.state_changed.emit(state, detail)
        return state, detail

    def is_admin(self):
        return is_admin()

    def request_elevation(self):
        ok, error = request_elevation()
        if ok:
            self.log.emit("Перезапуск с правами администратора...")
        else:
            self.log.emit(f"Не удалось повысить права: {error}")
        return ok

    def close(self):
        if self._closed:
            return
        self._closed = True
        _instances.discard(self)
        shutdown_native()

    # ---------- Releases ----------
    def fetch_releases_async(self):
        threading.Thread(target=self._fetch_releases, daemon=True).start()

    def _fetch_releases(self):
        try:
            req = urllib.request.Request(
                RELEASES_API,
                headers={
                    "User-Agent": "fsclient/1.0",
                    "Accept": "application/vnd.github+json",
                },
            )
            with urllib.request.urlopen(req, timeout=15) as resp:
                data = json.loads(resp.read().decode("utf-8"))
            result = []
            for index, release in enumerate(data[:15]):
                tag = release.get("tag_name", "")
                zip_url = None
                for asset in release.get("assets", []):
                    if asset.get("name", "").endswith(".zip"):
                        zip_url = asset.get("browser_download_url")
                        break
                result.append(
                    {
                        "tag": tag,
                        "url": zip_url,
                        "is_latest": index == 0,
                    }
                )
            if not result:
                raise ValueError("Пустой список релизов")
            self.releases = result
            self.release_list_ready.emit(result)
        except Exception as exc:
            self.log.emit(f"GitHub API недоступен: {exc}")
            self.log.emit("Использую резервный список для демонстрации.")
            self.releases = list(FALLBACK_RELEASES)
            self.release_list_ready.emit(list(FALLBACK_RELEASES))

    def is_downloaded(self, tag):
        if not tag:
            return False
        return os.path.isdir(os.path.join(RELEASES_DIR, tag))

    def list_downloaded_tags(self):
        if not os.path.isdir(RELEASES_DIR):
            return set()
        return set(
            name
            for name in os.listdir(RELEASES_DIR)
            if os.path.isdir(os.path.join(RELEASES_DIR, name))
            and not name.startswith(".")
        )

    def list_strategies(self, tag):
        directory = os.path.join(RELEASES_DIR, tag) if tag else ""
        if not directory or not os.path.isdir(directory):
            return []
        result = []
        try:
            for name in sorted(os.listdir(directory)):
                low = name.lower()
                if low.endswith(".bat") and "service" not in low:
                    result.append(name)
        except Exception:
            pass
        return result

    def _release_dir(self, tag):
        return os.path.abspath(os.path.join(RELEASES_DIR, tag or ""))

    def download_release(self, tag, url):
        if not url:
            self.download_failed.emit("Нет ссылки для скачивания (демо-режим)")
            return
        self._worker = DownloadWorker(tag, url, self)
        self._worker.start()

    # ---------- Native strategy / service ----------
    def run_strategy(self, tag, bat):
        rc = _dll.fs_run_strategy(self._release_dir(tag), bat)
        if rc == FS_OK:
            self.running_strategy = bat
            self.log.emit(f"Запуск стратегии: {bat}")
            self.strategy_started.emit(bat)
            return True

        error = last_error()
        self.log.emit(f"Запуск заблокирован: {error}")
        if rc == FS_ERR_NEED_ADMIN:
            self.log.emit("Запрос прав администратора...")
            self.request_elevation()
        return False

    def stop_strategy(self):
        rc = _dll.fs_stop_strategy()
        if rc == FS_OK:
            self.running_strategy = ""
            self.log.emit("Процесс обхода остановлен")
            self.poll_state()
            return True
        self.log.emit(f"Ошибка остановки: {last_error()}")
        return False

    def install_service(self, tag, bat):
        rc = _dll.fs_install_service(self._release_dir(tag), bat)
        if rc == FS_OK:
            self.installed_service = bat
            self.log.emit(f"Служба установлена: {bat}")
            self.service_changed.emit(bat)
            return True

        error = last_error()
        self.log.emit(f"Не удалось установить службу: {error}")
        if rc == FS_ERR_NEED_ADMIN:
            self.log.emit("Запрос прав администратора...")
            self.request_elevation()
        return False

    def remove_service(self):
        rc = _dll.fs_remove_service()
        if rc == FS_OK:
            if self.installed_service:
                self.log.emit(f"Служба удалена: {self.installed_service}")
            self.installed_service = ""
            self.service_changed.emit("")
            self.poll_state()
            return True
        self.log.emit(f"Не удалось удалить службу: {last_error()}")
        return False

    def start_service(self):
        rc = _dll.fs_start_service()
        if rc == FS_OK:
            self.log.emit("Служба zapret запущена")
            self.poll_state()
            return True
        self.log.emit(f"Не удалось запустить службу: {last_error()}")
        return False

    def stop_service(self):
        rc = _dll.fs_stop_service()
        if rc == FS_OK:
            self.log.emit("Служба zapret остановлена")
            self.poll_state()
            return True
        self.log.emit(f"Не удалось остановить службу: {last_error()}")
        return False

    # ---------- Presets ----------
    def preset_installed(self, tag, preset_id):
        if not tag:
            return False
        path = os.path.join(RELEASES_DIR, tag, ".presets", f"{preset_id}.installed")
        return os.path.exists(path)

    def install_preset(self, tag, preset_id):
        if not tag:
            self.log.emit("Не выбран релиз для установки сборки")
            return False
        directory = os.path.join(RELEASES_DIR, tag, ".presets")
        os.makedirs(directory, exist_ok=True)
        with open(
            os.path.join(directory, f"{preset_id}.installed"),
            "w",
            encoding="utf-8",
        ) as handle:
            handle.write("installed\n")
        self.log.emit(f"Сборка «{preset_id}» применена к {tag}")
        return True

    def remove_preset(self, tag, preset_id):
        if not tag:
            return False
        path = os.path.join(
            RELEASES_DIR, tag, ".presets", f"{preset_id}.installed"
        )
        if os.path.exists(path):
            try:
                os.remove(path)
            except Exception:
                pass
        self.log.emit(f"Сборка «{preset_id}» удалена из {tag}")
        return True

    # ---------- Folders / plugins ----------
    def open_folder(self, tag):
        directory = os.path.join(RELEASES_DIR, tag) if tag else RELEASES_DIR
        try:
            os.makedirs(directory, exist_ok=True)
            if os.name == "nt":
                os.startfile(directory)
            else:
                subprocess.Popen(["xdg-open", directory])
        except Exception as exc:
            self.log.emit(f"Не удалось открыть папку: {exc}")

    def open_plugins_folder(self):
        try:
            os.makedirs(PLUGINS_DIR, exist_ok=True)
            if os.name == "nt":
                os.startfile(PLUGINS_DIR)
            else:
                subprocess.Popen(["xdg-open", PLUGINS_DIR])
        except Exception as exc:
            self.log.emit(f"Не удалось открыть папку плагинов: {exc}")

    def list_plugins(self):
        if not os.path.isdir(PLUGINS_DIR):
            return []
        result = []
        try:
            for name in sorted(os.listdir(PLUGINS_DIR)):
                path = os.path.join(PLUGINS_DIR, name)
                if not os.path.isfile(path):
                    continue
                if not name.lower().endswith(PLUGIN_EXTS):
                    continue
                result.append(
                    {
                        "name": name,
                        "path": path,
                        "ext": name.rsplit(".", 1)[-1].lower(),
                    }
                )
        except Exception:
            pass
        return result

    def run_plugin(self, name):
        path = os.path.join(PLUGINS_DIR, name)
        if not os.path.exists(path):
            self.log.emit(f"Плагин не найден: {name}")
            return
        ext = name.rsplit(".", 1)[-1].lower()
        try:
            if ext == "py":
                command = [sys.executable, path]
            elif ext in ("bat", "cmd"):
                command = ["cmd", "/c", path] if os.name == "nt" else [path]
            elif ext == "ps1":
                command = [
                    "powershell", "-NoProfile", "-ExecutionPolicy",
                    "Bypass", "-File", path,
                ]
            else:
                command = [path]
            kwargs = {}
            if os.name == "nt":
                kwargs["creationflags"] = 0x08000000
            subprocess.Popen(command, **kwargs)
            self.log.emit(f"Плагин запущен: {name}")
        except Exception as exc:
            self.log.emit(f"Ошибка запуска плагина {name}: {exc}")
