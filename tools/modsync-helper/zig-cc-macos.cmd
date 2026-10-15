@echo off
rem Linker for the macOS build, used only by build.ps1 (which sets the variables below). It is cargo-zigbuild's own
rem "zig cc" - so rustc's arguments are filtered the way Zig needs - with one difference: cargo-zigbuild's generated
rem wrapper targets Zig's default macOS version (13.0), and this targets 10.13, the oldest macOS Unity 2022 runs on.
"%VE_CARGO_ZIGBUILD%" zig cc -- -g -fno-sanitize=all -target x86_64-macos.10.13-none %*
