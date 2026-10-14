"""Generate an exact APK delta by reusing unchanged compressed ZIP entries.

python scripts/New-AndroidDelta.py --base old.apk --target new.apk --from-code 1201
    --version 0.12.2 --version-code 1202 --output release --url https://host/releases
The full APK is always retained. Deltas >=60% of the APK are omitted.
"""
import argparse
import gzip
import hashlib
import json
from pathlib import Path
import shutil
import struct
import zipfile


def digest(data):
    return hashlib.sha256(data).hexdigest()


def regions(path, data):
    with zipfile.ZipFile(path) as archive:
        for entry in archive.infolist():
            start = entry.header_offset
            if data[start:start + 4] != b'PK\x03\x04':
                raise ValueError('Invalid local ZIP header')
            names, extras = struct.unpack_from('<HH', data, start + 26)
            start += 30 + names + extras
            if entry.compress_size:
                yield start, entry.compress_size


def generate(base, target, output):
    old, new = Path(base).read_bytes(), Path(target).read_bytes()
    lookup = {(size, digest(old[start:start + size])): start for start, size in regions(base, old)}
    # Fixed source blocks can move in the target (ZIP offsets and assembly tables change between builds).
    # Search within corresponding stored entries; keep exact raw bytes, including the signed APK layout.
    copies = []
    with zipfile.ZipFile(base) as old_zip, zipfile.ZipFile(target) as new_zip:
        old_entries = {entry.filename: (entry, region) for entry, region in zip(old_zip.infolist(),
            [(x.header_offset + 30 + sum(struct.unpack_from('<HH', old, x.header_offset + 26)), x.compress_size) for x in old_zip.infolist()])}
        for entry in new_zip.infolist():
            if entry.compress_type != zipfile.ZIP_STORED or entry.filename not in old_entries:
                continue
            previous, (old_start, old_size) = old_entries[entry.filename]
            if previous.compress_type != zipfile.ZIP_STORED:
                continue
            new_start = entry.header_offset + 30 + sum(struct.unpack_from('<HH', new, entry.header_offset + 26))
            body = new[new_start:new_start + entry.compress_size]
            for offset in range(old_start, old_start + old_size, 65536):
                block = old[offset:min(offset + 65536, old_start + old_size)]
                if len(block) < 64:
                    continue
                found = body.find(block)
                if found >= 0:
                    copies.append((new_start + found, len(block), offset))
    for start, size in regions(target, new):
        offset = lookup.get((size, digest(new[start:start + size])))
        if offset is not None and size >= 64:
            copies.append((start, size, offset))
    with gzip.GzipFile(filename=str(output), mode='wb', compresslevel=9, mtime=0) as patch:
        patch.write(b'FAD1')
        cursor = 0

        def literal(end):
            nonlocal cursor
            if end > cursor:
                patch.write(struct.pack('<Bi', 2, end - cursor))
                patch.write(new[cursor:end])
                cursor = end

        for start, size, offset in sorted(copies, key=lambda c: (c[0], -c[1])):
            if start < cursor:
                continue
            literal(start)
            patch.write(struct.pack('<Bqi', 1, offset, size))
            cursor = start + size
        literal(len(new))
        patch.write(b'\0')
    # Release only a patch that reconstructs the exact signed APK, including ZIP metadata and signature blocks.
    reconstructed = bytearray()
    with gzip.open(output, 'rb') as patch:
        assert patch.read(4) == b'FAD1'
        while True:
            op = patch.read(1)
            if op == b'\0':
                break
            if op == b'\1':
                offset, length = struct.unpack('<qi', patch.read(12))
                reconstructed.extend(old[offset:offset + length])
            elif op == b'\2':
                length, = struct.unpack('<i', patch.read(4))
                reconstructed.extend(patch.read(length))
            else:
                raise ValueError('Invalid generated patch')
        if patch.read(1) or reconstructed != new:
            raise ValueError('Patch reconstruction differs from signed APK')
    return old, new


def main():
    parser = argparse.ArgumentParser()
    for name in ('base', 'target', 'version', 'output', 'url'):
        parser.add_argument('--' + name, required=True)
    parser.add_argument('--from-code', required=True, type=int)
    parser.add_argument('--version-code', required=True, type=int)
    parser.add_argument('--notes', default='')
    args = parser.parse_args()
    output = Path(args.output)
    output.mkdir(parents=True, exist_ok=True)
    name = f'FurtherAgent-Android-{args.version}.apk'
    patch = output / f'FurtherAgent-{args.from_code}-to-{args.version_code}.fad'
    old, new = generate(args.base, args.target, patch)
    if Path(args.target).resolve() != (output / name).resolve():
        shutil.copyfile(args.target, output / name)
    url = args.url.rstrip('/')
    manifest = dict(version=args.version, versionCode=args.version_code, url=f'{url}/{name}',
                    sha256=digest(new), size=len(new), notes=args.notes, deltas=[])
    if patch.stat().st_size < len(new) * .6:
        manifest['deltas'].append(dict(algorithm='fad1', fromVersionCode=args.from_code, baseSha256=digest(old),
                                      url=f'{url}/{patch.name}', sha256=digest(patch.read_bytes()), size=patch.stat().st_size))
    else:
        patch.unlink()
    (output / 'latest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps(manifest, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
