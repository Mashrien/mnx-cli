# CLI package ownership

The CLI package owns `mnx` and `mnxi` in the same executable directory, plus its documentation. It requires the separately installed MNXFile engine runtime with application protocol 1 and C ABI 1. On Linux this means the library dependency `libmnxfile.so.1()(64bit)` on a 64-bit RPM target. A package manager must enforce protocol compatibility as well as the library filename.

It must not carry a private engine, codec DLLs, `libmnxfile.so.1`, the shared engine development header, or GUI desktop/MIME integration. Updating the engine package updates archive behavior in both executables when the protocol remains compatible.

NativeAOT removes the need for a separately installed .NET runtime. Remaining system dependencies must be generated from actual binaries and validated on a clean target. The original project code is GPL-3.0-only. Final RPM specs and clean-machine installation validation remain pending. Windows x64 build/runtime checks have been exercised.
