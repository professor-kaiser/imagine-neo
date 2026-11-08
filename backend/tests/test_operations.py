"""Prepared-tool routing tests; injected callbacks validate plumbing, not unfinished algorithms."""

import json
import mmap
from pathlib import Path
from uuid import uuid4
import numpy as np
import pytest
from jsonschema import Draft202012Validator
from app.ipc.protocol import parse_command, ProtocolError
from app.ipc.receiver import dispatch
from app.models.image_types import ColorRGBA, ColorFrequency, Rect
from app.models.operations import IMAGE_OPERATIONS, FILTER_OPERATIONS
from app.operation_service import OperationNotImplementedError, OperationResultError
from app.processing import filters, selection_ops, color_tools
from app.storage.image_registry import ImageRegistry

CASES = {
    "to_grayscale": {}, "apply_blur": {"radius": 2.5},
    "apply_border": {"thickness": 3, "color": {"r": 12, "g": 34, "b": 56, "a": 78}},
    "apply_contours": {"threshold1": 40, "threshold2": 90},
    "apply_color_separation": {"mode": "blue"},
    "replace_selection_with_color": {"color": {"r": 12, "g": 34, "b": 56, "a": 78}},
    "replace_selection_with_region": {"source_image_id": "other", "source_region": {"x": 0, "y": 1, "width": 2, "height": 1}},
    "pick_color": {"x": 2, "y": 1}, "extract_palette": {"max_colors": 8},
    "apply_to_selection": {"operation": "apply_blur", "params": {"radius": 2.5}}
}


def operation_payload(operation: str, mapping: str = "PhotoLab_Image_test") -> dict:
    """Build JSON parameters for operation (str), with an optional named output mapping."""
    message = {"protocol_version": 1, "message_type": "run_operation", "request_id": "request",
               "image_id": "target", "operation": operation, "params": CASES[operation],
               "selection": None if operation == "pick_color" else {"x": 1, "y": 1, "width": 2, "height": 1}}
    if operation in IMAGE_OPERATIONS:
        message.update(result_image_id="result", file_name="result.png", output_buffer={
            "mapping_name": mapping, "width": 4, "height": 3, "channels": 4,
            "dtype": "uint8", "pixel_format": "BGRA8", "row_stride_bytes": 16, "buffer_size_bytes": 48})
    return message


def registry_fixture() -> ImageRegistry:
    """Return a registry with distinct immutable target/source (3,4,4) BGRA8 arrays."""
    registry = ImageRegistry()
    registry.register_image("target", "target.png", np.arange(48, dtype=np.uint8).reshape(3, 4, 4))
    registry.register_image("other", "other.png", np.full((3, 4, 4), 200, dtype=np.uint8))
    return registry


def operation_module(operation: str):
    """Return the production module owning operation (str), to isolate callbacks in tests."""
    return filters if operation in FILTER_OPERATIONS else color_tools if operation in {"pick_color", "extract_palette"} else selection_ops


@pytest.mark.parametrize("operation", CASES)
def test_every_operation_routes_parameters_and_publishes_result(operation: str, monkeypatch) -> None:
    """Check typed argument delivery, output mappings and analysis serialization for each tool."""
    registry = registry_fixture()
    original = registry.get_image("target").pixels.copy()
    calls = []

    def implementation(image, **kwargs):
        """Record routed arguments and return a transport-only sentinel, not a real algorithm."""
        np.testing.assert_array_equal(image, original)
        assert not np.shares_memory(image, registry.get_image("target").pixels)
        calls.append(kwargs)
        if operation == "pick_color":
            assert kwargs == {"x": 2, "y": 1}
            return ColorRGBA(71, 83, 97, 109)
        assert kwargs["selection"] == Rect(1, 1, 2, 1)
        if operation == "extract_palette":
            assert kwargs["max_colors"] == 8
            return [ColorFrequency(ColorRGBA(10, 20, 30), 1, 0.5), ColorFrequency(ColorRGBA(40, 50, 60), 1, 0.5)]
        if operation in {"apply_border", "replace_selection_with_color"}:
            assert kwargs["color"] == ColorRGBA(12, 34, 56, 78)
        if operation == "apply_border":
            assert kwargs["thickness"] == 3
        if operation == "apply_blur":
            assert kwargs["radius"] == 2.5
        if operation == "apply_contours":
            assert kwargs["threshold1"] == 40 and kwargs["threshold2"] == 90
        if operation == "apply_color_separation":
            assert kwargs["mode"] == "blue"
        if operation == "apply_to_selection":
            assert kwargs["operation"] == "apply_blur" and kwargs["params"] == {"radius": 2.5}
        if operation == "replace_selection_with_region":
            assert kwargs["source_region"] == Rect(0, 1, 2, 1)
            np.testing.assert_array_equal(kwargs["source_image"], 200)
            kwargs["source_image"][:] = 0
        image[1, 1, :3] = [123, 124, 125]
        return image

    monkeypatch.setattr(operation_module(operation), operation, implementation)
    name = "PhotoLab_Image_" + uuid4().hex
    payload = operation_payload(operation, name)
    schema = json.loads((Path(__file__).parents[2] / "shared/contracts/ipc-v1.schema.json").read_text())
    validator = Draft202012Validator(schema)
    validator.validate(payload)
    with mmap.mmap(-1, 48, tagname=name) as mapping:
        response = dispatch(parse_command(json.dumps(payload).encode()), registry)
        validator.validate(response)
        if operation in IMAGE_OPERATIONS:
            assert response["message_type"] == "image_processed"
            result = registry.get_image("result").pixels
            assert bytes(mapping[:]) == result.tobytes()
            assert result[1, 1, 0] == 123
        elif operation == "pick_color":
            assert response["color"] == {"r": 71, "g": 83, "b": 97, "a": 109}
        else:
            assert len(response["palette"]) == 2
    assert len(calls) == 1
    np.testing.assert_array_equal(registry.get_image("target").pixels, original)
    np.testing.assert_array_equal(registry.get_image("other").pixels, 200)


@pytest.mark.parametrize("operation", CASES)
def test_placeholder_needs_only_its_function_replaced(operation: str, monkeypatch) -> None:
    """A None return reports not_implemented without creating results or touching mappings."""
    monkeypatch.setattr(operation_module(operation), operation, lambda *args, **kwargs: None)
    registry = registry_fixture()
    with pytest.raises(OperationNotImplementedError) as error:
        dispatch(parse_command(json.dumps(operation_payload(operation)).encode()), registry)
    assert error.value.code == "not_implemented"
    assert operation in str(error.value)
    assert len(registry.records()) == 2


@pytest.mark.parametrize("operation,params", [
    ("apply_blur", {"radius": True}), ("apply_blur", {"radius": 0}), ("apply_blur", {"radius": float("nan")}),
    ("apply_border", {"thickness": 1.5, "color": {"r": 0, "g": 0, "b": 0, "a": 255}}),
    ("apply_contours", {"threshold1": 200, "threshold2": 100}),
    ("apply_color_separation", {"mode": "invalid"}), ("pick_color", {"x": -1, "y": 0}),
    ("extract_palette", {"max_colors": 257}), ("replace_selection_with_color", {"color": {"r": 300, "g": 0, "b": 0, "a": 255}}),
    ("apply_to_selection", {"operation": "apply_to_selection", "params": {}}),
    ("apply_blur", {"radius": 3, "image": "forbidden"})])
def test_invalid_tool_parameters_rejected(operation: str, params: dict) -> None:
    """Reject malformed params (dict) for operation (str) before invoking its algorithm."""
    payload = operation_payload(operation)
    payload["params"] = params
    with pytest.raises(ProtocolError):
        parse_command(json.dumps(payload).encode())


@pytest.mark.parametrize("kind", ["dtype", "shape", "outside", "color", "palette"])
def test_bad_implementation_results_are_reported(kind: str, monkeypatch) -> None:
    """Reject an implementation's invalid return value without changing the original registry."""
    operation = "pick_color" if kind == "color" else "extract_palette" if kind == "palette" else "apply_blur"

    def bad_result(image, **kwargs):
        """Return a deliberately invalid test value selected by kind (str)."""
        if kind == "dtype":
            return image.astype(np.float32)
        if kind == "shape":
            return image[:, :, :3]
        if kind == "color":
            return (1, 2, 3, 4)
        if kind == "palette":
            return [ColorFrequency(ColorRGBA(1, 2, 3), 1, 1.0)]
        image[0, 0] = 255
        return image

    monkeypatch.setattr(operation_module(operation), operation, bad_result)
    registry = registry_fixture()
    with pytest.raises(OperationResultError):
        dispatch(parse_command(json.dumps(operation_payload(operation)).encode()), registry)
    assert len(registry.records()) == 2


def test_selection_and_source_errors_are_rejected_before_call(monkeypatch) -> None:
    """Check missing target selection, source identity and source extent without calling a stub."""
    def unexpected(*args, **kwargs):
        """Fail if invalid source geometry reaches a user algorithm."""
        pytest.fail("Invalid request reached the algorithm")
    monkeypatch.setattr(selection_ops, "replace_selection_with_region", unexpected)
    registry = registry_fixture()
    for selection, source_id, region in [(None, "other", Rect(0, 0, 2, 1)),
                                          (Rect(1, 1, 2, 1), "missing", Rect(0, 0, 2, 1)),
                                          (Rect(1, 1, 2, 1), "other", Rect(0, 0, 3, 1))]:
        from dataclasses import asdict
        payload = operation_payload("replace_selection_with_region")
        payload["selection"] = asdict(selection) if selection else None
        payload["params"] = {"source_image_id": source_id, "source_region": asdict(region)}
        with pytest.raises(ValueError):
            dispatch(parse_command(json.dumps(payload).encode()), registry)
