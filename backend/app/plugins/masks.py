"""OpenCV masks; BGRA is only the shared-memory transport envelope."""
import numpy as np
from app.models.image_types import Rect
from .validation import validate_image, validate_image_selection


def validate_mask_outside_value(value):
    if type(value) is not int or value not in (0, 255):
        raise ValueError("mask_outside_value must be 0 (black) or 255 (white)")
    return value


def encode_mask(mask, selection: Rect | None = None, outside_value: int = 0):
    """Encode uint8 (H,W) as opaque BGRA, with a black or white exterior.

    Coordinates stay relative to the full image. Neither the input nor its size
    changes; passing None keeps all mask pixels.
    """
    validate_mask_outside_value(outside_value)
    if (not isinstance(mask, np.ndarray) or mask.dtype != np.uint8
            or mask.ndim != 2 or min(mask.shape) < 1):
        raise ValueError("mask must be a nonempty uint8 single-channel array")
    height, width = mask.shape
    from app.ipc.protocol import validate_dimensions
    validate_dimensions(width, height, 4, width * 4, width * height * 4)
    if selection is not None:
        if not isinstance(selection, Rect):
            raise ValueError("selection must be a Rect or None")
        if selection.x + selection.width > width or selection.y + selection.height > height:
            raise ValueError("Selection extends outside the mask")
    r = selection or Rect(0, 0, width, height)
    area = np.s_[r.y:r.y + r.height, r.x:r.x + r.width]
    encoded = np.full((height, width, 4), outside_value, dtype=np.uint8)
    encoded[:, :, 3] = 255
    encoded[area][:, :, :3] = mask[area][:, :, None]
    return encoded


def decode_mask(encoded):
    validate_image(encoded, "mask buffer")
    if (not np.array_equal(encoded[:, :, 0], encoded[:, :, 1])
            or not np.array_equal(encoded[:, :, 0], encoded[:, :, 2])
            or not np.all(encoded[:, :, 3] == 255)):
        raise ValueError("Mask buffers require B=G=R and alpha=255")
    return encoded[:, :, 0].copy(order="C")


def apply_masked_result(image, result, selection=None, masks=None):
    """Copy processed pixels where any selected mask is nonzero, intersected with ROI.

    No masks means the entire ROI. A supplied all-zero mask means no active pixels.
    Plugins may use each mask independently instead of this union helper.
    """
    validate_image_selection(image, selection, masks)
    validate_image(result, "result")
    if result.shape != image.shape:
        raise ValueError("Masked results must have the source dimensions")
    active = np.ones(image.shape[:2], dtype=bool) if not masks else np.logical_or.reduce([m != 0 for m in masks])
    if selection is not None:
        region = np.zeros(image.shape[:2], dtype=bool)
        r = selection
        region[r.y:r.y+r.height, r.x:r.x+r.width] = True
        active &= region
    output = image.copy(order="C")
    output[active] = result[active]
    return output
