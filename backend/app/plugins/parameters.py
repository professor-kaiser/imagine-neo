"""Reusable typed constructor parameters."""
def validate_hsv(value, name="HSV"):
    """Return an independent OpenCV HSV triplet: H 0..179, S/V 0..255."""
    if (not isinstance(value, list) or len(value) != 3
            or any(type(v) is not int or not 0 <= v <= maximum for v, maximum in zip(value, (179, 255, 255)))):
        raise ValueError(f"{name}: expected [H, S, V] integers with H in [0, 179], S/V in [0, 255]")
    return value.copy()


def validate_bgra(value, name="BGRA"):
    """Return an independent [B, G, R, A] color; each channel is an integer in 0..255."""
    if (not isinstance(value, list) or len(value) != 4
            or any(type(v) is not int or not 0 <= v <= 255 for v in value)):
        raise ValueError(f"{name}: expected [B, G, R, A] integers in [0, 255]")
    return value.copy()
