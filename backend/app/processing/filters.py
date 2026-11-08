import cv2
import numpy as np

from app.models.image_types import ImageArray, Rect, ColorRGBA
from app.core.processing.blur import BlurFactory
from app.core.processing.border import Border

def to_grayscale(image: ImageArray, selection: Rect | None = None) -> ImageArray:
    if not isinstance(image, np.ndarray):
        raise TypeError("image must be a NumPy array")
    
    if image.dtype != np.uint8 or image.ndim != 3 or image.shape[2] != 4:
        raise ValueError("Expected uint8 BGRA8 (height, width, 4)")
    
    height, width, _ = image.shape
    if height == 0 or width == 0:
        raise ValueError("Image dimensions must be positive")
    
    if selection is not None and not isinstance(selection, Rect):
        raise TypeError("selection must be Rect or None")
    
    region = selection if selection is not None else Rect(0, 0, width, height)
    if region.x + region.width > width or region.y + region.height > height:
        raise ValueError("Selection extends outside the image")

    rows = slice(region.y, region.y + region.height)
    columns = slice(region.x, region.x + region.width)
    roi = np.ascontiguousarray(image[rows, columns])
    gray = cv2.cvtColor(roi, cv2.COLOR_BGRA2GRAY)
    converted = cv2.cvtColor(gray, cv2.COLOR_GRAY2BGRA)
    converted[:, :, 3] = roi[:, :, 3]
    
    result = image.copy(order="C")
    result[rows, columns] = converted
    return result


def apply_blur(
        image: ImageArray, 
        radius: float = 5.0, 
        selection: Rect | None = None, *,
        blur_type: str = "gaussian", 
        kernel_size: int = 5
    ) -> ImageArray:
    BlurFactory.check_blur_type(blur_type)

    if not isinstance(image, np.ndarray):
        raise TypeError("image must be a NumPy array")
    
    height, width, _ = image.shape
    if height == 0 or width == 0:
        raise ValueError("Image dimensions must be positive")
    
    if selection is not None and not isinstance(selection, Rect):
        raise TypeError("selection must be Rect or None")
    
    region = selection if selection is not None else Rect(0, 0, width, height)
    if region.x + region.width > width or region.y + region.height > height:
        raise ValueError("Selection extends outside the image")

    params = {"radius": radius, "kernel_size": kernel_size}
    blur = BlurFactory.create(name=blur_type, params=params)

    rows = slice(region.y, region.y + region.height)
    cols = slice(region.x, region.x + region.width)
    roi = np.ascontiguousarray(image[rows, cols])
    blured = blur.apply(roi)

    blured[:, :, 3] = roi[:, :, 3]
    result = image.copy(order="C")
    result[rows, cols] = blured
    return result
    

def apply_border(
        image: ImageArray, 
        thickness: int, 
        color: ColorRGBA, 
        selection: Rect | None = None, *,
        border_type: str = "constant"
    ) -> ImageArray:
    if not isinstance(image, np.ndarray):
        raise TypeError("image must be a NumPy array")
    
    height, width, _ = image.shape
    if height == 0 or width == 0:
        raise ValueError("Image dimensions must be positive")
    
    if selection is not None and not isinstance(selection, Rect):
        raise TypeError("selection must be Rect or None")
    
    region = selection if selection is not None else Rect(0, 0, width, height)
    if region.x + region.width > width or region.y + region.height > height:
        raise ValueError("Selection extends outside the image")

    if type(thickness) is not int or thickness <= 0:
        raise ValueError("thickness must be a positive integer")

    rows = slice(region.y, region.y + region.height)
    cols = slice(region.x, region.x + region.width)
    roi = image[rows, cols]

    bgra = (color.b, color.g, color.r, color.a)
    border = Border(border_type, thickness, bgra)

    if 2 * thickness >= min(region.height, region.width):
        if border_type != "constant":
            raise ValueError("Border too thick: no interior pixels remain")

        bordered = np.empty_like(roi)
        bordered[:] = bgra
    else:
        inner = np.ascontiguousarray(
            roi[thickness:-thickness, thickness:-thickness]
        )
        bordered = border.apply(inner)

    result = image.copy(order="C")
    result[rows, cols] = bordered
    return result


def apply_contours(
        image: ImageArray, 
        threshold1: float = 100.0, 
        threshold2: float = 200.0, 
        selection: Rect | None = None
    ) -> ImageArray:
    if not isinstance(image, np.ndarray):
        raise TypeError("image must be a NumPy array")
    
    height, width, _ = image.shape
    if height == 0 or width == 0:
        raise ValueError("Image dimensions must be positive")
    
    if selection is not None and not isinstance(selection, Rect):
        raise TypeError("selection must be Rect or None")
    
    region = selection if selection is not None else Rect(0, 0, width, height)
    if region.x + region.width > width or region.y + region.height > height:
        raise ValueError("Selection extends outside the image")

    if threshold1 > threshold2:
        raise ValueError("Lower edge threshold must < Upper Edge threshold")

    rows = slice(region.y, region.y + region.height)
    cols = slice(region.x, region.x + region.width)
    roi = np.ascontiguousarray(image[rows, cols])
    canny = cv2.Canny(cv2.cvtColor(roi, cv2.COLOR_BGRA2GRAY), threshold1, threshold2)
    canny = cv2.cvtColor(canny, cv2.COLOR_GRAY2BGRA)

    canny[:, :, 3] = roi[:, :, 3]
    result = image.copy(order="C")
    result[rows, cols] = canny
    return result


def apply_color_separation(image: ImageArray, mode: str, selection: Rect | None = None) -> ImageArray:
    """Preview a selected BGRA channel using OpenCV (not implemented).

    Args:
        image (ImageArray): Original uint8 BGRA8 array, shape (height, width, 4).
        mode (str): One of "red", "green", "blue", "alpha". RGB channel modes
            isolate that color and preserve alpha. Alpha mode shows opaque grayscale.
            Future color-space modes require an explicit documented extension.
        selection (Rect | None): In-bounds rectangle in pixels, or None for the full image.
    Returns:
        ImageArray: Expected uint8 BGRA8 output of shape (height, width, 4).
        Pixels outside the selection must remain unchanged; input must not be mutated.
    Raises:
        ValueError: Expected for invalid shape/dtype, coordinates, selection or parameters.
        TypeError: Expected for unsupported parameter types.
        cv2.error: Expected if an OpenCV operation fails after validation.
    Implementation guide (OpenCV):
        1. Validate input, ROI and mode; reject unknown mode strings with ValueError.
           Copy the full image and extract an independent contiguous ROI.
        2. Use cv2.split on the BGRA ROI. The order is blue, green, red, alpha;
           each returned channel has shape (roi_h, roi_w) and dtype uint8.
        3. For red/green/blue, build zero channels with np.zeros_like and use
           cv2.merge to combine the chosen channel, two zeros and the original alpha
           in BGRA order. Do not return the single channel as the image result.
        4. For alpha, convert the alpha plane with cv2.COLOR_GRAY2BGRA. Keep its
           newly opaque alpha so even fully transparent source pixels can be inspected.
        5. Paste the preview into the copied result and return it; pixels outside the
           ROI, including their alpha, must remain byte-for-byte unchanged.
    Note:
        This contract currently returns None because its body is deliberately pass.
        The documented output and errors describe the future implementation.
    """
    # TODO: Use cv2.split/cv2.merge for color channels or cv2.cvtColor for alpha preview.
    pass

