import cv2
from app.plugins import ImagePlugin, validate_image_selection
from app.models.image_types import ImageArray, Rect

class MorphologicalPlugin(ImagePlugin):
    def __init__(self, operation: str = "dilation", 
                 kernel: str = "rectangular",
                 kernel_size: int = 3, 
                 iterations: int = 1):
        self.operation = operation
        self.kernel = kernel
        self.kernel_size = kernel_size
        self.iterations = iterations

        self.operations_map = {
            "dilation": lambda img, kernel, iterations: cv2.dilate(img, kernel, iterations=iterations),
            "erosion": lambda img, kernel, iterations: cv2.erode(img, kernel, iterations=iterations),
            "opening": lambda img, kernel, iterations: cv2.morphologyEx(img, cv2.MORPH_OPEN, kernel, iterations=iterations),
            "closing": lambda img, kernel, iterations: cv2.morphologyEx(img, cv2.MORPH_CLOSE, kernel, iterations=iterations),
            "gradient": lambda img, kernel, iterations: cv2.morphologyEx(img, cv2.MORPH_GRADIENT, kernel, iterations=iterations),
            "top-hat": lambda img, kernel, iterations: cv2.morphologyEx(img, cv2.MORPH_TOPHAT, kernel, iterations=iterations),
            "black-hat": lambda img, kernel, iterations: cv2.morphologyEx(img, cv2.MORPH_BLACKHAT, kernel, iterations=iterations),
        }

        self.kernels_map = {
            "rectangular": cv2.MORPH_RECT,
            "elliptical": cv2.MORPH_ELLIPSE,
            "cross-shaped": cv2.MORPH_CROSS,
            "diamond": cv2.MORPH_DIAMOND,
        }

    def apply(self, image: ImageArray, 
              selection: Rect | None, 
              masks: list[ImageArray]) -> ImageArray | list[ImageArray]:
        validate_image_selection(image, selection, masks)
        img_gray = cv2.cvtColor(image, cv2.COLOR_BGRA2GRAY)
        kernel = cv2.getStructuringElement(self.kernels_map[self.kernel], (self.kernel_size, self.kernel_size))
        operation_func = self.operations_map[self.operation]

        transformed = operation_func(img_gray, kernel, self.iterations)
        return cv2.cvtColor(transformed, cv2.COLOR_GRAY2BGRA)
