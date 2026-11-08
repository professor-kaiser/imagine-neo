"""Output geometry for transport validation; no image transformation algorithms."""
import math

TRANSFORM_OPERATIONS = {"rotate_image", "scale_image", "rotate_scale_image"}


def output_size(operation: str, params: dict[str, object], width: int, height: int) -> tuple[int, int]:
    """Return the expanded output extent (width, height) and enforce IPC limits.

    Rotation is clockwise about the image center; scaling is uniform. The extent
    encloses the rotated, scaled image without cropping. Near-integer floating
    residue (1e-9 px) is ignored, matching the UI's geometry calculation.
    This helper only sizes buffers; it never transforms image pixels.
    """
    if operation not in TRANSFORM_OPERATIONS:
        return width, height
    angle = params.get("angle_degrees", 0)
    scale = params.get("scale_factor", 1)
    if type(angle) not in (int, float) or not math.isfinite(angle) or not -360 <= angle <= 360:
        raise ValueError("angle_degrees must be finite and between -360 and 360")
    if type(scale) not in (int, float) or not math.isfinite(scale) or not 0.01 <= scale <= 8:
        raise ValueError("scale_factor must be finite and between 0.01 and 8")
    angle = math.radians(angle)
    c, s = abs(math.cos(angle)), abs(math.sin(angle))
    out_width = max(1, math.ceil(scale * (width*c + height*s) - 1e-9))
    out_height = max(1, math.ceil(scale * (width*s + height*c) - 1e-9))
    if out_width > 32768 or out_height > 32768 or out_width*out_height*4 > 512*1024*1024:
        raise ValueError("Transformed image exceeds the 32768-pixel or 512 MiB limit")
    return out_width, out_height
