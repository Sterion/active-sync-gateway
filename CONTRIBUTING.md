# Contributing

Thanks for your interest. Please read the licensing section below **before**
opening a pull request — it is unusual, and it is not negotiable after the fact.

## Licensing of contributions

This project is dual-licensed (see [LICENSE](LICENSE)):

- `src/ActiveSync.Contracts/` and `src/ActiveSync.Protocol/` — MIT
- everything else — PolyForm Noncommercial 1.0.0, with commercial licensing
  reserved to the copyright holder (see [COMMERCIAL.md](COMMERCIAL.md))

For that reservation to mean anything, the copyright holder has to be able to
license the whole work. So, by submitting a contribution (a pull request, patch,
or any code, documentation or other material) you agree that:

1. You grant Ruben Andersen a perpetual, worldwide, non-exclusive, royalty-free,
   irrevocable licence to use, reproduce, modify, distribute and **sublicense**
   your contribution, **including under commercial licence terms and including
   relicensing it under different terms**.
2. You retain your own copyright in your contribution. This is a licence grant,
   not an assignment — you keep the right to use your own work however you like.
3. You have the right to grant that licence: the work is yours, and it is not
   subject to an employment agreement, a contract, or another licence that would
   prevent it.
4. Your contribution is your original work. Do **not** paste code from other
   projects, and in particular do not port code from
   [Z-Push](https://github.com/Z-Hub/Z-Push) — it is AGPLv3, and this project is
   a deliberate clean-room implementation from Microsoft's published
   specifications. Consult Z-Push for *behaviour* if useful; never for code.

If you cannot agree to all four, please open an issue describing the fix instead
of submitting the code — a well-described bug report is genuinely useful and
carries none of this baggage.

## Practical guidance

The architecture, invariants and conventions live in [AGENTS.md](AGENTS.md).
Read it before changing anything; it documents a lot that is not obvious from
the file tree. In particular:

- **Async end-to-end** is a hard rule. `.Result` / `.Wait()` fail the build
  (VSTHRD002/VSTHRD103 are errors).
- The build must stay at **zero warnings**, and `dotnet test ActiveSync.slnx`
  must stay green.
- **House style is tabs + CRLF** for new files. Many older files are 4-space/LF
  and are deliberately *not* reformatted — match the file you are editing.
- Protocol changes (WBXML tables, code pages) need round-trip tests and must be
  verified against the MS-AS\* specifications, never guessed.
- If you add an option, a CLI verb or a backend, update the matching file under
  [`docs/`](docs).

`scripts/test-fast.ps1` (or `.sh`) is the recommended per-change check.
