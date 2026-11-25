# -*- coding: utf-8 -*-
"""Modal windows with fade animations."""
from PyQt6.QtWidgets import (
    QWidget, QFrame, QVBoxLayout, QHBoxLayout, QLabel, QPushButton,
    QListWidget, QListWidgetItem, QGraphicsOpacityEffect
)
from PyQt6.QtCore import (
    Qt, pyqtSignal, QPropertyAnimation, QEasingCurve, QSize
)


class ModalOverlay(QWidget):
    """Full-window overlay + centered card."""

    def __init__(self, parent):
        super().__init__(parent)
        self.setAttribute(Qt.WidgetAttribute.WA_StyledBackground, True)
        self.setStyleSheet("background: rgba(6, 4, 15, 0.6);")
        self.setGeometry(parent.rect())

        self._opacity = QGraphicsOpacityEffect(self)
        self._opacity.setOpacity(0.0)
        self.setGraphicsEffect(self._opacity)

        self._anim_in = QPropertyAnimation(self._opacity, b"opacity")
        self._anim_in.setDuration(200)
        self._anim_in.setStartValue(0.0)
        self._anim_in.setEndValue(1.0)
        self._anim_in.setEasingCurve(QEasingCurve.Type.OutCubic)

        self._anim_out = QPropertyAnimation(self._opacity, b"opacity")
        self._anim_out.setDuration(150)
        self._anim_out.setStartValue(1.0)
        self._anim_out.setEndValue(0.0)
        self._anim_out.finished.connect(self._on_fade_out_done)

        self._closing = False

    def show_animated(self):
        self.setGeometry(self.parent().rect())
        self._closing = False
        self.show()
        self.raise_()
        self._anim_in.start()

    def close_animated(self):
        if self._closing:
            return
        self._closing = True
        self._anim_out.start()

    def _on_fade_out_done(self):
        self.hide()
        self.deleteLater()

    def mousePressEvent(self, e):
        if e.target() is self:
            self.close_animated()

    def resizeEvent(self, e):
        super().resizeEvent(e)
        if self.parent():
            self.setGeometry(self.parent().rect())


class StrategyModal(ModalOverlay):
    """Select a strategy and choose action."""

    chosen = pyqtSignal(str, str)

    def __init__(self, parent, strategies, current_service=""):
        super().__init__(parent)
        self._strategies = list(strategies)
        self._current_service = current_service

        self.card = QFrame(self)
        self.card.setObjectName("ModalCard")
        self.card.setStyleSheet("""
            QFrame#ModalCard {
                background: #1C192B;
                border: 1px solid rgba(255,255,255,0.1);
                border-radius: 16px;
            }
        """)
        self.card.setFixedWidth(460)

        lay = QVBoxLayout(self.card)
        lay.setContentsMargins(24, 20, 24, 20)
        lay.setSpacing(14)

        header = QHBoxLayout()
        title = QLabel("Выберите стратегию запуска")
        title.setStyleSheet(
            "color: #F4F2FA; font-size: 16px; font-weight: 700; "
            "background: transparent;"
        )
        header.addWidget(title)
        header.addStretch()
        close_btn = QPushButton("✕")
        close_btn.setFixedSize(28, 28)
        close_btn.setCursor(Qt.CursorShape.PointingHandCursor)
        close_btn.setStyleSheet("""
            QPushButton {
                background: transparent;
                color: #9E97B8;
                border: none;
                border-radius: 6px;
                font-size: 14px;
            }
            QPushButton:hover {
                background: rgba(255,255,255,0.08);
                color: #F4F2FA;
            }
        """)
        close_btn.clicked.connect(self.close_animated)
        header.addWidget(close_btn)
        lay.addLayout(header)

        if current_service:
            hint = QLabel(f"Активна служба: {current_service}")
            hint.setStyleSheet(
                "color: #4ADE80; font-size: 11px; "
                "background: rgba(74, 222, 128, 0.1); "
                "border: 1px solid rgba(74, 222, 128, 0.25); "
                "border-radius: 6px; padding: 6px 10px;"
            )
            lay.addWidget(hint)

        self.list = QListWidget()
        self.list.setStyleSheet("""
            QListWidget {
                background: rgba(255,255,255,0.02);
                border: 1px solid rgba(255,255,255,0.06);
                border-radius: 10px;
                outline: none;
                padding: 4px;
            }
            QListWidget::item {
                background: transparent;
                color: #F4F2FA;
                border-radius: 8px;
                padding: 10px 12px;
                margin: 2px;
                font-size: 13px;
            }
            QListWidget::item:hover {
                background: rgba(124,92,255,0.12);
            }
            QListWidget::item:selected {
                background: rgba(124,92,255,0.25);
            }
        """)
        self.list.setVerticalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAsNeeded)
        self.list.setHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff)
        self.list.setMinimumHeight(220)
        self.list.setMaximumHeight(300)
        for s in self._strategies:
            li = QListWidgetItem(s)
            li.setSizeHint(QSize(0, 38))
            self.list.addItem(li)
        if self._strategies:
            self.list.setCurrentRow(0)
        lay.addWidget(self.list)

        btns = QHBoxLayout()
        btns.setSpacing(10)
        btns.addStretch()
        run_btn = QPushButton("Запустить")
        run_btn.setCursor(Qt.CursorShape.PointingHandCursor)
        run_btn.setFixedHeight(40)
        run_btn.setMinimumWidth(120)
        run_btn.setStyleSheet("""
            QPushButton {
                background: rgba(255,255,255,0.06);
                color: #F4F2FA;
                border: 1px solid rgba(255,255,255,0.1);
                border-radius: 10px;
                font-size: 13px;
                font-weight: 500;
            }
            QPushButton:hover {
                background: rgba(255,255,255,0.1);
            }
        """)
        run_btn.clicked.connect(lambda: self._emit("run"))
        install_btn = QPushButton("Установить службу")
        install_btn.setCursor(Qt.CursorShape.PointingHandCursor)
        install_btn.setFixedHeight(40)
        install_btn.setMinimumWidth(150)
        install_btn.setStyleSheet("""
            QPushButton {
                background: #7C5CFF;
                color: #FFFFFF;
                border: none;
                border-radius: 10px;
                font-size: 13px;
                font-weight: 600;
            }
            QPushButton:hover {
                background: #9175FF;
            }
            QPushButton:pressed {
                background: #6847EB;
            }
        """)
        install_btn.clicked.connect(lambda: self._emit("install"))
        btns.addWidget(run_btn)
        btns.addWidget(install_btn)
        lay.addLayout(btns)

        self.card.adjustSize()
        self._center_card()

    def _center_card(self):
        p = self.parent()
        if not p:
            return
        cw = self.card.width()
        ch = self.card.sizeHint().height()
        x = (p.width() - cw) // 2
        y = (p.height() - ch) // 2
        self.card.move(max(0, x), max(0, y))

    def resizeEvent(self, e):
        super().resizeEvent(e)
        self._center_card()

    def _emit(self, action):
        item = self.list.currentItem()
        if not item:
            return
        name = item.text()
        self.chosen.emit(name, action)
        self.close_animated()


class ConfirmModal(ModalOverlay):
    """Confirm replacing existing service."""

    confirmed = pyqtSignal()

    def __init__(self, parent, current_service, new_strategy):
        super().__init__(parent)
        self.card = QFrame(self)
        self.card.setObjectName("ModalCard")
        self.card.setStyleSheet("""
            QFrame#ModalCard {
                background: #1C192B;
                border: 1px solid rgba(255,255,255,0.1);
                border-radius: 16px;
            }
        """)
        self.card.setFixedWidth(440)

        lay = QVBoxLayout(self.card)
        lay.setContentsMargins(24, 22, 24, 20)
        lay.setSpacing(14)

        title = QLabel("Заменить активную службу?")
        title.setStyleSheet(
            "color: #F4F2FA; font-size: 16px; font-weight: 700; "
            "background: transparent;"
        )
        lay.addWidget(title)

        msg = QLabel(
            f"Служба «{current_service}» уже установлена.\n"
            f"Хотите удалить текущую службу и применить стратегию "
            f"«{new_strategy}»?"
        )
        msg.setWordWrap(True)
        msg.setStyleSheet(
            "color: #9E97B8; font-size: 13px; background: transparent;"
        )
        lay.addWidget(msg)

        btns = QHBoxLayout()
        btns.setSpacing(10)
        btns.addStretch()
        cancel = QPushButton("Отмена")
        cancel.setCursor(Qt.CursorShape.PointingHandCursor)
        cancel.setFixedHeight(38)
        cancel.setMinimumWidth(100)
        cancel.setStyleSheet("""
            QPushButton {
                background: transparent;
                color: #9E97B8;
                border: 1px solid rgba(255,255,255,0.1);
                border-radius: 10px;
                font-size: 13px;
            }
            QPushButton:hover {
                background: rgba(255,255,255,0.05);
                color: #F4F2FA;
            }
        """)
        cancel.clicked.connect(self.close_animated)
        confirm = QPushButton("Да, заменить")
        confirm.setCursor(Qt.CursorShape.PointingHandCursor)
        confirm.setFixedHeight(38)
        confirm.setMinimumWidth(130)
        confirm.setStyleSheet("""
            QPushButton {
                background: #F87171;
                color: #FFFFFF;
                border: none;
                border-radius: 10px;
                font-size: 13px;
                font-weight: 600;
            }
            QPushButton:hover {
                background: #FCA5A5;
            }
            QPushButton:pressed {
                background: #EF4444;
            }
        """)
        confirm.clicked.connect(self._on_confirm)
        btns.addWidget(cancel)
        btns.addWidget(confirm)
        lay.addLayout(btns)

        self.card.adjustSize()
        self._center_card()

    def _center_card(self):
        p = self.parent()
        if not p:
            return
        cw = self.card.width()
        ch = self.card.sizeHint().height()
        x = (p.width() - cw) // 2
        y = (p.height() - ch) // 2
        self.card.move(max(0, x), max(0, y))

    def resizeEvent(self, e):
        super().resizeEvent(e)
        self._center_card()

    def _on_confirm(self):
        self.confirmed.emit()
        self.close_animated()
