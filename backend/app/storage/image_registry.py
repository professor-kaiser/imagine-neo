"""Bounded session registry. Originals are immutable, ready for future revisions."""

from datetime import datetime, timezone
import numpy as np
from app.models.image_types import ImageArray, ImageMetadata, ImageRecord
from app.ipc.protocol import validate_dimensions


class ImageRegistry:
    """Own original arrays; mutation/history will use separate document revisions."""

    def __init__(self, max_bytes: int = 1024 * 1024 * 1024) -> None:
        """Create an empty registry capped by max_bytes (int, bytes), default 1 GiB."""
        self._records: dict[str, ImageRecord] = {}
        self.max_bytes = max_bytes
        self.total_bytes = 0
        self.plugin_batches: dict[str, list[str]] = {}

    def register_image(self, image_id: str, file_name: str, image: ImageArray) -> ImageRecord:
        """Copy and store an immutable original, independent of all transport resources.

        Args:
            image_id (str): Nonempty unique image identity.
            file_name (str): Display filename, never opened by the backend.
            image (ImageArray): Contiguous uint8 BGRA8, shape (height, width, 4).
        Returns:
            ImageRecord: Metadata and registry-owned read-only BGRA8 array of the same shape.
        Raises:
            ValueError: Duplicate/empty identity, invalid dtype or shape.
            MemoryError: Session storage would exceed max_bytes.
        """
        if not image_id or not file_name or image_id in self._records:
            raise ValueError("Image identity is empty or already registered")
        if image.dtype != np.uint8 or image.ndim != 3 or image.shape[2] != 4 or not image.flags.c_contiguous:
            raise ValueError("Expected contiguous uint8 BGRA8 (height, width, 4)")
        height, width, channels = image.shape
        validate_dimensions(width, height, channels, width * 4, image.nbytes)
        if self.total_bytes + image.nbytes > self.max_bytes or len(self._records) >= 128:
            raise MemoryError("Session storage limit reached (1 GiB / 128 images by default)")
        pixels = image.copy()
        pixels.flags.writeable = False
        record = ImageRecord(ImageMetadata(image_id, file_name, width, height, datetime.now(timezone.utc)), pixels)
        self._records[image_id] = record
        self.total_bytes += pixels.nbytes
        return record

    def stage_plugin_results(self, batch_id: str, file_name: str, images: list[ImageArray]) -> list[ImageRecord]:
        if batch_id in self.plugin_batches:
            raise ValueError("Plugin batch identity already exists")
        created = []
        try:
            for index, image in enumerate(images):
                created.append(self.register_image(f"{batch_id}:{index}", file_name, image))
        except Exception:
            for record in created:
                self.remove_image(record.metadata.image_id)
            raise
        self.plugin_batches[batch_id] = [record.metadata.image_id for record in created]
        return created

    def release_plugin_results(self, batch_id: str) -> None:
        for identity in self.plugin_batches.pop(batch_id, []):
            self.remove_image(identity)

    def clear_plugin_results(self) -> None:
        for identity in list(self.plugin_batches):
            self.release_plugin_results(identity)

    def get_image(self, image_id: str) -> ImageRecord | None:
        """Return ImageRecord with (h,w,4) BGRA8 pixels for image_id (str), or None if absent."""
        return self._records.get(image_id)

    def remove_image(self, image_id: str) -> bool:
        """Remove image_id (str); return bool indicating whether it existed."""
        record = self._records.pop(image_id, None)
        if record is None:
            return False
        self.total_bytes -= record.pixels.nbytes
        return True

    def records(self) -> tuple[ImageRecord, ...]:
        """Return an immutable tuple of records whose arrays have shape (h,w,4) BGRA8."""
        return tuple(self._records.values())


_default_registry = ImageRegistry()


def register_image(image_id: str, file_name: str, image: ImageArray) -> ImageRecord:
    """Store image_id/file_name (str) and (h,w,4) uint8 BGRA8 image in the default registry.

    Returns ImageRecord; raises ValueError for invalid/duplicate images, MemoryError at capacity.
    """
    return _default_registry.register_image(image_id, file_name, image)


def get_image(image_id: str) -> ImageRecord | None:
    """Return default ImageRecord for image_id (str), or None; pixels are (h,w,4) BGRA8."""
    return _default_registry.get_image(image_id)


def remove_image(image_id: str) -> bool:
    """Remove image_id (str) from the default registry; return bool indicating existence."""
    return _default_registry.remove_image(image_id)
