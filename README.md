# my-assets-studio

## ZZZ Branch

`ZZZ` is the long-lived Zenless Zone Zero development branch, separate from
`HSR`. Its existing implementation targets ZZZ 3.0; the branch rename does not
establish support for later clients. Keep parser layouts, profiles and test
evidence labeled with their actual source version.

This repository is a personal baseline mirror derived from [Escartem/AnimeStudio](https://github.com/Escartem/AnimeStudio).

- Upstream repository: `https://github.com/Escartem/AnimeStudio.git`
- Imported upstream commit: `7cfb26b9c158f6150c887f9157de97a3fa7672e1`
- Imported upstream version: `1.1.0`

Branch policy:

- `main` is kept as the baseline reference branch only
- `HSR` and `ZZZ` are separate long-lived game development branches
- Client updates use version-specific profiles, layout checks and evidence, not new development branches
- Former versioned branch tips are preserved as `archive/branches/<former-name>` tags; release tags remain unchanged
- Experimental or long-term maintained forks should not be committed directly to `main`
