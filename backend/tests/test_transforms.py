"""Transform parameter/output contracts; pixel algorithms stay unimplemented."""
import json
import mmap
from pathlib import Path
from uuid import uuid4
import numpy as np
import pytest
from jsonschema import Draft202012Validator
from app.ipc.protocol import parse_command, ProtocolError
from app.ipc.receiver import dispatch
from app.models.operations import normalize_parameters
from app.models.transforms import output_size, TRANSFORM_OPERATIONS
from app.operation_service import OperationNotImplementedError, OperationResultError
from app.processing import transforms
from app.storage.image_registry import ImageRegistry

CASES = {
    'rotate_image': {'angle_degrees': 90},
    'scale_image': {'scale_factor': 2},
    'rotate_scale_image': {'angle_degrees': -90, 'scale_factor': 2},
}


def payload(operation):
    width, height = output_size(operation, CASES[operation], 4, 3)
    return dict(protocol_version=1, message_type='run_operation', request_id='transform', image_id='source',
                operation=operation, params=CASES[operation].copy(), result_image_id='result', file_name='result.png',
                output_buffer=dict(mapping_name='PhotoLab_Image_'+uuid4().hex, width=width, height=height,
                    channels=4, dtype='uint8', pixel_format='BGRA8', row_stride_bytes=width*4, buffer_size_bytes=width*height*4))


def registry():
    store = ImageRegistry()
    store.register_image('source', 'source.png', np.arange(48, dtype=np.uint8).reshape(3, 4, 4))
    return store


def validator():
    return Draft202012Validator(json.loads((Path(__file__).parents[2]/'shared/contracts/ipc-v1.schema.json').read_text()))


@pytest.mark.parametrize('operation', CASES)
def test_transform_routes_values_and_returns_different_dimensions(operation, monkeypatch):
    store = registry()
    original = store.get_image('source').pixels.copy()
    request = payload(operation)
    width, height = output_size(operation, request['params'], 4, 3)
    expected = np.full((height, width, 4), [17, 83, 209, 127], dtype=np.uint8)
    calls = []
    def sentinel(image, **kwargs):
        assert kwargs == request['params'] and all(type(v) is float for v in kwargs.values())
        np.testing.assert_array_equal(image, original)
        image[:] = 0
        calls.append(True)
        return expected
    monkeypatch.setattr(transforms, operation, sentinel)
    validator().validate(request)
    descriptor = request['output_buffer']
    with mmap.mmap(-1, descriptor['buffer_size_bytes'], tagname=descriptor['mapping_name']) as mapping:
        response = dispatch(parse_command(json.dumps(request).encode()), store)
        validator().validate(response)
        assert (response['width'], response['height']) == (width, height)
        assert mapping[:] == expected.tobytes()
        np.testing.assert_array_equal(store.get_image('result').pixels, expected)
    assert calls == [True]
    np.testing.assert_array_equal(store.get_image('source').pixels, original)


@pytest.mark.parametrize('operation', CASES)
def test_placeholders_preserve_original_and_output(operation):
    request = payload(operation)
    store = registry()
    original = store.get_image('source').pixels.copy()
    descriptor = request['output_buffer']
    with mmap.mmap(-1, descriptor['buffer_size_bytes'], tagname=descriptor['mapping_name']) as mapping:
        mapping[:] = bytes([73])*descriptor['buffer_size_bytes']
        with pytest.raises(OperationNotImplementedError, match=operation):
            dispatch(parse_command(json.dumps(request).encode()), store)
        assert mapping[:] == bytes([73])*descriptor['buffer_size_bytes']
    assert len(store.records()) == 1
    np.testing.assert_array_equal(store.get_image('source').pixels, original)


@pytest.mark.parametrize('angle,scale,expected', [
    (0, 1, (4,3)), (90, 1, (3,4)), (-90, 1, (3,4)), (180, 1, (4,3)),
    (360, 1, (4,3)), (-360, 1, (4,3)), (45, 1, (5,5)), (-45, 1, (5,5)),
    (90, 2, (6,8)), (0, 0.5, (2,2)), (0, 0.01, (1,1)), (0, 8, (32,24))])
def test_output_bounds_without_cropping(angle, scale, expected):
    assert output_size('rotate_scale_image', dict(angle_degrees=angle, scale_factor=scale), 4, 3) == expected


@pytest.mark.parametrize('operation,key,bad', [
    ('rotate_image','angle_degrees',True), ('rotate_image','angle_degrees',float('nan')),
    ('rotate_image','angle_degrees',float('inf')), ('rotate_image','angle_degrees',-361),
    ('rotate_image','angle_degrees',361), ('rotate_image','angle_degrees','90'),
    ('scale_image','scale_factor',True), ('scale_image','scale_factor',0),
    ('scale_image','scale_factor',-1), ('scale_image','scale_factor',0.001),
    ('scale_image','scale_factor',8.1), ('scale_image','scale_factor',float('nan'))])
def test_invalid_transform_values_rejected(operation, key, bad):
    request = payload(operation)
    request['params'][key] = bad
    with pytest.raises(ProtocolError): parse_command(json.dumps(request).encode())


@pytest.mark.parametrize('operation', CASES)
@pytest.mark.parametrize('invalid', ['selection','missing','extra'])
def test_selections_and_unexpected_parameters_rejected(operation, invalid):
    request = payload(operation)
    if invalid == 'selection': request['selection'] = dict(x=0,y=0,width=1,height=1)
    elif invalid == 'missing': request['params'].clear()
    else: request['params']['interpolation'] = 'unknown'
    with pytest.raises(ProtocolError): parse_command(json.dumps(request).encode())


@pytest.mark.parametrize('invalid', ['shape','dtype','exception'])
def test_bad_transform_result_does_not_create_version(invalid, monkeypatch):
    request = payload('rotate_scale_image')
    store = registry()
    original = store.get_image('source').pixels.copy()
    def bad(image, **kwargs):
        if invalid == 'exception': raise RuntimeError('transform failed')
        if invalid == 'dtype': return np.zeros((8,6,4), dtype=float)
        return image
    monkeypatch.setattr(transforms, 'rotate_scale_image', bad)
    with pytest.raises(RuntimeError if invalid == 'exception' else OperationResultError):
        dispatch(parse_command(json.dumps(request).encode()), store)
    assert len(store.records()) == 1
    np.testing.assert_array_equal(store.get_image('source').pixels, original)


def test_output_mapping_extent_is_checked_before_invoking_algorithm(monkeypatch):
    request = payload('rotate_image')
    request['output_buffer'].update(width=4,height=3,row_stride_bytes=16,buffer_size_bytes=48)
    def forbidden(*args, **kwargs): pytest.fail('invalid output reached the algorithm')
    monkeypatch.setattr(transforms, 'rotate_image', forbidden)
    with pytest.raises(ValueError, match='Output dimensions'):
        dispatch(parse_command(json.dumps(request).encode()), registry())


def test_expanded_output_limits_and_no_nested_transform():
    with pytest.raises(ValueError, match='limit'):
        output_size('scale_image', {'scale_factor':8}, 32768, 1)
    with pytest.raises(ValueError, match='limit'):
        output_size('rotate_image', {'angle_degrees':45}, 11000, 11000)
    with pytest.raises(ValueError):
        normalize_parameters('apply_to_selection', {'operation':'rotate_image','params':{'angle_degrees':90}})
