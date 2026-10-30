#!/usr/bin/env python3
"""Check directory selection and recursive listing through the published CLI."""
from pathlib import Path
import os
import re
import subprocess
import sys
import tempfile

cli = Path(sys.argv[1]).resolve()
with tempfile.TemporaryDirectory(prefix="mnx-list-") as folder:
    work = Path(folder)
    source = work / "source"
    (source / "alpha/beta/gamma").mkdir(parents=True)
    (source / "empty").mkdir()
    (source / "file.bin").write_bytes(bytes(range(256)) * 6)
    (source / "alpha/beta/gamma/same.bin").write_bytes(b"deep file")
    sibling = work / "source-plus"
    sibling.mkdir()
    (sibling / "other.bin").write_bytes(b"sibling")
    loose = work / "loose.bin"
    loose.write_bytes(b"")
    archive = work / "test.mnx"
    env = dict(os.environ, MNX_LIST_TEST_PASSWORD="disposable listing test")
    auth = ["--password-env", "MNX_LIST_TEST_PASSWORD"]

    def run(*args):
        return subprocess.run([str(cli), *map(str, args), *auth], env=env,
                              capture_output=True, text=True, check=True).stdout

    def rows(output):
        assert output.startswith("\n"), "initial blank line"
        return [line for line in output.splitlines() if line.startswith(" [")]

    def names(output, recursive=False):
        start = 32 if recursive else 7
        return {line[start:start + 20].strip() for line in rows(output)}

    run("create", archive, "--compression", "snappy")
    run("add", archive, source, sibling, loose)
    plain = run("list", archive)
    assert names(plain) == {"source", "source-plus", "loose.bin"}
    assert plain.splitlines()[1].startswith(" ["), "no heading without PATH"
    selected = run("list", archive, "source")
    assert selected.splitlines()[1] == "/source"
    assert names(selected) == {"alpha", "empty", "file.bin"}
    assert run("list", archive, "source/") == selected
    empty = run("list", archive, "source/empty")
    assert empty == "\n/source/empty\n0 entries.\n", empty
    root = run("list", archive, "/")
    assert root.splitlines()[1] == "/" and names(root) == names(plain)
    recursive = run("list", archive, "-r", "source")
    assert recursive.splitlines()[1] == "/source"
    assert names(recursive, True) == {"alpha", "beta", "gamma", "same.bin", "empty", "file.bin"}
    for row in rows(recursive):
        parent = row[7:29]
        assert len(parent) == 22 and parent == parent.strip().rjust(22)
        assert row[29:32] == "   "
        assert "  [" in row and "]  (" in row, "CES separators retained"
    deep = next(row for row in rows(recursive) if row[32:52].strip() == "same.bin")
    assert deep[7:29].strip() == "/source/.../gamma"
    assert "[CE ]" in deep
    recursive_root = run("list", "-r", archive)
    assert len(rows(recursive_root)) == 10
    assert run("list", archive, "source", "--recursive") == recursive
    with_ids = run("list", archive, "--ids")
    source_line = next(row for row in rows(with_ids) if row[7:27].strip() == "source")
    source_id = source_line.split()[-1]
    assert re.fullmatch(r"[0-9a-f-]{36}", source_id)
    assert run("list", archive, source_id) == selected
    for args in [("list", archive, "source/file.bin"), ("list", archive, "missing"),
                 ("list", archive, "-r", "--recursive")]:
        failure = subprocess.run([str(cli), *map(str, args), *auth], env=env,
                                 capture_output=True, text=True)
        assert failure.returncode != 0, args
    print("PASS: blank line, root-only, folder heading/filter, empty folder, recursive subtree, fixed 22-character paths, filenames, IDs, option errors")
    print(recursive, end="")
