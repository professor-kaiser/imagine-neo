"""CLI supervisor: wait for an acknowledged backend ping before starting WinUI."""

import json
import os
from pathlib import Path
import subprocess
import sys
import time
from uuid import uuid4
import pywintypes
import win32file
from app.ipc.named_pipe_server import PipeConnection, default_pipe_name
from app.runtime_info import SOURCE_REVISION


class BackendRevisionError(RuntimeError):
    """A running backend has not loaded the current source files."""


def connect_ready(timeout: float = 1.0, expected_revision: str | None = None) -> PipeConnection:
    """Connect and ping within timeout (float seconds); return connection or raise TimeoutError.

    expected_revision (str | None) optionally requires the loaded source SHA-256 to match;
    a missing or mismatched revision raises BackendRevisionError. The pipe transports
    metadata only. Callers must close the returned connection.
    """
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            connection = PipeConnection(win32file.CreateFile("\\\\.\\pipe\\" + default_pipe_name(), 0xC0000000, 0, None, 3, 0, None))
        except pywintypes.error:
            time.sleep(0.05)
            continue
        try:
            connection.send({"protocol_version": 1, "message_type": "ping", "request_id": "launcher"})
            response = json.loads(connection.read_line())
            if (response.get("message_type") != "pong" or response.get("request_id") != "launcher"
                    or response.get("protocol_version") != 1 or response.get("status") != "ok"):
                raise RuntimeError("Réponse backend incompatible")
            if expected_revision is not None and response.get("backend_revision") != expected_revision:
                raise BackendRevisionError("Le backend en cours utilise une ancienne version des sources.")
            return connection
        except Exception:
            connection.close()
            raise
    raise TimeoutError("Le backend n’est pas disponible ou une autre interface est connectée.")


def main() -> None:
    """Start the executable supplied by CLI after readiness; propagate its exit code.

    Optional subsequent arguments are passed unchanged to the UI. Only a backend created
    by this supervisor is stopped on exit. Raises SystemExit for launch or readiness failures.
    """
    if len(sys.argv) < 2 or not Path(sys.argv[1]).is_file():
        raise SystemExit("ERREUR : executable absent. Lancez scripts\\build_ui.cmd.")
    owned = None
    result = 1
    # A combined launch owns a fresh session unless a caller explicitly supplies a pipe.
    if not os.environ.get("PHOTOLAB_PIPE_NAME"):
        os.environ["PHOTOLAB_PIPE_NAME"] = "PhotoLab_session_" + uuid4().hex
    try:
        try:
            ready = connect_ready(expected_revision=SOURCE_REVISION)
        except (TimeoutError, BackendRevisionError) as error:
            if isinstance(error, BackendRevisionError):
                print("Ancien backend detecte. Ouverture d'une nouvelle session a jour.", flush=True)
                os.environ["PHOTOLAB_PIPE_NAME"] = "PhotoLab_session_" + uuid4().hex
            owned = subprocess.Popen([sys.executable, "-m", "app.main"], creationflags=subprocess.CREATE_NO_WINDOW)
            ready = connect_ready(15, expected_revision=SOURCE_REVISION)
        ready.close()
        print("Backend ready. Starting Imagine Neo.", flush=True)
        result = subprocess.call(sys.argv[1:], env=os.environ.copy())
    finally:
        if owned is not None and owned.poll() is None:
            try:
                connection = connect_ready(3)
                try:
                    connection.send({"protocol_version": 1, "message_type": "shutdown", "request_id": "launcher-stop"})
                    connection.read_line()
                finally:
                    connection.close()
                owned.wait(timeout=5)
            except Exception:
                owned.terminate()
                owned.wait(timeout=5)
    raise SystemExit(result)


if __name__ == "__main__":
    main()
