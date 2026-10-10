"""Named-pipe frame codec and blocking client.

Shared by the helper pipe (`okww.rdphost.*`, the .NET RdpHost.exe is the server)
and the root<->child instance IPC pipe. Frame layout matches BetterGenshinImpact's
`InstanceIpcProtocol`: uint32 payloadLength (LE, max 1 MiB) + byte payloadType + payload.
"""

import struct
import sys
import time

from ok import Logger

logger = Logger.get_logger(__name__)

PAYLOAD_JSON = 1
PAYLOAD_RELATIVE_MOUSE_BATCH = 2
PAYLOAD_RELATIVE_MOUSE_RESULT = 3

MAX_PAYLOAD_LENGTH = 1024 * 1024
_HEADER = struct.Struct("<IB")


def encode_frame(payload_type: int, payload: bytes = b"") -> bytes:
    if len(payload) > MAX_PAYLOAD_LENGTH:
        raise ValueError(f"命名管道消息超过 {MAX_PAYLOAD_LENGTH} 字节限制。")
    return _HEADER.pack(len(payload), payload_type) + payload


def parse_header(header: bytes):
    length, payload_type = _HEADER.unpack(header)
    if length > MAX_PAYLOAD_LENGTH:
        raise ValueError(f"命名管道消息超过 {MAX_PAYLOAD_LENGTH} 字节限制。")
    return length, payload_type


class PipeClient:
    """Blocking named-pipe client (pywin32). One reader thread is expected."""

    def __init__(self, name: str):
        if not name.startswith("\\\\.\\pipe\\"):
            name = "\\\\.\\pipe\\" + name
        self._path = name
        self._handle = None

    @property
    def connected(self) -> bool:
        return self._handle is not None

    def connect(self, timeout: float = 30.0):
        import pywintypes
        import win32file

        deadline = time.time() + timeout
        while True:
            try:
                self._handle = win32file.CreateFile(
                    self._path,
                    win32file.GENERIC_READ | win32file.GENERIC_WRITE,
                    0, None, win32file.OPEN_EXISTING, 0, None)
                return
            except pywintypes.error:
                if time.time() >= deadline:
                    raise
                time.sleep(0.2)

    def write_frame(self, payload_type: int, payload: bytes = b""):
        import win32file

        if self._handle is None:
            raise ConnectionError("命名管道尚未连接。")
        win32file.WriteFile(self._handle, encode_frame(payload_type, payload))

    def read_frame(self):
        """Return (payload_type, payload), or None on clean EOF."""
        header = self._read_exact(_HEADER.size)
        if header is None:
            return None
        length, payload_type = parse_header(header)
        payload = b""
        if length:
            payload = self._read_exact(length)
            if payload is None:
                return None
        return payload_type, payload

    def _read_exact(self, size: int):
        import win32file

        buffer = bytearray()
        while len(buffer) < size:
            try:
                _, data = win32file.ReadFile(self._handle, size - len(buffer))
            except Exception:
                if not buffer:
                    return None
                raise
            if not data:
                return None if not buffer else bytes(buffer)
            buffer.extend(data)
        return bytes(buffer)

    def close(self):
        if self._handle is not None:
            try:
                import win32file

                win32file.CloseHandle(self._handle)
            except Exception:
                pass
            self._handle = None


if sys.platform != "win32":  # pragma: no cover
    def _unsupported(*_args, **_kwargs):
        raise NotImplementedError("命名管道仅在 Windows 上可用。")
