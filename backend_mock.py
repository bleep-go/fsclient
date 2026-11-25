# -*- coding: utf-8 -*-
"""Backend: GitHub releases, download, presets, mock service, plugins."""
import os
import sys
import json
import zipfile
import shutil
import subprocess
import urllib.request
import threading

from PyQt6.QtCore import QObject, pyqtSignal, QThread

RELEASES_API = "https://api.github.com/repos/Flowseal/zapret-discord-youtube/releases"
RELEASES_DIR = "releases"
PLUGINS_DIR = "plugins"

PLUGIN_EXTS = ('.py', '.bat', '.cmd', '.ps1', '.exe')

FALLBACK_RELEASES = [
    {"tag": "v1.10.4", "url": None, "is_latest": True},
    {"tag": "v1.10.3", "url": None, "is_latest": False},
    {"tag": "v1.10.2", "url": None, "is_latest": False},
    {"tag": "v1.10.1", "url": None, "is_latest": False},
    {"tag": "v1.10.0", "url": None, "is_latest": False},
]


class DownloadWorker(QThread):
    progress = pyqtSignal(int, str)
    finished_ok = pyqtSignal(str)
    failed = pyqtSignal(str)

    def __init__(self, tag, url, parent=None):
        super().__init__(parent)
        self.tag = tag
        self.url = url

    def run(self):
        try:
            os.makedirs(RELEASES_DIR, exist_ok=True)
            target_dir = os.path.join(RELEASES_DIR, self.tag)
            zip_path = os.path.join(RELEASES_DIR, f"{self.tag}.zip")

            self.progress.emit(0, "Скачивание архива...")
            req = urllib.request.Request(
                self.url, headers={"User-Agent": "fsclient/1.0"}
            )
            with urllib.request.urlopen(req, timeout=60) as resp:
                total = int(resp.headers.get("Content-Length", "0") or "0")
                downloaded = 0
                with open(zip_path, "wb") as f:
                    while True:
                        chunk = resp.read(65536)
                        if not chunk:
                            break
                        f.write(chunk)
                        downloaded += len(chunk)
                        if total > 0:
                            pct = min(90, int(downloaded * 90 / total))
                            self.progress.emit(pct, "Скачивание архива...")

            self.progress.emit(92, "Распаковка...")
            if os.path.exists(target_dir):
                shutil.rmtree(target_dir)
            os.makedirs(target_dir, exist_ok=True)
            with zipfile.ZipFile(zip_path, "r") as z:
                z.extractall(target_dir)

            entries = [e for e in os.listdir(target_dir) if not e.startswith(".")]
            if len(entries) == 1 and os.path.isdir(os.path.join(target_dir, entries[0])):
                sub = os.path.join(target_dir, entries[0])
                for f in os.listdir(sub):
                    shutil.move(os.path.join(sub, f), os.path.join(target_dir, f))
                os.rmdir(sub)

            try:
                os.remove(zip_path)
            except Exception:
                pass

            self.progress.emit(100, "Готово")
            self.finished_ok.emit(self.tag)
        except Exception as e:
            self.failed.emit(str(e))


class Backend(QObject):
    log = pyqtSignal(str)
    release_list_ready = pyqtSignal(list)
    download_progress = pyqtSignal(int, str)
    download_done = pyqtSignal(str)
    download_failed = pyqtSignal(str)
    service_changed = pyqtSignal(str)
    strategy_started = pyqtSignal(str)

    def __init__(self, parent=None):
        super().__init__(parent)
        self.releases = []
        self.installed_service = ""
        self.running_strategy = ""
        self._worker = None
        try:
            os.makedirs(PLUGINS_DIR, exist_ok=True)
        except Exception:
            pass

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
            for i, r in enumerate(data[:15]):
                tag = r.get("tag_name", "")
                assets = r.get("assets", [])
                zip_url = None
                for a in assets:
                    if a.get("name", "").endswith(".zip"):
                        zip_url = a.get("browser_download_url")
                        break
                result.append({"tag": tag, "url": zip_url, "is_latest": i == 0})
            if not result:
                raise ValueError("Пустой список релизов")
            self.releases = result
            self.release_list_ready.emit(result)
        except Exception as e:
            self.log.emit(f"GitHub API недоступен: {e}")
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
            d for d in os.listdir(RELEASES_DIR)
            if os.path.isdir(os.path.join(RELEASES_DIR, d)) and not d.startswith(".")
        )

    def list_strategies(self, tag):
        d = os.path.join(RELEASES_DIR, tag) if tag else ""
        if not d or not os.path.isdir(d):
            return []
        result = []
        try:
            for f in sorted(os.listdir(d)):
                low = f.lower()
                if low.endswith(".bat") and "service" not in low:
                    result.append(f)
        except Exception:
            pass
        return result

    def download_release(self, tag, url):
        if not url:
            self.download_failed.emit("Нет ссылки для скачивания (демо-режим)")
            return
        self._worker = DownloadWorker(tag, url)
        self._worker.progress.connect(self.download_progress)
        self._worker.finished_ok.connect(self.download_done)
        self._worker.failed.connect(self.download_failed)
        self._worker.start()

    # ---------- Strategy / service ----------
    def run_strategy(self, tag, bat_name):
        self.running_strategy = bat_name
        self.log.emit(f"Запуск стратегии: {bat_name}")
        self.strategy_started.emit(bat_name)

    def install_service(self, tag, bat_name):
        self.installed_service = bat_name
        self.service_changed.emit(bat_name)
        self.log.emit(f"Служба установлена: {bat_name}")

    def remove_service(self):
        if self.installed_service:
            self.log.emit(f"Служба удалена: {self.installed_service}")
        self.installed_service = ""
        self.service_changed.emit("")

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
        d = os.path.join(RELEASES_DIR, tag, ".presets")
        os.makedirs(d, exist_ok=True)
        with open(os.path.join(d, f"{preset_id}.installed"), "w", encoding="utf-8") as f:
            f.write("installed\n")
        self.log.emit(f"Сборка «{preset_id}» применена к {tag}")
        return True

    def remove_preset(self, tag, preset_id):
        if not tag:
            return False
        p = os.path.join(RELEASES_DIR, tag, ".presets", f"{preset_id}.installed")
        if os.path.exists(p):
            try:
                os.remove(p)
            except Exception:
                pass
        self.log.emit(f"Сборка «{preset_id}» удалена из {tag}")
        return True

    # ---------- Folders ----------
    def open_folder(self, tag):
        d = os.path.join(RELEASES_DIR, tag) if tag else RELEASES_DIR
        try:
            os.makedirs(d, exist_ok=True)
            if os.name == "nt":
                os.startfile(d)
            else:
                subprocess.Popen(["xdg-open", d])
        except Exception as e:
            self.log.emit(f"Не удалось открыть папку: {e}")

    def open_plugins_folder(self):
        try:
            os.makedirs(PLUGINS_DIR, exist_ok=True)
            if os.name == "nt":
                os.startfile(PLUGINS_DIR)
            else:
                subprocess.Popen(["xdg-open", PLUGINS_DIR])
        except Exception as e:
            self.log.emit(f"Не удалось открыть папку плагинов: {e}")

    # ---------- Plugins ----------
    def list_plugins(self):
        if not os.path.isdir(PLUGINS_DIR):
            return []
        result = []
        try:
            for f in sorted(os.listdir(PLUGINS_DIR)):
                p = os.path.join(PLUGINS_DIR, f)
                if not os.path.isfile(p):
                    continue
                if not f.lower().endswith(PLUGIN_EXTS):
                    continue
                ext = f.rsplit(".", 1)[-1].lower()
                result.append({"name": f, "path": p, "ext": ext})
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
                cmd = [sys.executable, path]
            elif ext in ("bat", "cmd"):
                if os.name == "nt":
                    cmd = ["cmd", "/c", path]
                else:
                    cmd = [path]
            elif ext == "ps1":
                cmd = ["powershell", "-NoProfile", "-ExecutionPolicy",
                       "Bypass", "-File", path]
            else:
                cmd = [path]
            kwargs = {}
            if os.name == "nt":
                kwargs["creationflags"] = 0x08000000  # CREATE_NO_WINDOW
            subprocess.Popen(cmd, **kwargs)
            self.log.emit(f"Плагин запущен: {name}")
        except Exception as e:
            self.log.emit(f"Ошибка запуска плагина {name}: {e}")
