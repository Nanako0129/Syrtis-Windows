# Contributing

Two conventions, shared with [TokenBar](https://github.com/Nanako0129/TokenBar/blob/main/CONTRIBUTING.md) and [tokscale-core](https://github.com/Nanako0129/tokscale-core/blob/main/CONTRIBUTING.md) so a branch reads the same across the three repositories.

| Area | Convention |
|---|---|
| Branch name | `<type>/<kebab-summary>`, where `type` is the Conventional Commit prefix of the branch's primary concern — the type its pull-request title carries (`feat`, `fix`, `docs`, `chore`, `refactor`, `test`, `ci`, `build`, `perf`, or any other prefix this repository's commits already use; the list is not closed) — such as `fix/grok-weekly-credit-pool`. Supporting commits on the branch may use other types. One reviewable concern per branch. |
| Merging | Pull requests merge with a merge commit. The commits on the branch are preserved, not squashed. |

Build and verification steps are in the [README](README.md#build).
