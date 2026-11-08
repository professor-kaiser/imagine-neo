"""Prepared routing and result contracts, separate from user-implemented image algorithms."""

from dataclasses import asdict
import math
import numpy as np
from app.models.image_types import ImageArray, Rect, ColorRGBA, ColorFrequency
from app.models.operations import FILTER_OPERATIONS
from app.processing import filters, selection_ops, color_tools, drawing, transforms
from app.models.transforms import TRANSFORM_OPERATIONS, output_size
from app.storage.image_registry import ImageRegistry


class OperationNotImplementedError(NotImplementedError):
    """The selected function returned None or explicitly raised NotImplementedError."""

    code = "not_implemented"


class OperationResultError(ValueError):
    """The implementation returned a value that violates its published contract."""

    code = "invalid_result"


def validate_region(region: Rect | None, width: int, height: int) -> None:
    """Validate region (Rect/None) against image dimensions (int pixels); raise ValueError."""
    if region is not None and (region.x + region.width > width or region.y + region.height > height):
        raise ValueError("Selection extends outside the image")


def execute_operation(operation: str, params: dict[str, object], image_id: str, selection: Rect | None,
                      registry: ImageRegistry, mask: ImageArray | None = None, masks: list[ImageArray] | None = None) -> ImageArray | list[ImageArray] | ColorRGBA | list[ColorFrequency]:
    """Invoke exactly one prepared function and validate its result without implementing it.

    Args:
        operation (str): Prepared function identifier, validated by the protocol.
        params (dict[str, object]): Typed parameters from normalize_parameters.
        image_id (str): Registered target image identity.
        selection (Rect | None): Target region in image pixels; None means whole image.
        registry (ImageRegistry): Store of immutable uint8 BGRA8 (height, width, 4) arrays.
    Returns:
        ImageArray | ColorRGBA | list[ColorFrequency]: A validated full-size BGRA8 result
        or typed color-analysis values. Originals are isolated by copies before the call.
    Raises:
        OperationNotImplementedError: The function still returns None (pass).
        OperationResultError: Invalid array, changed outside pixels, color or palette.
        ValueError: Invalid source/selection/coordinates. Implementation exceptions propagate
        to the IPC error boundary, which keeps the backend session alive.
    """
    record = registry.get_image(image_id)
    if record is None:
        raise ValueError("Source image is not registered")
    original = record.pixels
    height, width, _ = original.shape
    validate_region(selection, width, height)
    if operation == "run_plugin":
        from app.plugins.executor import PluginExecutor
        return PluginExecutor().apply(params["plugin_id"], params["arguments"], original, selection, masks,
                                      mask_outside_value=params.get("mask_outside_value", 0))
    if operation in TRANSFORM_OPERATIONS and selection is not None:
        raise ValueError("Transforms require the entire image without a selection")
    result_width, result_height = output_size(operation, params, width, height)
    arguments = dict(params)
    module = filters if operation in FILTER_OPERATIONS else color_tools if operation in {"pick_color", "extract_palette"} else selection_ops
    if operation == "apply_drawing":
        module = drawing
        if selection is not None or not isinstance(mask, np.ndarray) or mask.dtype != np.uint8 or mask.shape != original.shape:
            raise ValueError("Drawing requires a full-size uint8 BGRA8 mask and no selection")
        arguments["mask"] = mask.copy(order="C")
    if operation in TRANSFORM_OPERATIONS:
        module = transforms
    function = getattr(module, operation)
    if operation == "pick_color":
        if arguments["x"] >= width or arguments["y"] >= height:
            raise ValueError("Pixel coordinates extend outside the image")
    elif operation != "apply_drawing" and operation not in TRANSFORM_OPERATIONS:
        arguments["selection"] = selection
    if operation in {"replace_selection_with_color", "replace_selection_with_region"} and selection is None:
        raise ValueError("This operation requires a target selection")
    if operation == "replace_selection_with_region":
        source = registry.get_image(arguments.pop("source_image_id"))
        if source is None:
            raise ValueError("Replacement source image is not registered")
        region = arguments["source_region"]
        validate_region(region, source.metadata.width, source.metadata.height)
        if (region.width, region.height) != (selection.width, selection.height):
            raise ValueError("Source and target regions must have identical dimensions")
        arguments["source_image"] = source.pixels.copy()
    try:
        result = function(original.copy(), **arguments)
    except NotImplementedError as error:
        raise OperationNotImplementedError(f"Implement {operation} in {module.__name__.replace('.', '/')}.py") from error
    if result is None:
        raise OperationNotImplementedError(f"Implement {operation} in {module.__name__.replace('.', '/')}.py (currently returns None)")
    if operation == "pick_color":
        if not isinstance(result, ColorRGBA):
            raise OperationResultError("pick_color must return ColorRGBA with integer components")
        return result
    if operation == "extract_palette":
        if not isinstance(result, list) or not 1 <= len(result) <= params["max_colors"]:
            raise OperationResultError("extract_palette must return a nonempty list within max_colors")
        pixel_count = selection.width * selection.height if selection else width * height
        for item in result:
            if not isinstance(item, ColorFrequency) or not isinstance(item.color, ColorRGBA):
                raise OperationResultError("Palette entries must be ColorFrequency values with ColorRGBA colors")
            if not math.isfinite(item.frequency) or not math.isclose(item.frequency, item.count / pixel_count, abs_tol=1e-6):
                raise OperationResultError("Palette frequencies must match count / selected pixel count")
        if sum(item.count for item in result) != pixel_count or len({item.color for item in result}) != len(result):
            raise OperationResultError("Palette counts must cover the region and colors must be unique")
        return result
    if not isinstance(result, np.ndarray) or result.dtype != np.uint8 or result.shape != (result_height, result_width, 4):
        raise OperationResultError("Return a uint8 BGRA8 array matching the expected output dimensions")
    if operation == "apply_drawing" and not np.array_equal(original[mask[:, :, 3] == 0], result[mask[:, :, 3] == 0]):
        raise OperationResultError("Pixels outside the drawing mask must remain unchanged")
    if selection:
        x, y, right, bottom = selection.x, selection.y, selection.x + selection.width, selection.y + selection.height
        for before, after in ((original[:y], result[:y]), (original[bottom:], result[bottom:]),
                              (original[y:bottom, :x], result[y:bottom, :x]), (original[y:bottom, right:], result[y:bottom, right:])):
            if not np.array_equal(before, after):
                raise OperationResultError("Pixels outside the selection must remain unchanged")
    return np.ascontiguousarray(result)


def describe_analysis(result: ColorRGBA | list[ColorFrequency]) -> dict[str, object]:
    """Return JSON color/palette values from a typed result; no image pixels are serialized."""
    if isinstance(result, ColorRGBA):
        return {"message_type": "color_picked", "color": asdict(result)}
    return {"message_type": "palette_extracted", "palette": [asdict(item) for item in result]}
