"""Exercise the combined launcher with real processes, replacing only the GUI executable."""

import json
import os
from pathlib import Path
import subprocess
import sys
from uuid import uuid4
from app.runtime_info import SOURCE_REVISION
from test_server import connect


def launch_probe(tmp_path: Path, environment: dict[str, str]) -> dict:
    """Launch a child probe and return its ping report; the real supervisor owns the backend.

    Args:
        tmp_path (Path): Isolated pytest output directory.
        environment (dict[str, str]): Child process environment, optionally with an explicit pipe.
    Returns:
        dict: Runtime pipe name, source revision and process ID; no image data.
    Raises:
        CalledProcessError, TimeoutExpired: Supervised launch failed or did not finish.
    """
    report = tmp_path / (uuid4().hex + ".json")
    probe = ("import json,os,sys; from app.launcher import connect_ready; "
             "c=connect_ready(); c.send({'protocol_version':1,'message_type':'ping','request_id':'probe'}); "
             "r=json.loads(c.read_line()); c.close(); r['pipe']=os.environ['PHOTOLAB_PIPE_NAME']; "
             "open(sys.argv[1],'w').write(json.dumps(r))")
    subprocess.run([sys.executable, "-m", "app.launcher", sys.executable, "-c", probe, str(report)],
                   env=environment, check=True, timeout=25, creationflags=subprocess.CREATE_NO_WINDOW)
    return json.loads(report.read_text())


def test_default_combined_launches_have_independent_current_backends(tmp_path: Path) -> None:
    """A normal run_all session must not attach to an old per-user server after the UI restarts."""
    environment = os.environ.copy()
    environment.pop("PHOTOLAB_PIPE_NAME", None)
    first = launch_probe(tmp_path, environment)
    second = launch_probe(tmp_path, environment)
    assert first["pipe"] != second["pipe"]
    assert first["pipe"].startswith("PhotoLab_session_")
    assert first["backend_revision"] == second["backend_revision"] == SOURCE_REVISION


def test_explicit_outdated_backend_is_preserved_and_bypassed(tmp_path: Path) -> None:
    """A configured but outdated external backend retains its registry while the UI gets a new one."""
    name = "PhotoLab_old_" + uuid4().hex
    legacy = "import app.ipc.receiver as r; r.SOURCE_REVISION='0'*64; from app.main import main; main()"
    process = subprocess.Popen([sys.executable, "-c", legacy, "--pipe-name", name], creationflags=subprocess.CREATE_NO_WINDOW)
    try:
        ready = connect(name)
        ready.close()
        environment = {**os.environ, "PHOTOLAB_PIPE_NAME": name}
        result = launch_probe(tmp_path, environment)
        assert result["pipe"] != name and result["backend_revision"] == SOURCE_REVISION
        assert process.poll() is None
        original = connect(name)
        try:
            original.send({"protocol_version": 1, "message_type": "ping", "request_id": "still-running"})
            assert json.loads(original.read_line())["backend_revision"] == "0" * 64
            original.send({"protocol_version": 1, "message_type": "shutdown", "request_id": "stop-test"})
            assert json.loads(original.read_line())["message_type"] == "shutdown_ack"
        finally:
            original.close()
        assert process.wait(timeout=5) == 0
    finally:
        if process.poll() is None:
            process.terminate()
        process.wait(timeout=5)
