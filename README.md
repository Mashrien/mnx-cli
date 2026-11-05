# mnx-cli

**Terminal tools for large network archives and NAS backups.**

MNX was created for archives/backups that can reach hundreds of gigabytes or
terabytes on network storage. Its front index lets these tools browse/search without
scanning payloads; bounded-memory streaming and chunk extents keep large transfers
practical without loading an archive into RAM.

| Program | Use |
| --- | --- |
| `mnx` | Individual commands for a terminal, scripts and file-manager actions |
| `mnxi` | Interactive archive session with prompts, history and completion |

Both call the separately installed **[MNXFile](https://github.com/Mashrien/MNXFile)**
native engine. Archive code and codecs belong to that project. For the desktop GUI,
see **[MNXArchiver](https://github.com/Mashrien/MNXArchiver)**.

## Downloads and installation

Latest source changes: [portable Linux library lookup](docs/releases/1.2.0-alpha.5.md).

[Releases](https://github.com/Mashrien/mnx-cli/releases) contain **mnx-cli** RPM/DEB,
a portable Linux tar and a Windows x64 ZIP. Every package contains both `mnx` and
`mnxi`. Install the [MNXFile engine](https://github.com/Mashrien/MNXFile/releases)
first; applications do not bundle it. Linux requires glibc2.39+ and OpenSSL3.
Windows10 x64 and later use the engine ZIP's per-user installer. No installed .NET
runtime is needed. [Packaging instructions](packaging/README.md) cover release builds.

## Quick use

```sh
mnx create backup.mnx --compression snappy
mnx add backup.mnx ./Documents ./Pictures
mnx list backup.mnx
mnx list backup.mnx /Documents -r
mnx search backup.mnx report
mnx verify backup.mnx
mnx extract backup.mnx /Documents ./restored
mnx compact backup.mnx
```

`list` shows root entries by default; an archive folder path selects another folder,
and `-r` includes descendants. Search is a case-insensitive substring match on file
and folder **names**. `mnx help` lists options and commands, including range reads,
delete, metadata and queue resume.

For encryption, create with `--password-env VARIABLE` and supply the same option
when reopening the archive. Avoid literal passwords in command-line arguments.
Compression choices are `none`, `brotli`, `lz4` and `snappy`; Brotli uses `--level 1..9`.
Compression/encryption/chunk-layout choices are fixed at creation.

```sh
mnxi
mnxi backup.mnx
mnx --interactive backup.mnx
```

Interactive aliases are `-i`, `-interactive`, `--i`, `--interactive` and `/i`.
Keep `mnx` and `mnxi` together so forwarding can locate the interactive executable.
Type `help` inside a session. Terminal progress updates overwrite a line; redirected
output uses readable log lines. Ctrl+C during supported work requests cancellation.
Saved queues resume with `mnx resume backup.mnq [ARCHIVE]` using the archive's existing
compression/encryption. See [MNXFile's queue format](https://github.com/Mashrien/MNXFile/blob/master/MNQ_FORMAT.md).

## Build from source

Install the **.NET 10 SDK**; native builds also need the platform's Native AOT tools
(`clang` and development headers on Linux; Visual Studio C++ tools/Windows SDK on
Windows). No installed .NET runtime is required to run the published executables.

The projects pin **`Mash.MNXFile.Client` version `1.2.0-alpha.3`**. It is not yet on
NuGet.org. Build that package from MNXFile first, then build this repository:

```sh
git clone https://github.com/Mashrien/MNXFile.git
git clone https://github.com/Mashrien/mnx-cli.git
(cd MNXFile && MNXBuildNumber=3 ./build.sh --test)
cd mnx-cli
export MNXFILE_NATIVE_LIBRARY="$(realpath ../MNX_Builds/mnxfile/linux-x64/libmnxfile.so.1)"
./build.sh --test
```

Use `MNXPackageFeed=/absolute/path/to/feed ./build.sh --test` for another package
feed. The default is `../MNX_Builds/mnxfile/packages`. No engine source checkout is
needed after obtaining the client package. For IDE-only builds, add that feed as a
NuGet source. Open `MNX-CLI.slnx`.

On Windows, build the engine/client dependency first, then run:

```powershell
$env:MNXFILE_NATIVE_LIBRARY = (Resolve-Path "..\MNX_Builds\mnxfile\win-x64\MNXFile.Native.dll").Path
.\build.cmd -Clean -Test -Runtime win-x64
```

`MNXPackageFeed` selects the dependency feed in PowerShell too. Each build reserves
its own counter; current CLI version is `1.2.0-alpha.N`. An explicit `MNXBuildNumber`
reuses that number without advancing the counter.

## Runtime and output

Install the matching MNXFile native runtime once: `libmnxfile.so.1` on Linux or
`MNXFile.Native.dll` on Windows. A development override is
`MNXFILE_NATIVE_LIBRARY=/absolute/path/to/library`. `--help` and `--version` work
without it; archive operations need a compatible ABI1/protocol1 engine.
Neither executable embeds a private copy of the archive engine or its codecs.

Releases go to `../MNX_Builds/mnx-cli/linux-x64/` or `win-x64/` and also stage Linux
executables in `../MNX_Builds/binaries-final/`. Intermediates stay under
`../mnx_clutter/build/mnx-cli/`. `MNXBuildRoot`, `MNXReleaseRoot` and `MNXFinalRoot`
override those paths. Building does not install anything. [Packaging scripts](packaging/README.md) create
`mnx-cli` RPM/DEB and portable archives, containing both executables.

## Source map and checks

- `src/`: argument-based tool, formatting and progress.
- `interactive/`: interactive shell and line editor.
- `tests/`: archive workflows, listing/search, forwarding/version, terminal behavior
  and cancellation/resume.
- `build.sh`, `build.ps1`, `build.cmd`: independent native build entry points.
- [packaging/OWNERSHIP.md](packaging/OWNERSHIP.md): package/runtime boundaries.

Linux x64 is the primary test target; Windows x64 builds are supported and have been
tested. Native AOT is built on the target OS; Linux cannot cross-build Windows AOT.

## License

Copyright Mashrien. This project's original code is licensed under
[GNU GPL version 3](LICENSE) (`GPL-3.0-only`). Bundled and downloaded dependencies
retain their own copyright and license notices.
