"""Regression coverage for native numeric error codes and malformed implementation exceptions."""

import json
from pathlib import Path
import cv2
import numpy as np
import pytest
from jsonschema import Draft202012Validator
from app.ipc.error_responses import error_response
from app.ipc.protocol import encode_message, ProtocolError
from app.models.messages import CommandMessage
from app.operation_service import OperationNotImplementedError, OperationResultError


def test_real_opencv_error_has_text_code_and_retains_native_details() -> None:
    """Reproduce the float-kernel OpenCV failure that previously emitted code=-5 to C#."""
    with pytest.raises(cv2.error) as caught:
        cv2.blur(np.zeros((2, 3, 4), dtype=np.uint8), (5.0, 5.0))
    assert isinstance(caught.value.code, int)
    message = CommandMessage(1, "run_operation", "request", "image")
    response = error_response(caught.value, message)
    assert response["code"] == "opencv_error"
    assert response["request_id"] == "request" and response["image_id"] == "image"
    assert "ksize" in response["message"] and "-5" in response["message"]
    schema = json.loads((Path(__file__).parents[2] / "shared/contracts/ipc-v1.schema.json").read_text())
    Draft202012Validator(schema).validate(json.loads(encode_message(response)))


@pytest.mark.parametrize("native_code", [-5, None, True, {"native": -5}, [1, 2], np.int64(-5), "", " "])
def test_nontext_exception_codes_cannot_leak_into_json(native_code: object) -> None:
    """Normalize arbitrary native_code attributes to a stable class-name string."""
    error = RuntimeError("implementation failed")
    error.code = native_code
    response = error_response(error)
    assert response["code"] == "RuntimeError"
    assert json.loads(encode_message(response))["code"] == "RuntimeError"


@pytest.mark.parametrize("error,code", [(OperationNotImplementedError("pending"), "not_implemented"),
                                        (OperationResultError("bad result"), "invalid_result"),
                                        (ValueError("invalid"), "ValueError")])
def test_application_error_codes_are_preserved(error: Exception, code: str) -> None:
    """Keep explicit protocol code (str) for typed application exceptions."""
    assert error_response(error)["code"] == code


def test_malformed_command_retains_its_safe_correlation() -> None:
    """Use validated IDs from ProtocolError when no CommandMessage was constructed."""
    response = error_response(ProtocolError("invalid descriptor", "request-1", "image-1"))
    assert response["request_id"] == "request-1" and response["image_id"] == "image-1"


def test_long_or_invalid_unicode_details_stay_serializable_and_bounded() -> None:
    """Contain oversized implementation messages within the JSON-line transport limit."""
    response = error_response(RuntimeError("\ud800" + "\n" * 100000))
    encoded = encode_message(response)
    assert len(encoded) < 65536
    assert json.loads(encoded)["message"].endswith("[message truncated]")
