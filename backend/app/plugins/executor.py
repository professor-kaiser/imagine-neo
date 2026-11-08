"""Host pipeline: validate, isolate, invoke once, validate all results, protect ROI."""

import numpy as np
from app.models.image_types import ImageArray, Rect
from .catalog import PluginFactory, PluginRegistry, get_plugin_registry
from .masks import encode_mask, validate_mask_outside_value
from .validation import MAX_PLUGIN_IMAGES, validate_image, validate_image_selection


class PluginResultError(ValueError):
    code = "invalid_result"


class PluginExecutor:
    def __init__(self, registry: PluginRegistry | None = None):
        self.registry = registry if registry is not None else get_plugin_registry()

    def apply(self, plugin_id: str, arguments: dict, image: ImageArray,
              selection: Rect | None = None, masks: list[ImageArray] | None = None,
              *, mask_outside_value: int = 0) -> list[ImageArray]:
        masks = [] if masks is None else masks
        validate_image_selection(image, selection, masks)
        definition = self.registry.get(plugin_id)
        validate_mask_outside_value(mask_outside_value)
        if mask_outside_value != 0 and definition.output_kind != "mask":
            raise ValueError("mask_outside_value is only supported for mask generators")
        plugin = PluginFactory.create(definition, arguments)
        result = plugin.apply(image.copy(order="C"), selection, [mask.copy(order="C") for mask in masks])
        results = result if isinstance(result, list) else [result]
        if not 1 <= len(results) <= MAX_PLUGIN_IMAGES:
            raise PluginResultError(f"apply must return between 1 and {MAX_PLUGIN_IMAGES} BGRA images")
        # Validate the entire batch before preparing or publishing any output.
        try:
            for index, value in enumerate(results):
                validate_image(value, f"result[{index}]")
                if definition.output_kind == "mask" and selection is not None and value.shape != image.shape:
                    raise ValueError("Mask results with a selection must have the source dimensions")
                if definition.output_kind == "mask" and (not np.all(value[:, :, 0] == value[:, :, 1])
                        or not np.all(value[:, :, 0] == value[:, :, 2]) or not np.all(value[:, :, 3] == 255)):
                    raise ValueError("Mask results must encode one channel as B=G=R with opaque alpha")
        except ValueError as error:
            raise PluginResultError(str(error)) from error
        outputs = []
        for value in results:
            if selection is not None and definition.output_kind == "mask":
                # Enforce ROI even for third-party generators that ignore selection.
                output = encode_mask(value[:, :, 0], selection, mask_outside_value)
            elif selection is not None and value.shape == image.shape:
                r = selection
                area = np.s_[r.y:r.y + r.height, r.x:r.x + r.width]
                output = image.copy(order="C")
                output[area] = value[area]
            else:
                # A different-sized image is an independent output, never pasted
                # into the source selection or used to mutate its outside pixels.
                output = value.copy(order="C")
            outputs.append(output)
        return outputs
