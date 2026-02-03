"""Console helpers for friendly CLI bridge logging."""

from __future__ import annotations

import logging
import os
import sys
from datetime import datetime
from typing import Any


_LEVEL_LABELS = {
    logging.DEBUG: "DEBUG",
    logging.INFO: "INFO",
    logging.WARNING: "WARN",
    logging.ERROR: "ERROR",
    logging.CRITICAL: "FATAL",
}

_COLOR_CODES = {
    "reset": "\x1b[0m",
    "dim": "\x1b[2m",
    "red": "\x1b[31m",
    "yellow": "\x1b[33m",
    "cyan": "\x1b[36m",
    "green": "\x1b[32m",
}

_LEVEL_COLORS = {
    logging.DEBUG: "dim",
    logging.INFO: "cyan",
    logging.WARNING: "yellow",
    logging.ERROR: "red",
    logging.CRITICAL: "red",
}


def supports_color() -> bool:
    if os.environ.get("NO_COLOR"):
        return False
    return sys.stdout.isatty()


def _style(text: str, color: str | None = None, *, bold: bool = False, enable: bool = True) -> str:
    if not enable:
        return text
    if not color and not bold:
        return text
    parts = []
    if bold:
        parts.append("\x1b[1m")
    if color:
        parts.append(_COLOR_CODES.get(color, ""))
    parts.append(text)
    parts.append(_COLOR_CODES["reset"])
    return "".join(parts)


def _format_value(value: Any, max_len: int = 120) -> str:
    if isinstance(value, dict):
        return f"dict({len(value)})"
    if isinstance(value, (list, tuple, set)):
        return f"{type(value).__name__}({len(value)})"
    text = str(value)
    if len(text) > max_len:
        return text[: max_len - 3] + "..."
    return text


def format_event(event: str, message: str, **fields: Any) -> str:
    event_label = event.strip().upper()
    parts = [f"{event_label}: {message}"]
    for key in sorted(fields.keys()):
        value = fields[key]
        if value is None:
            continue
        parts.append(f"{key}={_format_value(value)}")
    return " ".join(parts)


def log_event(logger: logging.Logger, level: int, event: str, message: str, **fields: Any) -> None:
    logger.log(level, format_event(event, message, **fields))


class FriendlyFormatter(logging.Formatter):
    def __init__(self, use_color: bool = True) -> None:
        super().__init__()
        self._use_color = use_color

    def format(self, record: logging.LogRecord) -> str:
        timestamp = datetime.now().strftime("%H:%M:%S")
        label = _LEVEL_LABELS.get(record.levelno, record.levelname)
        color = _LEVEL_COLORS.get(record.levelno)
        label_text = _style(label.ljust(5), color=color, bold=True, enable=self._use_color)
        message = record.getMessage()
        return f"{timestamp} {label_text} {message}"
