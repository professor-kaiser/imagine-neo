from abc import ABC
import cv2 as cv

class Blur(ABC):
    def apply(self, image):
        pass

class AverageBlur(Blur):
    def __init__(self, kernel_size=5):
        self.kernel_size = kernel_size

    def apply(self, image):
        return cv.blur(
            image, 
            (self.kernel_size, self.kernel_size)
        )

class GaussianBlur(Blur):
    def __init__(self, radius):
        if radius < 0:
            raise ValueError("Radius must be > 0")
        self.radius = radius

    def apply(self, image):
        return cv.GaussianBlur(
            image,
            (0, 0),
            sigmaX = self.radius,
            sigmaY = self.radius,
            borderType = cv.BORDER_DEFAULT,
        )

class MedianBlur(Blur):
    def __init__(self, kernel_size):
        self.kernel_size = kernel_size

    def apply(self, image):
        return cv.medianBlur(
            image,
            self.kernel_size
        )

class BlurFactory:
    @staticmethod
    def check_blur_type(blur_type):
        if blur_type not in ("gaussian", "average", "median", "bilateral"):
            raise ValueError("Unknown blur type")

    @staticmethod
    def create(name, params):
        if name == "average":
            return AverageBlur(kernel_size=params["kernel_size"])
        elif name == "gaussian":
            return GaussianBlur(radius=params["radius"])
        elif name == "median":
            return MedianBlur(kernel_size=params["kernel_size"])