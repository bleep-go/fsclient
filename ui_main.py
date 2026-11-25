# -*- coding: utf-8 -*-
"""Main window with native title bar, real frosted glass, splash intro."""
import os
import sys
import webbrowser
from datetime import datetime

from PyQt6.QtWidgets import (
    QMainWindow, QWidget, QFrame, QVBoxLayout, QHBoxLayout, QLabel,
    QPushButton, QStackedWidget, QSizePolicy, QSystemTrayIcon, QMenu,
    QApplication, QGraphicsBlurEffect, QGraphicsScene, QGraphicsPixmapItem
)
from PyQt6.QtCore import (
    Qt, QTimer, QEvent, QPointF, QRect, QRectF, QVariantAnimation
)
from PyQt6.QtGui import (
    QColor, QAction, QIcon, QPixmap, QPainter, QFont, QImage,
    QLinearGradient, QBrush, QPainterPath, QPen
)

from theme import ACCENT, GLOBAL_QSS
from widgets import (
    HoverButton, IconButton, AnimatedProgressBar, CustomComboBox,
    TabBar, ProductCard, ConsoleBar, DevPlaceholderCard,
    make_icon_pixmap,
)
from modals import StrategyModal, ConfirmModal
from backend_ctypes import (
    Backend,
    FS_STATE_IDLE,
    FS_STATE_RUNNING_PROCESS,
    FS_STATE_RUNNING_SERVICE,
)


DEFAULT_STRATEGIES = [
    "general.bat",
    "general (ALT).bat",
    "general (ALT2).bat",
    "general (ALT3).bat",
    "general (ALT4).bat",
]

APP_DIR = (
    os.path.dirname(sys.executable)
    if getattr(sys, "frozen", False)
    else os.path.dirname(os.path.abspath(__file__))
)
BUNDLE_DIR = getattr(sys, "_MEIPASS", APP_DIR)
ASSETS_BG_CANDIDATES = [
    os.path.join(BUNDLE_DIR, "assets", "background.png"),
    os.path.join(BUNDLE_DIR, "assets", "background.jpg"),
    os.path.join(BUNDLE_DIR, "assets", "background.jpeg"),
]
ASSETS_ICON = os.path.join(BUNDLE_DIR, "assets", "zapret_icon.png")
APP_ICON = os.path.join(BUNDLE_DIR, "assets", "fsclient.ico")


# ============================================================
# Splash overlay (bouncing FSCLIENT letters)
# ============================================================
class SplashOverlay(QWidget):
    finished = None  # signal created lazily; we emit via callback instead

    def __init__(self, parent, on_finished=None):
        super().__init__(parent)
        self._on_finished = on_finished
        self.setGeometry(parent.rect())
        self.setAttribute(Qt.WidgetAttribute.WA_StyledBackground, True)
        self.setStyleSheet("background: #0E0D15;")

        self._text = "FSCLIENT"
        self._offsets = [80.0] * len(self._text)
        self._opacity = 1.0
        self._anims = []
        self._fade_anim = None

        self.raise_()
        QTimer.singleShot(80, self._start)

    def _start(self):
        for i in range(len(self._text)):
            anim = QVariantAnimation(self)
            anim.setDuration(520)
            anim.setStartValue(80.0)
            anim.setEndValue(0.0)
            anim.setEasingCurve(QEasingCurve.Type.OutBack)
            anim.valueChanged.connect(
                lambda v, idx=i: self._set_offset(idx, v)
            )
            QTimer.singleShot(i * 60, anim.start)
            self._anims.append(anim)
        total = 80 + (len(self._text) - 1) * 60 + 520 + 200
        QTimer.singleShot(total, self._fade_out)

    def _set_offset(self, idx, v):
        self._offsets[idx] = float(v)
        self.update()

    def _fade_out(self):
        self._fade_anim = QVariantAnimation(self)
        self._fade_anim.setDuration(400)
        self._fade_anim.setStartValue(1.0)
        self._fade_anim.setEndValue(0.0)
        self._fade_anim.valueChanged.connect(self._set_opacity)
        self._fade_anim.finished.connect(self._on_faded)
        self._fade_anim.start()

    def _set_opacity(self, v):
        self._opacity = float(v)
        self.update()

    def _on_faded(self):
        if self._on_finished:
            self._on_finished()
        self.hide()
        self.deleteLater()

    def paintEvent(self, e):
        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        grad = QLinearGradient(0, 0, 0, self.height())
        grad.setColorAt(0.0, QColor(20, 18, 32))
        grad.setColorAt(1.0, QColor(14, 13, 21))
        p.fillRect(self.rect(), QBrush(grad))

        alpha = int(255 * self._opacity)
        font = QFont("Segoe UI")
        font.setPixelSize(64)
        font.setWeight(QFont.Weight.Black)
        p.setFont(font)

        fm = p.fontMetrics()
        spacing = 8
        widths = [fm.horizontalAdvance(ch) for ch in self._text]
        total_w = sum(widths) + spacing * (len(widths) - 1)
        x = (self.width() - total_w) // 2
        base_y = self.height() // 2

        p.setPen(QColor(255, 255, 255, alpha))
        for i, ch in enumerate(self._text):
            w = widths[i]
            y_off = int(self._offsets[i])
            r = QRect(x, base_y - 60 + y_off, w, 120)
            p.drawText(r, Qt.AlignmentFlag.AlignCenter, ch)
            x += w + spacing
        p.end()

    # import shortcut
from PyQt6.QtCore import QEasingCurve


# ============================================================
# Zapret icon
# ============================================================
class ZapretIconWidget(QWidget):
    def __init__(self, size=52, parent=None):
        super().__init__(parent)
        self.setFixedSize(size, size)
        self._pixmap = None
        if os.path.exists(ASSETS_ICON):
            pix = QPixmap(ASSETS_ICON)
            if not pix.isNull():
                self._pixmap = pix

    def paintEvent(self, e):
        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        rect = self.rect()

        grad = QLinearGradient(0, 0, rect.width(), rect.height())
        grad.setColorAt(0.0, QColor(124, 92, 255, 220))
        grad.setColorAt(1.0, QColor(166, 75, 99, 220))
        p.setPen(Qt.PenStyle.NoPen)
        p.setBrush(QBrush(grad))
        p.drawRoundedRect(rect, 12, 12)

        if self._pixmap and not self._pixmap.isNull():
            inner = rect.adjusted(5, 5, -5, -5)
            scaled = self._pixmap.scaled(
                inner.size(),
                Qt.AspectRatioMode.KeepAspectRatio,
                Qt.TransformationMode.SmoothTransformation,
            )
            ix = inner.x() + (inner.width() - scaled.width()) // 2
            iy = inner.y() + (inner.height() - scaled.height()) // 2
            p.drawPixmap(ix, iy, scaled)
        else:
            w, h = float(rect.width()), float(rect.height())
            path = QPainterPath()
            path.moveTo(w * 0.58, h * 0.14)
            path.lineTo(w * 0.28, h * 0.56)
            path.lineTo(w * 0.47, h * 0.56)
            path.lineTo(w * 0.41, h * 0.86)
            path.lineTo(w * 0.72, h * 0.44)
            path.lineTo(w * 0.53, h * 0.44)
            path.closeSubpath()
            p.setBrush(QColor(255, 255, 255, 245))
            p.drawPath(path)
        p.end()


# ============================================================
# RightCanvas (background + frosted-blur source for children)
# ============================================================
class RightCanvas(QWidget):
    def __init__(self, parent=None):
        super().__init__(parent)
        self._blurred_pixmap = None
        self._bg_pixmap = None
        for path in ASSETS_BG_CANDIDATES:
            if os.path.exists(path):
                pix = QPixmap(path)
                if not pix.isNull():
                    self._bg_pixmap = pix
                    break

    def _paint_background(self, painter, rect):
        """Draw base background into the given rect (used for both live paint
        and blur source generation)."""
        if self._bg_pixmap and not self._bg_pixmap.isNull():
            scaled = self._bg_pixmap.scaled(
                rect.size(),
                Qt.AspectRatioMode.KeepAspectRatioByExpanding,
                Qt.TransformationMode.SmoothTransformation,
            )
            x = rect.x() + (rect.width() - scaled.width()) // 2
            y = rect.y() + (rect.height() - scaled.height()) // 2
            painter.drawPixmap(x, y, scaled)
            overlay = QLinearGradient(rect.x(), rect.y(),
                                      rect.x(), rect.y() + rect.height())
            overlay.setColorAt(0.0, QColor(14, 13, 21, 70))
            overlay.setColorAt(0.45, QColor(14, 13, 21, 120))
            overlay.setColorAt(0.78, QColor(14, 13, 21, 210))
            overlay.setColorAt(1.0, QColor(14, 13, 21, 255))
            painter.fillRect(rect, QBrush(overlay))
        else:
            base = QLinearGradient(rect.x(), rect.y(),
                                   rect.x(), rect.y() + rect.height())
            base.setColorAt(0.0, QColor("#1E1938"))
            base.setColorAt(0.35, QColor("#4A2559"))
            base.setColorAt(0.65, QColor("#A64B63"))
            base.setColorAt(0.85, QColor("#E5987E"))
            base.setColorAt(1.0, QColor("#E5987E"))
            painter.fillRect(rect, QBrush(base))
            overlay = QLinearGradient(rect.x(), rect.y(),
                                      rect.x(), rect.y() + rect.height())
            overlay.setColorAt(0.0, QColor(14, 13, 21, 0))
            overlay.setColorAt(0.55, QColor(14, 13, 21, 45))
            overlay.setColorAt(0.78, QColor(14, 13, 21, 170))
            overlay.setColorAt(0.92, QColor(14, 13, 21, 235))
            overlay.setColorAt(1.0, QColor(14, 13, 21, 255))
            painter.fillRect(rect, QBrush(overlay))

    def resizeEvent(self, e):
        super().resizeEvent(e)
        self._regenerate_blur()

    def showEvent(self, e):
        super().showEvent(e)
        QTimer.singleShot(0, self._regenerate_blur)

    def _regenerate_blur(self):
        size = self.size()
        if size.width() <= 0 or size.height() <= 0:
            return
        sw = max(1, size.width() // 4)
        sh = max(1, size.height() // 4)

        small = QImage(sw, sh, QImage.Format.Format_ARGB32_Premultiplied)
        small.fill(Qt.GlobalColor.transparent)
        p = QPainter(small)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        self._paint_background(p, QRectF(0, 0, sw, sh).toRect())
        p.end()

        scene = QGraphicsScene()
        item = QGraphicsPixmapItem(QPixmap.fromImage(small))
        blur = QGraphicsBlurEffect()
        blur.setBlurRadius(8)
        item.setGraphicsEffect(blur)
        scene.addItem(item)

        out = QImage(sw, sh, QImage.Format.Format_ARGB32_Premultiplied)
        out.fill(Qt.GlobalColor.transparent)
        p = QPainter(out)
        scene.render(p, QRectF(0, 0, sw, sh), QRectF(0, 0, sw, sh))
        p.end()

        self._blurred_pixmap = QPixmap.fromImage(out).scaled(
            size.width(), size.height(),
            Qt.AspectRatioMode.IgnoreAspectRatio,
            Qt.TransformationMode.SmoothTransformation,
        )

    def paintEvent(self, e):
        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        self._paint_background(p, self.rect())
        p.end()


# ============================================================
# MainWindow
# ============================================================
class MainWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.setWindowTitle("fsclient")
        self.setMinimumSize(1080, 720)
        self.resize(1200, 800)
        self.setStyleSheet(GLOBAL_QSS)
        self.setWindowIcon(self._make_app_icon())

        self.backend = Backend(self)
        self._downloading = False
        self._splash = None
        self._splash_shown = False
        self._runtime_state = FS_STATE_IDLE
        self._runtime_detail = ""

        self.container = QFrame()
        self.container.setObjectName("MainContainer")
        self.container.setStyleSheet(
            "QFrame#MainContainer { background: #12111A; border: none; }"
        )

        root = QVBoxLayout(self.container)
        root.setContentsMargins(0, 0, 0, 0)
        root.setSpacing(0)

        body = QHBoxLayout()
        body.setContentsMargins(0, 0, 0, 0)
        body.setSpacing(0)

        self.sidebar = self._build_sidebar()
        body.addWidget(self.sidebar)

        self.right_canvas = RightCanvas()
        right_lay = QVBoxLayout(self.right_canvas)
        right_lay.setContentsMargins(0, 0, 0, 0)
        right_lay.setSpacing(0)

        self.stack = QStackedWidget()
        self.stack.setStyleSheet("QStackedWidget { background: transparent; }")
        self.zapret_page = self._build_zapret_page()
        self.stack.addWidget(self.zapret_page)
        self.stack.addWidget(self._build_placeholder_page())
        right_lay.addWidget(self.stack)

        body.addWidget(self.right_canvas, 1)
        root.addLayout(body, 1)
        self.setCentralWidget(self.container)

        self.backend.log.connect(self._append_log)
        self.backend.release_list_ready.connect(self._on_releases_ready)
        self.backend.download_progress.connect(self._on_download_progress)
        self.backend.download_done.connect(self._on_download_done)
        self.backend.download_failed.connect(self._on_download_failed)
        self.backend.service_changed.connect(self._on_service_changed)
        self.backend.strategy_started.connect(self._on_strategy_started)
        self.backend.state_changed.connect(self._on_native_state)

        self.state_timer = QTimer(self)
        self.state_timer.setInterval(1800)
        self.state_timer.timeout.connect(self._poll_state)
        self.state_timer.start()

        self._setup_tray()
        self._append_log("fsclient запущен. Загрузка релизов...")
        QTimer.singleShot(100, self._poll_state)
        QTimer.singleShot(200, self.backend.fetch_releases_async)

    def showEvent(self, e):
        super().showEvent(e)
        if not self._splash_shown:
            self._splash_shown = True
            QTimer.singleShot(30, self._show_splash)

    def _show_splash(self):
        self._splash = SplashOverlay(self, on_finished=self._on_splash_done)
        self._splash.setGeometry(self.rect())
        self._splash.raise_()
        self._splash.show()

    def _on_splash_done(self):
        self._splash = None

    def _make_app_icon(self):
        if os.path.isfile(APP_ICON):
            return QIcon(APP_ICON)
        pix = QPixmap(64, 64)
        pix.fill(Qt.GlobalColor.transparent)
        p = QPainter(pix)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        grad = QLinearGradient(0, 0, 64, 64)
        grad.setColorAt(0.0, QColor("#7C5CFF"))
        grad.setColorAt(1.0, QColor("#E5987E"))
        p.setBrush(QBrush(grad))
        p.setPen(Qt.PenStyle.NoPen)
        p.drawRoundedRect(4, 4, 56, 56, 14, 14)
        p.setPen(QColor(255, 255, 255))
        f = QFont("Segoe UI")
        f.setPixelSize(24)
        f.setWeight(QFont.Weight.Bold)
        p.setFont(f)
        p.drawText(pix.rect(), Qt.AlignmentFlag.AlignCenter, "fs")
        p.end()
        return QIcon(pix)

    # ---------- Sidebar ----------
    def _build_sidebar(self):
        w = QFrame()
        w.setFixedWidth(230)
        w.setStyleSheet("QFrame { background: #181524; border: none; }")
        lay = QVBoxLayout(w)
        lay.setContentsMargins(12, 18, 12, 16)
        lay.setSpacing(8)

        lbl = QLabel("ПРОДУКТЫ")
        lbl.setStyleSheet(
            "color: #6B6480; font-size: 10px; font-weight: 700; "
            "letter-spacing: 1.5px; background: transparent; padding-left: 8px;"
        )
        lay.addWidget(lbl)
        lay.addSpacing(4)

        self.card_zapret = ProductCard("zapret", "lightning", "Zapret")
        self.card_zapret.set_status("Не скачано", "#6B6480")
        self.card_zapret.set_active(True)
        self.card_zapret.clicked.connect(self._on_product_clicked)
        self.card_zapret.menu_requested.connect(self._on_product_menu)
        lay.addWidget(self.card_zapret)

        self.card_tg = ProductCard("tgws", "plug", "TG-WS-Proxy")
        self.card_tg.set_status("В разработке", "#6B6480")
        self.card_tg.clicked.connect(self._on_product_clicked)
        lay.addWidget(self.card_tg)

        lay.addStretch()

        gh = QPushButton("  GitHub Flowseal")
        gh.setCursor(Qt.CursorShape.PointingHandCursor)
        gh.setFixedHeight(36)
        gh.setIcon(QIcon(make_icon_pixmap("external", 14)))
        gh.setStyleSheet("""
            QPushButton {
                background: transparent;
                color: #9E97B8;
                border: none;
                border-radius: 8px;
                font-size: 12px;
                text-align: left;
                padding-left: 10px;
            }
            QPushButton:hover {
                background: rgba(255,255,255,0.04);
                color: #F4F2FA;
            }
        """)
        gh.clicked.connect(self._open_github)
        lay.addWidget(gh)

        ver = QLabel("fsclient v1.0.0")
        ver.setStyleSheet(
            "color: #4A4558; font-size: 10px; background: transparent; "
            "padding-left: 10px;"
        )
        lay.addWidget(ver)
        return w

    def _on_product_clicked(self, product_id):
        if product_id == "zapret":
            self.card_zapret.set_active(True)
            self.card_tg.set_active(False)
            self.stack.setCurrentIndex(0)
        else:
            self.card_zapret.set_active(False)
            self.card_tg.set_active(True)
            self.stack.setCurrentIndex(1)

    def _on_product_menu(self, action):
        if action == "open_folder":
            self.backend.open_folder(self.combo.current_tag())
        elif action == "check_updates":
            self._append_log("Проверка обновлений...")
            self.backend.fetch_releases_async()
        elif action == "settings":
            self._append_log("Настройки продукта (в разработке)")

    def _open_github(self):
        webbrowser.open("https://github.com/Flowseal/zapret-discord-youtube")

    # ---------- Zapret page ----------
    def _build_zapret_page(self):
        page = QWidget()
        page.setStyleSheet("background: transparent;")
        outer = QVBoxLayout(page)
        outer.setContentsMargins(0, 0, 0, 0)
        outer.setSpacing(0)

        # HERO (fixed)
        hero = QWidget()
        hero.setStyleSheet("background: transparent;")
        hero.setFixedHeight(160)
        hl = QVBoxLayout(hero)
        hl.setContentsMargins(32, 20, 32, 10)
        hl.setSpacing(10)

        badge_row = QHBoxLayout()
        badge_row.setSpacing(8)
        pill_style = (
            "color: rgba(255,255,255,0.92);"
            "background: rgba(20, 19, 32, 0.58);"
            "border: 1px solid rgba(255, 255, 255, 0.14);"
            "border-radius: 10px;"
            "padding: 4px 10px;"
            "font-size: 10px;"
            "font-weight: 600;"
            "letter-spacing: 1px;"
        )
        b1 = QLabel("FLOWSEAL EDITION")
        b1.setStyleSheet(pill_style)
        b1.setSizePolicy(QSizePolicy.Policy.Maximum, QSizePolicy.Policy.Fixed)
        badge_row.addWidget(b1)
        b2 = QLabel("DPI BYPASS")
        b2.setStyleSheet(pill_style)
        b2.setSizePolicy(QSizePolicy.Policy.Maximum, QSizePolicy.Policy.Fixed)
        badge_row.addWidget(b2)
        badge_row.addStretch()
        hl.addLayout(badge_row)

        title_row = QHBoxLayout()
        title_row.setSpacing(14)
        icon = ZapretIconWidget(52)
        title_row.addWidget(icon, 0, Qt.AlignmentFlag.AlignVCenter)

        title_col = QVBoxLayout()
        title_col.setSpacing(3)
        t = QLabel("Zapret")
        t.setStyleSheet(
            "color: #FFFFFF; font-size: 32px; font-weight: 800; "
            "background: transparent;"
        )
        title_col.addWidget(t)
        subtitle = QLabel(
            "Инструмент обхода блокировок Discord и YouTube. "
            "Выберите релиз и запустите нужную стратегию в один клик."
        )
        subtitle.setStyleSheet(
            "color: rgba(255,255,255,0.85); font-size: 12.5px; "
            "background: transparent;"
        )
        subtitle.setWordWrap(True)
        title_col.addWidget(subtitle)
        title_row.addLayout(title_col, 1)
        hl.addLayout(title_row)
        hl.addStretch()
        outer.addWidget(hero)

        # CONTROLS (fixed)
        controls = QWidget()
        controls.setStyleSheet("background: transparent;")
        controls_lay = QVBoxLayout(controls)
        controls_lay.setContentsMargins(32, 0, 32, 8)
        controls_lay.setSpacing(10)

        action_row = QHBoxLayout()
        action_row.setSpacing(12)

        self.main_btn = HoverButton("Скачать", icon_kind="download",
                                     accent=True, height=56)
        self.main_btn.setMinimumWidth(220)
        self.main_btn.clicked.connect(self._on_main_action)
        action_row.addWidget(self.main_btn)

        self.combo = CustomComboBox()
        self.combo.currentIndexChanged.connect(self._on_release_changed)
        action_row.addWidget(self.combo)

        self.folder_btn = IconButton("folder", size=56,
                                      tooltip="Открыть папку релиза")
        self.folder_btn.clicked.connect(self._on_folder_click)
        action_row.addWidget(self.folder_btn)

        self.stop_btn = HoverButton("Остановить", icon_kind="stop",
                                     accent=False, height=56)
        self.stop_btn.setVisible(False)
        self.stop_btn.clicked.connect(self._on_stop_service)
        action_row.addWidget(self.stop_btn)

        action_row.addStretch()
        controls_lay.addLayout(action_row)

        self.progress_container = QFrame()
        self.progress_container.setMaximumHeight(0)
        self.progress_container.setStyleSheet(
            "background: transparent; border: none;"
        )
        pc = QVBoxLayout(self.progress_container)
        pc.setContentsMargins(0, 2, 0, 0)
        pc.setSpacing(6)
        self.progress_bar = AnimatedProgressBar()
        pc.addWidget(self.progress_bar)
        self.progress_lbl = QLabel("Скачивание...")
        self.progress_lbl.setStyleSheet(
            "color: #9E97B8; font-size: 11px; background: transparent;"
        )
        pc.addWidget(self.progress_lbl)
        controls_lay.addWidget(self.progress_container)

        self.console_bar = ConsoleBar()
        controls_lay.addWidget(self.console_bar)

        self.tabs = TabBar(["Плагины", "Сборки"])
        self.tabs.currentChanged.connect(self._on_tab_changed)
        controls_lay.addWidget(self.tabs)

        outer.addWidget(controls)

        # TAB CONTENT
        tab_wrapper = QWidget()
        tab_wrapper.setStyleSheet("background: transparent;")
        tab_wrap_lay = QVBoxLayout(tab_wrapper)
        tab_wrap_lay.setContentsMargins(32, 0, 32, 16)
        tab_wrap_lay.setSpacing(0)

        self.tab_stack = QStackedWidget()
        self.tab_stack.setStyleSheet("QStackedWidget { background: transparent; }")
        self.tab_stack.addWidget(self._build_dev_page())
        self.tab_stack.addWidget(self._build_dev_page())
        tab_wrap_lay.addWidget(self.tab_stack)

        outer.addWidget(tab_wrapper, 1)
        return page

    def _build_dev_page(self):
        wrapper = QWidget()
        wrapper.setStyleSheet("background: transparent;")
        lay = QVBoxLayout(wrapper)
        lay.setContentsMargins(0, 20, 0, 20)
        lay.setAlignment(Qt.AlignmentFlag.AlignCenter)
        lay.addWidget(DevPlaceholderCard(),
                      alignment=Qt.AlignmentFlag.AlignCenter)
        return wrapper

    def _build_placeholder_page(self):
        w = QWidget()
        w.setStyleSheet("background: transparent;")
        lay = QVBoxLayout(w)
        lay.setAlignment(Qt.AlignmentFlag.AlignCenter)
        lay.setSpacing(12)

        badge = QLabel("В РАЗРАБОТКЕ")
        badge.setStyleSheet("""
            color: #FBBF24;
            background: rgba(251,191,36,0.12);
            border: 1px solid rgba(251,191,36,0.3);
            border-radius: 10px;
            font-size: 11px;
            font-weight: 700;
            letter-spacing: 1.5px;
            padding: 4px 10px;
        """)
        badge.setAlignment(Qt.AlignmentFlag.AlignCenter)
        badge.setSizePolicy(QSizePolicy.Policy.Maximum, QSizePolicy.Policy.Fixed)
        lay.addWidget(badge, alignment=Qt.AlignmentFlag.AlignCenter)

        title = QLabel("TG-WS-Proxy")
        title.setStyleSheet(
            "color: #F4F2FA; font-size: 34px; font-weight: 800; "
            "background: transparent;"
        )
        title.setAlignment(Qt.AlignmentFlag.AlignCenter)
        lay.addWidget(title)

        desc = QLabel("Скоро будет доступно\nПрокси-сервер через Telegram WebSocket")
        desc.setStyleSheet(
            "color: #9E97B8; font-size: 14px; background: transparent;"
        )
        desc.setAlignment(Qt.AlignmentFlag.AlignCenter)
        lay.addWidget(desc)
        return w

    # ---------- Tabs ----------
    def _on_tab_changed(self, idx):
        self.tab_stack.setCurrentIndex(idx)

    # ---------- Releases ----------
    def _on_releases_ready(self, releases):
        self.combo.set_items(releases, self.backend.list_downloaded_tags())
        if releases:
            self._on_release_changed(0)

    def _on_release_changed(self, idx):
        tag = self.combo.current_tag()
        if not tag:
            return
        self.combo.set_downloaded(self.backend.list_downloaded_tags())
        self._update_runtime_ui()

    # ---------- Real-time native state ----------
    def _poll_state(self):
        state, detail = self.backend.poll_state()
        self._on_native_state(state, detail)

    def _on_native_state(self, state, detail):
        self._runtime_state = int(state)
        self._runtime_detail = detail or ""
        self._update_runtime_ui()

    def _active_label(self, state, detail):
        if state == FS_STATE_RUNNING_PROCESS:
            text = detail or ""
            if " — " in text:
                return text.split(" — ", 1)[1]
            if text.startswith("winws.exe"):
                return self.backend.running_strategy or "winws.exe"
            return text or self.backend.running_strategy or "winws.exe"
        if state == FS_STATE_RUNNING_SERVICE:
            return self.backend.installed_service or detail or "zapret"
        return ""

    @staticmethod
    def _short_label(text, limit=22):
        text = str(text)
        if len(text) <= limit:
            return text
        return text[:limit - 1] + "…"

    def _update_runtime_ui(self):
        if self._downloading or not hasattr(self, "main_btn"):
            return

        state = self._runtime_state
        detail = self._runtime_detail

        if state == FS_STATE_IDLE:
            tag = self.combo.current_tag()
            downloaded = bool(tag) and self.backend.is_downloaded(tag)
            self.main_btn.setText("Запустить" if downloaded else "Скачать")
            self.main_btn._icon_kind = "play" if downloaded else "download"
            self.main_btn.set_accent_mode("accent")
            self.main_btn.setToolTip("")
            self.stop_btn.setVisible(False)
            if downloaded:
                self.card_zapret.set_status("Готово", "#4ADE80")
            else:
                self.card_zapret.set_status("Не скачано", "#6B6480")
            self.console_bar.set_dot_color("#6B6480")
            self.main_btn.update()
            return

        label = self._active_label(state, detail)
        short = self._short_label(label)
        self.main_btn.set_accent_mode("danger")
        self.main_btn._icon_kind = "stop"
        self.stop_btn.setVisible(True)
        if state == FS_STATE_RUNNING_SERVICE:
            self.main_btn.setText(f"Стоп службы · {short}")
            self.main_btn.setToolTip(f"Активна служба zapret: {label}")
            self.card_zapret.set_status("Служба активна", "#4ADE80")
            self.console_bar.set_dot_color("#4ADE80")
        else:
            self.main_btn.setText(f"Остановить · {short}")
            self.main_btn.setToolTip(f"Активный процесс winws.exe: {label}")
            self.card_zapret.set_status("Обход активен", "#FF8A7A")
            self.console_bar.set_dot_color("#FF8A7A")
        self.main_btn.update()

    def _on_main_action(self):
        if self._downloading:
            return
        if self._runtime_state != FS_STATE_IDLE:
            self._on_stop_service()
            return
        tag = self.combo.current_tag()
        if not tag:
            self._append_log("Релиз не выбран")
            return
        if self.backend.is_downloaded(tag):
            self._open_strategy_modal(tag)
        else:
            url = None
            for r in self.backend.releases:
                if r["tag"] == tag:
                    url = r.get("url")
                    break
            if not url:
                self._append_log(
                    "Ссылка для скачивания недоступна (демо-режим). "
                    "Попробуйте другой релиз или проверьте соединение."
                )
                return
            self._start_download(tag, url)

    def _start_download(self, tag, url):
        self._downloading = True
        self.main_btn.setEnabled(False)
        self.main_btn.setText("Скачивание...")
        self.main_btn._icon_kind = None
        self.main_btn.update()
        self._show_progress_section()
        self.progress_bar.setValue(0)
        self.progress_lbl.setText("Подготовка...")
        self._append_log(f"Начинаю скачивание {tag}...")
        self.backend.download_release(tag, url)

    def _on_download_progress(self, pct, stage):
        self.progress_bar.animate_to(pct)
        self.progress_lbl.setText(f"{stage}  {pct}%")

    def _on_download_done(self, tag):
        self._downloading = False
        self.main_btn.setEnabled(True)
        self.progress_bar.animate_to(100)
        self.progress_lbl.setText("Готово")
        self._append_log(f"Релиз {tag} успешно скачан")
        QTimer.singleShot(600, self._hide_progress_section)
        self.combo.set_downloaded(self.backend.list_downloaded_tags())
        self._update_runtime_ui()

    def _on_download_failed(self, err):
        self._downloading = False
        self.main_btn.setEnabled(True)
        self._append_log(f"Ошибка скачивания: {err}")
        self.progress_lbl.setText(f"Ошибка: {err}")
        QTimer.singleShot(1500, self._hide_progress_section)

    def _show_progress_section(self):
        anim = QVariantAnimation(self)
        anim.setDuration(260)
        anim.setStartValue(self.progress_container.maximumHeight())
        anim.setEndValue(70)
        anim.valueChanged.connect(
            lambda v: self.progress_container.setMaximumHeight(int(v))
        )
        anim.start()
        self._progress_anim = anim

    def _hide_progress_section(self):
        anim = QVariantAnimation(self)
        anim.setDuration(260)
        anim.setStartValue(self.progress_container.maximumHeight())
        anim.setEndValue(0)
        anim.valueChanged.connect(
            lambda v: self.progress_container.setMaximumHeight(int(v))
        )
        anim.start()
        self._progress_anim = anim

    def _on_folder_click(self):
        self.backend.open_folder(self.combo.current_tag())

    # ---------- Strategy ----------
    def _open_strategy_modal(self, tag):
        strategies = self.backend.list_strategies(tag)
        if not strategies:
            strategies = DEFAULT_STRATEGIES
        modal = StrategyModal(self, strategies, self.backend.installed_service)
        modal.chosen.connect(
            lambda bat, action: self._on_strategy_chosen(tag, bat, action)
        )
        modal.show_animated()

    def _on_strategy_chosen(self, tag, bat, action):
        if self.backend.installed_service and \
                self.backend.installed_service != bat:
            confirm = ConfirmModal(self, self.backend.installed_service, bat)
            confirm.confirmed.connect(
                lambda: self._apply_strategy(tag, bat, action)
            )
            confirm.show_animated()
        else:
            self._apply_strategy(tag, bat, action)

    def _apply_strategy(self, tag, bat, action):
        if self.backend.installed_service:
            self.backend.remove_service()
        if action == "run":
            self.backend.run_strategy(tag, bat)
        else:
            self.backend.install_service(tag, bat)

    def _on_service_changed(self, bat_name):
        self._update_runtime_ui()

    def _on_strategy_started(self, bat_name):
        self._runtime_state = FS_STATE_RUNNING_PROCESS
        self._runtime_detail = bat_name
        self.backend.running_strategy = bat_name
        self._update_runtime_ui()

    def _on_stop_service(self):
        self.backend.stop_strategy()
        self.backend.remove_service()
        self._runtime_state = FS_STATE_IDLE
        self._runtime_detail = ""
        self._update_runtime_ui()

    def _append_log(self, msg):
        ts = datetime.now().strftime("%H:%M:%S")
        self.console_bar.push_log(f"[{ts}] {msg}")

    # ---------- Tray ----------
    def _setup_tray(self):
        self.tray = QSystemTrayIcon(self)
        self.tray.setIcon(self.windowIcon())
        self.tray.setToolTip("fsclient")

        menu = QMenu()
        menu.setStyleSheet("""
            QMenu {
                background: #1C192B;
                color: #F4F2FA;
                border: 1px solid rgba(255,255,255,0.1);
                border-radius: 8px;
                padding: 6px;
            }
            QMenu::item {
                padding: 8px 20px;
                border-radius: 6px;
                font-size: 12px;
            }
            QMenu::item:selected {
                background: rgba(124,92,255,0.2);
            }
        """)

        act_open = QAction("Открыть", self)
        act_open.triggered.connect(self._show_from_tray)
        menu.addAction(act_open)

        act_status = QAction("Статус", self)
        act_status.triggered.connect(self._tray_status)
        menu.addAction(act_status)

        menu.addSeparator()

        act_quit = QAction("Выход", self)
        act_quit.triggered.connect(self._quit_app)
        menu.addAction(act_quit)

        self.tray.setContextMenu(menu)
        self.tray.activated.connect(self._on_tray_activated)
        self.tray.show()

    def _on_tray_activated(self, reason):
        if reason == QSystemTrayIcon.ActivationReason.Trigger:
            self._show_from_tray()

    def _show_from_tray(self):
        self.show()
        self.raise_()
        self.activateWindow()

    def _tray_status(self):
        if self.backend.installed_service:
            self.tray.showMessage(
                "fsclient",
                f"Служба активна: {self.backend.installed_service}",
                QSystemTrayIcon.MessageIcon.Information, 3000,
            )
        else:
            self.tray.showMessage(
                "fsclient",
                "Лаунчер работает, служба не установлена",
                QSystemTrayIcon.MessageIcon.Information, 3000,
            )

    def _quit_app(self):
        self.tray.hide()
        QApplication.instance().quit()

    def closeEvent(self, e):
        e.ignore()
        self.hide()
