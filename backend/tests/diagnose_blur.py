"""Compare local blur behavior with an existing or freshly started backend, without editing it."""

import argparse
from dataclasses import asdict
import json
import mmap
import os
from pathlib import Path
import subprocess
import sys
from uuid import uuid4
import numpy as np

sys.path.insert(0, str(Path(__file__).parents[1]))
from app.launcher import connect_ready
from app.models.image_types import Rect
from app.processing.filters import apply_blur


def main() -> None:
    """Write a diagnostic JSON report using temporary BGRA8 mappings and disposable records.

    CLI arguments:
        --fresh: Start and stop an isolated backend running the current disk sources.
        --report (str): Output report path; no pixel data or existing image metadata is logged.
    Returns:
        None. Each algorithm outcome is recorded, without treating errors as successes.
    Raises:
        OSError, TimeoutError: The pipe is inaccessible or the backend does not respond.
        AssertionError: Temporary records changed the pre-existing registry contents.
    """
    parser = argparse.ArgumentParser()
    parser.add_argument("--fresh", action="store_true")
    parser.add_argument("--report", required=True)
    args = parser.parse_args()
    result = {"fresh_backend": args.fresh, "local_calls": []}
    pixels = np.arange(16 * 12 * 4, dtype=np.uint8).reshape(12, 16, 4)
    selection = Rect(2, 3, 8, 5)
    for radius in (5.0, 5):
        try:
            output = apply_blur(pixels.copy(), radius, selection)
            result["local_calls"].append({"radius_type": type(radius).__name__, "status": "returned",
                "shape": list(output.shape), "alpha_preserved": bool(np.array_equal(output[:, :, 3], pixels[:, :, 3]))})
        except Exception as error:
            result["local_calls"].append({"radius_type": type(radius).__name__, "status": "error",
                "exception": type(error).__name__, "native_code": getattr(error, "code", None), "message": str(error)})
    process = None
    if args.fresh:
        os.environ["PHOTOLAB_PIPE_NAME"] = "PhotoLab_diagnostic_" + uuid4().hex
        process = subprocess.Popen([sys.executable, "-m", "app.main"], cwd=Path(__file__).parents[1],
                                   creationflags=subprocess.CREATE_NO_WINDOW)
    connection = None
    image_id = "diagnostic_" + uuid4().hex
    result_id = "diagnostic_" + uuid4().hex
    def send(kind: str, **fields: object) -> dict:
        """Send kind (str) with JSON fields to the connected pipe; return its raw response dict."""
        connection.send({"protocol_version": 1, "message_type": kind, "request_id": uuid4().hex, **fields})
        return json.loads(connection.read_line())
    try:
        connection = connect_ready(10 if args.fresh else 2)
        baseline = send("list_images")["images"]
        try:
            name = "PhotoLab_Image_" + uuid4().hex
            out_name = "PhotoLab_Image_" + uuid4().hex
            descriptor = {"mapping_name": name, "width": 16, "height": 12, "channels": 4, "dtype": "uint8",
                          "pixel_format": "BGRA8", "row_stride_bytes": 64, "buffer_size_bytes": pixels.nbytes}
            with mmap.mmap(-1, pixels.nbytes, tagname=name) as mapping, mmap.mmap(-1, pixels.nbytes, tagname=out_name):
                mapping[:] = pixels.tobytes()
                imported = send("import_image", image_id=image_id, batch_id=uuid4().hex,
                                file_name="diagnostic.png", pixel_buffer=descriptor)
                assert imported["status"] == "ok", imported
                result["backend_response"] = send("run_operation", image_id=image_id, operation="apply_blur",
                    params={"radius": 5.0}, selection=asdict(selection), result_image_id=result_id,
                    file_name="diagnostic-result.png", output_buffer={**descriptor, "mapping_name": out_name})
                result["backend_code_type"] = type(result["backend_response"].get("code")).__name__
                result["ping_after_error"] = send("ping")["status"]
        finally:
            send("remove_image", image_id=result_id)
            send("remove_image", image_id=image_id)
        assert send("list_images")["images"] == baseline
        result["existing_registry_unchanged"] = True
    finally:
        if connection is not None:
            if process is not None:
                send("shutdown")
            connection.close()
        if process is not None:
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.terminate()
                process.wait(timeout=5)
    Path(args.report).write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
