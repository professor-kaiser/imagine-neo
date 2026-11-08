"""Protocol and canonical layout validation against real examples and JSON Schema."""

import json
from pathlib import Path
import pytest
from jsonschema import Draft202012Validator
from app.ipc.protocol import ProtocolError, parse_command, validate_dimensions
from app.models.messages import ImportImageMessage


def import_payload() -> dict:
    """Return a JSON-compatible test import descriptor for a (2,3,4) BGRA8 image."""
    return {"protocol_version": 1, "message_type": "import_image", "request_id": "r1", "batch_id": "b1",
            "image_id": "i1", "file_name": "été.png", "pixel_buffer": {"mapping_name": "PhotoLab_Image_test",
            "width": 3, "height": 2, "channels": 4, "dtype": "uint8", "pixel_format": "BGRA8",
            "row_stride_bytes": 12, "buffer_size_bytes": 24}}


def test_valid_import_and_schema() -> None:
    """Check that the parser and published schema accept a canonical import."""
    payload = import_payload()
    message = parse_command(json.dumps(payload).encode())
    assert isinstance(message, ImportImageMessage)
    assert message.file_name == "été.png"
    schema = json.loads((Path(__file__).parents[2] / "shared/contracts/ipc-v1.schema.json").read_text())
    Draft202012Validator(schema).validate(payload)


@pytest.mark.parametrize("data", [b"{", b"[]", b"\xff", b'{"protocol_version":1,"protocol_version":1}',
                                      b'{"protocol_version":true}', b"x" * 65537],
                         ids=["truncated", "array", "invalid_utf8", "duplicate_key", "boolean_version", "oversized"])
def test_malformed_json(data: bytes) -> None:
    """Reject data (bytes) containing malformed, oversized or ambiguous envelopes."""
    with pytest.raises(ProtocolError):
        parse_command(data)


@pytest.mark.parametrize("key,value", [("dtype", "float32"), ("pixel_format", "RGBA8"), ("channels", 3),
                                       ("width", 0), ("height", -1), ("width", True), ("width", 1.5),
                                       ("row_stride_bytes", 16), ("buffer_size_bytes", 25),
                                       ("mapping_name", "arbitrary_mapping")])
def test_invalid_descriptor(key: str, value: object) -> None:
    """Reject a descriptor with field key (str) replaced by invalid value (object)."""
    payload = import_payload()
    payload["pixel_buffer"][key] = value
    with pytest.raises(ProtocolError) as error:
        parse_command(json.dumps(payload).encode())
    assert error.value.request_id == "r1"
    assert error.value.image_id == "i1"


def test_unsupported_version_and_pixels_in_json() -> None:
    """Reject an incompatible protocol version and unexpected pixel content."""
    payload = import_payload()
    payload["protocol_version"] = 2
    with pytest.raises(ProtocolError):
        parse_command(json.dumps(payload).encode())
    payload["protocol_version"] = 1
    payload["pixels"] = "encoded bytes are forbidden"
    with pytest.raises(ProtocolError):
        parse_command(json.dumps(payload).encode())


def test_layout_limit() -> None:
    """Reject dimensions whose product exceeds the configured transfer limit."""
    with pytest.raises(ValueError):
        validate_dimensions(32768, 32768, 4, 131072, 4294967296)

