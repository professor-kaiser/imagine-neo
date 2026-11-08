"""Drawing compositing, mask transport and result contracts."""
import copy
import json
import mmap
from pathlib import Path
from uuid import uuid4

import numpy as np
import pytest
from jsonschema import Draft202012Validator, ValidationError
from app.ipc.protocol import parse_command, ProtocolError
from app.ipc.receiver import dispatch
from app.operation_service import OperationNotImplementedError, OperationResultError
from app.processing import drawing
from app.storage.image_registry import ImageRegistry


def descriptor(name, width=4, height=3):
    return dict(mapping_name=name, width=width, height=height, channels=4, dtype="uint8",
                pixel_format="BGRA8", row_stride_bytes=width*4, buffer_size_bytes=width*height*4)


def payload():
    return dict(protocol_version=1, message_type="run_operation", request_id="draw", image_id="source",
                result_image_id="result", file_name="draw.png", operation="apply_drawing", params={},
                mask_buffer=descriptor("PhotoLab_Image_"+uuid4().hex), output_buffer=descriptor("PhotoLab_Image_"+uuid4().hex))


def registry():
    store = ImageRegistry()
    store.register_image("source", "source.png", np.arange(48, dtype=np.uint8).reshape(3, 4, 4))
    return store


def validator():
    return Draft202012Validator(json.loads((Path(__file__).parents[2]/"shared/contracts/ipc-v1.schema.json").read_text()))


def test_colored_mask_is_copied_and_full_result_returned(monkeypatch):
    message = payload()
    mask = np.zeros((3, 4, 4), dtype=np.uint8)
    mask[1, 2] = [13, 87, 219, 128]
    store = registry()
    original = store.get_image("source").pixels.copy()
    calls = []

    def sentinel(image, mask):
        np.testing.assert_array_equal(mask[1, 2], [13, 87, 219, 128])
        assert image.flags.writeable and mask.flags.writeable
        assert image.flags.c_contiguous and mask.flags.c_contiguous
        assert not np.shares_memory(image, store.get_image("source").pixels)
        calls.append(True)
        image[1, 2] = [77, 88, 99, 111]  # Test sentinel, not a blending algorithm.
        mask[:] = 0
        return image

    monkeypatch.setattr(drawing, "apply_drawing", sentinel)
    validator().validate(message)
    with mmap.mmap(-1, 48, tagname=message['mask_buffer']['mapping_name']) as incoming, mmap.mmap(-1, 48, tagname=message['output_buffer']['mapping_name']) as outgoing:
        incoming[:] = mask.tobytes()
        response = dispatch(parse_command(json.dumps(message).encode()), store)
        validator().validate(response)
        assert calls == [True]
        assert incoming[:] == mask.tobytes()
        assert outgoing[:] == store.get_image('result').pixels.tobytes()
        assert outgoing[24:28] == bytes([77, 88, 99, 111])
    np.testing.assert_array_equal(store.get_image('source').pixels, original)


def test_unavailable_callback_keeps_source_and_does_not_publish_result(monkeypatch):
    monkeypatch.setattr(drawing, "apply_drawing", lambda image, mask: None)
    message = payload()
    store = registry()
    with mmap.mmap(-1, 48, tagname=message['mask_buffer']['mapping_name']) as incoming, mmap.mmap(-1, 48, tagname=message['output_buffer']['mapping_name']) as outgoing:
        incoming[:] = bytes([0, 0, 255, 255])*12
        outgoing[:] = bytes([123])*48
        with pytest.raises(OperationNotImplementedError):
            dispatch(parse_command(json.dumps(message).encode()), store)
        assert outgoing[:] == bytes([123])*48
        assert len(store.records()) == 1


@pytest.mark.parametrize('change', ['missing', 'stride', 'format', 'selection', 'params', 'extra'])
def test_malformed_mask_commands_rejected(change):
    message = payload()
    if change == 'missing': del message['mask_buffer']
    elif change == 'stride': message['mask_buffer']['row_stride_bytes'] = 3
    elif change == 'format': message['mask_buffer']['pixel_format'] = 'RGBA8'
    elif change == 'selection': message['selection'] = dict(x=0, y=0, width=1, height=1)
    elif change == 'params': message['params']['size'] = 5
    elif change == 'extra': message['mask_buffer']['pixels'] = [1, 2, 3]
    with pytest.raises(ProtocolError): parse_command(json.dumps(message).encode())
    if change != 'stride':
        with pytest.raises(ValidationError): validator().validate(message)


@pytest.mark.parametrize('change', ['shape', 'alias'])
def test_mask_dimensions_and_mapping_identity_checked_before_read(change):
    message = payload()
    if change == 'shape': message['mask_buffer'] = descriptor(message['mask_buffer']['mapping_name'], 2, 6)
    else: message['mask_buffer'] = copy.deepcopy(message['output_buffer'])
    with pytest.raises(ValueError): dispatch(parse_command(json.dumps(message).encode()), registry())


@pytest.mark.parametrize('failure', ['outside', 'dtype', 'shape', 'exception'])
def test_failed_implementation_is_not_committed(failure, monkeypatch):
    message = payload()
    store = registry()
    original = store.get_image('source').pixels.copy()
    def bad(image, mask):
        if failure == 'dtype': return image.astype(float)
        if failure == 'shape': return image[:1]
        if failure == 'exception': raise RuntimeError('drawing failed')
        image[0, 0] = 0
        return image
    monkeypatch.setattr(drawing, 'apply_drawing', bad)
    with mmap.mmap(-1, 48, tagname=message['mask_buffer']['mapping_name']) as incoming:
        incoming[:] = bytes(48)
        with pytest.raises(RuntimeError if failure == 'exception' else OperationResultError):
            dispatch(parse_command(json.dumps(message).encode()), store)
    assert len(store.records()) == 1
    np.testing.assert_array_equal(store.get_image('source').pixels, original)


@pytest.mark.parametrize("background,foreground,expected", [
    ([20, 40, 60, 255], [220, 140, 20, 128], [120, 90, 40, 255]),
    ([200, 100, 50, 0], [10, 80, 230, 128], [10, 80, 230, 128]),
    ([0, 0, 255, 128], [255, 0, 0, 128], [170, 0, 85, 192]),
    ([0, 0, 255, 1], [255, 0, 0, 1], [128, 0, 127, 2]),
    ([21, 87, 231, 0], [200, 55, 91, 0], [21, 87, 231, 0]),
    ([21, 87, 231, 140], [200, 55, 91, 0], [21, 87, 231, 140]),
    ([20, 40, 60, 12], [221, 142, 23, 255], [221, 142, 23, 255]),
    ([255, 255, 255, 255], [255, 255, 255, 254], [255, 255, 255, 255]),
])
def test_known_source_over_colors_and_alpha(background, foreground, expected):
    image = np.array([[background]], dtype=np.uint8)
    mask = np.array([[foreground]], dtype=np.uint8)
    result = drawing.apply_drawing(image, mask)
    np.testing.assert_array_equal(result[0, 0], expected)
    np.testing.assert_array_equal(image[0, 0], background)
    np.testing.assert_array_equal(mask[0, 0], foreground)
    assert result.dtype == np.uint8 and result.flags.c_contiguous
    assert not np.shares_memory(result, image) and not np.shares_memory(result, mask)


def test_random_colors_against_exact_porter_duff_reference():
    from fractions import Fraction
    rng = np.random.default_rng(418)
    image = rng.integers(0, 256, size=(16, 16, 4), dtype=np.uint8)
    mask = rng.integers(0, 256, size=image.shape, dtype=np.uint8)
    # Exercise every source alpha, including transparent and opaque endpoints.
    mask[:, :, 3] = np.arange(256, dtype=np.uint8).reshape(16, 16)
    result = drawing.apply_drawing(image, mask)
    def quantize(value):
        return int(value + Fraction(1, 2))
    for position in np.ndindex(image.shape[:2]):
        front, back = mask[position], image[position]
        af, ab = Fraction(int(front[3]), 255), Fraction(int(back[3]), 255)
        if af == 0:
            np.testing.assert_array_equal(result[position], back)
            continue
        out_alpha = af + ab * (1 - af)
        expected = [quantize((int(front[c]) * af + int(back[c]) * ab * (1-af)) / out_alpha) for c in range(3)]
        expected.append(quantize(out_alpha * 255))
        np.testing.assert_array_equal(result[position], expected)


def test_read_only_strided_inputs_and_erased_pixels():
    image = np.arange(8 * 10 * 4, dtype=np.uint8).reshape(8, 10, 4)[::-2, ::-2]
    mask = np.zeros((8, 10, 4), dtype=np.uint8)[::-2, ::-2]
    mask[1, 2] = [200, 100, 50, 255]
    mask[2, 3] = [99, 88, 77, 0]  # Erased coverage must not affect hidden RGB.
    original, original_mask = image.copy(), mask.copy()
    image.flags.writeable = mask.flags.writeable = False
    expected = original.copy()
    expected[1, 2] = [200, 100, 50, 255]
    result = drawing.apply_drawing(image, mask)
    np.testing.assert_array_equal(result, expected)
    np.testing.assert_array_equal(image, original)
    np.testing.assert_array_equal(mask, original_mask)
    assert result.flags.c_contiguous and result.flags.writeable


def test_chunk_boundaries_and_empty_mask_return_independent_images():
    image = np.full((513, 1024, 4), [0, 0, 255, 128], dtype=np.uint8)
    mask = np.zeros_like(image)
    unchanged = drawing.apply_drawing(image, mask)
    np.testing.assert_array_equal(unchanged, image)
    assert not np.shares_memory(unchanged, image)
    expected = image.copy()
    for row in (0, 255, 256, 511, 512):
        mask[row, -1] = [255, 0, 0, 128]
        expected[row, -1] = [170, 0, 85, 192]
    np.testing.assert_array_equal(drawing.apply_drawing(image, mask), expected)


@pytest.mark.parametrize("argument", ["image", "mask"])
@pytest.mark.parametrize("bad", [None, [[[1, 2, 3, 4]]], np.zeros((1, 1, 4), dtype=float),
    np.zeros((1, 4), dtype=np.uint8), np.zeros((1, 1, 3), dtype=np.uint8),
    np.zeros((0, 1, 4), dtype=np.uint8), np.zeros((1, 0, 4), dtype=np.uint8),
    np.zeros((2, 1, 4), dtype=np.uint8)])
def test_invalid_algorithm_inputs(argument, bad):
    inputs = dict(image=np.zeros((1, 1, 4), dtype=np.uint8), mask=np.zeros((1, 1, 4), dtype=np.uint8))
    inputs[argument] = bad
    with pytest.raises(TypeError if not isinstance(bad, np.ndarray) else ValueError):
        drawing.apply_drawing(**inputs)


def test_real_drawing_is_published_through_shared_memory():
    message = payload()
    store = ImageRegistry()
    original = np.full((3, 4, 4), [0, 0, 255, 128], dtype=np.uint8)
    store.register_image("source", "source.png", original)
    mask = np.zeros_like(original)
    mask[1, 2] = [255, 0, 0, 128]
    expected = original.copy()
    expected[1, 2] = [170, 0, 85, 192]
    with mmap.mmap(-1, 48, tagname=message['mask_buffer']['mapping_name']) as incoming, mmap.mmap(-1, 48, tagname=message['output_buffer']['mapping_name']) as outgoing:
        incoming[:] = mask.tobytes()
        response = dispatch(parse_command(json.dumps(message).encode()), store)
        validator().validate(response)
        assert response['message_type'] == 'image_processed'
        assert outgoing[:] == expected.tobytes()
        assert incoming[:] == mask.tobytes()
        np.testing.assert_array_equal(store.get_image('result').pixels, expected)
    np.testing.assert_array_equal(store.get_image('source').pixels, original)
