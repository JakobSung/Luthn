# Container Releases

[한국어](releases.ko.md)

The container is Luthn's only versioned release artifact. A pull request merged
to `main` automatically publishes the development references `main` and
`sha-<commit>`. It does not create a stable release.

An owner must explicitly request each stable release. Humans, Codex, Claude
Code, and other agents use the same repository entrypoint:

```bash
python3 scripts/release-container.py v0.1.0 --dry-run
python3 scripts/release-container.py v0.1.0
```

The entrypoint reads the current remote `main` commit without inspecting or
changing the local worktree. It rejects a non-strict version, a source commit
that is not the exact remote `main` tip, and an existing release tag. A valid
request dispatches the `Release container` GitHub workflow with the version and
fixed source commit.

The workflow is the enforcement boundary. It publishes and verifies:

| Reference | Meaning |
| --- | --- |
| `main` | Latest development image, updated by ordinary `main` merges. |
| `sha-<commit>` | Exact source revision for development and diagnostics. |
| `vMAJOR.MINOR.PATCH` | Immutable stable release selected by the owner. |
| `vMAJOR.MINOR` | Latest patch in that release line. |
| `stable` | General-install and update channel. |
| `@sha256:<digest>` | Exact running and rollback identity. |

The release workflow builds the fixed source commit, labels every platform with
the SemVer and source revision, verifies the multi-architecture manifest and
anonymous pull, and then creates the immutable Git `vMAJOR.MINOR.PATCH` tag.
There is no separate package, binary asset, or required GitHub Release page.

## Installation And Updates

General installations follow `stable` but persist its resolved digest as the
running image:

```bash
curl -fsSL https://raw.githubusercontent.com/JakobSung/Luthn/main/scripts/install.sh \
  | bash -s -- --channel stable
```

Select one exact release when automatic channel updates are not wanted:

```bash
curl -fsSL https://raw.githubusercontent.com/JakobSung/Luthn/main/scripts/install.sh \
  | bash -s -- --version v0.1.0
```

`luthn version --json` reports the update channel separately from the immutable
installed image and running digest. `luthn update check` inspects a mutable
official channel without pulling or changing local state. `luthn update`
resolves the selected channel, records the exact target digest, backs up the
database, migrates, restarts, and verifies readiness. Explicit SemVer installs
remain pinned until the operator selects `stable`, `main`, or another release.

Do not duplicate this procedure in agent-specific instruction files. Those
files may point here and require an explicit owner release request; the shared
entrypoint and GitHub workflow remain the source of truth.
