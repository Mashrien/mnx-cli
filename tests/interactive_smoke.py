#!/usr/bin/env python3
"""Run real mnxi sessions through redirected input, including recovery and Unicode paths."""
import os
from pathlib import Path
import subprocess
import sys
import tempfile

mnx = Path(sys.argv[1]).resolve()
mnxi = Path(sys.argv[2]).resolve()

def invoke(executable, args=(), commands='', environment=None):
    command = ['dotnet', str(executable)] if executable.suffix == '.dll' else [str(executable)]
    return subprocess.run([*command, *map(str, args)], input=commands, text=True,
                          capture_output=True, timeout=45, env=environment)

with tempfile.TemporaryDirectory(prefix='mnxi smoke ') as directory:
    work = Path(directory)
    archive = work / 'archive café 猫.mnx'
    source = work / 'source café 猫'
    source.mkdir()
    literal_source = work / 'literal $(touch MNXI_SHOULD_NOT_EXIST) *.txt'
    literal_source.write_text('shell syntax stays a file name')
    (source / 'hello world.txt').write_text('Unicode café 猫\n', encoding='utf-8')
    top_level = work / 'top level.txt'
    top_level.write_text('a file in root and nested directories')
    output = work / 'extracted result.txt'
    result = invoke(mnxi, commands=f'''help
create "{archive}"
add "{source}" "{top_level}" "{literal_source}"
cd "source café 猫"
add "{top_level}"
pwd
list
search hello
extract "hello world.txt" "{output}"
info
unknown-command
cd missing-directory
list "unterminated
pwd
verify
delete "hello world.txt"
compact
close
pwd
quit
''')
    assert result.returncode == 0, (result.stdout, result.stderr)
    assert output.read_bytes() == (source / 'hello world.txt').read_bytes()
    assert '/source café 猫' in result.stdout, result.stdout
    assert result.stdout.count('Added ') == 2, (result.stdout, result.stderr)
    assert 'hello world.txt' in result.stdout and 'PASS' in result.stdout, result.stdout
    assert 'Unknown command' in result.stderr and 'No archive entry' in result.stderr, result.stderr
    assert 'Unterminated quote' in result.stderr and 'No archive is open' in result.stderr, result.stderr
    # An initial open failure should not consume the following commands or end the session.
    result = invoke(mnxi, [work / 'absent.mnx'], 'help\nquit\n')
    assert result.returncode == 0 and 'Commands:' in result.stdout, (result.stdout, result.stderr)
    # Password values travel through the existing environment option, never command history.
    environment = dict(os.environ, MNXI_TEST_PASSWORD='secret café 猫')
    encrypted = work / 'encrypted.mnx'
    result = invoke(mnxi, commands=f'create "{encrypted}" --password-env MNXI_TEST_PASSWORD\ninfo\nclose\nopen "{encrypted}" --password-env MNXI_TEST_PASSWORD\nverify\nquit\n', environment=environment)
    assert result.returncode == 0 and 'PASS' in result.stdout, (result.stdout, result.stderr)
    assert environment['MNXI_TEST_PASSWORD'] not in result.stdout + result.stderr
    # A password prompt consumes one redirected line then returns to the command loop.
    result = invoke(mnxi, [encrypted], environment['MNXI_TEST_PASSWORD'] + '\ninfo\nquit\n')
    assert result.returncode == 0 and 'Encrypted:     True' in result.stdout, (result.stdout, result.stderr)
    assert environment['MNXI_TEST_PASSWORD'] not in result.stdout + result.stderr
    prompted_create = work / 'prompted encrypted.mnx'
    result = invoke(mnxi, commands=f'create "{prompted_create}" --encrypt\nsecret from input\ninfo\nclose\nopen "{prompted_create}"\nsecret from input\nverify\nquit\n')
    assert result.returncode == 0 and 'PASS' in result.stdout and 'Encrypted:     True' in result.stdout, (result.stdout, result.stderr)
    assert 'secret from input' not in result.stdout + result.stderr
    for alias in ('-i', '-interactive', '--i', '--interactive', '/i'):
        result = invoke(mnx, [alias, archive], 'info\nquit\n')
        assert result.returncode == 0 and str(archive) in result.stdout, (alias, result.stdout, result.stderr)
    result = invoke(mnx)
    assert result.returncode != 0 and 'Usage:' in result.stdout and 'mnxi>' not in result.stdout
    result = invoke(mnxi, ['--invalid'])
    assert result.returncode == 1, (result.stdout, result.stderr)
    result = invoke(mnx, ['--interactive', '--invalid'])
    assert result.returncode == 1, (result.stdout, result.stderr)
    assert not (Path.cwd() / 'MNXI_SHOULD_NOT_EXIST').exists()
    # End of redirected input exits even without a quit command.
    result = invoke(mnxi, commands='help\n')
    assert result.returncode == 0, result.stderr
    print('PASS: persistent interactive commands, Unicode quoting, recovery, passwords, aliases and EOF')
