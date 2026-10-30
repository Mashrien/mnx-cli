# MNX-CLI: start here

Open `MNX-CLI.slnx` for this component only. Read [README.md](README.md) for build and runtime setup.

Run `./build.sh` on Linux or `build.cmd` (or `./build.ps1`) on Windows. Output is `../MNX_Builds/mnx-cli`; intermediates are `../mnx_clutter/build/mnx-cli`. Each repository owns its build counter. The applications pin client `1.2.0-alpha.3`; build that dependency from MNXFile with `MNXBuildNumber=3`. An explicit build number does not advance the counter.

The MNXFile shared engine is installed once. MNX-CLI and MNX-GUI consume its thin client NuGet package and require the installed runtime for archive operations. Their source projects do not reference the engine source.
