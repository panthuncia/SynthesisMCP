# 001: Platform versions

**Status:** accepted, September 2026

## Decision

| Component | Pinned version | Why |
| --- | --- | --- |
| .NET SDK / TFM | 10.0.301 / `net10.0` | The current Synthesis package targets net10.0 and net9.0. |
| Mutagen.Bethesda.Synthesis | 0.36.6 | Latest listed release. Depends on Mutagen 0.54.4. |
| Mutagen.Bethesda.Skyrim | 0.54.4 | Matches Synthesis. |
| Microsoft.CodeAnalysis.CSharp | 5.6.0 | Compiler for agent programs. The language version is pinned to C# 14. |
| Basic.Reference.Assemblies.Net100 | 1.8.12 | A pinned reference pack, so compilation does not depend on the machine's SDK. |
| xunit.v3 | 3.2.2 | 4.x was only weeks old when this was written. |
| ModelContextProtocol | 2.2.0 | The official C# MCP SDK, for `SafePatch.Mcp` only. |
| Microsoft.Extensions.Hosting | 10.0.12 | The host the MCP SDK runs in. |

Mutagen and Synthesis are pinned as exact ranges, and so is the `SafePatch.Synthesis` package that generated
patchers reference (ADR 007).

NuGet also lists `Mutagen.Bethesda.Synthesis 5.1.0` and `Mutagen.Bethesda.Skyrim 7.1.0 / 23.4.0`. All three are
unlisted legacy uploads with a 1900 publish date and should not be used.

## Consequences

Mutagen's source generator emits CS0436. Projects that reference Mutagen suppress it.
