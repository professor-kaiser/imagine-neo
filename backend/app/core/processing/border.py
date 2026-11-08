import cv2 as cv

class Border:
    def __init__(self, border_type, thickness, color):
        self.borders = {
            "constant": cv.BORDER_CONSTANT,
            "replicate": cv.BORDER_REPLICATE,
            "reflect": cv.BORDER_REFLECT,
            "reflect_101": cv.BORDER_REFLECT_101,
            "wrap": cv.BORDER_WRAP
        }

        if border_type not in self.borders.keys():
            raise ValueError("Unknown border type")

        self.border_code = self.borders[border_type]
        self.thickness = thickness
        self.color = color

    def apply(self, image):
        return cv.copyMakeBorder(
            image,
            self.thickness,
            self.thickness,
            self.thickness,
            self.thickness,
            self.border_code,
            value=self.color,
        )