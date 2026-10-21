"""Locate the installed Windows runtime without trusting process environment overrides."""
import ctypes
from pathlib import Path
import sys
import uuid


def _windows_local_app_data():
    # Load only OS DLLs from System32, never from the working directory or PATH.
    ole32 = ctypes.WinDLL("ole32.dll", winmode=0x800)
    shell32 = ctypes.WinDLL("shell32.dll", winmode=0x800)
    ole32.CoInitializeEx.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
    ole32.CoInitializeEx.restype = ctypes.c_int32
    ole32.CoUninitialize.argtypes = []
    ole32.CoUninitialize.restype = None
    ole32.CoTaskMemFree.argtypes = [ctypes.c_void_p]
    ole32.CoTaskMemFree.restype = None
    guid_type = ctypes.c_ubyte * 16
    shell32.SHGetKnownFolderPath.argtypes = [
        ctypes.POINTER(guid_type), ctypes.c_uint32, ctypes.c_void_p,
        ctypes.POINTER(ctypes.c_void_p)]
    shell32.SHGetKnownFolderPath.restype = ctypes.c_int32
    initialized = ole32.CoInitializeEx(None, 2)
    if initialized < 0 and initialized & 0xFFFFFFFF != 0x80010106:  # RPC_E_CHANGED_MODE
        raise OSError(f"COM initialization failed: 0x{initialized & 0xFFFFFFFF:08X}")
    try:
        local_app_data = guid_type.from_buffer_copy(
            uuid.UUID("F1B32785-6FBA-4FCF-9D55-7B8E7F157091").bytes_le)
        result = ctypes.c_void_p()
        try:
            status = shell32.SHGetKnownFolderPath(ctypes.byref(local_app_data), 0, None,
                                                 ctypes.byref(result))
            if status < 0 or not result.value:
                raise OSError(f"Local application-data lookup failed: 0x{status & 0xFFFFFFFF:08X}")
            return Path(ctypes.wstring_at(result.value))
        finally:
            if result.value:
                ole32.CoTaskMemFree(result)
    finally:
        if initialized >= 0:
            ole32.CoUninitialize()


def installed_runtime_root():
    """Return the invoking user's installed runtime; never fall back to environment data."""
    if sys.platform != "win32":
        raise OSError("The installed-runtime diagnostics require Windows.")
    local_app_data = _windows_local_app_data()
    if not local_app_data.is_absolute():
        raise OSError("Windows returned a non-absolute local application-data directory.")
    return local_app_data / "LocalAi"
