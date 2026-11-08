"""TEST ONLY: inject transport sentinels without editing or implementing production algorithms.

Used exclusively by scripts/test_ui.cmd <configuration> <mode> fixtures. Image callbacks
return an unchanged copy; analysis callbacks return fixed typed values. These are not image
processing algorithms and must never be used by a normal PhotoLab launch.
"""

from pathlib import Path
import sys
import cv2
import numpy as np

sys.path.insert(0, str(Path(__file__).parents[1]))

from app.main import main
from app.models.image_types import ImageArray, Rect, ColorRGBA, ColorFrequency
from app.processing import filters, selection_ops, color_tools, drawing, transforms
from app.models.transforms import TRANSFORM_OPERATIONS, output_size


def image_sentinel(image: ImageArray, **kwargs: object) -> ImageArray:
    """Return a copy of image (uint8 BGRA8, shape h,w,4) to validate transport only."""
    return image.copy()


def transform_sentinel(image: ImageArray, **kwargs: object) -> ImageArray:
    """Test-only buffer with the requested dimensions; deliberately no pixel algorithm."""
    width, height = output_size("rotate_scale_image", kwargs, image.shape[1], image.shape[0])
    return np.full((height, width, 4), [31, 47, 59, 127], dtype=np.uint8)


def color_sentinel(image: ImageArray, x: int, y: int) -> ColorRGBA:
    """Return fixed ColorRGBA for valid image coordinates; no sampling algorithm is provided."""
    return ColorRGBA(71, 83, 97, 109)


def palette_sentinel(image: ImageArray, selection: Rect | None = None, max_colors: int = 16) -> list[ColorFrequency]:
    """Return a fixed one-color palette with counts covering the supplied h,w,4 image/ROI."""
    count = selection.width * selection.height if selection else image.shape[0] * image.shape[1]
    return [ColorFrequency(ColorRGBA(31, 47, 59, 255), count, 1.0)]


def opencv_failure(image: ImageArray, radius: float, selection: Rect | None = None, **kwargs: object) -> ImageArray:
    """Raise real cv2.error for a float kernel; test error transport, never a blur algorithm.

    Args:
        image (ImageArray): uint8 BGRA8 array of shape (height, width, 4).
        radius (float): Deliberately invalid kernel dimension in pixels for cv2.blur.
        selection (Rect | None): Unused test selection in image pixels.
    Returns:
        ImageArray: No result is expected; this test callback always raises.
    Raises:
        cv2.error: OpenCV rejects float kernel dimensions with a native numeric code.
    """
    return cv2.blur(image, (float(radius), float(radius)))


if __name__ == "__main__":
    error_mode = "--opencv-error" in sys.argv
    if error_mode:
        sys.argv.remove("--opencv-error")
    for name in ("apply_blur", "apply_border", "apply_contours", "apply_color_separation"):
        setattr(filters, name, image_sentinel)
    if error_mode:
        filters.apply_blur = opencv_failure
    for name in ("apply_to_selection", "replace_selection_with_color", "replace_selection_with_region"):
        setattr(selection_ops, name, image_sentinel)
    for name in TRANSFORM_OPERATIONS:
        setattr(transforms, name, transform_sentinel)
    drawing.apply_drawing = image_sentinel
    color_tools.pick_color = color_sentinel
    color_tools.extract_palette = palette_sentinel
    from app.plugins import ImagePlugin, validate_image_selection
    from app.plugins.catalog import get_plugin_registry, PluginDefinition, Parameter
    class LayerFixture(ImagePlugin):
        def apply(self, image, selection, masks):
            validate_image_selection(image, selection, masks)
            assert not masks
            if selection is not None:
                first, second = image.copy(), image.copy()
                first[:, :, :3] = [17, 31, 47]
                second[:, :, :3] = [61, 73, 89]
                return [first, second]
            return [np.full((3, 5, 4), [255, 0, 0, 255], np.uint8),
                    np.full((7, 2, 4), [0, 0, 255, 128], np.uint8)]
    get_plugin_registry().definitions["test_layers"] = PluginDefinition(
        "test_layers", "Layer fixture", "Test-only multiple dimensions", LayerFixture, ())
    class ColorFixture(ImagePlugin):
        def __init__(self, accent: list[int], tint: list[int]):
            assert accent == [120, 255, 255]
            self.tint = tint
        def apply(self, image, selection, masks):
            result = image.copy()
            result[:] = self.tint
            return result
    get_plugin_registry().definitions["test_colors"] = PluginDefinition(
        "test_colors", "Color fixture", "Test-only public color types", ColorFixture, (
            Parameter.parse("accent", {"type": "color_hsv", "default": [120, 255, 255]}),
            Parameter.parse("tint", {"type": "color_bgra", "default": [11, 22, 33, 44]})))
    main()
