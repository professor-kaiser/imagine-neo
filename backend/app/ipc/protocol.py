"""Strict JSON-line protocol validation, bounded independently of pixel size."""

import json
import re
from typing import Any
from app.models.messages import CommandMessage, ImportImageMessage, GrayscaleMessage, OperationMessage, PixelBuffer, PluginResultsMessage
from app.models.image_types import Rect
from app.models.operations import ALL_OPERATIONS, IMAGE_OPERATIONS, normalize_parameters
from app.models.transforms import TRANSFORM_OPERATIONS

PROTOCOL_VERSION = 1
MAX_LINE_BYTES = 65536
MAX_IMAGE_BYTES = 512 * 1024 * 1024
MAX_DIMENSION = 32768


class ProtocolError(ValueError):
    """Malformed or unsupported version 1 envelope with best-effort correlation."""

    def __init__(self, message: str, request_id: str | None = None, image_id: str | None = None) -> None:
        """Store message (str) and optional validated correlation IDs (str or None)."""
        super().__init__(message)
        self.request_id = request_id
        self.image_id = image_id


def validate_dimensions(width: int, height: int, channels: int, row_stride_bytes: int, buffer_size_bytes: int) -> None:
    """Validate a tightly packed uint8 BGRA8 (height, width, 4) byte layout.

    Args:
        width (int): Pixel columns, 1..32768.
        height (int): Pixel rows, 1..32768.
        channels (int): Exactly four components per pixel.
        row_stride_bytes (int): Exactly width * 4 bytes.
        buffer_size_bytes (int): Exactly height * width * 4, at most 512 MiB.
    Returns:
        None.
    Raises:
        ValueError: Noninteger values, unsupported dimensions, stride or size.
    """
    if any(type(v) is not int for v in (width, height, channels, row_stride_bytes, buffer_size_bytes)):
        raise ValueError("Pixel dimensions must be integers")
    if not (0 < width <= MAX_DIMENSION and 0 < height <= MAX_DIMENSION and channels == 4):
        raise ValueError("Expected positive BGRA8 dimensions with four channels")
    if row_stride_bytes != width * 4 or buffer_size_bytes != height * row_stride_bytes:
        raise ValueError("Noncanonical stride or buffer size")
    if buffer_size_bytes > MAX_IMAGE_BYTES:
        raise ValueError("Image exceeds the 512 MiB transfer limit")


def _text(data: dict[str, Any], key: str, maximum: int = 256) -> str:
    """Return required nonempty string data[key], up to maximum characters; raise ValueError."""
    value = data.get(key)
    if not isinstance(value, str) or not value or len(value) > maximum:
        raise ValueError(f"Invalid {key}")
    return value


def _unique_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    """Build a JSON object from key/value pairs; raise ValueError for duplicate keys."""
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"Duplicate JSON key: {key}")
        result[key] = value
    return result


def _pixel_buffer(descriptor: object) -> PixelBuffer:
    """Validate descriptor (JSON object) and return a canonical BGRA8 mapping description.

    The referenced array has shape (height, width, 4), dtype uint8 and packed rows.
    Raises ValueError/TypeError for an invalid name, shape, format or descriptor fields.
    """
    if not isinstance(descriptor, dict):
        raise ValueError("Pixel buffer descriptor must be an object")
    pixels = PixelBuffer(**descriptor)
    if not isinstance(pixels.mapping_name, str) or not re.fullmatch(r"PhotoLab_Image_[A-Za-z0-9_-]{1,100}", pixels.mapping_name):
        raise ValueError("Invalid mapping name")
    if pixels.dtype != "uint8" or pixels.pixel_format != "BGRA8":
        raise ValueError("Only uint8 BGRA8 is supported")
    validate_dimensions(pixels.width, pixels.height, pixels.channels, pixels.row_stride_bytes, pixels.buffer_size_bytes)
    return pixels


def parse_command(line: bytes) -> CommandMessage:
    """Decode one UTF-8 JSON command without pixels.

    Args:
        line (bytes): One JSON line, optional trailing LF; at most 65536 bytes.
    Returns:
        CommandMessage: Validated envelope; imports and grayscale commands contain
        BGRA8 mapping metadata, and grayscale may include an image-pixel rectangle.
    Raises:
        ProtocolError: Invalid UTF-8/JSON, version, type, identity or pixel descriptor.
    """
    data: dict[str, Any] = {}
    try:
        if len(line) > MAX_LINE_BYTES:
            raise ValueError("Command exceeds line limit")
        decoded = json.loads(line.decode("utf-8"), object_pairs_hook=_unique_object)
        if not isinstance(decoded, dict):
            raise ValueError("Command must be a JSON object")
        data = decoded
        if type(data.get("protocol_version")) is not int or data["protocol_version"] != 1:
            raise ValueError("Unsupported protocol version")
        kind, request_id = _text(data, "message_type"), _text(data, "request_id")
        base = {"protocol_version", "message_type", "request_id"}
        if kind == "import_image":
            if set(data) != base | {"batch_id", "image_id", "file_name", "pixel_buffer"}:
                raise ValueError("Unexpected or missing import field")
            pixels = _pixel_buffer(data["pixel_buffer"])
            return ImportImageMessage(protocol_version=1, message_type=kind, request_id=request_id,
                                      image_id=_text(data, "image_id"), batch_id=_text(data, "batch_id"),
                                      file_name=_text(data, "file_name", 1024), pixel_buffer=pixels)
        if kind == "to_grayscale":
            required = base | {"image_id", "result_image_id", "file_name", "output_buffer"}
            optional = {"selection"}
            if not required <= set(data) or set(data) - required - optional:
                raise ValueError("Unexpected or missing grayscale field")
            image_id, result_image_id = _text(data, "image_id"), _text(data, "result_image_id")
            if image_id == result_image_id:
                raise ValueError("Grayscale requires a new result image identity")
            raw_selection = data.get("selection")
            if kind == "run_operation" and operation in TRANSFORM_OPERATIONS and raw_selection is not None:
                raise ValueError("Transforms operate on the entire image, without a selection")
            if raw_selection is not None and not isinstance(raw_selection, dict):
                raise ValueError("selection must be a rectangle object or null")
            selection = Rect(**raw_selection) if raw_selection is not None else None
            return GrayscaleMessage(protocol_version=1, message_type=kind, request_id=request_id,
                                    image_id=image_id, result_image_id=result_image_id,
                                    file_name=_text(data, "file_name", 1024),
                                    output_buffer=_pixel_buffer(data["output_buffer"]), selection=selection)
        if kind in {"read_plugin_results", "release_plugin_results"}:
            required = base | {"result_image_id"}
            if kind == "read_plugin_results":
                required.add("output_buffers")
            if set(data) != required:
                raise ValueError("Unexpected or missing plugin transfer field")
            buffers = data.get("output_buffers", [])
            if not isinstance(buffers, list) or len(buffers) > 64 or kind == "read_plugin_results" and not buffers:
                raise ValueError("Expected 1..64 output buffers")
            return PluginResultsMessage(protocol_version=1, message_type=kind, request_id=request_id,
                result_image_id=_text(data, "result_image_id"),
                output_buffers=tuple(_pixel_buffer(p) for p in buffers))
        if kind == "run_operation":
            operation = _text(data, "operation")
            if operation not in ALL_OPERATIONS:
                raise ValueError("Unknown operation")
            required = base | {"image_id", "operation", "params"}
            if operation in IMAGE_OPERATIONS:
                required |= {"result_image_id", "file_name"}
                if operation != "run_plugin":
                    required.add("output_buffer")
            if operation == "apply_drawing":
                required.add("mask_buffer")
                if data.get("selection") is not None:
                    raise ValueError("Drawing uses a mask, not a rectangular selection")
            optional = {"selection"} | ({"mask_buffers", "output_buffer"} if operation == "run_plugin" else set())
            if not required <= set(data) or set(data) - required - optional:
                raise ValueError("Unexpected or missing operation field")
            image_id = _text(data, "image_id")
            output = None
            result_id = file_name = None
            if operation in IMAGE_OPERATIONS:
                result_id, file_name = _text(data, "result_image_id"), _text(data, "file_name", 1024)
                if result_id == image_id:
                    raise ValueError("Processing requires a new result image identity")
                output = _pixel_buffer(data["output_buffer"]) if "output_buffer" in data else None
            raw_selection = data.get("selection")
            if kind == "run_operation" and operation in TRANSFORM_OPERATIONS and raw_selection is not None:
                raise ValueError("Transforms operate on the entire image, without a selection")
            if raw_selection is not None and not isinstance(raw_selection, dict):
                raise ValueError("selection must be a rectangle object or null")
            masks = data.get("mask_buffers", [])
            if not isinstance(masks, list) or len(masks) > 64:
                raise ValueError("mask_buffers must contain at most 64 BGRA buffers")
            return OperationMessage(protocol_version=1, message_type=kind, request_id=request_id,
                                    image_id=image_id, operation=operation,
                                    params=normalize_parameters(operation, data["params"]),
                                    selection=Rect(**raw_selection) if raw_selection is not None else None,
                                    result_image_id=result_id, file_name=file_name, output_buffer=output,
                                    mask_buffer=_pixel_buffer(data["mask_buffer"]) if operation == "apply_drawing" else None,
                                    mask_buffers=tuple(_pixel_buffer(p) for p in masks))
        if kind not in {"remove_image", "ping", "list_images", "list_plugins", "shutdown"}:
            raise ValueError("Unsupported command")
        required = base | ({"image_id"} if kind == "remove_image" else set())
        if set(data) != required:
            raise ValueError("Unexpected or missing command field")
        return CommandMessage(1, kind, request_id, _text(data, "image_id") if kind == "remove_image" else None)
    except (ValueError, TypeError, KeyError, RecursionError, OverflowError) as error:
        request = data.get("request_id")
        identity = data.get("image_id")
        raise ProtocolError(str(error), request if isinstance(request, str) and len(request) <= 256 else None,
                            identity if isinstance(identity, str) and len(identity) <= 256 else None) from error


def encode_message(message: dict[str, object]) -> bytes:
    """Encode message (JSON-compatible dict) as UTF-8 plus LF; raise ValueError above 64 KiB."""
    encoded = json.dumps(message, ensure_ascii=False, separators=(",", ":"), allow_nan=False).encode("utf-8") + b"\n"
    if len(encoded) > MAX_LINE_BYTES:
        raise ValueError("Response exceeds line limit")
    return encoded

