# -*- coding: utf-8 -*-
"""Theme colors, QSS styles, constants."""

BG_MAIN = "#12111A"
BG_DEEP = "#0E0D15"
BG_SIDEBAR = "#181524"
BG_CARD_SOLID = "#1C192B"
BORDER = "rgba(255,255,255,0.08)"
ACCENT = "#7C5CFF"
ACCENT_HOVER = "#9175FF"
ACCENT_PRESSED = "#6847EB"
TEXT_PRIMARY = "#F4F2FA"
TEXT_SECONDARY = "#9E97B8"
TEXT_MUTED = "#6B6480"
SUCCESS = "#4ADE80"
DANGER = "#F87171"

GRADIENT_TOP = "#1E1938"
GRADIENT_MID = "#4A2559"
GRADIENT_WARM = "#A64B63"
GRADIENT_BOTTOM = "#E5987E"

# Frosted glass
FROST_TINT = (20, 19, 32, 115)         # base overlay
FROST_TINT_HOVER = (20, 19, 32, 145)   # hover overlay
FROST_BORDER = (255, 255, 255, 36)     # 1px border
FROST_BORDER_HOVER = (255, 255, 255, 60)

TITLE_BAR_COLOR = "#12111A"

GLOBAL_QSS = """
QWidget {
    color: #F4F2FA;
    font-family: 'Segoe UI', 'Inter', 'San Francisco', sans-serif;
    font-size: 13px;
}
QToolTip {
    background: #1C192B;
    color: #F4F2FA;
    border: 1px solid rgba(255,255,255,0.1);
    border-radius: 6px;
    padding: 6px 8px;
}
QScrollBar:vertical {
    background: transparent;
    width: 6px;
    margin: 2px;
}
QScrollBar::handle:vertical {
    background: rgba(255,255,255,0.15);
    border-radius: 3px;
    min-height: 24px;
}
QScrollBar::handle:vertical:hover {
    background: rgba(124,92,255,0.5);
}
QScrollBar::add-line:vertical, QScrollBar::sub-line:vertical {
    height: 0px;
}
QScrollBar::add-page:vertical, QScrollBar::sub-page:vertical {
    background: transparent;
}
QScrollBar:horizontal {
    background: transparent;
    height: 6px;
}
QScrollBar::handle:horizontal {
    background: rgba(255,255,255,0.15);
    border-radius: 3px;
    min-width: 24px;
}
QScrollBar::add-line:horizontal, QScrollBar::sub-line:horizontal {
    width: 0px;
}
"""
