"""Stable operation identifiers and typed parameter validation for every prepared tool."""

import math
from app.models.image_types import ColorRGBA, Rect
from app.models.transforms import TRANSFORM_OPERATIONS

FILTER_OPERATIONS = {"to_grayscale", "apply_blur", "apply_border", "apply_contours", "apply_color_separation"}
IMAGE_OPERATIONS = FILTER_OPERATIONS | TRANSFORM_OPERATIONS | {"run_plugin", "apply_drawing", "replace_selection_with_color", "replace_selection_with_region", "apply_to_selection"}
ANALYSIS_OPERATIONS = {"pick_color", "extract_palette"}
ALL_OPERATIONS = IMAGE_OPERATIONS | ANALYSIS_OPERATIONS
BLUR_TYPES = ("gaussian", "average", "median", "bilateral")
BORDER_TYPES = ("constant", "replicate", "reflect", "reflect_101", "wrap")


def _keys(params: dict[str, object], required: set[str], optional: set[str] | None = None) -> None:
    """Validate params (dict) against required/optional key sets; raise ValueError on mismatch."""
    if not required <= params.keys() or params.keys() - required - (optional or set()):
        raise ValueError("Unexpected or missing operation parameters")


def _number(value: object, name: str, minimum: float = 0, strict: bool = False) -> float:
    """Return finite numeric value as float, enforcing minimum; reject bools with ValueError."""
    if type(value) not in (int, float) or not math.isfinite(value):
        raise ValueError(f"{name} must be a finite number")
    if value < minimum or (strict and value == minimum):
        raise ValueError(f"Invalid {name}")
    return float(value)


def _integer(value: object, name: str, minimum: int, maximum: int) -> int:
    """Return value (int) within inclusive bounds; raise ValueError for invalid type/range."""
    if type(value) is not int or not minimum <= value <= maximum:
        raise ValueError(f"{name} must be an integer in [{minimum}, {maximum}]")
    return value


def _color(value: object) -> ColorRGBA:
    """Convert a JSON RGBA object into ColorRGBA; raise ValueError for invalid components."""
    if not isinstance(value, dict) or set(value) != {"r", "g", "b", "a"}:
        raise ValueError("color must contain r, g, b and a")
    return ColorRGBA(**value)


def normalize_parameters(operation: str, params: object) -> dict[str, object]:
    """Validate JSON parameters and return typed keyword arguments for a prepared function.

    Args:
        operation (str): One of ALL_OPERATIONS; function names are stable UI identifiers.
        params (object): JSON object containing only the declared operation parameters.
    Returns:
        dict[str, object]: Scalars plus ColorRGBA/Rect values where required. Array-valued
        source_image is resolved from the registry later, never transported in JSON.
    Raises:
        ValueError/TypeError: Unknown operation, fields, values or malformed structured types.
    """
    if operation not in ALL_OPERATIONS or not isinstance(params, dict):
        raise ValueError("Unknown operation or invalid params object")
    if operation == "run_plugin":
        _keys(params, {"plugin_id", "arguments"}, {"mask_outside_value"})
        identity = params["plugin_id"]
        if not isinstance(identity, str) or not 1 <= len(identity) <= 100:
            raise ValueError("Invalid plugin_id")
        if not isinstance(params["arguments"], dict):
            raise ValueError("Plugin arguments must be an object")
        # Binding uses the same registry definition that generated the UI fields.
        from app.plugins.catalog import get_plugin_registry
        definition = get_plugin_registry().get(identity)
        result = {"plugin_id": identity, "arguments": definition.bind(params["arguments"])}
        if "mask_outside_value" in params:
            from app.plugins.masks import validate_mask_outside_value
            if definition.output_kind != "mask":
                raise ValueError("mask_outside_value is only supported for mask generators")
            result["mask_outside_value"] = validate_mask_outside_value(params["mask_outside_value"])
        return result
    if operation in TRANSFORM_OPERATIONS:
        required = ({"angle_degrees"} if operation != "scale_image" else set()) | ({"scale_factor"} if operation != "rotate_image" else set())
        _keys(params, required)
        result = {}
        if "angle_degrees" in required:
            angle = _number(params["angle_degrees"], "angle_degrees", -360)
            if angle > 360:
                raise ValueError("angle_degrees must not exceed 360")
            result["angle_degrees"] = angle
        if "scale_factor" in required:
            scale = _number(params["scale_factor"], "scale_factor", 0.01)
            if scale > 8:
                raise ValueError("scale_factor must not exceed 8")
            result["scale_factor"] = scale
        return result
    if operation in {"to_grayscale", "apply_drawing"}:
        _keys(params, set())
        return {}
    if operation == "apply_blur":
        blur_type = params.get("blur_type", "gaussian")
        if blur_type not in BLUR_TYPES:
            raise ValueError("Unknown blur type")
        if blur_type == "gaussian":
            _keys(params, {"radius"}, {"blur_type"})
            result = {"radius": _number(params["radius"], "radius", strict=True)}
            # Preserve legacy kwargs for callers that omit the new selector.
            if "blur_type" in params:
                result["blur_type"] = blur_type
            return result
        if blur_type in ("average", "median"):
            _keys(params, {"blur_type", "kernel_size"})
            kernel = _integer(params["kernel_size"], "kernel_size", 3 if blur_type == "median" else 1, 255)
            if blur_type == "median" and kernel % 2 == 0:
                raise ValueError("Median blur requires an odd kernel size")
            return {"blur_type": blur_type, "kernel_size": kernel}
        _keys(params, {"blur_type", "diameter", "sigma_color", "sigma_space"})
        result = {"blur_type": blur_type, "diameter": _integer(params["diameter"], "diameter", 1, 255)}
        for name in ("sigma_color", "sigma_space"):
            result[name] = _number(params[name], name, strict=True)
            if result[name] > 1000:
                raise ValueError(f"{name} must not exceed 1000")
        return result
    if operation == "apply_border":
        _keys(params, {"thickness", "color"}, {"border_type"})
        result = {"thickness": _integer(params["thickness"], "thickness", 1, 32768), "color": _color(params["color"])}
        if "border_type" in params:
            if params["border_type"] not in BORDER_TYPES:
                raise ValueError("Unknown border type")
            result["border_type"] = params["border_type"]
        return result
    if operation == "apply_contours":
        _keys(params, set(), {"threshold1", "threshold2"})
        low = _number(params.get("threshold1", 100.0), "threshold1")
        high = _number(params.get("threshold2", 200.0), "threshold2", low)
        return {"threshold1": low, "threshold2": high}
    if operation == "apply_color_separation":
        _keys(params, {"mode"})
        if params["mode"] not in ("red", "green", "blue", "alpha"):
            raise ValueError("Unknown separation mode")
        return {"mode": params["mode"]}
    if operation == "replace_selection_with_color":
        _keys(params, {"color"})
        return {"color": _color(params["color"])}
    if operation == "replace_selection_with_region":
        _keys(params, {"source_image_id", "source_region"})
        identity = params["source_image_id"]
        if not isinstance(identity, str) or not 1 <= len(identity) <= 256:
            raise ValueError("Invalid source_image_id")
        region = params["source_region"]
        if not isinstance(region, dict):
            raise ValueError("source_region must be a rectangle")
        return {"source_image_id": identity, "source_region": Rect(**region)}
    if operation == "pick_color":
        _keys(params, {"x", "y"})
        return {"x": _integer(params["x"], "x", 0, 32767), "y": _integer(params["y"], "y", 0, 32767)}
    if operation == "extract_palette":
        _keys(params, set(), {"max_colors"})
        return {"max_colors": _integer(params.get("max_colors", 16), "max_colors", 1, 256)}
    _keys(params, {"operation", "params"})
    nested = params["operation"]
    if not isinstance(nested, str) or nested not in FILTER_OPERATIONS:
        raise ValueError("apply_to_selection requires an image filter operation")
    return {"operation": nested, "params": normalize_parameters(nested, params["params"])}
