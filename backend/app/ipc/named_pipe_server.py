"""Windows byte-stream named pipes using the maintained pywin32 Win32 bindings."""

import getpass
import os
import re
import pywintypes
import win32file
import win32pipe
from app.ipc.protocol import MAX_LINE_BYTES, ProtocolError, encode_message


def default_pipe_name() -> str:
    """Return str pipe identity for the current Windows user, overridable by environment."""
    return os.environ.get("PHOTOLAB_PIPE_NAME", "PhotoLab_v1_" + re.sub(r"[^A-Za-z0-9_-]", "_", getpass.getuser()))


class PipeConnection:
    """One buffered byte stream; messages may span or share arbitrary ReadFile calls."""

    def __init__(self, handle: pywintypes.HANDLE) -> None:
        """Wrap handle (pywin32 HANDLE); ownership transfers to this connection."""
        self.handle = handle
        self._buffer = bytearray()
        self._closed = False

    def read_line(self) -> bytes:
        """Return one UTF-8 JSON line as bytes, retaining any following partial frame.

        Raises EOFError on disconnection or OSError on other pipe errors; ProtocolError
        for oversized frames (the connection must then close). No image arrays are read.
        """
        while True:
            newline = self._buffer.find(b"\n")
            if newline >= 0:
                if newline + 1 > MAX_LINE_BYTES:
                    raise ProtocolError("Frame exceeds the 64 KiB limit")
                result = bytes(self._buffer[:newline + 1])
                del self._buffer[:newline + 1]
                return result
            if len(self._buffer) >= MAX_LINE_BYTES:
                raise ProtocolError("Frame exceeds the 64 KiB limit")
            try:
                _, chunk = win32file.ReadFile(self.handle, 4096)
            except pywintypes.error as error:
                if error.winerror in {109, 232, 233}:
                    raise EOFError("UI disconnected") from error
                raise OSError(error.winerror, error.strerror) from error
            if not chunk:
                raise EOFError("UI disconnected")
            self._buffer.extend(chunk)

    def send(self, message: dict[str, object]) -> None:
        """Send message (JSON-compatible dict) plus LF; raise OSError for broken pipes."""
        data = encode_message(message)
        while data:
            try:
                _, written = win32file.WriteFile(self.handle, data)
            except pywintypes.error as error:
                raise OSError(error.winerror, error.strerror) from error
            if written <= 0:
                raise OSError("No bytes written to pipe")
            data = data[written:]

    def close(self) -> None:
        """Release this connection's owned handle; safe to call repeatedly."""
        if not self._closed:
            self._closed = True
            self.handle.Close()


class NamedPipeServer:
    """Single-UI server; reconnects are accepted without losing the image registry."""

    def __init__(self, pipe_name: str | None = None) -> None:
        """Configure pipe_name (str or None for per-user default); reject invalid names."""
        name = pipe_name or default_pipe_name()
        if not re.fullmatch(r"[A-Za-z0-9_-]{1,180}", name):
            raise ValueError("Invalid pipe name")
        self.path = "\\\\.\\pipe\\" + name
        self._listener: pywintypes.HANDLE | None = None

    def start(self) -> None:
        """Create the Windows listener; raise OSError if the name is already owned."""
        self._listener = win32pipe.CreateNamedPipe(
            self.path, win32pipe.PIPE_ACCESS_DUPLEX | 0x00080000,
            win32pipe.PIPE_TYPE_BYTE | win32pipe.PIPE_READMODE_BYTE | win32pipe.PIPE_WAIT | 0x8,
            1, MAX_LINE_BYTES, MAX_LINE_BYTES, 0, None)

    def accept_connection(self) -> PipeConnection:
        """Block until a local UI connects; return owning PipeConnection, or raise OSError."""
        if self._listener is None:
            self.start()
        try:
            win32pipe.ConnectNamedPipe(self._listener, None)
        except pywintypes.error as error:
            if error.winerror != 535:
                raise
        connection = PipeConnection(self._listener)
        self._listener = None
        return connection

    def close(self) -> None:
        """Close a pending listener and release resources; connections own their handles."""
        if self._listener is not None:
            self._listener.Close()
            self._listener = None

