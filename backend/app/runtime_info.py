"""Identify the backend source revision loaded by a persistent process."""

import hashlib
import os
from pathlib import Path


def compute_source_revision(app_directory: Path | None = None) -> str:
    """Hash Python sources deterministically, using the same layout as the C# client.

    Args:
        app_directory (Path | None): Backend app source directory; defaults to this module's
            parent. Relative POSIX paths and raw file bytes participate in the digest.
    Returns:
        str: Lowercase SHA-256 hex digest. Each path and file content ends with a NUL byte.
            Python cache files are excluded. No image data is read.
    Raises:
        OSError: A source file cannot be read. ValueError: No Python sources were found.
    """
    root = app_directory if app_directory is not None else Path(__file__).parent
    sources = [(path.relative_to(root).as_posix(), path) for path in root.rglob("*.py")]
    if app_directory is None:
        plugins = root.parent.parent / "plugins"
        sources.extend(("plugins/" + path.relative_to(plugins).as_posix(), path)
                       for path in plugins.rglob("*") if path.is_file() and path.suffix in {".py", ".yaml", ".yml"})
    sources.sort(key=lambda item: item[0])
    if not sources:
        raise ValueError("Backend Python sources are missing")
    digest = hashlib.sha256()
    for relative, path in sources:
        digest.update(relative.encode("utf-8") + b"\0")
        digest.update(path.read_bytes())
        digest.update(b"\0")
    return digest.hexdigest()


# Snapshot once at import: a long-running process must not claim later disk edits are loaded.
SOURCE_REVISION = compute_source_revision()
PROCESS_ID = os.getpid()
