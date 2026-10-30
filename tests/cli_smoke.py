#!/usr/bin/env python3
"""Exercise the published CLI using disk paths; no .NET APIs or archive internals."""
import os
import re
from pathlib import Path
import subprocess
import sys
import tempfile

cli = Path(sys.argv[1]).resolve()
assert cli.is_file(), cli
with tempfile.TemporaryDirectory(prefix="mnx-native-cli-") as folder:
    work = Path(folder)
    source = work / "source"
    (source / "empty").mkdir(parents=True)
    first = bytes((i * 37 + (i >> 13) * 17) & 255 for i in range(300_000))
    second = bytes((i * 41 + (i >> 11) * 29) & 255 for i in range(700_000))
    (source / "first.bin").write_bytes(first)
    (source / "second.bin").write_bytes(second)
    (source / "file-link").symlink_to(source / "first.bin")
    (source / "directory-link").symlink_to(source / "empty", target_is_directory=True)
    (source / "cycle-link").symlink_to(source, target_is_directory=True)
    (source / "dangling-link").symlink_to(work / "missing-target")
    links = [source / "file-link", source / "directory-link", source / "dangling-link"]
    for codec in ("none", "brotli", "snappy", "lz4"):
        archive = work / f"{codec}.mnx"
        auth = [] if codec == "none" else ["--password-env", "MNX_NATIVE_TEST_PASSWORD"]
        environment = dict(os.environ, MNX_NATIVE_TEST_PASSWORD="CLI integration test passphrase")

        def run(*args):
            result = subprocess.run([str(cli), *map(str, args), *auth], env=environment,
                                    capture_output=True, text=True)
            if result.returncode:
                raise AssertionError(f"{args!r}\n{result.stdout}\n{result.stderr}")
            return result.stdout

        run("create", archive, "--compression", codec, "--level", "1" if codec == "brotli" else "0")
        with archive.open("rb") as stream:
            assert stream.read(4) == b"MNX7", "new archive magic"
        run("add", archive, *links, source)
        listing = run("list", archive, "source")
        assert listing.startswith("\n/source\n"), listing
        rows = listing.splitlines()[2:-1]
        pattern = re.compile(r"^ \[([DF])\]   (.*?)   (.{4})  ( B|KB|MB|GB|TB)  \[([C ][E ] )\]  \(( *\d+)\)  (\d{2}-\d{2}-\d{4})$")
        parsed = [pattern.fullmatch(row) for row in rows]
        assert len(rows) == 3 and all(parsed), listing
        assert len({row.index("(") for row in rows}) == 1, "byte columns align"
        by_name = {match[2].lstrip(): match for match in parsed}
        assert by_name["empty"][1] == "D"
        assert by_name["first.bin"][1] == "F"
        assert by_name["second.bin"][6].lstrip() == "700000"
        assert by_name["first.bin"][6].lstrip() == "300000"
        assert len(by_name["second.bin"][6]) == 14
        assert by_name["second.bin"][5] == ("   " if codec == "none" else "CE ")
        assert by_name["empty"][5] == ("   " if codec == "none" else " E ")
        with_ids = run("list", archive, "source", "--ids")
        assert re.search(r"\d{2}-\d{2}-\d{4}  [0-9a-f-]{36}$", with_ids, re.MULTILINE)
        assert "32,768" in run("info", archive)
        run("compare", archive, *links, source)
        run("verify", archive)
        partial = work / f"{codec}-range.bin"
        run("range", archive, "source/second.bin", "4090", "5000", partial)
        assert partial.read_bytes() == second[4090:9090]
        extracted = work / f"{codec}-extracted"
        run("extract", archive, "source", extracted)
        assert (extracted / "first.bin").read_bytes() == first
        assert (extracted / "second.bin").read_bytes() == second
        assert (extracted / "empty").is_dir()
        before = archive.stat().st_size
        run("delete", archive, "source/first.bin")
        run("compact", archive)
        assert archive.stat().st_size < before
        run("verify", archive)
        surviving = work / f"{codec}-surviving.bin"
        run("extract", archive, "source/second.bin", surviving)
        assert surviving.read_bytes() == second
        print(f"PASS: native CLI {codec}: add, list, info, compare, verify, range, extract, delete, compact")
