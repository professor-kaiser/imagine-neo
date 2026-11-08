from app.models.image_types import ImageArray
import cv2 as cv
import numpy as np

from app.models.transforms import output_size

def _transform(image, angle_degrees=0.0, scale_factor=1.0):
    if not isinstance(image, np.ndarray):
        raise TypeError("image doit être un tableau NumPy")
    if image.dtype != np.uint8 or image.ndim != 3 or image.shape[2] != 4:
        raise ValueError("Format attendu : uint8 BGRA8")

    height, width = image.shape[:2]
    if height == 0 or width == 0:
        raise ValueError("L'image doit être non vide")

    params = {
        "angle_degrees": angle_degrees,
        "scale_factor": scale_factor,
    }
    new_w, new_h = output_size(
        "rotate_scale_image", params, width, height
    )

    if angle_degrees % 360 == 0 and scale_factor == 1:
        return image.copy(order="C")

    matrix = cv.getRotationMatrix2D(
        ((width - 1) / 2, (height - 1) / 2),
        -angle_degrees,
        scale_factor,
    )

    matrix[0, 2] += (new_w - width) / 2
    matrix[1, 2] += (new_h - height) / 2

    pixels = image.astype(np.float32)
    pixels[:, :, :3] *= pixels[:, :, 3:4] / 255.0

    result = cv.warpAffine(
        pixels,
        matrix,
        (new_w, new_h),
        flags=cv.INTER_LINEAR,
        borderMode=cv.BORDER_CONSTANT,
        borderValue=(0, 0, 0, 0),
    )

    alpha = result[:, :, 3:4]
    result[:, :, :3] = np.divide(
        result[:, :, :3] * 255.0,
        alpha,
        out=np.zeros_like(result[:, :, :3]),
        where=alpha > 0,
    )

    return np.ascontiguousarray(
        np.clip(np.floor(result + 0.5), 0, 255).astype(np.uint8)
    )


def rotate_image(image, angle_degrees):
    return _transform(image, angle_degrees=angle_degrees)


def scale_image(image, scale_factor):
    return _transform(image, scale_factor=scale_factor)


def rotate_scale_image(image, angle_degrees, scale_factor):
    return _transform(image, angle_degrees, scale_factor)