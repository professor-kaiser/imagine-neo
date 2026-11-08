"""Strategy interface shared by every image plugin."""

from abc import ABC, abstractmethod
from app.models.image_types import ImageArray, Rect


class ImagePlugin(ABC):
    """Process a full, isolated BGRA image, optional rectangle and OpenCV uint8 (H, W) masks.

    Results may have any positive dimensions. Return one image or a nonempty list.
    The host copies inputs and protects outside-selection pixels for full-size results.
    """

    @abstractmethod
    def apply(self, image: ImageArray, selection: Rect | None,
              masks: list[ImageArray]) -> ImageArray | list[ImageArray]:
        raise NotImplementedError
