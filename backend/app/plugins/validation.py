"""Reusable validation for plugin inputs and BGRA results."""

import numpy as np
from app.models.image_types import ImageArray, Rect

MAX_PLUGIN_IMAGES = 64


def validate_image(image: ImageArray, name: str = "image") -> None:
    """Raise ValueError unless image is a nonempty uint8 BGRA array.

    Channel order and straight alpha are semantic requirements. Strided arrays are
    accepted; the executor makes contiguous independent copies for transport.
    """
    if (not isinstance(image, np.ndarray) or image.dtype != np.uint8
            or image.ndim != 3 or image.shape[2] != 4 or min(image.shape[:2]) < 1):
        raise ValueError(f"{name} must be a nonempty uint8 BGRA image")
    from app.ipc.protocol import validate_dimensions
    height, width, channels = image.shape
    validate_dimensions(width, height, channels, width * 4, image.nbytes)


def validate_image_selection(image: ImageArray, selection: Rect | None = None,
                             masks: list[ImageArray] | None = None) -> None:
    """Validate image, nullable Rect and OpenCV uint8 single-channel masks.

    Masks must have the same dimensions as the image.
    No input is modified; invalid data raises ValueError with a field name.
    """
    validate_image(image)
    if selection is not None:
        if not isinstance(selection, Rect):
            raise ValueError("selection must be a Rect or None")
        if selection.x + selection.width > image.shape[1] or selection.y + selection.height > image.shape[0]:
            raise ValueError("Selection extends outside the image")
    if masks is not None:
        if not isinstance(masks, list) or len(masks) > MAX_PLUGIN_IMAGES:
            raise ValueError(f"masks must be a list of at most {MAX_PLUGIN_IMAGES} OpenCV masks")
        for index, mask in enumerate(masks):
            if (not isinstance(mask, np.ndarray) or mask.dtype != np.uint8
                    or mask.ndim != 2 or mask.shape != image.shape[:2]):
                raise ValueError(f"masks[{index}] must be a uint8 single-channel mask with image dimensions")
