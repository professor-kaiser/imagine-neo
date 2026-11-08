"""Real OpenCV mask semantics, parameterized generation and transport boundaries."""
import cv2
import numpy as np
import pytest
from app.models.image_types import Rect
from app.plugins import validate_image_selection, apply_masked_result
from app.plugins.catalog import get_plugin_registry
from app.plugins.executor import PluginExecutor
from app.plugins.masks import decode_mask


def source():
    return np.array([[[0, 0, 0, 255], [0, 0, 100, 255], [0, 0, 150, 255]],
                     [[250, 0, 200, 255], [255, 255, 255, 0], [1, 2, 3, 127]]], np.uint8)


def generate(identity, image=None, **arguments):
    return PluginExecutor().apply(identity, arguments, source() if image is None else image)[0]


def test_catalog_exposes_mask_generators():
    catalog = get_plugin_registry()
    assert not catalog.errors
    assert catalog.get("mask_from_image").describe()["output_kind"] == "mask"
    assert catalog.get("mask_threshold").output_kind == "mask"
    assert catalog.get("denoise").output_kind == "image"


def test_image_as_mask_uses_nonzero_pixels_and_transparency():
    image = source()
    before = image.copy()
    mask = decode_mask(generate("mask_from_image", image))
    np.testing.assert_array_equal(mask, [[0, 255, 255], [255, 0, 255]])
    np.testing.assert_array_equal(decode_mask(generate("mask_from_image", invert=True)), [[255, 0, 0], [0, 0, 0]])
    np.testing.assert_array_equal(image, before)
    # This is a valid OpenCV mask without channel conversion.
    result = cv2.bitwise_and(image, image, mask=mask)
    assert not result[mask == 0].any()
    np.testing.assert_array_equal(result[mask != 0], image[mask != 0])


def test_inclusive_threshold_and_alpha_channel():
    mask = decode_mask(generate("mask_threshold", channel="red", lower=100, upper=200))
    np.testing.assert_array_equal(mask, [[0, 255, 255], [255, 0, 0]])
    inverted = decode_mask(generate("mask_threshold", channel="red", lower=100, upper=200, invert=True))
    np.testing.assert_array_equal(inverted, [[255, 0, 0], [0, 0, 255]])
    alpha = decode_mask(generate("mask_threshold", channel="alpha", lower=0, upper=127))
    np.testing.assert_array_equal(alpha, [[0, 0, 0], [0, 255, 255]])


@pytest.mark.parametrize("channel", ["gray", "blue", "green", "red", "alpha"])
def test_channel_and_blur_match_opencv(channel):
    image = np.random.default_rng(8).integers(0, 256, (13, 17, 4), np.uint8)
    plane = cv2.cvtColor(image, cv2.COLOR_BGRA2GRAY) if channel == "gray" else image[:, :, ["blue", "green", "red", "alpha"].index(channel)]
    expected = cv2.inRange(cv2.GaussianBlur(plane, (3, 3), 0), 90, 170)
    if channel != "alpha":
        expected[image[:, :, 3] == 0] = 0
    np.testing.assert_array_equal(decode_mask(generate("mask_threshold", image, channel=channel, lower=90, upper=170, blur_size=3)), expected)


@pytest.mark.parametrize("arguments", [
    {"lower": 200, "upper": 10}, {"channel": "purple"}, {"blur_size": 2},
    {"lower": -1}, {"upper": 256}, {"invert": 1}, {"blur_size": 1.5}])
def test_invalid_mask_parameters(arguments):
    with pytest.raises(ValueError):
        generate("mask_threshold", **arguments)


def test_empty_list_zero_mask_union_and_roi_are_distinct():
    image = source()
    changed = np.full_like(image, 99)
    zero = np.zeros(image.shape[:2], np.uint8)
    first, second = zero.copy(), zero.copy()
    first[0, 0], second[1, 2] = 1, 255
    np.testing.assert_array_equal(apply_masked_result(image, changed, masks=[]), changed)
    np.testing.assert_array_equal(apply_masked_result(image, changed, masks=[zero]), image)
    expected = image.copy()
    expected[0, 0] = expected[1, 2] = 99
    np.testing.assert_array_equal(apply_masked_result(image, changed, masks=[first, second]), expected)
    expected[0, 0] = image[0, 0]
    np.testing.assert_array_equal(apply_masked_result(image, changed, Rect(1, 0, 2, 2), [first, second]), expected)


@pytest.mark.parametrize("mask", [
    np.zeros((2, 3, 4), np.uint8), np.zeros((3, 2), np.uint8),
    np.zeros((2, 3), np.float32), np.zeros((2, 3), bool)])
def test_reject_non_opencv_masks(mask):
    with pytest.raises(ValueError):
        validate_image_selection(source(), masks=[mask])


def test_transport_rejects_color_or_translucent_layers():
    with pytest.raises(ValueError):
        decode_mask(source())
    encoded = np.full((2, 3, 4), 255, np.uint8)
    encoded[0, 0, 3] = 127
    with pytest.raises(ValueError):
        decode_mask(encoded)


def test_mask_output_is_zero_outside_selection():
    image = source()
    encoded = PluginExecutor().apply("mask_from_image", {}, image, Rect(1, 0, 1, 2))[0]
    mask = decode_mask(encoded)
    assert not mask[:, 0].any() and not mask[:, 2].any()
    np.testing.assert_array_equal(mask[:, 1], [255, 0])


@pytest.mark.parametrize("identity", ["denoise", "laplacian"])
def test_shipped_transforms_preserve_unmasked_pixels_and_alpha(identity):
    image = np.random.default_rng(19).integers(0, 256, (12, 16, 4), np.uint8)
    before = image.copy()
    mask = np.zeros(image.shape[:2], np.uint8)
    mask[4:9, 5:11] = 255
    before_mask = mask.copy()
    roi = Rect(3, 2, 5, 6)
    executor = PluginExecutor()
    full = executor.apply(identity, {}, image, roi)[0]
    result = executor.apply(identity, {}, image, roi, [mask])[0]
    expected = image.copy()
    expected[4:8, 5:8] = full[4:8, 5:8]
    np.testing.assert_array_equal(result, expected)
    np.testing.assert_array_equal(result[:, :, 3], image[:, :, 3])
    np.testing.assert_array_equal(image, before)
    np.testing.assert_array_equal(mask, before_mask)
    np.testing.assert_array_equal(executor.apply(identity, {}, image, masks=[mask * 0])[0], image)
