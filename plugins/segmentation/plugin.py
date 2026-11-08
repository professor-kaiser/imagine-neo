
from abc import ABC, abstractmethod

import cv2
import numpy as np
from app.plugins import ImagePlugin, validate_image_selection
from app.models.image_types import ImageArray, Rect
from app.plugins.validation import MAX_PLUGIN_IMAGES

class ImageSegmentation(ABC):
    @abstractmethod
    def segment(self, image: ImageArray) -> ImageArray | list[ImageArray]:
        pass

class KMeansImageSegmentation(ImageSegmentation):
    def __init__(self, selection: Rect | None = None,
                 masks: list[ImageArray] | None = None, 
                 n_clusters: int = 2,
                 background_color: list[int] = [0, 0, 0, 255],
                 color_space: str = "BGRA"):
        if type(n_clusters) is not int or not 2 <= n_clusters <= MAX_PLUGIN_IMAGES:
            raise ValueError(f"n_clusters must be an integer between 2 and {MAX_PLUGIN_IMAGES}.")
        self.selection = selection
        self.masks = [] if masks is None else masks
        self.n_clusters = n_clusters
        self.background_color = background_color
        self.color_space = color_space
        self.available_color_spaces = ["BGRA", "HSV"]

        if self.color_space not in self.available_color_spaces:
            raise ValueError(f"Unsupported color space: {self.color_space}. Supported color spaces: {self.available_color_spaces}")

    def segment(self, image: ImageArray) -> list[ImageArray]:
        validate_image_selection(image, self.selection, self.masks)
        r = self.selection or Rect(0, 0, image.shape[1], image.shape[0])
        area = (slice(r.y, r.y + r.height), slice(r.x, r.x + r.width))
        image_area = image[area]
        alpha_area = image_area[:, :, 3]
        
        roi = image_area[:, :, :3]
        if self.color_space != "BGRA":
            roi = cv2.cvtColor(roi, getattr(cv2, f"COLOR_BGR2{self.color_space}"))

        samples = np.ascontiguousarray(roi[:, :, :3].reshape(-1, 3), dtype=np.float32)
        criteria = (cv2.TERM_CRITERIA_EPS + cv2.TERM_CRITERIA_MAX_ITER, 100, 0.2)
        _, labels, _ = cv2.kmeans(samples, self.n_clusters, None, criteria, 10, cv2.KMEANS_PP_CENTERS)
        labels = labels.ravel()

        results = []
        for k in range(self.n_clusters):
            background = np.full(image_area.shape, self.background_color, dtype=np.uint8)
            image_result = np.full(image.shape, self.background_color, dtype=np.uint8)

            mask = (labels == k).reshape(roi.shape[:2]).astype(np.uint8) * 255
            keep = mask > 0
            background[keep, :3] = roi[keep, :3]
            background[keep, 3] = alpha_area[keep]

            image_result[area] = background
            results.append(image_result)

        return results

class ThresholdingImageSegmentation(ImageSegmentation):
    def __init__(self, selection: Rect | None = None, 
                 masks: list[ImageArray] = None, 
                 background_color: list[int] = [0, 0, 0, 255]):
        self.selection = selection
        self.masks = masks
        self.background_color = background_color

    def segment(self, image: ImageArray):
        validate_image_selection(image, self.selection, self.masks)
        fg = image.copy()
        bg = np.full(image.shape, self.background_color, dtype=np.uint8)

        final_mask = self.masks[0] if self.masks else np.ones(image.shape[:2], dtype=np.uint8) * 255
        for mask in self.masks[1:]:
            final_mask = cv2.bitwise_and(final_mask, mask)

        fg = cv2.bitwise_and(fg, fg, mask=final_mask)
        bg[final_mask > 0] = fg[final_mask > 0]

        return bg

class SegmentationPlugin(ImagePlugin):
    def __init__(self, algorithm: str = "thresholding", 
                 background_color: list[int]=[0, 0, 0, 255], 
                 n_clusters: int=2, 
                 color_space: str="BGRA"):
        self.algorithm = algorithm
        self.background_color = background_color
        self.n_clusters = n_clusters
        self.color_space = color_space

    def apply(self, image: ImageArray, 
              selection: Rect | None, 
              masks: list[ImageArray]) -> ImageArray | list[ImageArray]:
        if self.algorithm == "thresholding":
            segmentation = ThresholdingImageSegmentation(
                selection, 
                masks, 
                background_color=self.background_color
            )
        elif self.algorithm == "k-means":
            segmentation = KMeansImageSegmentation(
                selection,
                masks,
                n_clusters=self.n_clusters,
                background_color=self.background_color,
                color_space=self.color_space
            )
        else:
            raise ValueError(f"Unsupported segmentation algorithm: {self.algorithm}")
        return segmentation.segment(image)