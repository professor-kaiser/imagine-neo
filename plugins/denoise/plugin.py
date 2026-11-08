"""OpenCV non-local means denoising, with alpha preserved."""
import cv2
from app.plugins import ImagePlugin, validate_image_selection, apply_masked_result
from app.models.image_types import ImageArray, Rect


class DenoisePlugin(ImagePlugin):
    def __init__(self, strength: float, template_window: int, search_window: int):
        if template_window % 2 == 0 or search_window % 2 == 0:
            raise ValueError("Les fenêtres doivent être des nombres impairs.")
        if search_window < template_window:
            raise ValueError("La fenêtre de recherche doit être au moins aussi grande que la fenêtre de comparaison.")
        self.strength = strength
        self.template_window = template_window
        self.search_window = search_window

    def apply(self, image: ImageArray, selection: Rect | None, masks: list[ImageArray]) -> ImageArray:
        validate_image_selection(image, selection, masks)
        result = image.copy()
        r = selection or Rect(0, 0, image.shape[1], image.shape[0])
        area = (slice(r.y, r.y + r.height), slice(r.x, r.x + r.width))
        result[area][:, :, :3] = cv2.fastNlMeansDenoisingColored(
            image[area][:, :, :3], None, self.strength, self.strength,
            self.template_window, self.search_window)
        return apply_masked_result(image, result, selection, masks)
