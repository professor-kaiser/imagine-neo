"""Normalize Python/OpenCV exceptions into the stable JSON error contract."""

import cv2
from app.models.messages import CommandMessage


def _identifier(value: object) -> str | None:
    """Return a valid correlation string up to 256 characters, otherwise None."""
    return value if isinstance(value, str) and 0 < len(value) <= 256 else None


def error_response(error: Exception, message: CommandMessage | None = None) -> dict[str, object]:
    """Create a correlated, JSON-compatible failure envelope with a textual error code.

    Args:
        error (Exception): A protocol, implementation, storage or OpenCV failure.
            cv2.error.code is a native integer and must never replace the string code.
        message (CommandMessage | None): Parsed request; absent for malformed input.
    Returns:
        dict[str, object]: Version 1 error envelope. code is always a nonempty str;
        correlation IDs are str/None and message is bounded valid Unicode text.
        No image arrays or arbitrary exception attributes are serialized.
    """
    if isinstance(error, cv2.error):
        code = "opencv_error"
    else:
        candidate = getattr(error, "code", None)
        code = candidate if isinstance(candidate, str) and candidate.strip() else type(error).__name__
    try:
        details = str(error)
    except Exception:
        details = type(error).__name__
    details = details.encode("utf-8", errors="replace").decode("utf-8")
    if len(details) > 4096:
        details = details[:4096] + " [message truncated]"
    return {"protocol_version": 1, "message_type": "error", "status": "error",
            "request_id": _identifier(message.request_id if message else getattr(error, "request_id", None)),
            "image_id": _identifier(message.image_id if message else getattr(error, "image_id", None)),
            "code": code[:128], "message": details}
