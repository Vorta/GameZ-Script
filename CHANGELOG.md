# Changelog

## 1.2.0 — 2026-11-02

- Add a dedicated GameGen Script mode for `.gs`/`.gw`, command recognition, operand hover, completion, signatures, inlay hints, outline, and project navigation.
- Diagnose unloaded AIV templates at the exact vehicle name, with mission context and related source locations; update from unsaved support-script changes.
- Trace includes, macros, conditions, ordered search paths, build capture, and supported runtime node mutations. Check related resource/model/texture dependencies and receiver classes.
- Keep lightweight assistance separate from bounded mission jobs; cache immutable worlds with input and missing-path revalidation. Report unverified/unsupported semantics without speculative errors.
- Preserve standalone packaging, existing language/extension identifiers, Restricted Mode, and read-only analysis. Refresh the SDK-selected ILLink lock entry for the current .NET patch.
## 1.1.0 — 2026-10-24

- Add context-specific names for roots, sequences, animations, sounds, effects, lights, cameras, target nodes and weapon definitions; distinguish attachment, position-reference and recipient nodes. Hide labels that repeat field names while retaining hover and signature help.
- Explain timing units, frame rates, activation damage thresholds and execution distances. Correct root-selection help: literal alternatives select the first existing root; a single wildcard pattern expands to matches.

- Add verified argument inlay hints, operand hover and signature help across animation and supported resource layouts.
- Recognize incomplete network files by filename; suggest unused node names, node bodies, coordinate/link tuples and live node links.
- Explain strategy-dependent pursuit parameters and unused network fields.
- Keep unknown operand meanings unlabelled and preserve authored source text.

## 1.0.0 — 2026-10-24

- First standalone release of GameZ Script for VS Code, extracted from zStudio under the MIT license.
- Syntax highlighting, contextual completion and hover, document outline, Go to Definition, live diagnostics and explicit project validation for RECOIL `.zad` and `.zrd` text files.
- Exact float/integer storage checks, nested resource validation, contextual unused-field warnings and engine callback documentation.
- Unsaved-buffer analysis, project references, Latin-1 encoding diagnostics and bounded read-only validation.
- Includes a self-contained Windows x64 language server. No zStudio installation or external source checkout is required.
