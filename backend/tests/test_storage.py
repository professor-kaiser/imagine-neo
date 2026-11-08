"""Registry ownership and real Windows raw-section mapping tests."""

import mmap
from uuid import uuid4
import numpy as np
import pytest
from app.ipc.shared_memory_io import read_shared_image, write_shared_image
from app.storage.image_registry import ImageRegistry


def test_mapping_copy_and_write() -> None:
    """Copy a (2,3,4) BGRA8 mapping, then verify independent pixels after its destruction."""
    name = "PhotoLab_Image_" + uuid4().hex
    expected = np.arange(24, dtype=np.uint8).reshape(2, 3, 4)
    with mmap.mmap(-1, 24, tagname=name) as mapping:
        mapping.write(expected.tobytes())
        actual = read_shared_image(name, 3, 2, 4, 12, 24)
        write_shared_image(name, np.full((2, 3, 4), 127, dtype=np.uint8))
        assert mapping[:] == bytes([127]) * 24
    np.testing.assert_array_equal(actual, expected)
    assert actual.flags.owndata


def test_missing_mapping_does_not_create_it() -> None:
    """OpenFileMapping must reject absent mappings instead of creating zero-filled images."""
    with pytest.raises(OSError):
        read_shared_image("PhotoLab_Image_" + uuid4().hex, 1, 1, 4, 4, 4)


def test_mapping_too_small() -> None:
    """Reject a mapping smaller than the requested Windows page extent."""
    name = "PhotoLab_Image_" + uuid4().hex
    with mmap.mmap(-1, 4096, tagname=name):
        with pytest.raises(OSError):
            read_shared_image(name, 2048, 1, 4, 8192, 8192)


def test_registry_lifecycle() -> None:
    """Register/get/delete a (2,3,4) uint8 image while proving immutable ownership."""
    registry = ImageRegistry()
    original = np.full((2, 3, 4), 40, dtype=np.uint8)
    record = registry.register_image("a", "a.png", original)
    original[:] = 0
    assert registry.get_image("a") is record
    assert record.pixels[0, 0, 0] == 40
    assert not record.pixels.flags.writeable
    assert registry.total_bytes == 24
    with pytest.raises(ValueError):
        registry.register_image("a", "duplicate.png", original)
    assert registry.remove_image("a")
    assert not registry.remove_image("a")
    assert registry.get_image("a") is None
    assert registry.total_bytes == 0


def test_registry_limits_and_invalid_image() -> None:
    """Reject invalid arrays and capacity overflow without changing registry contents."""
    registry = ImageRegistry(max_bytes=4)
    with pytest.raises(ValueError):
        registry.register_image("a", "a", np.zeros((1, 1, 3), dtype=np.uint8))
    with pytest.raises(MemoryError):
        registry.register_image("a", "a", np.zeros((2, 2, 4), dtype=np.uint8))
    assert not registry.records()

