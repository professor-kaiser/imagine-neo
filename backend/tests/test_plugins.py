"""Plugin contracts: discovery, constructor binding, isolated pixels and IPC rollback."""

import json
from pathlib import Path
from dataclasses import asdict
from uuid import uuid4
import mmap

import numpy as np
import pytest
import yaml
from jsonschema import Draft202012Validator

from app.plugins.catalog import PluginRegistry, PluginFactory, get_plugin_registry, PLUGIN_ROOT
from app.plugins.executor import PluginExecutor, PluginResultError
from app.models.image_types import Rect
from app.storage.image_registry import ImageRegistry
from app.ipc.protocol import parse_command, ProtocolError
from app.ipc.receiver import dispatch
from app.ipc.error_responses import error_response


SOURCE = """from app.plugins import ImagePlugin
class Example(ImagePlugin):
    instances = 0
    def __init__(self, amount: int, label: str, ratio: float, enabled: bool):
        type(self).instances += 1
        assert label == 'test' and ratio == 0.5 and enabled is True
        self.amount = amount
    def apply(self, image, selection, masks):
        image[:, :, 0] = self.amount
        return image
"""


def make_registry(tmp_path, source=SOURCE, **overrides):
    folder = tmp_path / "example"
    folder.mkdir(exist_ok=True)
    (folder / "plugin.py").write_text(source, encoding="utf-8")
    entry = {"id": "example", "name": "Example", "file": "plugin.py", "class": "Example",
             "parameters": {"amount": {"type": "integer", "min": 0, "max": 255},
                            "label": {"type": "text", "default": "test"},
                            "ratio": {"type": "number", "default": 0.5},
                            "enabled": {"type": "boolean", "default": True}}}
    entry.update(overrides)
    (folder / "plugins.yaml").write_text(yaml.safe_dump({"version": 1, "plugins": [entry]}), encoding="utf-8")
    (tmp_path / "plugins.yaml").write_text(yaml.safe_dump({"version": 1, "plugins": ["example"]}), encoding="utf-8")
    return PluginRegistry(tmp_path)


def pixels():
    return np.random.default_rng(42).integers(0, 256, (12, 16, 4), dtype=np.uint8)


def test_constructor_binding_defaults_and_new_instance_per_apply(tmp_path):
    registry = make_registry(tmp_path)
    assert not registry.errors
    definition = registry.get("example")
    assert definition.bind({"amount": 73}) == {"amount": 73, "label": "test", "ratio": 0.5, "enabled": True}
    for _ in range(2):
        plugin = PluginFactory.create(definition, {"amount": 73})
        assert plugin.amount == 73
    assert definition.plugin_class.instances == 2
    described = {p["name"]: p for p in definition.describe()["parameters"]}
    assert described["amount"]["required"] is True and "default" not in described["amount"]
    assert described["ratio"]["default"] == 0.5


@pytest.mark.parametrize("arguments", [{}, {"amount": True}, {"amount": 1.5}, {"amount": -1},
    {"amount": 256}, {"amount": "2"}, {"amount": 2, "extra": 1}, {"amount": 2, "image": []},
    {"amount": 2, "ratio": float("nan")}, {"amount": 2, "ratio": float("inf")},
    {"amount": 2, "label": 3}, {"amount": 2, "enabled": 1}])
def test_invalid_values_rejected_before_constructor(tmp_path, arguments):
    registry = make_registry(tmp_path)
    definition = registry.get("example")
    with pytest.raises(ValueError):
        PluginFactory.create(definition, arguments)
    assert definition.plugin_class.instances == 0


@pytest.mark.parametrize("selection", [None, Rect(2, 3, 5, 4), Rect(0, 0, 1, 1)])
def test_roi_is_copied_and_merged_without_modifying_original(tmp_path, selection):
    registry = make_registry(tmp_path)
    image = pixels()
    before = image.copy()
    image.setflags(write=False)
    result = PluginExecutor(registry).apply("example", {"amount": 73}, image, selection)[0]
    expected = before.copy()
    r = selection or Rect(0, 0, image.shape[1], image.shape[0])
    expected[r.y:r.y+r.height, r.x:r.x+r.width, 0] = 73
    np.testing.assert_array_equal(image, before)
    np.testing.assert_array_equal(result, expected)
    assert result.flags.c_contiguous and not np.shares_memory(image, result)


@pytest.mark.parametrize("expression", ["None", "image[:, :, :3]", "image.astype('float32')", "image[:0]"])
def test_invalid_results_rejected_without_mutating_source(tmp_path, expression):
    registry = make_registry(tmp_path, SOURCE.replace("return image", "return " + expression))
    image = pixels()
    before = image.copy()
    with pytest.raises(PluginResultError):
        PluginExecutor(registry).apply("example", {"amount": 73}, image)
    np.testing.assert_array_equal(before, image)


@pytest.mark.parametrize("overrides", [
    {"file": "../outside.py"}, {"file": "missing.py"}, {"file": "missing/plugin.py"},
    {"class": "Missing"}, {"parameters": {}},
    {"parameters": {"image": {"type": "text"}}},
    {"parameters": {"amount": {"type": "unknown"}}},
    {"parameters": {"amount": {"type": "integer", "default": True}}},
    {"parameters": {"amount": {"type": "number", "min": 2, "max": 1}}},
])
def test_bad_definitions_are_reported_without_crashing(tmp_path, overrides):
    registry = make_registry(tmp_path, **overrides)
    assert not registry.definitions and registry.errors


@pytest.mark.parametrize("source", [
    "class Example: pass",
    "from app.plugins import ImagePlugin\nclass Example(ImagePlugin): pass",
    SOURCE.replace("amount: int", "amount: float"),
    SOURCE.replace("amount: int, label: str, ratio: float, enabled: bool", "**kwargs"),
    SOURCE.replace("def apply(self, image, selection, masks):", "def apply(self, image, required):"),
])
def test_class_and_signatures_are_validated(tmp_path, source):
    registry = make_registry(tmp_path, source)
    assert registry.errors and not registry.definitions


def test_relative_helper_import_and_parameterless_plugin(tmp_path):
    folder = tmp_path / "example"
    folder.mkdir()
    (folder / "helpers.py").write_text("VALUE = 91")
    source = """from app.plugins import ImagePlugin
from .helpers import VALUE
class Example(ImagePlugin):
    def apply(self, image, selection, masks):
        image[:, :, 0] = VALUE
        return image
"""
    registry = make_registry(tmp_path, source, parameters={})
    assert not registry.errors
    result = PluginExecutor(registry).apply("example", {}, pixels())[0]
    assert np.all(result[:, :, 0] == 91)


def test_duplicate_yaml_keys_rejected(tmp_path):
    (tmp_path / "plugins.yaml").write_text("version: 1\nplugins: []\nplugins: []\n")
    registry = PluginRegistry(tmp_path)
    assert registry.errors and not registry.definitions


def test_disabled_and_broken_plugins_do_not_hide_valid_plugins(tmp_path):
    registry = make_registry(tmp_path)
    config = yaml.safe_load((tmp_path / "example/plugins.yaml").read_text())
    config["plugins"].extend([{"id": "disabled", "enabled": False},
                              {"id": "broken", "name": "Broken", "file": "bad/plugin.py", "class": "Missing"}])
    (tmp_path / "example/plugins.yaml").write_text(yaml.safe_dump(config))
    registry = PluginRegistry(tmp_path)
    assert list(registry.definitions) == ["example"] and len(registry.errors) == 1


def test_algorithm_exception_preserves_source(tmp_path):
    registry = make_registry(tmp_path, SOURCE.replace("return image", "raise RuntimeError('algorithm failed')"))
    image = pixels()
    before = image.copy()
    with pytest.raises(RuntimeError, match="algorithm failed"):
        PluginExecutor(registry).apply("example", {"amount": 73}, image)
    np.testing.assert_array_equal(image, before)
    with pytest.raises(ValueError, match="outside"):
        PluginExecutor(registry).apply("example", {"amount": 73}, image, Rect(0, 0, 100, 100))


def test_demo_reduces_noise_and_preserves_alpha():
    registry = PluginRegistry(PLUGIN_ROOT)
    assert not registry.errors
    image = np.full((48, 48, 4), 128, np.uint8)
    noise = np.random.default_rng(17).normal(0, 15, (48, 48, 3))
    image[:, :, :3] = np.clip(128 + noise, 0, 255).astype(np.uint8)
    image[:, :, 3] = np.arange(48, dtype=np.uint8)[:, None]
    result = PluginExecutor(registry).apply("denoise", {}, image)[0]
    assert np.mean((result[:, :, :3].astype(float) - 128)**2) < np.mean((image[:, :, :3].astype(float) - 128)**2)
    np.testing.assert_array_equal(result[:, :, 3], image[:, :, 3])
    for bad in ({"template_window": 4}, {"template_window": 9, "search_window": 7}):
        with pytest.raises(ValueError):
            PluginExecutor(registry).apply("denoise", bad, image)
    tiny = PluginExecutor(registry).apply("denoise", {}, image[:1, :1])[0]
    assert tiny.shape == (1, 1, 4)


def payload(mapping_name, selection=None):
    return {"protocol_version": 1, "message_type": "run_operation", "request_id": "plugin-test",
            "image_id": "source", "operation": "run_plugin", "params": {"plugin_id": "denoise", "arguments": {}},
            "result_image_id": "result", "file_name": "result.png", "selection": selection,
            "output_buffer": {"mapping_name": mapping_name, "width": 16, "height": 12, "channels": 4,
                              "dtype": "uint8", "pixel_format": "BGRA8", "row_stride_bytes": 64, "buffer_size_bytes": 768}}


def test_ipc_schema_catalog_parameters_and_result_mapping(monkeypatch):
    schema = json.loads((Path(__file__).parents[2] / "shared/contracts/ipc-v1.schema.json").read_text())
    validator = Draft202012Validator(schema)
    images = ImageRegistry()
    source = pixels()
    images.register_image("source", "source.png", source)
    request = {"protocol_version": 1, "message_type": "list_plugins", "request_id": "catalog"}
    validator.validate(request)
    catalog = dispatch(parse_command(json.dumps(request).encode()), images)
    validator.validate(catalog)
    assert any(p["id"] == "denoise" for p in catalog["plugins"]) and not catalog["plugin_errors"]
    name = "PhotoLab_Image_" + uuid4().hex
    region = Rect(2, 3, 5, 4)
    command = payload(name, asdict(region))
    validator.validate(command)
    with mmap.mmap(-1, source.nbytes, tagname=name) as mapping:
        response = dispatch(parse_command(json.dumps(command).encode()), images)
        validator.validate(response)
        returned = np.frombuffer(mapping[:], np.uint8).reshape(source.shape)
        expected = PluginExecutor().apply("denoise", {}, source, region)[0]
        np.testing.assert_array_equal(returned, expected)
    command["params"]["arguments"] = {"strength": "wrong"}
    with pytest.raises(ProtocolError) as failure:
        parse_command(json.dumps(command).encode())
    assert failure.value.request_id == "plugin-test"
    assert error_response(failure.value)["status"] == "error"
    command["params"] = {"plugin_id": "missing", "arguments": {}}
    with pytest.raises(ProtocolError):
        parse_command(json.dumps(command).encode())
    command["params"] = {"plugin_id": "denoise", "arguments": {}}
    command["result_image_id"] = "rollback"
    import app.ipc.receiver as receiver
    def fail_write(*args):
        raise OSError("write failure")
    monkeypatch.setattr(receiver, "write_shared_image", fail_write)
    with pytest.raises(OSError):
        dispatch(parse_command(json.dumps(command).encode()), images)
    assert images.get_image("rollback") is None
    np.testing.assert_array_equal(images.get_image("source").pixels, source)


def test_plugin_can_resize_input_in_place_without_mutating_source(tmp_path):
    source = SOURCE.replace("return image", "image.resize((1, 1, 4), refcheck=False)\n        return image")
    registry = make_registry(tmp_path, source)
    image = pixels()
    before = image.copy()
    result = PluginExecutor(registry).apply("example", {"amount": 73}, image, Rect(2, 3, 5, 4))
    assert result[0].shape == (1, 1, 4)
    np.testing.assert_array_equal(image, before)


def test_full_image_selection_and_independent_masks(tmp_path):
    source = SOURCE.replace("image[:, :, 0] = self.amount", """assert image.shape == (12, 16, 4)
        assert selection.x == 2
        assert len(masks) == 2 and masks[0].shape == (12, 16)
        masks[0][:] = 99
        masks.clear()
        image[:, :, 0] = self.amount""")
    registry = make_registry(tmp_path, source)
    image = pixels()
    mask = np.zeros((12, 16), np.uint8)
    masks = [mask, image[:, :, 0]]
    result = PluginExecutor(registry).apply("example", {"amount": 73}, image, Rect(2, 3, 5, 4), masks)
    assert len(result) == 1 and len(masks) == 2
    assert not mask.any()
    assert not np.shares_memory(result[0], image)


@pytest.mark.parametrize("expression", ["image[1:]", "[image, image[:2, :3], image[:1, :1]]"])
def test_arbitrary_dimensions_and_multiple_outputs(tmp_path, expression):
    registry = make_registry(tmp_path, SOURCE.replace("return image", "return " + expression))
    image = pixels()
    before = image.copy()
    results = PluginExecutor(registry).apply("example", {"amount": 73}, image, Rect(2, 3, 5, 4))
    assert [r.shape for r in results] == ([(11, 16, 4)] if expression == "image[1:]" else [(12, 16, 4), (2, 3, 4), (1, 1, 4)])
    np.testing.assert_array_equal(image, before)
    for result in results:
        assert result.flags.c_contiguous and not np.shares_memory(result, image)


@pytest.mark.parametrize("expression", ["[]", "[image, None]", "[image] * 65", "(image, image)"])
def test_entire_result_list_must_be_valid(tmp_path, expression):
    registry = make_registry(tmp_path, SOURCE.replace("return image", "return " + expression))
    with pytest.raises(PluginResultError):
        PluginExecutor(registry).apply("example", {"amount": 73}, pixels())


@pytest.mark.parametrize("image,selection,masks", [
    (np.zeros((2, 2, 3), np.uint8), None, []),
    (np.zeros((2, 2, 4), np.float32), None, []),
    (pixels(), {}, []), (pixels(), Rect(15, 0, 2, 1), []),
    (pixels(), None, ()), (pixels(), None, [np.zeros((2, 2), np.uint8)]),
    (pixels(), None, [pixels()] * 65)
])
def test_common_input_validation(image, selection, masks):
    from app.plugins import validate_image_selection
    with pytest.raises(ValueError):
        validate_image_selection(image, selection, masks)


def test_nested_yaml_path_and_failure_isolation(tmp_path):
    make_registry(tmp_path)
    config = {"version": 1, "plugins": ["example", "../outside", "missing", "example"]}
    (tmp_path / "plugins.yaml").write_text(yaml.safe_dump(config))
    registry = PluginRegistry(tmp_path)
    assert list(registry.definitions) == ["example"]
    assert len(registry.errors) == 3


def test_batch_transfer_with_layer_and_rollback(tmp_path, monkeypatch):
    import app.plugins.catalog as catalog
    import app.ipc.receiver as receiver
    from app.ipc.protocol import encode_message
    source = SOURCE.replace("return image", """assert masks[0].shape == (12, 16)
        return [image[:3, :5], image[:7, :2]]""")
    registry = make_registry(tmp_path, source)
    monkeypatch.setattr(catalog, "get_plugin_registry", lambda: registry)
    monkeypatch.setattr("app.plugins.executor.get_plugin_registry", lambda: registry)
    images = ImageRegistry()
    original = pixels()
    images.register_image("source", "source.png", original)
    schema = json.loads((Path(__file__).parents[2] / "shared/contracts/ipc-v1.schema.json").read_text())
    validator = Draft202012Validator(schema)
    def call(command):
        validator.validate(command)
        response = dispatch(parse_command(json.dumps(command).encode()), images)
        validator.validate(response)
        encode_message(response)
        return response
    def buffer(name, width, height):
        return dict(mapping_name=name, width=width, height=height, channels=4, dtype="uint8",
                    pixel_format="BGRA8", row_stride_bytes=width*4, buffer_size_bytes=width*height*4)
    name = "PhotoLab_Image_" + uuid4().hex
    with mmap.mmap(-1, original.nbytes, tagname=name) as mapping:
        mapping[:] = np.full(original.shape, [0, 0, 0, 255], np.uint8).tobytes()
        command = payload(name)
        command.pop("output_buffer")
        command["params"] = {"plugin_id": "example", "arguments": {"amount": 73}}
        command["mask_buffers"] = [buffer(name, 16, 12)]
        ready = call(command)
        assert [(r["width"], r["height"]) for r in ready["images"]] == [(5, 3), (2, 7)]
        count_after_stage = images.total_bytes
        maps = [mmap.mmap(-1, w*h*4, tagname=n) for n, w, h in
                [(name+"a", 5, 3), (name+"b", 2, 7)]]
        try:
            fetch = {"protocol_version": 1, "message_type": "read_plugin_results", "request_id": "read",
                     "result_image_id": "result", "output_buffers": [buffer(name+"a", 5, 3), buffer(name+"b", 2, 7)]}
            call(fetch)
            assert maps[0][0] == 73 and maps[1][0] == 73
            writes = 0
            def fail_second(*args):
                nonlocal writes
                writes += 1
                if writes == 2:
                    raise OSError("second write failed")
            monkeypatch.setattr(receiver, "write_shared_image", fail_second)
            with pytest.raises(OSError):
                call(fetch)
            assert images.total_bytes < count_after_stage
            assert list(r.metadata.image_id for r in images.records()) == ["source"]
            assert not images.plugin_batches
            call({"protocol_version": 1, "message_type": "release_plugin_results",
                  "request_id": "release", "result_image_id": "result"})
        finally:
            for mapping in maps:
                mapping.close()
    np.testing.assert_array_equal(images.get_image("source").pixels, original)


def test_batch_storage_failure_is_atomic():
    images = ImageRegistry(max_bytes=pixels().nbytes * 2)
    original = pixels()
    images.register_image("source", "source.png", original)
    with pytest.raises(MemoryError):
        images.stage_plugin_results("batch", "result.png", [original, original])
    assert len(images.records()) == 1 and images.total_bytes == original.nbytes
    assert not images.plugin_batches
