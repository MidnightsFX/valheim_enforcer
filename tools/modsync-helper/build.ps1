# Builds the mod sync helper for Windows, Linux and macOS and copies all three into
# ValheimEnforcer/assets/modsync, where the mod embeds them. Run it after changing anything under src/, then
# rebuild the mod.
#
# Needs the GNU Rust toolchain, which brings its own linker - no Visual Studio required - plus the musl target for
# the static Linux build and the macOS target:
#   rustup toolchain install stable-x86_64-pc-windows-gnu --profile minimal
#   rustup target add x86_64-unknown-linux-musl x86_64-apple-darwin --toolchain stable-x86_64-pc-windows-gnu
# and, for the macOS link, Zig and cargo-zigbuild (both are on PyPI):
#   python -m pip install --user ziglang cargo-zigbuild
#
# The macOS binary cannot be run here; the "modsync helper" GitHub workflow runs the tests and an end to end
# check on a Mac.

# Exit codes are checked by hand: cargo reports progress on stderr, which Windows PowerShell would otherwise
# treat as a failure.
$toolchain = '+stable-x86_64-pc-windows-gnu'
$here = $PSScriptRoot
$assets = Join-Path $here '..\..\ValheimEnforcer\assets\modsync'

Push-Location $here
try {
    cargo $toolchain test --target x86_64-pc-windows-gnu
    if ($LASTEXITCODE -ne 0) { throw 'tests failed' }
    cargo $toolchain build --release --target x86_64-pc-windows-gnu
    if ($LASTEXITCODE -ne 0) { throw 'Windows build failed' }
    cargo $toolchain build --release --target x86_64-unknown-linux-musl
    if ($LASTEXITCODE -ne 0) { throw 'Linux build failed' }

    # macOS, linked by Zig, which carries the macOS system library stubs so no Apple SDK is needed. Linked through
    # zig-cc-macos.cmd rather than "cargo zigbuild": the wrapper zigbuild generates asks Zig for its default minimum
    # macOS (13.0), and this one asks for 10.13. The variables are set for this build only, so a native build on a
    # Mac is unaffected.
    $zig = Join-Path (python -c "import ziglang, os; print(os.path.dirname(ziglang.__file__))") 'zig.exe'
    $scripts = python -c "import sysconfig, os; print(sysconfig.get_path('scripts', f'{os.name}_user'))"
    $zigbuild = Join-Path $scripts 'cargo-zigbuild.exe'
    if (-not (Test-Path $zigbuild)) { $zigbuild = (Get-Command cargo-zigbuild -ErrorAction Stop).Source }
    if (-not (Test-Path $zig)) { throw 'zig not found: python -m pip install --user ziglang' }
    $env:VE_CARGO_ZIGBUILD = $zigbuild
    $env:CARGO_ZIGBUILD_ZIG_COMMAND = $zig
    $env:CARGO_ZIGBUILD_ZIG_VERSION = (& $zig version)
    $env:CARGO_TARGET_X86_64_APPLE_DARWIN_LINKER = Join-Path $here 'zig-cc-macos.cmd'
    $env:MACOSX_DEPLOYMENT_TARGET = '10.13'
    cargo $toolchain build --release --target x86_64-apple-darwin
    if ($LASTEXITCODE -ne 0) { throw 'macOS build failed' }

    New-Item -ItemType Directory -Force $assets -ErrorAction Stop | Out-Null
    Copy-Item 'target\x86_64-pc-windows-gnu\release\ve-modsync.exe' (Join-Path $assets 've-modsync-windows-x64.exe') -Force -ErrorAction Stop
    Copy-Item 'target\x86_64-unknown-linux-musl\release\ve-modsync' (Join-Path $assets 've-modsync-linux-x64') -Force -ErrorAction Stop
    Copy-Item 'target\x86_64-apple-darwin\release\ve-modsync' (Join-Path $assets 've-modsync-macos-x64') -Force -ErrorAction Stop
    Get-ChildItem $assets | ForEach-Object { '{0,-30} {1,8:N0} bytes  {2}' -f $_.Name, $_.Length, (Get-FileHash $_.FullName -Algorithm SHA256).Hash }
} finally {
    foreach ($name in 'VE_CARGO_ZIGBUILD', 'CARGO_ZIGBUILD_ZIG_COMMAND', 'CARGO_ZIGBUILD_ZIG_VERSION', 'CARGO_TARGET_X86_64_APPLE_DARWIN_LINKER', 'MACOSX_DEPLOYMENT_TARGET') {
        Remove-Item "Env:$name" -ErrorAction SilentlyContinue
    }
    Pop-Location
}
