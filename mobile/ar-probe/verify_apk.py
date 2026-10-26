"""Inspect APK integrity, ARM64 libraries, and 16 KB native-library alignment."""
import hashlib
import json
import struct
import sys
import zipfile
from pathlib import Path


def inspect(apk: Path) -> dict:
    libraries = []
    with zipfile.ZipFile(apk) as archive, apk.open("rb") as raw:
        corrupt = archive.testzip()
        if corrupt:
            raise ValueError(f"Corrupt ZIP entry: {corrupt}")
        for entry in archive.infolist():
            if not entry.filename.startswith("lib/") or not entry.filename.endswith(".so"):
                continue
            if not entry.filename.startswith("lib/arm64-v8a/"):
                raise ValueError(f"Unexpected ABI: {entry.filename}")
            data = archive.read(entry)
            if data[:6] != b"\x7fELF\x02\x01" or struct.unpack_from("<H", data, 18)[0] != 183:
                raise ValueError(f"Expected little-endian ARM64 ELF: {entry.filename}")
            offset = struct.unpack_from("<Q", data, 32)[0]
            size, count = struct.unpack_from("<HH", data, 54)
            load_alignments = []
            for index in range(count):
                kind, _, file_offset, address, _, _, _, alignment = struct.unpack_from(
                    "<IIQQQQQQ", data, offset + index * size
                )
                if kind == 1:
                    if alignment < 16384 or (address - file_offset) % 16384 != 0:
                        raise ValueError(f"ELF segment not 16 KB compatible: {entry.filename}")
                    load_alignments.append(alignment)
            if not load_alignments:
                raise ValueError(f"No ELF LOAD segments: {entry.filename}")
            raw.seek(entry.header_offset)
            header = raw.read(30)
            name_length, extra_length = struct.unpack_from("<HH", header, 26)
            zip_offset = entry.header_offset + 30 + name_length + extra_length
            if entry.compress_type == zipfile.ZIP_STORED and zip_offset % 16384 != 0:
                raise ValueError(f"Uncompressed library not ZIP-aligned to 16 KB: {entry.filename}")
            libraries.append({
                "name": entry.filename,
                "loadAlignments": load_alignments,
                "compressed": entry.compress_type != zipfile.ZIP_STORED,
            })
        names = {Path(item["name"]).name for item in libraries}
        required = {"libunity.so", "libil2cpp.so", "libUnityARCore.so"}
        if not required.issubset(names):
            raise ValueError(f"Missing runtime libraries: {sorted(required - names)}")
    with apk.open("rb") as source:
        digest = hashlib.file_digest(source, "sha256").hexdigest()
    return {
        "apk": apk.name,
        "bytes": apk.stat().st_size,
        "sha256": digest,
        "zipIntegrity": "passed",
        "arm64And16KbAlignment": "passed",
        "libraries": libraries,
        "deviceRuntime": "not tested",
    }


if __name__ == "__main__":
    target = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).parent / "Builds/AR-Device-Probe.apk"
    print(json.dumps(inspect(target), indent=2))
