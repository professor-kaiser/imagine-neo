"""Open existing Windows section objects directly: no multiprocessing headers."""

import ctypes
from ctypes import wintypes
from contextlib import contextmanager
from collections.abc import Iterator
import os
import numpy as np
from app.models.image_types import ImageArray
from app.ipc.protocol import validate_dimensions


@contextmanager
def _mapped_view(mapping_name: str, size: int, write: bool = False) -> Iterator[int]:
    """Yield an existing mapping address for size bytes; always unmap and close handles.

    Args:
        mapping_name (str): Exact Windows section object name.
        size (int): Positive byte extent to map.
        write (bool): Request writable access instead of read-only access.
    Yields:
        int: Native address valid only inside this context.
    Raises:
        OSError: Missing mapping, access denied, or mapping shorter than requested pages.
        RuntimeError: Host is not Windows.
    """
    if os.name != "nt":
        raise RuntimeError("Windows shared memory is required")
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.OpenFileMappingW.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.LPCWSTR]
    kernel.OpenFileMappingW.restype = wintypes.HANDLE
    kernel.MapViewOfFile.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.DWORD, wintypes.DWORD, ctypes.c_size_t]
    kernel.MapViewOfFile.restype = ctypes.c_void_p
    kernel.UnmapViewOfFile.argtypes = [ctypes.c_void_p]
    kernel.UnmapViewOfFile.restype = wintypes.BOOL
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel.CloseHandle.restype = wintypes.BOOL
    access = 0x0002 if write else 0x0004
    handle = kernel.OpenFileMappingW(access, False, mapping_name)
    if not handle:
        raise ctypes.WinError(ctypes.get_last_error())
    address = None
    try:
        address = kernel.MapViewOfFile(handle, access, 0, 0, size)
        if not address:
            raise ctypes.WinError(ctypes.get_last_error())
        yield address
    finally:
        if address:
            kernel.UnmapViewOfFile(address)
        kernel.CloseHandle(handle)


def read_shared_image(mapping_name: str, width: int, height: int, channels: int,
                      row_stride_bytes: int, buffer_size_bytes: int) -> ImageArray:
    """Read and copy canonical BGRA8 pixels from an existing named Windows mapping.

    Args:
        mapping_name (str): Section name created and kept alive by C# until acknowledgement.
        width (int): Positive image width in pixels.
        height (int): Positive image height in pixels.
        channels (int): Must equal 4.
        row_stride_bytes (int): Must equal width * 4.
        buffer_size_bytes (int): Must equal height * width * 4, at most 512 MiB.
    Returns:
        ImageArray: Owned contiguous uint8 BGRA8 array of shape (height, width, 4),
        independent of mapping lifetime; straight alpha is preserved.
    Raises:
        ValueError: Invalid dimensions, stride or buffer size.
        OSError: Mapping is missing, inaccessible or too small.
    """
    validate_dimensions(width, height, channels, row_stride_bytes, buffer_size_bytes)
    with _mapped_view(mapping_name, buffer_size_bytes) as address:
        view = np.ctypeslib.as_array((ctypes.c_ubyte * buffer_size_bytes).from_address(address))
        return view.reshape(height, width, 4).copy()


def write_shared_image(mapping_name: str, image: ImageArray) -> None:
    """Write BGRA8 bytes into an existing mapping owned by the receiving process.

    Args:
        mapping_name (str): Existing writable Windows section name; this function never creates it.
        image (ImageArray): Contiguous uint8 BGRA8 array, shape (height, width, 4), straight alpha.
    Returns:
        None. Mapping ownership remains with the caller.
    Raises:
        ValueError: Image dtype, shape, contiguity or dimensions are invalid.
        OSError: Mapping is missing, inaccessible or too small.
    """
    if image.dtype != np.uint8 or image.ndim != 3 or image.shape[2] != 4 or not image.flags.c_contiguous:
        raise ValueError("Expected contiguous uint8 BGRA8 (height, width, 4)")
    height, width, channels = image.shape
    validate_dimensions(width, height, channels, width * 4, image.nbytes)
    with _mapped_view(mapping_name, image.nbytes, write=True) as address:
        ctypes.memmove(address, image.ctypes.data, image.nbytes)

