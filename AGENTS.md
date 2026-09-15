# Agent guidance

Routing only. This file says where things are written down, not what to do.

## Where to look

| Looking for | Read |
| --- | --- |
| What the project is, how to build and test it | [`README.md`](README.md), Build section |
| Which release channels exist and what they promise | [`docs/lite-distribution.md`](docs/lite-distribution.md) |
| How a release is cut, and the post-release steps | [`docs/release-velopack.md`](docs/release-velopack.md) |
| Which gate proved which claim, and when | [`docs/verification-history.md`](docs/verification-history.md) |
| What ships inside the binary that isn't ours | [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) |
| Package-manager manifests and what remains before submission | [`packaging/winget/README.md`](packaging/winget/README.md), [`bucket/syrtis.json`](bucket/syrtis.json) |

## Public repository

Never put private paths, credentials, or machine-specific tooling details in
tracked files. A local `.agent-local/` overlay holds machine-specific
instructions and is deliberately outside this tree.

The distinction that matters is not "local" but "particular to one machine".
`tools/sdkfree/global.json` is tracked because it encodes no path and no
identity — it is an escape hatch any machine can use. "dotnet lives at
`/opt/homebrew/...` on this laptop" is not, and belongs in the overlay.

## Running the tests when the pinned SDK is missing

The root `global.json` pins .NET SDK 10.0.301 with `rollForward: disable`, so a
machine carrying only a later 10.0.x cannot run `dotnet` from the repository
root at all. Run it from `tools/sdkfree/` instead — the Build section of the
README has the command, and the Windows caveat: there the test project also
needs its native tuple, which the escape hatch does not provide.

This is written down because forgetting it has a specific, expensive shape:
the failure looks like "this machine cannot run the suite", which is a sentence
about the machine rather than about a missing line of configuration, and it
survives being written into a commit message as though it were a fact.
