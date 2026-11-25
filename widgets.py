# -*- coding: utf-8 -*-
"""Custom animated widgets with real frosted-glass backdrops."""
from PyQt6.QtWidgets import (
    QWidget, QPushButton, QLabel, QFrame, QVBoxLayout, QHBoxLayout,
    QListWidget, QListWidgetItem, QGraphicsDropShadowEffect, QMenu,
    QSizePolicy
)
from PyQt6.QtCore import (
    Qt, pyqtSignal, pyqtProperty, QPropertyAnimation, QEasingCurve,
    QPoint, QPointF, QRect, QRectF, QSize, QTimer, QVariantAnimation
)
from PyQt6.QtGui import (
    QPainter, QColor, QBrush, QPen, QLinearGradient, QPainterPath, QPixmap
)

from theme import (
    ACCENT, FROST_TINT, FROST_TINT_HOVER, FROST_BORDER, FROST_BORDER_HOVER
)


# ------------------------------------------------------------
# Vector icon factory
# ------------------------------------------------------------
def make_icon_pixmap(kind, size=20, color=None, scale=2):
    """Draw a clean vector icon, return QPixmap with DPR=scale."""
    if color is None:
        color = QColor(244, 242, 250)
    px = int(size * scale)
    pix = QPixmap(px, px)
    pix.fill(Qt.GlobalColor.transparent)
    p = QPainter(pix)
    p.setRenderHint(QPainter.RenderHint.Antialiasing)
    p.scale(scale, scale)

    s = float(size)
    pen = QPen(color, 1.8)
    pen.setCapStyle(Qt.PenCapStyle.RoundCap)
    pen.setJoinStyle(Qt.PenJoinStyle.RoundJoin)
    p.setPen(pen)
    p.setBrush(Qt.BrushStyle.NoBrush)

    if kind == "download":
        p.drawLine(QPointF(s * 0.5, s * 0.20), QPointF(s * 0.5, s * 0.72))
        p.drawLine(QPointF(s * 0.32, s * 0.55), QPointF(s * 0.5, s * 0.73))
        p.drawLine(QPointF(s * 0.68, s * 0.55), QPointF(s * 0.5, s * 0.73))
        p.drawLine(QPointF(s * 0.25, s * 0.86), QPointF(s * 0.75, s * 0.86))
    elif kind == "play":
        path = QPainterPath()
        path.moveTo(s * 0.32, s * 0.20)
        path.lineTo(s * 0.80, s * 0.50)
        path.lineTo(s * 0.32, s * 0.80)
        path.closeSubpath()
        p.fillPath(path, color)
    elif kind == "stop":
        p.fillRect(QRectF(s * 0.28, s * 0.28, s * 0.44, s * 0.44), color)
    elif kind == "folder":
        path = QPainterPath()
        path.moveTo(s * 0.15, s * 0.32)
        path.lineTo(s * 0.40, s * 0.32)
        path.lineTo(s * 0.48, s * 0.42)
        path.lineTo(s * 0.85, s * 0.42)
        path.lineTo(s * 0.85, s * 0.82)
        path.lineTo(s * 0.15, s * 0.82)
        path.closeSubpath()
        p.drawPath(path)
    elif kind == "more":
        p.setPen(Qt.PenStyle.NoPen)
        p.setBrush(color)
        for dx in (-0.26, 0.0, 0.26):
            p.drawEllipse(QPointF(s * 0.5 + s * dx, s * 0.5), s * 0.06, s * 0.06)
    elif kind == "lightning":
        path = QPainterPath()
        path.moveTo(s * 0.58, s * 0.14)
        path.lineTo(s * 0.28, s * 0.56)
        path.lineTo(s * 0.47, s * 0.56)
        path.lineTo(s * 0.41, s * 0.86)
        path.lineTo(s * 0.72, s * 0.44)
        path.lineTo(s * 0.53, s * 0.44)
        path.closeSubpath()
        p.fillPath(path, color)
    elif kind == "plug":
        p.drawLine(QPointF(s * 0.5, s * 0.15), QPointF(s * 0.5, s * 0.45))
        p.drawArc(QRectF(s * 0.22, s * 0.30, s * 0.56, s * 0.56), 60 * 16, 240 * 16)
    elif kind == "external":
        p.drawLine(QPointF(s * 0.28, s * 0.72), QPointF(s * 0.75, s * 0.25))
        p.drawLine(QPointF(s * 0.45, s * 0.25), QPointF(s * 0.75, s * 0.25))
        p.drawLine(QPointF(s * 0.75, s * 0.25), QPointF(s * 0.75, s * 0.55))
    elif kind == "chevron_down":
        p.drawLine(QPointF(s * 0.30, s * 0.42), QPointF(s * 0.50, s * 0.62))
        p.drawLine(QPointF(s * 0.50, s * 0.62), QPointF(s * 0.70, s * 0.42))

    p.end()
    pix.setDevicePixelRatio(scale)
    return pix


# ------------------------------------------------------------
# Frosted-glass helpers
# ------------------------------------------------------------
def _find_canvas(widget):
    """Walk up the parent chain to find RightCanvas (has _blurred_pixmap)."""
    p = widget.parent()
    while p:
        if hasattr(p, "_blurred_pixmap"):
            return p
        p = p.parent()
    return None


def _paint_frosted(painter, widget, rect, radius=12, hover=False):
    """Frosted glass: blurred backdrop + tint + 1px border."""
    path = QPainterPath()
    path.addRoundedRect(QRectF(rect), radius, radius)
    painter.setClipPath(path)

    canvas = _find_canvas(widget)
    if canvas is not None and canvas._blurred_pixmap is not None \
            and not canvas._blurred_pixmap.isNull():
        pos = widget.mapTo(canvas, QPoint(0, 0))
        widget_in_canvas = QRect(pos, rect.size())
        pix_rect = QRect(
            0, 0,
            canvas._blurred_pixmap.width(),
            canvas._blurred_pixmap.height(),
        )
        visible = widget_in_canvas.intersected(pix_rect)
        if not visible.isEmpty():
            target = QRect(visible.topLeft() - pos, visible.size())
            painter.drawPixmap(target, canvas._blurred_pixmap, visible)

    tint = FROST_TINT_HOVER if hover else FROST_TINT
    painter.fillPath(path, QColor(*tint))

    border = FROST_BORDER_HOVER if hover else FROST_BORDER
    painter.setPen(QPen(QColor(*border), 1))
    painter.setBrush(Qt.BrushStyle.NoBrush)
    painter.drawPath(path)


def _apply_shadow(widget):
    sh = QGraphicsDropShadowEffect(widget)
    sh.setBlurRadius(18)
    sh.setOffset(0, 3)
    sh.setColor(QColor(0, 0, 0, 95))
    widget.setGraphicsEffect(sh)


# ------------------------------------------------------------
# HoverButton (accent gradient or frosted glass)
# ------------------------------------------------------------
class HoverButton(QPushButton):
    def __init__(self, text="", icon_kind=None, parent=None,
                 accent=True, height=56):
        super().__init__(text, parent)
        self._accent = accent
        self._danger = False
        self._icon_kind = icon_kind
        self._icon_size = 18
        self._hover = False
        self._radius = 12
        self.setFixedHeight(height)
        self.setCursor(Qt.CursorShape.PointingHandCursor)
        color = "#FFFFFF" if accent else "#F4F2FA"
        weight = "600" if accent else "500"
        self.setStyleSheet(f"""
            QPushButton {{
                background: transparent;
                color: {color};
                border: none;
                font-size: 14px;
                font-weight: {weight};
                padding: 0 22px;
            }}
        """)
        if accent:
            self._shadow = QGraphicsDropShadowEffect(self)
            self._shadow.setBlurRadius(0)
            self._shadow.setOffset(0, 4)
            self._shadow.setColor(QColor(124, 92, 255, 170))
            self.setGraphicsEffect(self._shadow)
            self._shadow_anim = QPropertyAnimation(self._shadow, b"blurRadius")
            self._shadow_anim.setDuration(200)
        else:
            self._shadow = None
            self._shadow_anim = None
            _apply_shadow(self)

    def set_accent_mode(self, mode):
        """Switch the accent gradient: 'accent' (violet) or 'danger' (coral)."""
        self._danger = (mode == "danger")
        self._accent = True
        if self._shadow:
            color = (QColor(248, 113, 113, 170) if self._danger
                     else QColor(124, 92, 255, 170))
            self._shadow.setColor(color)
        self.update()

    def enterEvent(self, e):
        self._hover = True
        if self._accent and self._shadow_anim:
            self._shadow_anim.stop()
            self._shadow_anim.setStartValue(self._shadow.blurRadius())
            self._shadow_anim.setEndValue(28)
            self._shadow_anim.start()
        self.update()
        super().enterEvent(e)

    def leaveEvent(self, e):
        self._hover = False
        if self._accent and self._shadow_anim:
            self._shadow_anim.stop()
            self._shadow_anim.setStartValue(self._shadow.blurRadius())
            self._shadow_anim.setEndValue(0)
            self._shadow_anim.start()
        self.update()
        super().leaveEvent(e)

    def paintEvent(self, e):
        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        rect = self.rect()

        if self._accent:
            path = QPainterPath()
            path.addRoundedRect(QRectF(rect), self._radius, self._radius)
            p.setClipPath(path)
            if self._danger:
                if not self.isEnabled():
                    colors = ("#F87171", "#EF4444", 90)
                elif self.isDown():
                    colors = ("#B91C1C", "#DC2626", 255)
                elif self._hover:
                    colors = ("#FB7185", "#FCA5A5", 255)
                else:
                    colors = ("#F87171", "#EF4444", 255)
            else:
                if not self.isEnabled():
                    colors = ("#7C5CFF", "#8F6BFF", 90)
                elif self.isDown():
                    colors = ("#6847EB", "#6847EB", 255)
                elif self._hover:
                    colors = ("#9175FF", "#A285FF", 255)
                else:
                    colors = ("#7C5CFF", "#8F6BFF", 255)

            first = QColor(colors[0])
            second = QColor(colors[1])
            if colors[2] != 255:
                first.setAlpha(colors[2])
                second.setAlpha(colors[2])
            grad = QLinearGradient(0, 0, rect.width(), 0)
            grad.setColorAt(0, first)
            grad.setColorAt(1, second)
            p.fillPath(path, QBrush(grad))
        else:
            _paint_frosted(p, self, rect, self._radius, hover=self._hover)

        # Draw content (icon + text)
        p.setFont(self.font())
        fm = p.fontMetrics()
        text = self.text()
        text_w = fm.horizontalAdvance(text)
        total_w = text_w + (self._icon_size + 10 if self._icon_kind else 0)
        start_x = max(12, (rect.width() - total_w) // 2)

        if self._icon_kind:
            icon_y = (rect.height() - self._icon_size) // 2
            pix = make_icon_pixmap(self._icon_kind, self._icon_size)
            p.drawPixmap(
                QRect(start_x, icon_y, self._icon_size, self._icon_size), pix
            )
            text_x = start_x + self._icon_size + 10
        else:
            text_x = start_x

        if not self.isEnabled():
            p.setPen(QColor(255, 255, 255, 130))
        else:
            p.setPen(QColor("#FFFFFF") if self._accent else QColor("#F4F2FA"))
        text_rect = QRect(text_x, 0, text_w + 10, rect.height())
        p.drawText(
            text_rect,
            Qt.AlignmentFlag.AlignVCenter | Qt.AlignmentFlag.AlignLeft,
            text,
        )
        p.end()


# ------------------------------------------------------------
# IconButton (frosted square, icon only)
# ------------------------------------------------------------
class IconButton(QPushButton):
    def __init__(self, icon_kind, parent=None, size=48, tooltip=""):
        super().__init__("", parent)
        self._icon_kind = icon_kind
        self._hover = False
        self.setFixedSize(size, size)
        self.setCursor(Qt.CursorShape.PointingHandCursor)
        if tooltip:
            self.setToolTip(tooltip)
        self.setStyleSheet(
            "QPushButton { background: transparent; border: none; }"
        )
        _apply_shadow(self)

    def enterEvent(self, e):
        self._hover = True
        self.update()
        super().enterEvent(e)

    def leaveEvent(self, e):
        self._hover = False
        self.update()
        super().leaveEvent(e)

    def paintEvent(self, e):
        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        rect = self.rect()
        _paint_frosted(p, self, rect, radius=12, hover=self._hover)
        icon_size = 22
        icon_x = (rect.width() - icon_size) // 2
        icon_y = (rect.height() - icon_size) // 2
        pix = make_icon_pixmap(self._icon_kind, icon_size)
        p.drawPixmap(QRect(icon_x, icon_y, icon_size, icon_size), pix)
        p.end()


# ------------------------------------------------------------
# PlainIconButton (no background, hover tint only)
# ------------------------------------------------------------
class PlainIconButton(QPushButton):
    def __init__(self, icon_kind, parent=None, size=24):
        super().__init__("", parent)
        self._icon_kind = icon_kind
        self._hover = False
        self.setFixedSize(size, size)
        self.setCursor(Qt.CursorShape.PointingHandCursor)
        self.setStyleSheet(
            "QPushButton { background: transparent; border: none; }"
        )

    def enterEvent(self, e):
        self._hover = True
        self.update()
        super().enterEvent(e)

    def leaveEvent(self, e):
        self._hover = False
        self.update()
        super().leaveEvent(e)

    def paintEvent(self, e):
        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        if self._hover:
            p.setPen(Qt.PenStyle.NoPen)
            p.setBrush(QColor(255, 255, 255, 22))
            p.drawRoundedRect(self.rect(), 6, 6)
        icon_size = 14
        ix = (self.width() - icon_size) // 2
        iy = (self.height() - icon_size) // 2
        color = QColor(244, 242, 250) if self._hover else QColor(158, 151, 184)
        pix = make_icon_pixmap(self._icon_kind, icon_size, color)
        p.drawPixmap(QRect(ix, iy, icon_size, icon_size), pix)
        p.end()


# ------------------------------------------------------------
# Progress bar
# ------------------------------------------------------------
class AnimatedProgressBar(QWidget):
    def __init__(self, parent=None):
        super().__init__(parent)
        self._value = 0.0
        self.setFixedHeight(8)
        self._anim = QPropertyAnimation(self, b"value")
        self._anim.setDuration(280)
        self._anim.setEasingCurve(QEasingCurve.Type.OutCubic)

    def getValue(self):
        return self._value

    def setValue(self, v):
        self._value = float(v)
        self.update()

    value = pyqtProperty(float, getValue, setValue)

    def animate_to(self, v):
        self._anim.stop()
        self._anim.setStartValue(self._value)
        self._anim.setEndValue(float(v))
        self._anim.start()

    def paintEvent(self, e):
        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        p.setPen(Qt.PenStyle.NoPen)
        p.setBrush(QColor(255, 255, 255, 20))
        p.drawRoundedRect(self.rect(), 4, 4)
        w = int(self.width() * self._value / 100.0)
        if w > 2:
            grad = QLinearGradient(0, 0, self.width(), 0)
            grad.setColorAt(0.0, QColor("#7C5CFF"))
            grad.setColorAt(0.5, QColor("#A64B63"))
            grad.setColorAt(1.0, QColor("#E5987E"))
            p.setBrush(QBrush(grad))
            p.drawRoundedRect(0, 0, w, self.height(), 4, 4)


# ------------------------------------------------------------
# Toggle switch
# ------------------------------------------------------------
class ToggleSwitch(QWidget):
    toggled = pyqtSignal(bool)

    def __init__(self, parent=None, checked=False):
        super().__init__(parent)
        self._checked = checked
        self._pos = 1.0 if checked else 0.0
        self.setFixedSize(44, 24)
        self.setCursor(Qt.CursorShape.PointingHandCursor)
        self._anim = QPropertyAnimation(self, b"pos")
        self._anim.setDuration(200)
        self._anim.setEasingCurve(QEasingCurve.Type.InOutCubic)

    def getPos(self):
        return self._pos

    def setPos(self, v):
        self._pos = v
        self.update()

    pos = pyqtProperty(float, getPos, setPos)

    def isChecked(self):
        return self._checked

    def mousePressEvent(self, e):
        if e.button() == Qt.MouseButton.LeftButton:
            self._checked = not self._checked
            self._anim.stop()
            self._anim.setStartValue(self._pos)
            self._anim.setEndValue(1.0 if self._checked else 0.0)
            self._anim.start()
            self.toggled.emit(self._checked)
        super().mousePressEvent(e)

    def paintEvent(self, e):
        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        p.setPen(Qt.PenStyle.NoPen)
        c1 = QColor(58, 53, 80)
        c2 = QColor(ACCENT)
        t = self._pos
        r = int(c1.red() + (c2.red() - c1.red()) * t)
        g = int(c1.green() + (c2.green() - c1.green()) * t)
        b = int(c1.blue() + (c2.blue() - c1.blue()) * t)
        p.setBrush(QColor(r, g, b))
        p.drawRoundedRect(0, 0, self.width(), self.height(), 12, 12)
        knob_x = 2 + self._pos * (self.width() - self.height())
        p.setBrush(QColor(255, 255, 255))
        p.drawEllipse(int(knob_x), 2, self.height() - 4, self.height() - 4)


# ------------------------------------------------------------
# CustomComboBox (frosted, compact 200px, vector chevron)
# ------------------------------------------------------------
class CustomComboBox(QFrame):
    currentIndexChanged = pyqtSignal(int)

    def __init__(self, parent=None):
        super().__init__(parent)
        self.setCursor(Qt.CursorShape.PointingHandCursor)
        self.setFixedHeight(56)
        self.setFixedWidth(200)
        self.setStyleSheet("QFrame { background: transparent; border: none; }")
        self._hover = False
        _apply_shadow(self)

        lay = QHBoxLayout(self)
        lay.setContentsMargins(14, 6, 14, 6)
        lay.setSpacing(4)
        col = QVBoxLayout()
        col.setSpacing(1)
        self.title_lbl = QLabel("Релиз")
        self.title_lbl.setStyleSheet(
            "color: #9E97B8; font-size: 10px; font-weight: 500; "
            "background: transparent; border: none; letter-spacing: 0.5px;"
        )
        self.value_lbl = QLabel("Загрузка...")
        self.value_lbl.setStyleSheet(
            "color: #F4F2FA; font-size: 12px; font-weight: 600; "
            "background: transparent; border: none;"
        )
        col.addWidget(self.title_lbl)
        col.addWidget(self.value_lbl)
        lay.addLayout(col, 1)
        lay.addSpacing(20)

        self._items = []
        self._current = -1
        self._popup = None
        self._list = None
        self._downloaded = set()

    def enterEvent(self, e):
        self._hover = True
        self.update()
        super().enterEvent(e)

    def leaveEvent(self, e):
        self._hover = False
        self.update()
        super().leaveEvent(e)

    def paintEvent(self, e):
        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        _paint_frosted(p, self, self.rect(), 12, hover=self._hover)

        arrow_x = self.width() - 26
        arrow_y = self.height() / 2
        pen = QPen(QColor(158, 151, 184), 1.8)
        pen.setCapStyle(Qt.PenCapStyle.RoundCap)
        pen.setJoinStyle(Qt.PenJoinStyle.RoundJoin)
        p.setPen(pen)
        p.drawLine(QPointF(arrow_x - 4, arrow_y - 2),
                   QPointF(arrow_x, arrow_y + 2))
        p.drawLine(QPointF(arrow_x, arrow_y + 2),
                   QPointF(arrow_x + 4, arrow_y - 2))
        p.end()

    def mousePressEvent(self, e):
        if e.button() == Qt.MouseButton.LeftButton:
            self._show_popup()
        super().mousePressEvent(e)

    def set_items(self, items, downloaded=None):
        self._items = items
        self._downloaded = downloaded or set()
        if items and self._current < 0:
            self._set_current_internal(0, emit=False)

    def set_downloaded(self, downloaded):
        self._downloaded = downloaded

    def current_tag(self):
        if 0 <= self._current < len(self._items):
            return self._items[self._current]["tag"]
        return None

    def _set_current_internal(self, idx, emit=True):
        if not (0 <= idx < len(self._items)):
            return
        self._current = idx
        item = self._items[idx]
        text = item["tag"]
        if item.get("is_latest"):
            text += "  •  Latest"
        self.value_lbl.setText(text)
        if emit:
            self.currentIndexChanged.emit(idx)

    def set_current(self, idx):
        self._set_current_internal(idx, emit=True)

    def _show_popup(self):
        if not self._items:
            return
        if self._popup is None:
            self._create_popup()
        self._refresh_popup()
        pos = self.mapToGlobal(QPoint(0, self.height() + 6))
        self._popup.move(pos)
        self._popup.setFixedWidth(self.width())
        self._popup.show()

    def _create_popup(self):
        self._popup = QFrame(None, Qt.WindowType.Popup)
        self._popup.setStyleSheet(
            "QFrame { background: #16141F; "
            "border: 1px solid rgba(255,255,255,0.14); "
            "border-radius: 12px; }"
        )
        lay = QVBoxLayout(self._popup)
        lay.setContentsMargins(4, 4, 4, 4)
        lay.setSpacing(0)
        self._list = QListWidget(self._popup)
        self._list.setStyleSheet("""
            QListWidget {
                background: transparent;
                border: none;
                outline: none;
            }
            QListWidget::item {
                background: transparent;
                color: #F4F2FA;
                border-radius: 8px;
                padding: 8px 12px;
                margin: 2px 4px;
                font-size: 12px;
            }
            QListWidget::item:hover {
                background: rgba(124,92,255,0.15);
            }
            QListWidget::item:selected {
                background: rgba(124,92,255,0.25);
            }
        """)
        self._list.setVerticalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAsNeeded)
        self._list.setHorizontalScrollBarPolicy(
            Qt.ScrollBarPolicy.ScrollBarAlwaysOff
        )
        self._list.itemClicked.connect(self._on_item_clicked)
        lay.addWidget(self._list)
        self._popup.setFixedHeight(5 * 40 + 8)

    def _refresh_popup(self):
        self._list.clear()
        for i, item in enumerate(self._items):
            text = item["tag"]
            if item.get("is_latest"):
                text += "  •  Latest"
            if item["tag"] in self._downloaded:
                text += "   ✓"
            li = QListWidgetItem(text)
            li.setData(Qt.ItemDataRole.UserRole, i)
            li.setSizeHint(QSize(0, 36))
            self._list.addItem(li)
            if i == self._current:
                self._list.setCurrentItem(li)

    def _on_item_clicked(self, item):
        idx = item.data(Qt.ItemDataRole.UserRole)
        self._popup.hide()
        self._set_current_internal(idx, emit=True)


# ------------------------------------------------------------
# TabBar
# ------------------------------------------------------------
class TabBar(QWidget):
    currentChanged = pyqtSignal(int)

    def __init__(self, labels, parent=None):
        super().__init__(parent)
        self._labels = labels
        self._current = 0
        self._btn_widgets = []
        self._indicator_x = 0.0
        self._indicator_w = 0.0
        self._anim_x = QPropertyAnimation(self, b"indicatorX")
        self._anim_w = QPropertyAnimation(self, b"indicatorW")
        for a in (self._anim_x, self._anim_w):
            a.setDuration(220)
            a.setEasingCurve(QEasingCurve.Type.OutCubic)

        self.setFixedHeight(44)
        lay = QHBoxLayout(self)
        lay.setContentsMargins(0, 0, 0, 8)
        lay.setSpacing(4)

        for i, lbl in enumerate(labels):
            b = QPushButton(lbl)
            b.setCursor(Qt.CursorShape.PointingHandCursor)
            b.setFixedHeight(30)
            b.setMinimumWidth(80)
            b.clicked.connect(lambda _, idx=i: self.set_current(idx))
            lay.addWidget(b)
            self._btn_widgets.append(b)
        lay.addStretch()
        self._update_buttons_style()

    def getIndicatorX(self):
        return self._indicator_x

    def setIndicatorX(self, v):
        self._indicator_x = v
        self.update()

    indicatorX = pyqtProperty(float, getIndicatorX, setIndicatorX)

    def getIndicatorW(self):
        return self._indicator_w

    def setIndicatorW(self, v):
        self._indicator_w = v
        self.update()

    indicatorW = pyqtProperty(float, getIndicatorW, setIndicatorW)

    def set_current(self, idx):
        if idx == self._current:
            return
        self._current = idx
        self._update_buttons_style()
        self._animate_indicator()
        self.currentChanged.emit(idx)

    def _update_buttons_style(self):
        for i, b in enumerate(self._btn_widgets):
            if i == self._current:
                b.setStyleSheet(
                    "QPushButton { background: transparent; color: #F4F2FA; "
                    "font-weight: 600; font-size: 13px; border: none; "
                    "padding: 4px 12px; }"
                )
            else:
                b.setStyleSheet(
                    "QPushButton { background: transparent; color: #9E97B8; "
                    "font-weight: 500; font-size: 13px; border: none; "
                    "padding: 4px 12px; }"
                    "QPushButton:hover { color: #F4F2FA; }"
                )

    def _animate_indicator(self):
        if not self._btn_widgets:
            return
        b = self._btn_widgets[self._current]
        self._anim_x.stop()
        self._anim_x.setStartValue(self._indicator_x)
        self._anim_x.setEndValue(float(b.x() + 12))
        self._anim_x.start()
        self._anim_w.stop()
        self._anim_w.setStartValue(self._indicator_w)
        self._anim_w.setEndValue(float(b.width() - 24))
        self._anim_w.start()

    def _init_indicator(self):
        if not self._btn_widgets:
            return
        b = self._btn_widgets[self._current]
        self._indicator_x = float(b.x() + 12)
        self._indicator_w = float(b.width() - 24)
        self.update()

    def showEvent(self, e):
        super().showEvent(e)
        QTimer.singleShot(0, self._init_indicator)

    def resizeEvent(self, e):
        super().resizeEvent(e)
        QTimer.singleShot(0, self._init_indicator)

    def paintEvent(self, e):
        super().paintEvent(e)
        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        p.setPen(Qt.PenStyle.NoPen)
        p.setBrush(QColor(ACCENT))
        if self._indicator_w > 0:
            p.drawRoundedRect(
                int(self._indicator_x), self.height() - 4,
                int(self._indicator_w), 2, 1, 1
            )


# ------------------------------------------------------------
# ConsoleBar (compact one-line frosted console, vector dot)
# ------------------------------------------------------------
class ConsoleBar(QFrame):
    def __init__(self, parent=None):
        super().__init__(parent)
        self.setFixedHeight(44)
        self.setStyleSheet("QFrame { background: transparent; border: none; }")
        self._dot_color = QColor(107, 100, 128)
        self._log_text = "Ожидание..."
        self._text_opacity = 1.0
        self._fade_anim = None
        _apply_shadow(self)

    def set_dot_color(self, color_hex):
        self._dot_color = QColor(color_hex)
        self.update()

    def push_log(self, text):
        self._log_text = text
        self._text_opacity = 0.0
        self.update()
        if self._fade_anim is not None:
            self._fade_anim.stop()
        self._fade_anim = QVariantAnimation(self)
        self._fade_anim.setDuration(220)
        self._fade_anim.setStartValue(0.0)
        self._fade_anim.setEndValue(1.0)
        self._fade_anim.valueChanged.connect(self._on_fade)
        self._fade_anim.start()

    def _on_fade(self, v):
        self._text_opacity = float(v)
        self.update()

    def paintEvent(self, e):
        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        rect = self.rect()
        _paint_frosted(p, self, rect, 12, hover=False)

        p.setPen(Qt.PenStyle.NoPen)
        p.setBrush(self._dot_color)
        p.drawEllipse(QPointF(20, rect.height() / 2), 4, 4)

        p.setPen(QColor(184, 178, 204, int(255 * self._text_opacity)))
        f = p.font()
        f.setFamily("Consolas")
        f.setPointSize(10)
        p.setFont(f)
        tr = QRect(38, 0, rect.width() - 50, rect.height())
        p.drawText(
            tr,
            Qt.AlignmentFlag.AlignVCenter | Qt.AlignmentFlag.AlignLeft,
            self._log_text,
        )
        p.end()


# ------------------------------------------------------------
# ProductCard (frosted active, vector dot + 3-dot menu)
# ------------------------------------------------------------
class ProductCard(QFrame):
    clicked = pyqtSignal(str)
    menu_requested = pyqtSignal(str)

    def __init__(self, product_id, icon_kind, name, parent=None):
        super().__init__(parent)
        self.product_id = product_id
        self._active = False
        self._hover = False
        self.setCursor(Qt.CursorShape.PointingHandCursor)
        self.setFixedHeight(58)
        self.setStyleSheet("QFrame { background: transparent; border: none; }")

        lay = QHBoxLayout(self)
        lay.setContentsMargins(12, 9, 10, 9)
        lay.setSpacing(10)

        self.icon_lbl = QLabel()
        self.icon_lbl.setFixedSize(24, 24)
        self.icon_lbl.setStyleSheet("background: transparent; border: none;")
        self.icon_lbl.setPixmap(make_icon_pixmap(icon_kind, 20))
        lay.addWidget(self.icon_lbl)

        col = QVBoxLayout()
        col.setSpacing(3)
        self.name_lbl = QLabel(name)
        self.name_lbl.setStyleSheet(
            "color: #F4F2FA; font-size: 13px; font-weight: 600; "
            "background: transparent; border: none;"
        )
        col.addWidget(self.name_lbl)

        status_row = QHBoxLayout()
        status_row.setSpacing(6)
        status_row.setContentsMargins(0, 0, 0, 0)
        self.dot_lbl = QLabel()
        self.dot_lbl.setFixedSize(8, 8)
        self.dot_lbl.setStyleSheet(
            "background: #6B6480; border-radius: 4px; border: none;"
        )
        status_row.addWidget(self.dot_lbl)
        self.status_lbl = QLabel("Не скачано")
        self.status_lbl.setStyleSheet(
            "color: #6B6480; font-size: 11px; "
            "background: transparent; border: none;"
        )
        status_row.addWidget(self.status_lbl)
        status_row.addStretch()
        col.addLayout(status_row)
        lay.addLayout(col, 1)

        self.menu_btn = PlainIconButton("more", size=24)
        self.menu_btn.clicked.connect(self._show_menu)
        lay.addWidget(self.menu_btn)

    def _show_menu(self):
        menu = QMenu(self)
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
        a1 = menu.addAction("Открыть папку")
        a2 = menu.addAction("Проверить обновления")
        a3 = menu.addAction("Настройки")
        chosen = menu.exec(
            self.menu_btn.mapToGlobal(QPoint(0, self.menu_btn.height() + 4))
        )
        if chosen == a1:
            self.menu_requested.emit("open_folder")
        elif chosen == a2:
            self.menu_requested.emit("check_updates")
        elif chosen == a3:
            self.menu_requested.emit("settings")

    def set_status(self, text, color_hex="#6B6480"):
        self.status_lbl.setText(text)
        self.status_lbl.setStyleSheet(
            f"color: {color_hex}; font-size: 11px; "
            "background: transparent; border: none;"
        )
        self.dot_lbl.setStyleSheet(
            f"background: {color_hex}; border-radius: 4px; border: none;"
        )
        self.update()

    def set_active(self, active):
        self._active = active
        self.update()

    def enterEvent(self, e):
        self._hover = True
        self.update()
        super().enterEvent(e)

    def leaveEvent(self, e):
        self._hover = False
        self.update()
        super().leaveEvent(e)

    def paintEvent(self, e):
        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        rect = self.rect()
        if self._active:
            _paint_frosted(p, self, rect, 10, hover=self._hover)
        elif self._hover:
            path = QPainterPath()
            path.addRoundedRect(QRectF(rect), 10, 10)
            p.fillPath(path, QColor(255, 255, 255, 14))
        p.end()

    def mousePressEvent(self, e):
        if e.button() == Qt.MouseButton.LeftButton:
            self.clicked.emit(self.product_id)
        super().mousePressEvent(e)


# ------------------------------------------------------------
# DevPlaceholderCard (frosted "В разработке")
# ------------------------------------------------------------
class DevPlaceholderCard(QFrame):
    def __init__(self, parent=None):
        super().__init__(parent)
        self.setFixedSize(340, 130)
        self.setStyleSheet("QFrame { background: transparent; border: none; }")
        _apply_shadow(self)

        lay = QVBoxLayout(self)
        lay.setAlignment(Qt.AlignmentFlag.AlignCenter)
        lbl = QLabel("В разработке")
        lbl.setStyleSheet(
            "color: #9E97B8; font-size: 16px; font-weight: 600; "
            "background: transparent; border: none; letter-spacing: 0.5px;"
        )
        lbl.setAlignment(Qt.AlignmentFlag.AlignCenter)
        lay.addWidget(lbl)

    def paintEvent(self, e):
        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        _paint_frosted(p, self, self.rect(), 12, hover=False)
        p.end()
