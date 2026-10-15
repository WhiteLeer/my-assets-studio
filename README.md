# my-assets-studio

## HSR-4.6 Branch

The active target is HSR 4.6.0. This branch carries the earlier HSR implementation
while the 4.6 serialized layouts, Shader programs and dependencies are being
validated. Historical 4.4/4.5 parsers and fixtures retain their original version
labels; renaming a branch does not make those layouts compatible with 4.6.

## Current Extraction Checkpoint

- .NET 8 is the build target. The CLI can export structured Shader metadata,
  exact Material-to-Shader references and named parameter/keyword records with
  `--shader_report` and an optional `--shader_path_ids` selection.
- Compiled Shader blobs are opt-in through `--shader_blobs`; metadata export
  alone is not original HLSL or a complete Unity rendering pipeline.
- The current Android AnimationClip layout is selected by its serialized type
  hash. Its four-byte footer remains explicitly unclassified.
- Particle exports distinguish structurally parsed bytes from opaque tail
  bytes. A partial export is not marked as a complete parameter recovery.

Public-facing game naming is **HSR**. Historical source identifiers, the CLI
game selector `SR`, serialized field names and old-version evidence retain
their original spelling where changing them would alter the format or API.

Old 4.4/4.5 data remains useful for offline regression even when those clients
cannot log in. New live-client validation targets 4.6; branch naming is not a
substitute for version-specific parser and fixture validation.

This repository is a personal baseline mirror derived from [Escartem/AnimeStudio](https://github.com/Escartem/AnimeStudio).

- Upstream repository: `https://github.com/Escartem/AnimeStudio.git`
- Imported upstream commit: `7cfb26b9c158f6150c887f9157de97a3fa7672e1`
- Imported upstream version: `1.1.0`

Branch policy:

- `main` is kept as the baseline reference branch only
- Actual customizations and game-specific modifications are developed in separate branches
- Experimental or long-term maintained forks should not be committed directly to `main`
