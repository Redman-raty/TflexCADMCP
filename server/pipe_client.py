"""Length-prefixed local connection to the CAD plugin."""

from __future__ import annotations

import _winapi as win
import ctypes
from ctypes import wintypes
import json
import os
import re
import struct
import time

REQUEST_LIMIT = 1024 * 1024
RESPONSE_LIMIT = 64 * 1024 * 1024
SECURITY_IDENTIFICATION = 0x00110000  # SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION


class BridgeError(Exception):
    def __init__(self, code: str, message: str, details=None):
        super().__init__(message)
        self.payload = {"code": code, "message": message, "details": details}


def pipe_name() -> str:
    if name := os.environ.get("TFLEX_MCP_PIPE_NAME"):
        if not re.fullmatch(r"[A-Za-z0-9._-]{1,128}", name):
            raise ValueError("TFLEX_MCP_PIPE_NAME must be a simple local pipe name.")
        return rf"\\.\pipe\{name}"
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    function = kernel32.ProcessIdToSessionId
    function.argtypes = (wintypes.DWORD, ctypes.POINTER(wintypes.DWORD))
    function.restype = wintypes.BOOL
    session = wintypes.DWORD()
    if not function(os.getpid(), ctypes.byref(session)):
        raise ctypes.WinError(ctypes.get_last_error())
    prefix = "TFlexMcp.Edu" if os.environ.get("TFLEX_MCP_MODE") == "education" else "TFlexMcp"
    return rf"\\.\pipe\{prefix}.v1.{session.value}"


def connect() -> int:
    name = pipe_name()
    deadline = time.monotonic() + 3
    while True:
        try:
            return win.CreateFile(name, win.GENERIC_READ | win.GENERIC_WRITE, 0, win.NULL, win.OPEN_EXISTING,
                                  win.FILE_FLAG_OVERLAPPED | SECURITY_IDENTIFICATION, win.NULL)
        except OSError as exc:
            if exc.winerror not in (2, win.ERROR_PIPE_BUSY):
                raise
            if time.monotonic() >= deadline:
                if exc.winerror == win.ERROR_PIPE_BUSY:
                    raise BridgeError("cad_busy", "Another request is still running in T-FLEX CAD.") from exc
                instruction = ("Open the bridge GRS and run the Start macro in T-FLEX CAD."
                               if os.environ.get("TFLEX_MCP_MODE") == "education"
                               else "Open T-FLEX CAD and start T-FLEX MCP Bridge in this Windows session.")
                raise BridgeError("cad_unavailable", instruction) from exc
            time.sleep(0.05)


def transferred(overlapped, status: int, deadline: float) -> int:
    try:
        if status == win.ERROR_IO_PENDING:
            milliseconds = max(0, int((deadline - time.monotonic()) * 1000))
            if win.WaitForSingleObject(overlapped.event, milliseconds) == win.WAIT_TIMEOUT:
                raise BridgeError("cad_timeout", "CAD response timed out. Re-read the model before retrying a mutation.")
        count, error = overlapped.GetOverlappedResult(False)
        if error:
            raise ctypes.WinError(error)
        return count
    except BaseException:
        overlapped.cancel()
        overlapped.GetOverlappedResult(True)
        raise


def read_exact(handle: int, size: int, deadline: float) -> bytes:
    data = bytearray()
    while len(data) < size:
        operation, status = win.ReadFile(handle, min(size - len(data), 65536), overlapped=True)
        count = transferred(operation, status, deadline)
        if count == 0:
            raise BridgeError("connection_closed", "CAD disconnected before completing its reply.")
        data.extend(operation.getbuffer()[:count])
    return bytes(data)


def call(method: str, **arguments) -> dict:
    body = json.dumps({"method": method, **arguments}, ensure_ascii=False, allow_nan=False).encode("utf-8")
    if len(body) > REQUEST_LIMIT:
        raise BridgeError("request_too_large", "CAD request exceeds 1 MiB.")
    timeout = int(os.environ.get("TFLEX_MCP_TIMEOUT", "180"))
    if not 1 <= timeout <= 3600:
        raise ValueError("TFLEX_MCP_TIMEOUT must be between 1 and 3600 seconds.")
    handle = connect()
    try:
        deadline = time.monotonic() + timeout
        frame = struct.pack("<I", len(body)) + body
        offset = 0
        while offset < len(frame):
            operation, status = win.WriteFile(handle, frame[offset:offset + 65536], overlapped=True)
            count = transferred(operation, status, deadline)
            if not count:
                raise BridgeError("connection_closed", "CAD disconnected while receiving its request.")
            offset += count
        length, = struct.unpack("<I", read_exact(handle, 4, deadline))
        if not 0 < length <= RESPONSE_LIMIT:
            raise BridgeError("invalid_reply", "Invalid CAD reply length.")
        payload = read_exact(handle, length, deadline)
        operation, status = win.WriteFile(handle, b"\x01", overlapped=True)
        transferred(operation, status, deadline)
        response = json.loads(payload)
    finally:
        win.CloseHandle(handle)
    if not response["ok"]:
        error = response["error"]
        raise BridgeError(error["code"], error["message"], error.get("details"))
    return response["result"]
