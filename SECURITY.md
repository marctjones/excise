# Security policy

## Reporting a vulnerability

Report privately through GitHub: [Report a vulnerability](https://github.com/marctjones/excise/security/advisories/new).
Please do not open a public issue or discussion for a vulnerability, and do not attach a document that contains real
personal or confidential content. A minimal PDF that reproduces the problem is enough.

excise is maintained by one person. There is no fixed response time, but a report that redacted content survives is
treated as the highest priority.

## What counts

- **Redacted content that is still recoverable.** Text, images or graphics that excise reported as removed but that
  can still be read from the saved file: in a content stream, metadata, an attachment, an outline, a structure tree,
  an incremental update or any other part of the file. Say which independent tool recovered it (for example `mutool`
  or `pdftotext`).
- **Malformed input that hangs, exhausts memory or crashes** the parser, renderer or CLI.
- **Encryption or signature handling** that accepts what it should refuse, or discloses what it should protect.

## What does not count

Content in a place excise reports as one it cannot scrub is a documented limit, not a vulnerability; see
[docs/KNOWN_LIMITATIONS.md](docs/KNOWN_LIMITATIONS.md). A report that a carrier excise did not report is still
holding the text is a vulnerability.

## Supported versions

Fixes land on `develop` and ship in the next release. Only the latest
[release](https://github.com/marctjones/excise/releases) is supported. The builds are unsigned (see the README).
