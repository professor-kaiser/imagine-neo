"""Real-process named pipe tests: fragmentation, batches, errors and reconnects."""

import hashlib
import json
import mmap
import os
import subprocess
import sys
import time
from uuid import uuid4
import pytest
import pywintypes
import win32file
import numpy as np
from app.ipc.named_pipe_server import PipeConnection
from app.ipc.protocol import encode_message


def connect(name: str) -> PipeConnection:
    """Connect to name (str) within 15 seconds; return PipeConnection or raise TimeoutError."""
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        try:
            return PipeConnection(win32file.CreateFile("\\\\.\\pipe\\" + name, 0xC0000000, 0, None, 3, 0, None))
        except pywintypes.error:
            time.sleep(0.05)
    raise TimeoutError("Backend did not become ready")


@pytest.fixture
def backend():
    """Yield (unique pipe name, subprocess) and terminate the test-owned backend on failure."""
    name = "PhotoLab_test_" + uuid4().hex
    process = subprocess.Popen([sys.executable, "-m", "app.main", "--pipe-name", name],
                               creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
    try:
        yield name, process
    finally:
        if process.poll() is None:
            process.terminate()
        process.wait(timeout=10)


def test_multiple_images_error_recovery_and_reconnect(backend) -> None:
    """Send two distinct raw mappings, verify hashes after disposal, and survive a bad import."""
    name, process = backend
    client = connect(name)
    try:
        # Deliberately fragment a command and coalesce the following one in the same write.
        ping = encode_message({"protocol_version": 1, "message_type": "ping", "request_id": "first"})
        win32file.WriteFile(client.handle, ping[:7])
        win32file.WriteFile(client.handle, ping[7:] + b"{bad}\n")
        assert json.loads(client.read_line())["message_type"] == "pong"
        assert json.loads(client.read_line())["status"] == "error"
        expected = {}
        for index in range(3):
            mapping_name = "PhotoLab_Image_" + uuid4().hex
            pixels = bytes((v * 7 + index) % 256 for v in range(24))
            payload = {"protocol_version": 1, "message_type": "import_image", "request_id": f"r{index}",
                       "batch_id": "batch", "image_id": f"i{index}", "file_name": f"test{index}.png",
                       "pixel_buffer": {"mapping_name": mapping_name, "width": 3, "height": 2,
                                        "channels": 4, "dtype": "uint8", "pixel_format": "BGRA8",
                                        "row_stride_bytes": 12, "buffer_size_bytes": 24}}
            if index == 1:
                client.send(payload)
                response = json.loads(client.read_line())
                assert response["status"] == "error" and response["image_id"] == "i1"
                continue
            with mmap.mmap(-1, 24, tagname=mapping_name) as mapping:
                mapping.write(pixels)
                client.send(payload)
                response = json.loads(client.read_line())
                assert response["status"] == "ok", response
                assert response["request_id"] == f"r{index}"
                expected[f"i{index}"] = hashlib.sha256(pixels).hexdigest()
                assert response["sha256"] == expected[f"i{index}"]
    finally:
        client.close()
    client = connect(name)
    try:
        client.send({"protocol_version": 1, "message_type": "list_images", "request_id": "list"})
        stored = json.loads(client.read_line())["images"]
        assert {r["image_id"]: r["sha256"] for r in stored} == expected
        client.send({"protocol_version": 1, "message_type": "remove_image", "request_id": "remove", "image_id": "i0"})
        assert json.loads(client.read_line())["removed"] is True
        client.send({"protocol_version": 1, "message_type": "shutdown", "request_id": "stop"})
        assert json.loads(client.read_line())["message_type"] == "shutdown_ack"
    finally:
        client.close()
    assert process.wait(timeout=10) == 0


def test_grayscale_round_trip_and_error_recovery(backend) -> None:
    """Return real processed pixels through a mapping; retain originals after an invalid ROI."""
    from test_grayscale import color_pixels, grayscale_payload

    name, process = backend
    client = connect(name)
    source = color_pixels()
    try:
        input_name = "PhotoLab_Image_" + uuid4().hex
        descriptor = grayscale_payload(input_name)["output_buffer"]
        with mmap.mmap(-1, source.nbytes, tagname=input_name) as mapping:
            mapping[:] = source.tobytes()
            client.send({"protocol_version": 1, "message_type": "import_image", "request_id": "source-import",
                         "image_id": "source", "file_name": "source.png", "batch_id": "gray-test",
                         "pixel_buffer": descriptor})
            assert json.loads(client.read_line())["status"] == "ok"
        for selection, result_id in [(None, "full"), ({"x": 1, "y": 1, "width": 2, "height": 1}, "partial")]:
            output_name = "PhotoLab_Image_" + uuid4().hex
            payload = grayscale_payload(output_name)
            payload.update(result_image_id=result_id, selection={"x": 5, "y": 0, "width": 1, "height": 1})
            with mmap.mmap(-1, source.nbytes, tagname=output_name) as mapping:
                client.send(payload)
                error = json.loads(client.read_line())
                assert error["status"] == "error" and error["request_id"] == payload["request_id"]
                payload["selection"] = selection
                client.send(payload)
                response = json.loads(client.read_line())
                assert response["message_type"] == "image_processed", response
                assert response["image_id"] == "source" and response["result_image_id"] == result_id
                pixels = bytes(mapping[:])
                assert hashlib.sha256(pixels).hexdigest() == response["sha256"]
                expected = source.copy()
                if selection is None:
                    expected[:, :, :3] = np.array([29, 150, 76, 255], dtype=np.uint8)[None, :, None]
                else:
                    expected[1, 1, :3] = 150
                    expected[1, 2, :3] = 76
                assert pixels == expected.tobytes()
        client.send({"protocol_version": 1, "message_type": "list_images", "request_id": "gray-list"})
        stored = json.loads(client.read_line())["images"]
        assert {r["image_id"] for r in stored} == {"source", "full", "partial"}
        assert next(r for r in stored if r["image_id"] == "source")["sha256"] == hashlib.sha256(source.tobytes()).hexdigest()
        client.send({"protocol_version": 1, "message_type": "shutdown", "request_id": "gray-stop"})
        assert json.loads(client.read_line())["message_type"] == "shutdown_ack"
    finally:
        client.close()
    assert process.wait(timeout=10) == 0
