# Changelog

User-facing changes are curated under **Unreleased** until a version is
prepared and tagged. Historical entries are imported from real Git tags with
git-changelog; unrecognized and emoji-prefixed commit messages are retained.
See [the release guide](docs/releases.md) for the update and publication commands.

## [Unreleased]

### Added

- XMODEM checksum, CRC16 and 1K engines; XMODEM-G/QMODEM-G and a separate
  WXMODEM four-block window protocol with flow control and fallback.
- YMODEM checksum and G branches, optional-length metadata and incremental
  byte-stream input for single-file and batch transfers.
- ZMODEM CRC16/CRC32, streaming and sliding windows, 4K/8K, ZedZap/ZedZip,
  resume/skip, ZCRC comparison hooks and public legacy variable-header/RLE modes.
- MODEM7, Telink and SEAlink engines, including Overdrive and absolute RESYNC.
- JMODEM variable frames, adaptive block sizing, RLE and exact final lengths.
- An aggregate `ModemKit` package referencing the five protocol libraries,
  all targeting netstandard2.0 and net10.0.
- Deterministic duplex fault fixtures and an independent lrzsz runner with
  retained wire captures, exit codes and content hashes.
- Documentation navigation, integration/testing guides and a source-linked
  branch matrix that distinguishes implemented behavior and compatibility limits.
- Strict MkDocs site builds and GitHub Pages deployment through GitHub Actions.
- Version-specific GitHub Release notes, package checksums, separate optional
  NuGet trusted publishing and a git-changelog-assisted changelog workflow.

### Changed

- Application and transport work is represented by ordered events/actions,
  with explicit timeout, buffering, persistence and closing-grace contracts.
- YMODEM send actions expose negotiated integrity; packet buffers own their
  data so caller mutation cannot change a retransmission.
- CI covers both library asset selections, packaging, documentation and
  independent interoperability. The recorded branch validation passed 759
  tests per asset selection and 156 native cases; these are dated results.

### Fixed

- The native interoperability runner's retry timer racing lrzsz's one-second
  X/YMODEM EOT quiet check on Linux process pipes.
- Lost header/data/final acknowledgements, truncated frames, sequence rollover,
  empty files, premature EOF and bounded recovery across supported profiles.
- SEAlink partial old-window input consuming the RESYNC acknowledgement.
- ZMODEM CRC-comparison corruption and premature completion before acceptance.
- JMODEM lost startup acknowledgements and damaged final retransmissions
  preventing a later valid retransmission from being acknowledged.

### Compatibility

- MobyTurbo, Pack-7, ambiguous ESC8 and unresolved proprietary ZMODEM-90 modes
  remain unsupported. Public legacy RLE support is not full commercial
  ZMODEM-90 compatibility.
- Protocols without an exact wire length retain padding. Fido mailer
  authentication/routing and arbitrary remote command execution remain outside
  the file-transfer engines.

## [v0.2.0](https://github.com/Modemkit/Modemkit/releases/tag/v0.2.0) - 2026-03-22

<small>[Compare with v0.1.0](https://github.com/Modemkit/Modemkit/compare/v0.1.0...v0.2.0)</small>

### Fixed

- Fix Ymodem protocol rollover handling, filename encoding, and Decode API compatibility (#5) ([0456dda](https://github.com/Modemkit/Modemkit/commit/0456ddaf22d7912c79090c7929c00efc26c9d263) by BalconyJH).

### Misc

- :bookmark: bump version to 0.2.0 ([1fb9fb9](https://github.com/Modemkit/Modemkit/commit/1fb9fb9bc176c7eec152bd62c733d1d30bb29e9b) by BalconyJH).
- Support dynamic 128/1K block sizing for headers and data and add  block-sizing tests (#12) ([41cbbfd](https://github.com/Modemkit/Modemkit/commit/41cbbfd78a51632b8a84cce43d37304ab0efeb20) by BalconyJH).
- :bookmark: bump version to 0.1.2 ([8994a5f](https://github.com/Modemkit/Modemkit/commit/8994a5ffeea8f2b67cb40b5a3133ba7b84b3b07f) by BalconyJH).
- Clarify 1K tail packet coverage (#10) ([b1bbd68](https://github.com/Modemkit/Modemkit/commit/b1bbd6870ca0b42ad86f3234b5aec57652d1dfdd) by BalconyJH).
- Adjust YMODEM tail block sizing (#9) ([015389a](https://github.com/Modemkit/Modemkit/commit/015389ad4ddc350de1093358bddee0814ddbbe5e) by BalconyJH).
- :bookmark: v0.1.1 ([61edf6c](https://github.com/Modemkit/Modemkit/commit/61edf6cb4b6322d5948bfd4ef23fa08777ba17c6) by BalconyJH).
- Handle block-number wrap-around (256 -> 0) in receivers and add tests (#6) ([7aec2cc](https://github.com/Modemkit/Modemkit/commit/7aec2cc6873e1bbb4983f110657f7975f076cdab) by BalconyJH).
- :green_heart: Add CI/CD workflow for NuGet package publishing and versioning ([4c28582](https://github.com/Modemkit/Modemkit/commit/4c28582b9fc183aad813d030a5beadb3dd1c217b) by BalconyJH).

## [v0.1.0](https://github.com/Modemkit/Modemkit/releases/tag/v0.1.0) - 2026-03-19

<small>[Compare with first commit](https://github.com/Modemkit/Modemkit/compare/3e7fe94a89d942eb1a19c3b20dc09d5449271b17...v0.1.0)</small>

### Misc

- :package: Update project file with package metadata and dependencies for Ymodem.Protocol ([ae946ed](https://github.com/Modemkit/Modemkit/commit/ae946ed3f56de874b8c48d0be982642b1afd70d9) by BalconyJH).
- :memo: Add README documentation for Ymodem.Protocol library ([d755bd1](https://github.com/Modemkit/Modemkit/commit/d755bd1e2620c0e7c90030618e112cf54c3f61aa) by BalconyJH).
- Initial commit ([3e7fe94](https://github.com/Modemkit/Modemkit/commit/3e7fe94a89d942eb1a19c3b20dc09d5449271b17) by BalconyJH).
- :sparkles: Implement YModem protocol classes and event handling for file transfers ([f39b3cf](https://github.com/Modemkit/Modemkit/commit/f39b3cf383e6bec88af45213c32109b38031190f) by BalconyJH).
- :tada: Add project files and configuration for Ymodem.Protocol and its tests ([114e61c](https://github.com/Modemkit/Modemkit/commit/114e61c619c9c9048c52d502979acc5aa3479a08) by BalconyJH).
