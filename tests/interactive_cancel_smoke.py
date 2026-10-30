#!/usr/bin/env python3
"""Interrupt real add work, then verify and continue in the same interactive process."""
import os
from pathlib import Path
import select
import signal
import subprocess
import sys
import tempfile
import time

mnx = Path(sys.argv[1]).resolve()
mnxi = Path(sys.argv[2]).resolve()
with tempfile.TemporaryDirectory(prefix='mnxi cancellation ') as directory:
    work = Path(directory)
    archive = work / 'cancel.mnx'
    source = work / 'large.bin'
    with source.open('wb') as stream:
        stream.truncate(512 * 1024 * 1024)
    subprocess.run([str(mnx), 'create', str(archive), '--compression', 'brotli', '--level', '9'], check=True, capture_output=True)
    child = subprocess.Popen([str(mnxi), str(archive)], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    try:
        child.stdin.write(f'add "{source}"\n'.encode())
        child.stdin.flush()
        error = bytearray()
        deadline = time.monotonic() + 10
        while b'add:' not in error and time.monotonic() < deadline:
            if select.select([child.stderr], [], [], 0.1)[0]:
                error.extend(os.read(child.stderr.fileno(), 65536))
        assert b'add:' in error, error
        child.send_signal(signal.SIGINT)
        output, remaining_error = child.communicate(b'info\nverify\nquit\n', timeout=20)
        error.extend(remaining_error)
        assert child.returncode == 0, (output, error)
        assert b'Archive:' in output and b'PASS' in output, (output, error)
        assert b'Cancelled' in error or b'cancel' in error.lower(), (output, error)
        result = subprocess.run([str(mnx), 'verify', str(archive)], capture_output=True, timeout=20)
        assert result.returncode == 0, (result.stdout, result.stderr)
        print('PASS: Ctrl-C cancels add work, preserves the archive and accepts subsequent session commands')
    finally:
        if child.poll() is None:
            child.kill()
            child.wait()
