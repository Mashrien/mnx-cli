#!/usr/bin/env python3
"""Version and launcher switches work without loading the archive engine."""
import os
from pathlib import Path
import re
import subprocess
import sys

mnx = Path(sys.argv[1]).resolve()
mnxi = Path(sys.argv[2]).resolve()
environment = dict(os.environ, MNXFILE_NATIVE_LIBRARY='/missing/mnx-version-must-not-load-native.so')

def run(executable, *arguments, commands=''):
    return subprocess.run([str(executable), *arguments], input=commands, text=True,
                          capture_output=True, timeout=10, env=environment)

versions = []
for executable, name in ((mnx, 'mnx'), (mnxi, 'mnxi')):
    result = run(executable, '--version')
    assert result.returncode == 0, (result.stdout, result.stderr)
    match = re.fullmatch(name + r' (\d+\.\d+\.\d+-alpha\.\d+)\n', result.stdout)
    assert match, result.stdout
    versions.append(match[1])
assert versions[0] == versions[1], versions
result = run(mnx, '--i', '--version')
assert result.returncode == 0 and result.stdout == f'mnxi {versions[1]}\n', (result.stdout, result.stderr)
result = run(mnx, '--i')
assert result.returncode == 0, (result.stdout, result.stderr)
result = run(mnx, '--i', '--invalid')
assert result.returncode == 1, (result.stdout, result.stderr)
print('PASS: both informational versions, forwarded version/exit status and redirected EOF without native library')
