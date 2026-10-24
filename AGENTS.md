# GameZ Script agent rules

GameZ Script is a standalone Windows x64 VS Code extension for RECOIL `.zad` and `.zrd` text sources. This repository is private. Do not change its visibility or publish to the VS Code Marketplace without an explicit request.

- Build and test without zStudio, engine checkouts, game assets, Python or Blender. The repository owns its parser/compiler support sources; never add a reference to a sibling checkout or installed application.
- Preserve the language ID `gamez-script` and extension ID `Vorta.gamez-script`. Source-project format compatibility, including the `.zstudio` directory name, is a file-format contract, not a dependency on the editor.
- Preserve MIT notices and attribution. Never commit game data, extracted assets, build output, credentials, `.agent/` or local corpus reports.
- Read `.agent/HANDOFF.md` if present. Preserve existing changes. Engine projects and user source projects are read-only evidence. Record verified semantics in `docs/engine-evidence.md`; do not guess storage kinds or change compiler arithmetic casually.
- Analysis is read-only. Unsaved buffers overlay disk in memory. Never save, export, rewrite encoding, or execute game code during analysis.
- Build `dotnet build GameZ.Script.slnx -c Release`; test `dotnet test --solution GameZ.Script.slnx -c Release`. Keep NuGet and npm lock files current. `tools/package.ps1` builds the self-contained server, client and VSIX and runs grammar/protocol/package checks. Run `npm run test:host` in `extension` on a private desktop/profile for UI validation; never close the user's VS Code windows.
- Review changes with fresh adversarial subagents: first round covers the full change, later rounds the fixes and directly interacting code. Fix realistic editing/retail-data defects and repeat until clean. Reviewers use recorded evidence, tests and behavior, not external engine projects. Record findings and validation in `.agent/`, not tracked documentation.
- Releases require passing checks, a matching extension/server version, an immutable Git tag and a verified VSIX with SHA256SUMS. Changes to the extension version use semver; the initial release tag is `v1.0`, with package version `1.0.0`.
