# ModemKit.Interop

[Documentation index](../docs/README.md) | [Testing guide](../docs/testing.md) |
[Branch coverage](../docs/protocol-branches.md)

A standalone .NET 10 console fixture that transfers deterministic binary files
between ModemKit and the independent **lrzsz** `sz` / `rz` executables.
It uses real process pipes and checks the native exit code, managed completion
state and received bytes. Native binaries are external prerequisites; this
project never installs software.

## Run

From the repository root, on Linux with lrzsz available:

```text
dotnet run --project ModemKit.Interop/ModemKit.Interop.csproj -c Release -- --lrzsz-directory /usr/bin
```

On Windows, the runner invokes `wsl.exe` with redirected binary pipes.
The default distribution is `Ubuntu`. Point `--lrzsz-directory` at a Windows
drive-letter directory containing Linux `sz` and `rz` binaries:

```text
dotnet run --project ModemKit.Interop/ModemKit.Interop.csproj -c Release -- --lrzsz-directory C:\work\ModemKit\artifacts\lrzsz\extracted\usr\bin --wsl-distribution Ubuntu
```

Replace the example checkout path with your own. The default Windows binary directory is
`artifacts/lrzsz/extracted/usr/bin` relative to the current working directory.
Drive-letter paths are mapped to `/mnt/<drive>/...`. UNC paths are rejected.
The Linux binaries must be executable and have their runtime dependencies
available in the selected distribution.

All options require a value:

| Option | Default | Meaning |
| --- | --- | --- |
| `--protocol` | `all` | Comma-separated `z32,z16,z4k,z8k,zedzap,zedzip,zstream,zwindow,y,yg,ychecksum,xchecksum,xcrc,x1k` |
| `--direction` | `both` | `send` or `receive` describes the managed endpoint |
| `--sizes` | `0,1,129,1025,8193,65537` | Comma-separated lengths, up to 16 MiB per file |
| `--lrzsz-directory` | platform-specific | Directory containing `sz` and `rz` |
| `--artifacts` | `artifacts` | Parent for a unique evidence directory |
| `--wsl-distribution` | `Ubuntu` | Windows WSL distribution |
| `--timeout-seconds` | `30` | Hard per-case wall-clock deadline |
| `--idle-timeout-ms` | `1000` | Managed protocol retry timer |

For example, test CRC16 and CRC32 ZMODEM in both directions:

```text
dotnet run --project ModemKit.Interop/ModemKit.Interop.csproj -c Release -- --protocol z16,z32 --sizes 0,129,8193
```

The default matrix has **156 cases**. YMODEM-G and YMODEM checksum run native
`sz` into the managed receiver because `rz` does not request these modes.
The managed sender implementations are exercised by deterministic integration
fixtures. Native large-packet sends explicitly use `--start-4k`/`--start-8k`
and `-L`; native windowed sends use `-w 16384`. Managed endpoints select the
corresponding profile and flow-control behavior.

ZedZap and ZedZip runs verify the compatible 8K/1K binary file-transfer wire
profiles, not a Fido mailer or YOOHOO session. The 65,537-byte cases cross both
large-packet boundaries and the configured 16K window.

XMODEM-G, WXMODEM, MODEM7, Telink, SEAlink, JMODEM and legacy variable-header/RLE
ZMODEM are not included in the lrzsz matrix. The validated native peer did not
honor XMODEM-G streaming semantics. These branches instead have primary-source wire
vectors, scripted peers and deterministic fault fixtures.

The [dated validation record](../docs/testing.md#recorded-validation) lists the
environment and results. The [matrix breakdown](../docs/testing.md#independent-lrzsz-runs)
shows the supported directions and how the default 156 cases are counted.

## Evidence and exit status

Every run creates a unique `interop-<UTC timestamp>-<id>` directory.
Every case retains:

- `source/payload.bin` and `received/payload.bin`;
- `command.json`, with the exact executable and argument array;
- `to-native.bin` and `from-native.bin`, the unmodified wire bytes;
- `native-stderr.txt`, kept separate from the binary protocol stream.

`results.json` is updated after each case. It records success/failure, elapsed
time, native exit code, source/received SHA-256 and the evidence directory.
Exit code `0` means every executed case passed; `1` means at least one failed
or no cases were executed; `2` means invalid CLI options.

Each case transfers one generated `payload.bin`, whose bytes cycle through
all 256 values. YMODEM and ZMODEM must match exactly. XMODEM does not convey
length, so its original-byte prefix must match and any remaining bytes must
be bounded `0x1A` padding. Padding is preserved in the evidence file.

The fixture uses `Process.ArgumentList`, no command shell, and
`StandardInput.BaseStream` / `StandardOutput.BaseStream`. Stderr is drained
independently. A hard deadline bounds every case; failures kill the child
process tree and preserve available evidence. The fixture never deletes
existing evidence or source files.
