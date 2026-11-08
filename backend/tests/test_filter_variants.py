"""Validate selectable OpenCV options and compatibility without implementing algorithms."""

import json
from pathlib import Path

import numpy as np
import pytest
from jsonschema import Draft202012Validator, ValidationError

from app.ipc.protocol import parse_command, ProtocolError
from app.models.image_types import ColorRGBA, Rect
from app.models.operations import normalize_parameters, BORDER_TYPES
from app.operation_service import execute_operation, OperationNotImplementedError
from app.processing import filters, selection_ops
from test_operations import operation_payload, registry_fixture

COLOR = {"r": 12, "g": 34, "b": 56, "a": 78}
CASES = [
    ("apply_blur", {"blur_type": "gaussian", "radius": 2.5}),
    ("apply_blur", {"blur_type": "average", "kernel_size": 4}),
    ("apply_blur", {"blur_type": "median", "kernel_size": 7}),
    ("apply_blur", {"blur_type": "bilateral", "diameter": 9, "sigma_color": 55.5, "sigma_space": 80}),
    *[("apply_border", {"border_type": mode, "thickness": 2, "color": COLOR}) for mode in BORDER_TYPES],
]
SCHEMA = Draft202012Validator(json.loads(
    (Path(__file__).parents[2] / "shared/contracts/ipc-v1.schema.json").read_text(encoding="utf-8")))


@pytest.mark.parametrize("operation,params", CASES)
@pytest.mark.parametrize("nested", [False, True])
def test_variants_reach_function_with_typed_arguments(operation, params, nested, monkeypatch):
    outer = "apply_to_selection" if nested else operation
    payload = operation_payload(outer)
    payload["params"] = {"operation": operation, "params": params} if nested else params
    SCHEMA.validate(payload)
    command = parse_command(json.dumps(payload).encode())
    expected = dict(params)
    if "color" in expected:
        expected["color"] = ColorRGBA(**COLOR)
    calls = []

    def implementation(image, **kwargs):
        calls.append(kwargs)
        assert kwargs.pop("selection") == Rect(1, 1, 2, 1)
        assert kwargs == ({"operation": operation, "params": expected} if nested else expected)
        return image

    monkeypatch.setattr(selection_ops if nested else filters, outer, implementation)
    registry = registry_fixture()
    execute_operation(command.operation, command.params, command.image_id, command.selection, registry)
    assert len(calls) == 1


INVALID = [
    ("apply_blur", {"blur_type": "unknown", "radius": 5}),
    ("apply_blur", {"blur_type": "median", "kernel_size": 4}),
    ("apply_blur", {"blur_type": "median", "kernel_size": 1}),
    ("apply_blur", {"blur_type": "average", "kernel_size": True}),
    ("apply_blur", {"blur_type": "average", "kernel_size": 3.5}),
    ("apply_blur", {"blur_type": "average", "kernel_size": 256}),
    ("apply_blur", {"blur_type": "gaussian", "radius": 5, "kernel_size": 7}),
    ("apply_blur", {"blur_type": "bilateral", "diameter": 9, "sigma_color": 75}),
    ("apply_blur", {"blur_type": "bilateral", "diameter": 0, "sigma_color": 75, "sigma_space": 75}),
    ("apply_blur", {"blur_type": "bilateral", "diameter": 9, "sigma_color": 0, "sigma_space": 75}),
    ("apply_blur", {"blur_type": "bilateral", "diameter": 9, "sigma_color": 75, "sigma_space": 1001}),
    ("apply_border", {"border_type": "transparent", "thickness": 2, "color": COLOR}),
]


@pytest.mark.parametrize("operation,params", INVALID)
@pytest.mark.parametrize("nested", [False, True])
def test_invalid_variants_rejected_by_schema_and_runtime(operation, params, nested):
    payload = operation_payload("apply_to_selection" if nested else operation)
    payload["params"] = {"operation": operation, "params": params} if nested else params
    with pytest.raises(ValidationError):
        SCHEMA.validate(payload)
    with pytest.raises(ProtocolError):
        parse_command(json.dumps(payload).encode())


def test_legacy_gaussian_kwargs_and_pixels_are_preserved():
    assert normalize_parameters("apply_blur", {"radius": 2.5}) == {"radius": 2.5}
    assert normalize_parameters("apply_to_selection", {"operation": "apply_blur", "params": {"radius": 2.5}}) == {
        "operation": "apply_blur", "params": {"radius": 2.5}}
    image = np.random.default_rng(42).integers(0, 256, (12, 15, 4), dtype=np.uint8)
    original = image.copy()
    selection = Rect(2, 3, 8, 6)
    legacy = filters.apply_blur(image, 2.5, selection)
    explicit = filters.apply_blur(image, selection=selection, radius=2.5, blur_type="gaussian")
    np.testing.assert_array_equal(legacy, explicit)
    np.testing.assert_array_equal(image, original)
    np.testing.assert_array_equal(explicit[:, :, 3], original[:, :, 3])
    mask = np.ones(image.shape[:2], dtype=bool)
    mask[3:9, 2:10] = False
    np.testing.assert_array_equal(explicit[mask], original[mask])


@pytest.mark.parametrize("operation,params", CASES[1:4])
def test_unimplemented_blur_cannot_silently_apply_gaussian(operation, params):
    registry = registry_fixture()
    with pytest.raises(OperationNotImplementedError):
        execute_operation(operation, normalize_parameters(operation, params), "target", None, registry)
    assert len(registry.records()) == 2

