# ZMODEM branch reference notes

[ZMODEM package guide](README.md) | [Family branch matrix](../docs/protocol-branches.md)

The implementation is original C#. References are used to establish wire facts;
no historical C source is included in this package.

## Primary references

- Chuck Forsberg, *The ZMODEM Inter Application File Transfer Protocol*,
  revision October 14, 1988, preserved at
  https://raw.githubusercontent.com/FGasper/zmodemjs/master/reference/zmodem.txt
- Forsberg's 1989 `zm.c`, `zmr.c`, and `zmodem.h`, preserved in the 2.11BSD
  source archive:
  https://www.retro11.de/ouxr/211bsd/usr/src/local/zmodem/zm.c.html
  https://www.retro11.de/ouxr/211bsd/usr/src/local/zmodem/zmr.c.html
  https://www.retro11.de/ouxr/211bsd/usr/src/local/zmodem/zmodem.h.html
- FTSC, **FTS-0006.002**, revision November 30, 1991, *YOOHOO and YOOHOO/2U2*,
  section on ZMODEM and ZedZap, reproduced at
  https://brainpatchworkdx.com/viewtopic.php?t=6
- Synchronet's SEXYZ documentation, explicitly identifying its 8K setting with
  ZedZap: https://www.synchro.net/docs/sexyz.txt
- lrzsz's current protocol draft, particularly sections 2.1.2, 2.4, 9.4, 9.5
  and its constant tables:
  https://raw.githubusercontent.com/UweOhse/lrzsz/master/doc/zmodem-wip.txt
- Omen's archived `MOBYTRBO.TXT` explanation, including a February 1990
  discussion, from Simtel's `MOBYTRBO.ZIP`. Archive mirror inspected:
  https://ftp.zx.net.nz/pub/archive/ftp.icm.edu.pl/pub/coast/msdos/zmodem/mobytrbo.zip

## Legacy framing implemented

A variable header uses indicator `a`, `b`, `c` or `d` for binary16, hex16,
binary32 or binary-RLE32. Its length byte describes 0..16 header-data bytes;
type is separate. CRC covers type and header data, excluding the length byte.
The codec preserves all header-data bytes; state machines use the ordinary
first four bytes for position/flags. Fixed binary-RLE32 uses indicator `D`.

RLE transformation precedes ZDLE quoting. The encoded stream uses `0x7e` as
escape: `0x40` following it denotes a literal escape; `0x20..0x3f` denotes
3..34 spaces; a count code above `0x40` followed by a value repeats that value
`code - 0x40` times. Other runs remain literal. CRC32 covers the compressed
bytes plus the subpacket terminator. Both encoded storage and expanded output
are bounded. Legacy options are not advertised by default.

Independent test constants were calculated using Python's `zlib.crc32` and
`binascii.crc_hqx`, not this library. For example type/data `0a0000000055` has
CRC32 `0x0b5827be`; compressed-data/terminator `7e43417e207e40426b` has CRC32
`0x88ba3ba7`.

## Deliberate boundaries

FTS-0006.002 defines ZedZip as the 1K, null-batch-capable relative of the 8K
ZedZap profile. Its WaZOO role scheduling is a layer above these transfer
engines, not a compression algorithm.

The lrzsz draft explains that ESC8 and proprietary Omen quoting have conflicting
interpretations. Its discussion of MobyTurbo, Pack-7 and the `1` format relies
on reverse-engineering work, not a complete original public protocol contract.
This package does not label the implemented `D`/`a`/`b`/`c`/`d` subset as full
ZMODEM-90, nor alias unsupported proprietary modes to ordinary ZMODEM.
`MOBYTRBO.TXT` confirms reduced network-control quoting and retained CRC32/error
recovery, but supplies no capability negotiation, frame indicators/flag bytes,
exact quoting table or wire vectors. It cannot resolve those missing contracts.
