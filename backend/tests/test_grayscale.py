"""OpenCV grayscale correctness, ROI safety, validation and IPC contract tests."""

import json
import mmap
from pathlib import Path
from uuid import uuid4
import numpy as np
import pytest
from jsonschema import Draft202012Validator
from app.ipc.protocol import ProtocolError, parse_command
from app.ipc.receiver import dispatch
from app.models.image_types import Rect
from app.models.messages import GrayscaleMessage
from app.processing.filters import to_grayscale
from app.storage.image_registry import ImageRegistry


def color_pixels() -> np.ndarray:
    """Return a (2,4,4) BGRA8 image of blue, green, red and white with varied alpha."""
    return np.array([[[255, 0, 0, 0], [0, 255, 0, 64], [0, 0, 255, 128], [255, 255, 255, 255]],
                     [[255, 0, 0, 13], [0, 255, 0, 25], [0, 0, 255, 37], [255, 255, 255, 49]]], dtype=np.uint8)


def grayscale_payload(mapping_name: str) -> dict:
    """Return a command dict describing a (2,4,4) result in mapping_name (str)."""
    return {"protocol_version": 1, "message_type": "to_grayscale", "request_id": "gray-request",
            "image_id": "source", "result_image_id": "result", "file_name": "gray.png",
            "output_buffer": {"mapping_name": mapping_name, "width": 4, "height": 2, "channels": 4,
                              "dtype": "uint8", "pixel_format": "BGRA8", "row_stride_bytes": 16,
                              "buffer_size_bytes": 32}}


def test_grayscale_known_colors_alpha_and_ownership() -> None:
    """Known BGR luminance values must match while preserving immutable originals and alpha."""
    pixels = color_pixels()
    original = pixels.copy()
    pixels.flags.writeable = False
    result = to_grayscale(pixels)
    expected = np.array([[29, 150, 76, 255], [29, 150, 76, 255]], dtype=np.uint8)
    for channel in range(3):
        np.testing.assert_array_equal(result[:, :, channel], expected)
    np.testing.assert_array_equal(result[:, :, 3], original[:, :, 3])
    np.testing.assert_array_equal(pixels, original)
    assert result.dtype == np.uint8 and result.shape == (2, 4, 4)
    assert result.flags.c_contiguous and not np.shares_memory(result, pixels)


def test_grayscale_selection_only() -> None:
    """A bottom-row ROI changes only its two selected pixels, including no alpha changes."""
    pixels = color_pixels()
    expected = pixels.copy()
    expected[1, 1, :3] = 150
    expected[1, 2, :3] = 76
    result = to_grayscale(pixels, Rect(1, 1, 2, 1))
    np.testing.assert_array_equal(result, expected)
    np.testing.assert_array_equal(pixels, color_pixels())


def test_grayscale_strided_input() -> None:
    """Accept a noncontiguous (2,2,4) view and return a contiguous independent image."""
    pixels = color_pixels()[:, ::2]
    assert not pixels.flags.c_contiguous
    result = to_grayscale(pixels)
    np.testing.assert_array_equal(result[:, :, 0], [[29, 76], [29, 76]])
    np.testing.assert_array_equal(result[:, :, 3], pixels[:, :, 3])
    assert result.flags.c_contiguous


@pytest.mark.parametrize("pixels", [np.zeros((2, 2, 3), dtype=np.uint8), np.zeros((2, 2), dtype=np.uint8),
                                     np.zeros((2, 2, 4), dtype=np.uint16), np.zeros((0, 2, 4), dtype=np.uint8)],
                         ids=["three_channels", "single_channel", "uint16", "empty"])
def test_grayscale_rejects_invalid_arrays(pixels: np.ndarray) -> None:
    """Reject pixels (ndarray) that violate nonempty uint8 BGRA8 requirements."""
    with pytest.raises(ValueError):
        to_grayscale(pixels)


def test_grayscale_rejects_bounds_and_types() -> None:
    """Invalid rectangles must be rejected rather than silently clipped by NumPy."""
    with pytest.raises(ValueError):
        to_grayscale(color_pixels(), Rect(3, 1, 2, 1))
    with pytest.raises(ValueError):
        to_grayscale(color_pixels(), Rect(0, 2, 1, 1))
    with pytest.raises(TypeError):
        to_grayscale(color_pixels(), (0, 0, 1, 1))
    with pytest.raises(TypeError):
        to_grayscale([])


def test_grayscale_protocol_schema_and_optional_selection() -> None:
    """Validate grayscale commands with absent, null and explicit rectangle selections."""
    schema = json.loads((Path(__file__).parents[2] / "shared/contracts/ipc-v1.schema.json").read_text())
    validator = Draft202012Validator(schema)
    payload = grayscale_payload("PhotoLab_Image_test")
    for selection in ("absent", None, {"x": 1, "y": 0, "width": 2, "height": 1}):
        if selection != "absent":
            payload["selection"] = selection
        validator.validate(payload)
        assert isinstance(parse_command(json.dumps(payload).encode()), GrayscaleMessage)


@pytest.mark.parametrize("change", ["same_id", "negative_roi", "extra_roi_key", "bad_stride", "encoded_pixels"],
                         ids=str)
def test_grayscale_protocol_rejects_bad_commands(change: str) -> None:
    """Reject the invalid command variant selected by change (str)."""
    payload = grayscale_payload("PhotoLab_Image_test")
    if change == "same_id":
        payload["result_image_id"] = payload["image_id"]
    elif change == "negative_roi":
        payload["selection"] = {"x": -1, "y": 0, "width": 1, "height": 1}
    elif change == "extra_roi_key":
        payload["selection"] = {"x": 0, "y": 0, "width": 1, "height": 1, "right": 1}
    elif change == "bad_stride":
        payload["output_buffer"]["row_stride_bytes"] = 17
    else:
        payload["pixels"] = "forbidden"
    with pytest.raises(ProtocolError):
        parse_command(json.dumps(payload).encode())


def test_grayscale_failed_mapping_rolls_back_result() -> None:
    """A missing output mapping must leave the original registry and its quota intact."""
    registry = ImageRegistry()
    registry.register_image("source", "source.png", color_pixels())
    payload = grayscale_payload("PhotoLab_Image_" + uuid4().hex)
    with pytest.raises(OSError):
        dispatch(parse_command(json.dumps(payload).encode()), registry)
    assert registry.get_image("result") is None and registry.total_bytes == 32
    np.testing.assert_array_equal(registry.get_image("source").pixels, color_pixels())


def test_grayscale_quota_does_not_write_output() -> None:
    """Reject an over-quota derived image without altering the output mapping or source."""
    registry = ImageRegistry(max_bytes=32)
    registry.register_image("source", "source.png", color_pixels())
    name = "PhotoLab_Image_" + uuid4().hex
    with mmap.mmap(-1, 32, tagname=name) as mapping:
        mapping[:] = bytes([231]) * 32
        with pytest.raises(MemoryError):
            dispatch(parse_command(json.dumps(grayscale_payload(name)).encode()), registry)
        assert mapping[:] == bytes([231]) * 32
    assert registry.get_image("result") is None and len(registry.records()) == 1


def test_grayscale_rejects_missing_source_and_wrong_output_extent() -> None:
    """Reject unavailable source images and outputs whose dimensions differ from the source."""
    registry = ImageRegistry()
    payload = grayscale_payload("PhotoLab_Image_test")
    with pytest.raises(ValueError, match="Source image"):
        dispatch(parse_command(json.dumps(payload).encode()), registry)
    registry.register_image("source", "source.png", color_pixels())
    payload["output_buffer"].update(width=2, row_stride_bytes=8, buffer_size_bytes=16)
    with pytest.raises(ValueError, match="Output dimensions"):
        dispatch(parse_command(json.dumps(payload).encode()), registry)
    assert len(registry.records()) == 1
