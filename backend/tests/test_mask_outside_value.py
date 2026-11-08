"""The mask exterior is independent of the generator and its inversion setting."""
import json
import mmap
from pathlib import Path
from uuid import uuid4

import numpy as np
import pytest
from jsonschema import Draft202012Validator
from app.ipc.protocol import parse_command, ProtocolError
from app.ipc.receiver import dispatch
from app.models.image_types import Rect
from app.models.operations import normalize_parameters
from app.plugins import encode_mask
from app.plugins.executor import PluginExecutor
from app.plugins.masks import decode_mask
from app.storage.image_registry import ImageRegistry
from test_mask_selection import CASES
from test_plugins import payload


@pytest.mark.parametrize("identity,arguments", CASES)
@pytest.mark.parametrize("outside", [0, 255])
def test_mask_exterior_does_not_change_the_selected_pixels(identity, arguments, outside):
    image = np.random.default_rng(17).integers(0, 256, (6, 8, 4), np.uint8)
    image[0, 0] = 0  # Even transparent exterior pixels obey the explicit choice.
    before = image.copy()
    executor = PluginExecutor()
    roi = Rect(2, 1, 4, 3)
    baseline = executor.apply(identity, arguments, image, roi)[0]
    result = executor.apply(identity, arguments, image, roi, mask_outside_value=outside)[0]
    expected = np.full((6, 8), outside, np.uint8)
    expected[1:4, 2:6] = decode_mask(baseline)[1:4, 2:6]
    np.testing.assert_array_equal(decode_mask(result), expected)
    np.testing.assert_array_equal(image, before)
    full = executor.apply(identity, arguments, image)[0]
    np.testing.assert_array_equal(
        executor.apply(identity, arguments, image, mask_outside_value=outside)[0], full)


@pytest.mark.parametrize("value", [True, False, -1, 1, 127, 256, 255.0, "255", None])
def test_invalid_outside_values_are_rejected(value):
    params = {"plugin_id": "mask_from_image", "arguments": {}, "mask_outside_value": value}
    with pytest.raises(ValueError, match="mask_outside_value"):
        normalize_parameters("run_plugin", params)
    with pytest.raises(ValueError, match="mask_outside_value"):
        PluginExecutor().apply("mask_from_image", {}, np.zeros((2, 3, 4), np.uint8), mask_outside_value=value)
    with pytest.raises(ValueError, match="mask_outside_value"):
        encode_mask(np.zeros((2, 3), np.uint8), outside_value=value)


def test_outside_value_is_a_mask_host_option_not_a_constructor_argument():
    with pytest.raises(ValueError, match="only supported"):
        normalize_parameters("run_plugin", {"plugin_id": "denoise", "arguments": {}, "mask_outside_value": 255})
    with pytest.raises(ValueError, match="only supported"):
        PluginExecutor().apply("denoise", {}, np.zeros((2, 3, 4), np.uint8), mask_outside_value=255)
    with pytest.raises(ValueError, match="unknown constructor"):
        normalize_parameters("run_plugin", {"plugin_id": "mask_from_image", "arguments": {"mask_outside_value": 255}})


def test_encode_white_exterior_preserves_grayscale_values_and_input():
    mask = np.array([[0, 1, 127], [255, 42, 80]], np.uint8)
    before = mask.copy()
    encoded = encode_mask(mask, Rect(1, 0, 1, 2), outside_value=255)
    np.testing.assert_array_equal(decode_mask(encoded), [[255, 1, 255], [255, 42, 255]])
    np.testing.assert_array_equal(mask, before)


def test_white_exterior_round_trips_through_schema_protocol_and_shared_memory():
    schema = json.loads((Path(__file__).parents[2] / "shared/contracts/ipc-v1.schema.json").read_text())
    validator = Draft202012Validator(schema)
    images = ImageRegistry()
    image = np.full((12, 16, 4), 255, np.uint8)
    images.register_image("source", "source.png", image)
    name = "PhotoLab_Image_" + uuid4().hex
    command = payload(name, {"x": 2, "y": 3, "width": 5, "height": 4})
    command["params"] = {"plugin_id": "mask_from_image", "arguments": {"invert": True}, "mask_outside_value": 255}
    validator.validate(command)
    parsed = parse_command(json.dumps(command).encode())
    assert parsed.params["arguments"] == {"invert": True}
    assert parsed.params["mask_outside_value"] == 255
    with mmap.mmap(-1, image.nbytes, tagname=name) as mapping:
        response = dispatch(parsed, images)
        validator.validate(response)
        returned = np.frombuffer(mapping[:], np.uint8).reshape(image.shape)
        expected = np.full((12, 16), 255, np.uint8)
        expected[3:7, 2:7] = 0
        np.testing.assert_array_equal(decode_mask(returned), expected)
    np.testing.assert_array_equal(images.get_image("source").pixels, image)
    command["params"]["mask_outside_value"] = 127
    assert not validator.is_valid(command)
    with pytest.raises(ProtocolError):
        parse_command(json.dumps(command).encode())
