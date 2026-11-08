"""Create real OpenCV masks from source images; parameters come from YAML."""
import cv2
import numpy as np
from app.plugins import ImagePlugin, validate_image_selection, validate_hsv, encode_mask


class ImageMaskPlugin(ImagePlugin):
    def __init__(self, invert: bool):
        self.invert = invert

    def apply(self, image, selection, masks):
        validate_image_selection(image, selection, masks)
        # Existing masks: any nonzero visible color selects the pixel, as in OpenCV.
        active = np.any(image[:, :, :3] != 0, axis=2)
        if self.invert:
            active = ~active
        active &= image[:, :, 3] != 0
        return encode_mask(active.astype(np.uint8) * 255, selection)


class ThresholdMaskPlugin(ImagePlugin):
    def __init__(self, channel: str, lower: int, upper: int, invert: bool, blur_size: int,
                 lower_hsv: list[int], upper_hsv: list[int]):
        if channel not in ("gray", "blue", "green", "red", "alpha", "hsv"):
            raise ValueError("Channel: gray, blue, green, red, alpha or hsv.")
        if channel != "hsv" and not 0 <= lower <= upper <= 255:
            raise ValueError("Thresholds must satisfy 0 <= lower <= upper <= 255.")
        if blur_size < 1 or blur_size > 255 or blur_size % 2 == 0:
            raise ValueError("Blur size must be odd, between 1 and 255.")
        self.lower_hsv = validate_hsv(lower_hsv, "Lower HSV")
        self.upper_hsv = validate_hsv(upper_hsv, "Upper HSV")
        if channel == "hsv" and any(a > b for a, b in zip(self.lower_hsv[1:], self.upper_hsv[1:])):
            raise ValueError("HSV lower S/V must not exceed upper S/V. Hue may cross zero.")
        self.channel, self.lower, self.upper = channel, lower, upper
        self.invert, self.blur_size = invert, blur_size

    def apply(self, image, selection, masks):
        validate_image_selection(image, selection, masks)
        if self.channel == "hsv":
            # Blur in BGR before conversion: averaging hue across zero gives the wrong color.
            colors = image[:, :, :3]
            if self.blur_size > 1:
                colors = cv2.GaussianBlur(colors, (self.blur_size, self.blur_size), 0)
            channel = cv2.cvtColor(colors, cv2.COLOR_BGR2HSV)
            lo, hi = self.lower_hsv, self.upper_hsv
            if lo[0] <= hi[0]:
                mask = cv2.inRange(channel, np.array(lo, np.uint8), np.array(hi, np.uint8))
            else:
                mask = cv2.bitwise_or(
                    cv2.inRange(channel, np.array(lo, np.uint8), np.array([179, hi[1], hi[2]], np.uint8)),
                    cv2.inRange(channel, np.array([0, lo[1], lo[2]], np.uint8), np.array(hi, np.uint8)))
        else:
            channel = (cv2.cvtColor(image, cv2.COLOR_BGRA2GRAY) if self.channel == "gray"
                       else image[:, :, ("blue", "green", "red", "alpha").index(self.channel)].copy())
            if self.blur_size > 1:
                channel = cv2.GaussianBlur(channel, (self.blur_size, self.blur_size), 0)
            mask = cv2.inRange(channel, self.lower, self.upper)
        if self.invert:
            mask = cv2.bitwise_not(mask)
        # Transparent color pixels do not accidentally become active.
        if self.channel != "alpha":
            mask[image[:, :, 3] == 0] = 0
        return encode_mask(mask, selection)
