# -*- coding: utf-8 -*-
"""fsclient launcher - entry point."""
import sys
import os
import ctypes

from PyQt6.QtWidgets import QApplication, QMessageBox
from PyQt6.QtGui import QFont
from PyQt6.QtCore import Qt

from backend_ctypes import acquire_single_instance, release_single_instance
from ui_main import MainWindow


def apply_dark_title_bar(window):
    if os.name != "nt":
        return
    try:
        hwnd = int(window.winId())
    except Exception:
        return

    DWMWA_USE_IMMERSIVE_DARK_MODE = 20
    DWMWA_CAPTION_COLOR = 35
    DWMWA_TEXT_COLOR = 36
    DWMWA_BORDER_COLOR = 34

    def set_attr(attr, value):
        try:
            v = ctypes.c_int(value)
            ctypes.windll.dwmapi.DwmSetWindowAttribute(
                ctypes.c_void_p(hwnd), attr,
                ctypes.byref(v), ctypes.sizeof(v),
            )
        except Exception:
            pass

    set_attr(DWMWA_USE_IMMERSIVE_DARK_MODE, 1)
    # #12111A -> 0x00BBGGRR = 0x001A1112
    set_attr(DWMWA_CAPTION_COLOR, 0x001A1112)
    # #F4F2FA
    set_attr(DWMWA_TEXT_COLOR, 0x00FAF2F4)
    set_attr(DWMWA_BORDER_COLOR, 0x001A1112)


def main():
    QApplication.setHighDpiScaleFactorRoundingPolicy(
        Qt.HighDpiScaleFactorRoundingPolicy.PassThrough
    )
    app = QApplication(sys.argv)
    app.setQuitOnLastWindowClosed(False)
    app.setFont(QFont("Segoe UI", 10))

    acquired, error = acquire_single_instance()
    if not acquired:
        QMessageBox.warning(
            None,
            "fsclient",
            "Другой экземпляр fsclient уже запущен.\n"
            "Проверьте значок приложения в трее.",
        )
        sys.exit(1)

    app.aboutToQuit.connect(release_single_instance)

    window = MainWindow()
    app.aboutToQuit.connect(window.backend.close)
    window.show()
    apply_dark_title_bar(window)

    sys.exit(app.exec())


if __name__ == "__main__":
    main()
