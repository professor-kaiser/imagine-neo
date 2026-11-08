"""Validated immutable protocol version 1 command models."""

from dataclasses import dataclass
from app.models.image_types import Rect


@dataclass(frozen=True, slots=True)
class PixelBuffer:
    """Windows mapping descriptor for tightly packed, straight-alpha BGRA8 pixels."""

    mapping_name: str
    width: int
    height: int
    channels: int
    dtype: str
    pixel_format: str
    row_stride_bytes: int
    buffer_size_bytes: int


@dataclass(frozen=True, slots=True)
class CommandMessage:
    """Common command envelope; image_id is absent on session commands."""

    protocol_version: int
    message_type: str
    request_id: str
    image_id: str | None = None


@dataclass(frozen=True, slots=True, kw_only=True)
class ImportImageMessage(CommandMessage):
    """Import identity plus mapping metadata, never encoded image pixels."""

    batch_id: str
    file_name: str
    pixel_buffer: PixelBuffer


@dataclass(frozen=True, slots=True, kw_only=True)
class GrayscaleMessage(CommandMessage):
    """Create a derived grayscale image and return pixels through a UI-owned mapping."""

    result_image_id: str
    file_name: str
    output_buffer: PixelBuffer
    selection: Rect | None = None


@dataclass(frozen=True, slots=True, kw_only=True)
class OperationMessage(CommandMessage):
    """Generic prepared operation; image results have a UI-owned mapping, analyses do not."""

    operation: str
    params: dict[str, object]
    selection: Rect | None = None
    result_image_id: str | None = None
    file_name: str | None = None
    output_buffer: PixelBuffer | None = None
    mask_buffer: PixelBuffer | None = None
    mask_buffers: tuple[PixelBuffer, ...] = ()


@dataclass(frozen=True, slots=True, kw_only=True)
class PluginResultsMessage(CommandMessage):
    """Transfer or release an already computed plugin batch."""
    result_image_id: str
    output_buffers: tuple[PixelBuffer, ...] = ()


