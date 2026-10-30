#!/usr/bin/env python3
"""Exercise actual terminal editing, session history, quoted completion and Ctrl-C."""
import os
from pathlib import Path
import pty
import select
import signal
import subprocess
import sys
import tempfile
import time

mnx = Path(sys.argv[1]).resolve()
mnxi = Path(sys.argv[2]).resolve()
with tempfile.TemporaryDirectory(prefix='mnxi terminal ') as directory:
    work = Path(directory)
    archive = work / 'terminal café.mnx'
    source = work / 'source café 猫'
    source.mkdir()
    (source / 'hello.txt').write_text('hello')
    subprocess.run([str(mnx), 'create', str(archive)], check=True, capture_output=True)
    subprocess.run([str(mnx), 'add', str(archive), str(source)], check=True, capture_output=True)
    master, slave = pty.openpty()
    environment = dict(os.environ, TERM='xterm-256color')
    child = subprocess.Popen([str(mnxi), str(archive)], stdin=slave, stdout=slave, stderr=slave,
                             env=environment, start_new_session=True)
    os.close(slave)
    transcript = bytearray()
    pending = bytearray()

    def send(text):
        os.write(master, text.encode('utf-8'))

    def until(marker, timeout=8):
        wanted = marker.encode('utf-8')
        output = pending
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if wanted in output:
                end = output.index(wanted) + len(wanted)
                result = bytes(output[:end])
                del output[:end]
                return result
            if select.select([master], [], [], 0.1)[0]:
                data = os.read(master, 65536)
                transcript.extend(data)
                output.extend(data)
            if child.poll() is not None:
                break
        raise AssertionError((marker, output.decode('utf-8', errors='replace')))

    try:
        until('mnxi:/> ')
        send('he\t\n')
        until('Commands:')
        until('Ctrl-C cancels current work;')
        until('mnxi:/> ')
        assert b'Commands:' in transcript
        before = transcript.count(b'Commands:')
        send('\x1b[A\n')
        until('Commands:')
        until('Ctrl-C cancels current work;')
        until('mnxi:/> ')
        assert transcript.count(b'Commands:') == before + 1, 'Up recalls help'
        # Home, Right and Delete replace the typo in hylp.
        send('hylp\x1b[H\x1b[C\x1b[3~e\n')
        until('Commands:')
        until('Ctrl-C cancels current work;')
        until('mnxi:/> ')
        assert transcript.count(b'Commands:') == before + 2, 'editing forms help'
        send('cd "/sou\t\n')
        until('mnxi:/source café 猫> ')
        # Ctrl-C clears unfinished input and leaves the session alive.
        send('this command should be cancelled\x03')
        until('mnxi:/source café 猫> ')
        send('pwd\n')
        until('mnxi:/source café 猫> ')
        encrypted = work / 'hidden password.mnx'
        send(f'create "{encrypted}" --encrypt\n')
        until('New passphrase: ')
        send('hidden terminal secret 猫\n')
        until('Created ')
        until('mnxi:/> ')
        send('close\n')
        until('Archive closed.')
        until('mnxi> ')
        send(f'open "{encrypted}"\n')
        until('Passphrase: ')
        send('hidden terminal secret 猫\n')
        until('Opened ')
        until('mnxi:/> ')
        assert 'hidden terminal secret 猫'.encode() not in transcript, 'password entry must stay hidden'
        send('quit\n')
        assert child.wait(timeout=5) == 0
        assert b'Unknown command' not in transcript, transcript.decode('utf-8', errors='replace')
    finally:
        if child.poll() is None:
            child.kill()
            child.wait()
        os.close(master)
    print('PASS: terminal history, cursor editing, quoted Unicode completion Ctrl-C prompt recovery and hidden passwords')
