"""K-means returns isolated, cropped BGRA segments rather than full-image copies."""
from contextlib import ExitStack
from dataclasses import asdict
import json
import mmap
from pathlib import Path
from uuid import uuid4

import numpy as np
import pytest
from jsonschema import Draft202012Validator
from app.ipc.protocol import parse_command
from app.ipc.receiver import dispatch
from app.models.image_types import Rect
from app.plugins.catalog import PluginFactory, get_plugin_registry
from app.plugins.executor import PluginExecutor
from app.storage.image_registry import ImageRegistry


def source():
    image = np.full((6, 10, 4), [17, 84, 140, 0], np.uint8)
    image[1:4, 1:3] = [0, 0, 255, 255]
    image[2, 1] = [0, 0, 240, 73]  # Do not replace real pixels with cluster centers.
    image[3, 2, 3] = 1
    image[2:5, 6:9] = [255, 0, 0, 180]
    image[3, 7] = [18, 19, 20, 0]  # Transparent hole inside the blue cluster.
    return image


def clean_crop(image):
    result = image.copy()
    result[result[:, :, 3] == 0] = 0
    return result


def run(image, selection=None, masks=None, k=2, direct=False):
    arguments = {"algorithm": "k-means", "n_clusters": k}
    if direct:
        plugin = PluginFactory.create(get_plugin_registry().get("segmentation"), arguments)
        return plugin.apply(image, selection, [] if masks is None else masks)
    return PluginExecutor().apply("segmentation", arguments, image, selection, masks)


@pytest.mark.parametrize("direct", [False, True])
def test_each_cluster_is_a_cropped_cutout_with_original_colors_and_alpha(direct):
    image = source()
    before = image.copy()
    outputs = run(image, direct=direct)
    assert isinstance(outputs, list) and len(outputs) == 2
    expected = [clean_crop(image[1:4, 1:3]), clean_crop(image[2:5, 6:9])]
    for actual, wanted in zip(outputs, expected):
        np.testing.assert_array_equal(actual, wanted)
        assert actual.dtype == np.uint8 and actual.flags.c_contiguous
        assert not np.shares_memory(actual, image)
    assert not np.shares_memory(outputs[0], outputs[1])
    outputs[0][:] = 0
    np.testing.assert_array_equal(outputs[1], expected[1])
    np.testing.assert_array_equal(image, before)


@pytest.mark.parametrize("shape", [(1, 2), (2, 2), (2, 3), (3, 3)])
def test_bgr_feature_count_is_independent_of_image_shape(shape):
    image = np.zeros((*shape, 4), np.uint8)
    image[0, 0] = [0, 0, 255, 255]
    image[-1, -1] = [255, 0, 0, 255]
    outputs = run(image)
    assert len(outputs) == 2
    np.testing.assert_array_equal(outputs[0], [[[0, 0, 255, 255]]])
    np.testing.assert_array_equal(outputs[1], [[[255, 0, 0, 255]]])


@pytest.mark.parametrize("direct", [False, True])
def test_selection_and_intersection_of_chosen_masks_limit_cluster_input(direct):
    image = source()
    first = np.full(image.shape[:2], 255, np.uint8)
    second = first.copy()
    first[:, 8:] = 0
    second[:, :2] = 0
    # The outside rows contain another visible color; ROI must exclude it.
    image[0] = [0, 255, 0, 255]
    before, first_before, second_before = image.copy(), first.copy(), second.copy()
    outputs = run(image, Rect(1, 1, 8, 4), [first, second], direct=direct)
    assert len(outputs) == 2
    np.testing.assert_array_equal(outputs[0], clean_crop(image[1:4, 2:3]))
    np.testing.assert_array_equal(outputs[1], clean_crop(image[2:5, 6:8]))
    np.testing.assert_array_equal(image, before)
    np.testing.assert_array_equal(first, first_before)
    np.testing.assert_array_equal(second, second_before)


@pytest.mark.parametrize("empty", ["transparent", "mask", "roi"])
def test_empty_active_region_returns_a_clear_error_not_full_images(empty):
    image = source()
    masks, roi = [], None
    if empty == "transparent":
        image[:, :, 3] = 0
    elif empty == "mask":
        masks = [np.zeros(image.shape[:2], np.uint8)]
    else:
        roi = Rect(0, 0, 1, 1)
    with pytest.raises(ValueError, match="No visible pixels"):
        run(image, roi, masks)


def test_clusters_cannot_exceed_selected_visible_pixel_count():
    with pytest.raises(ValueError, match="exceeds.*visible pixels"):
        run(source(), Rect(1, 1, 1, 1))


@pytest.mark.parametrize("k", [True, 1, 0, -1, 65, 256, 2.5, "2"])
def test_invalid_cluster_counts_are_rejected(k):
    with pytest.raises(ValueError):
        run(source(), k=k)


def test_maximum_cluster_count_matches_the_result_limit():
    definition = get_plugin_registry().get("segmentation")
    field = next(p for p in definition.parameters if p.name == "n_clusters")
    assert field.maximum == 64 and field.visible_when == {"algorithm": ["k-means"]}
    image = np.zeros((8, 8, 4), np.uint8)
    image[:, :, 0] = np.arange(64, dtype=np.uint8).reshape(8, 8)
    image[:, :, 3] = 255
    outputs = run(image, k=64)
    assert len(outputs) == 64
    for i, output in enumerate(outputs):
        np.testing.assert_array_equal(output, [[[i, 0, 0, 255]]])


@pytest.mark.parametrize("selection", [None, Rect(0, 0, 5, 5)])
def test_cluster_spanning_image_bounds_still_has_transparent_other_clusters(selection):
    image = np.full((5, 5, 4), [0, 0, 255, 255], np.uint8)
    image[1:4, 1:4] = [255, 0, 0, 255]
    expected_outline = image.copy()
    expected_outline[1:4, 1:4] = 0
    outputs = run(image, selection)
    np.testing.assert_array_equal(outputs[0], expected_outline)
    np.testing.assert_array_equal(outputs[1], image[1:4, 1:4])


def test_segment_batch_negotiates_cropped_dimensions_and_transparent_pixels_over_ipc():
    schema = json.loads((Path(__file__).parents[2] / "shared/contracts/ipc-v1.schema.json").read_text())
    validator = Draft202012Validator(schema)
    images = ImageRegistry()
    original = source()
    images.register_image("source", "source.png", original)
    roi = Rect(2, 2, 6, 3)
    expected = [clean_crop(original[2:4, 2:3]), clean_crop(original[2:5, 6:8])]

    def call(body):
        command = {"protocol_version": 1, "request_id": uuid4().hex, **body}
        validator.validate(command)
        response = dispatch(parse_command(json.dumps(command).encode()), images)
        validator.validate(response)
        return response

    ready = call({"message_type": "run_operation", "image_id": "source", "operation": "run_plugin",
                  "params": {"plugin_id": "segmentation", "arguments": {"algorithm": "k-means", "n_clusters": 2}},
                  "result_image_id": "segments", "file_name": "segment.png", "selection": asdict(roi)})
    assert ready["message_type"] == "plugin_results_ready"
    assert [(image["width"], image["height"]) for image in ready["images"]] == [(1, 2), (2, 3)]
    with ExitStack() as stack:
        buffers, mappings = [], []
        for result in ready["images"]:
            w, h = result["width"], result["height"]
            name = "PhotoLab_Image_" + uuid4().hex
            mappings.append(stack.enter_context(mmap.mmap(-1, w * h * 4, tagname=name)))
            buffers.append({"mapping_name": name, "width": w, "height": h, "channels": 4,
                            "dtype": "uint8", "pixel_format": "BGRA8",
                            "row_stride_bytes": w * 4, "buffer_size_bytes": w * h * 4})
        call({"message_type": "read_plugin_results", "result_image_id": "segments", "output_buffers": buffers})
        for mapping, wanted in zip(mappings, expected):
            np.testing.assert_array_equal(np.frombuffer(mapping[:], np.uint8).reshape(wanted.shape), wanted)
    call({"message_type": "release_plugin_results", "result_image_id": "segments"})
    assert not images.plugin_batches and len(images.records()) == 1
    np.testing.assert_array_equal(images.get_image("source").pixels, original)


def test_thresholding_mode_still_uses_its_background_color():
    image = source()
    mask = np.zeros(image.shape[:2], np.uint8)
    mask[1:4, 1:3] = 255
    background = [11, 22, 33, 44]
    output = PluginExecutor().apply("segmentation", {"algorithm": "thresholding", "background_color": background},
                                    image, masks=[mask])[0]
    expected = np.full_like(image, background)
    expected[mask != 0] = image[mask != 0]
    np.testing.assert_array_equal(output, expected)
