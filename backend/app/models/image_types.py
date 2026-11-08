"""Canonical arrays are contiguous uint8 BGRA8 with shape (height, width, 4)."""

from dataclasses import dataclass
from datetime import datetime
import numpy as np
import numpy.typing as npt

ImageArray = npt.NDArray[np.uint8]


@dataclass(frozen=True, slots=True)
class Rect:
    """Image-pixel rectangle; nonnegative origin and strictly positive extent."""

    x: int
    y: int
    width: int
    height: int

    def __post_init__(self) -> None:
        """Validate integer pixel coordinates; raise ValueError on invalid bounds."""
        values = (self.x, self.y, self.width, self.height)
        if any(type(v) is not int for v in values) or min(self.x, self.y) < 0 or min(self.width, self.height) <= 0:
            raise ValueError("Invalid image rectangle")


@dataclass(frozen=True, slots=True)
class ColorRGBA:
    """Straight-alpha red, green, blue and alpha integer components in [0, 255]."""

    r: int
    g: int
    b: int
    a: int = 255

    def __post_init__(self) -> None:
        """Validate integer components; raise ValueError outside [0, 255]."""
        if any(type(v) is not int or not 0 <= v <= 255 for v in (self.r, self.g, self.b, self.a)):
            raise ValueError("RGBA components must be integers in [0, 255]")


@dataclass(frozen=True, slots=True)
class ImageMetadata:
    """Dimensions and identity of one BGRA8 image in session storage."""

    image_id: str
    file_name: str
    width: int
    height: int
    imported_at: datetime
    pixel_format: str = "BGRA8"


@dataclass(frozen=True, slots=True)
class ImageRecord:
    """Metadata and a registry-owned, read-only (height, width, 4) uint8 array."""

    metadata: ImageMetadata
    pixels: ImageArray


@dataclass(frozen=True, slots=True)
class ColorFrequency:
    """RGBA color, positive pixel count and relative frequency in [0, 1]."""

    color: ColorRGBA
    count: int
    frequency: float

    def __post_init__(self) -> None:
        """Raise ValueError for a nonpositive count or invalid frequency."""
        if type(self.count) is not int or self.count <= 0 or not 0 <= self.frequency <= 1:
            raise ValueError("Invalid color frequency")

