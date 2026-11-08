"""Regression checks for deterministic source identity and persistent revision snapshots."""

import hashlib
import json
from pathlib import Path
from jsonschema import Draft202012Validator
import pytest
from app import runtime_info
from app.ipc.receiver import dispatch
from app.models.messages import CommandMessage
from app.storage.image_registry import ImageRegistry


def test_source_revision_uses_sorted_relative_paths_and_raw_content(tmp_path: Path) -> None:
    """Hash fixture Python sources in the exact layout shared with C#, ignoring non-source files."""
    (tmp_path / "z.py").write_bytes(b"z\r\n")
    (tmp_path / "nested").mkdir()
    (tmp_path / "nested/a.py").write_bytes(b"a\n")
    (tmp_path / "cache.pyc").write_bytes(b"ignored")
    assert runtime_info.compute_source_revision(tmp_path) == hashlib.sha256(b"nested/a.py\0a\n\0z.py\0z\r\n\0").hexdigest()


def test_source_changes_and_renames_change_revision(tmp_path: Path) -> None:
    """Changing a function or module name invalidates the expected runtime identity."""
    source = tmp_path / "filters.py"
    source.write_bytes(b"pass")
    before = runtime_info.compute_source_revision(tmp_path)
    source.write_bytes(b"return image")
    after = runtime_info.compute_source_revision(tmp_path)
    assert before != after
    source.rename(tmp_path / "renamed.py")
    assert runtime_info.compute_source_revision(tmp_path) != after


def test_missing_sources_are_reported(tmp_path: Path) -> None:
    """A missing backend cannot be mistaken for a valid empty revision."""
    with pytest.raises(ValueError, match="missing"):
        runtime_info.compute_source_revision(tmp_path)


def test_pong_reports_loaded_snapshot_not_a_new_disk_digest(monkeypatch) -> None:
    """Keep the startup revision even if subsequent disk inspection would return a new value."""
    loaded = runtime_info.SOURCE_REVISION
    monkeypatch.setattr(runtime_info, "compute_source_revision", lambda: "0" * 64)
    response = dispatch(CommandMessage(1, "ping", "runtime-check"), ImageRegistry())
    assert response["backend_revision"] == loaded and response["backend_pid"] == runtime_info.PROCESS_ID
    schema = json.loads((Path(__file__).parents[2] / "shared/contracts/ipc-v1.schema.json").read_text())
    Draft202012Validator(schema).validate(response)
