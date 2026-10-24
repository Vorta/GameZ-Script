# Development

The extension client is in `extension/`. `src/GameZ.Script.LanguageServer` implements LSP over standard input/output. `src/GameZ.Script.Core/Language` owns analysis and metadata. The rest of Core is the repository's parser, source-format, compiler and world-validation support snapshot. Keeping this source in the repository preserves the existing deep validation without an installed editor, sibling checkout, submodule or downloaded zStudio binary.

The sources were extracted from MIT-licensed zStudio commit `4fbb958a1e381f73cf9fc7d0d212fa7964f33318`. The license is retained at the repository root and in the VSIX. Namespaces and assemblies are `GameZ.Script.*`; extension commands remain `gamez.*`. References to `.zstudio` inside the source-format code describe compatible project files and must not be renamed as product branding.

Use the .NET SDK selected by `global.json` and Node.js with npm. From this repository:

```powershell
dotnet restore GameZ.Script.slnx --locked-mode
dotnet build GameZ.Script.slnx -c Release --no-restore
dotnet test --solution GameZ.Script.slnx -c Release --no-build
./tools/package.ps1
```

Run `npm run test:host` from `extension/` on an isolated desktop. `GAMEZ_VSCODE` may select an installed VS Code executable; otherwise the test runner downloads VS Code. All test projects are temporary and require no game data.

The VSIX is written to `artifacts/`. Install it with VS Code's **Install from VSIX…** command. Releases are available from [GitHub Releases](https://github.com/Vorta/GameZ-Script/releases). Marketplace publication requires a separate explicit request. Before publishing a release, run CI and the extension-host check, verify the package version, then attach the VSIX and `SHA256SUMS` to the approved Git tag. Published release tags are immutable.
