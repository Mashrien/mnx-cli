#!/usr/bin/env python3
"""Case-insensitive literal name search, including proof that data chunks are not read."""
from pathlib import Path
import os
import struct
import subprocess
import sys
import tempfile

cli = Path(sys.argv[1]).resolve()
with tempfile.TemporaryDirectory(prefix="mnx-search-") as folder:
    work = Path(folder)
    source = work / "OnlyParent"
    source.mkdir()
    for name, data in [("NeEdLe.TXT", b"one"), ("literal[1].bin", b"two"),
                       ("CAFÉ.txt", b"three"), ("other.bin", b"four")]:
        (source / name).write_bytes(data)
    archive = work / "test.mnx"
    env = dict(os.environ, MNX_SEARCH_TEST_PASSWORD="disposable search test")
    auth = ["--password-env", "MNX_SEARCH_TEST_PASSWORD"]

    def call(*args):
        return subprocess.run([str(cli), *map(str, args), *auth], env=env,
                              capture_output=True, text=True)

    def run(*args):
        result = call(*args)
        assert result.returncode == 0, (result.stdout, result.stderr)
        return result.stdout

    def names(output):
        assert output.startswith("\n")
        return {row[32:52].strip() for row in output.splitlines() if row.startswith(" [")}

    run("create", archive, "--compression", "snappy")
    run("add", archive, source)
    found = run("search", archive, "needle")
    assert names(found) == {"NeEdLe.TXT"}
    assert run("search", archive, "NEEDLE") == found
    assert names(run("search", archive, ".TXT")) == {"NeEdLe.TXT", "CAFÉ.txt"}
    assert names(run("search", archive, "cafÉ")) == {"CAFÉ.txt"}
    assert names(run("search", archive, "onlyparent")) == {"OnlyParent"}, "names, not parent paths"
    assert names(run("search", archive, "[1]")) == {"literal[1].bin"}, "literal substring, not regex"
    assert run("search", archive, "no-such-name") == "\n0 matches.\n"
    with_ids = run("search", archive, "needle", "--ids")
    assert names(with_ids) == {"NeEdLe.TXT"} and with_ids.splitlines()[1].split()[-1].count("-") == 4
    assert call("search", archive, "").returncode != 0
    assert call("search", archive).returncode != 0
    # Damage a data chunk's magic; index-only operations must still work.
    with archive.open("r+b") as stream:
        header = stream.read(64)
        chunk_size = struct.unpack_from("<I", header, 8)[0]
        index_chunks = struct.unpack_from("<I", header, 56)[0]
        offset = (index_chunks + 1) * chunk_size
        stream.seek(offset)
        assert stream.read(4) == b"MNXC"
        stream.seek(offset)
        stream.write(b"!")
    assert call("verify", archive).returncode != 0, "data corruption is detectable"
    assert run("search", archive, "needle") == found, "search does not inspect data chunks"
    print("PASS: always case-insensitive name search, Unicode, literal substrings, folders, no matches, IDs, invalid queries, front-index-only reads")
    print(found, end="")
