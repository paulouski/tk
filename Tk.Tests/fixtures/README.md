# Snapshot fixtures (golden corpus)

This directory is the regression oracle for tk's output **filters** (`Tk/Filters/*`). It
captures their *current* behavior, including known quirks/limitations — nothing here is
"the right answer", it's "what tk does today". Later refactors are expected to keep these
snapshots green; if a refactor intentionally changes output, update the snapshot deliberately
(see below) as part of that change, with the diff reviewed in the PR.

The harness lives in `Tk.Tests/Snapshots/` (`SnapshotHarness.cs`, `SnapshotTests.cs`,
`SnapshotNormalizer.cs`, `DiffFormatter.cs`, `FixtureMeta.cs`). It runs entirely in-process —
no subprocess spawning — so `dotnet test` stays fast and deterministic.

## Layout

```
fixtures/<area>/<case>/
  meta.json             # which filter, exit code, detail level(s), filter-specific params
  input.txt             # raw text the filter receives (or the log file content, for "log")
  state.txt             # optional: git-status only, the plain-text `git status` used for
                         # repo-state detection (mid-merge/mid-rebase/detached HEAD)
  expected.default.txt  # checked-in snapshot for DetailLevel.Default
  expected.more.txt     # checked-in snapshot for DetailLevel.More (only if meta.json
                         # requests it — usually only when the filter's output actually
                         # differs between Default and More)
```

`<area>` is just a grouping folder (`dotnet`, `git`, `log`, `grep`, `find`); the harness
discovers cases by scanning `fixtures/*/*/meta.json` — no registration needed.

### meta.json fields

| field          | used by                          | meaning                                              |
|----------------|-----------------------------------|-------------------------------------------------------|
| `filter`       | all                                | `dotnet-build` \| `dotnet-test` \| `dotnet-restore` \| `git-status` \| `git-diff` \| `git-show` \| `git-log` \| `git-compact` \| `grep` \| `find` \| `log` |
| `exitCode`     | all except `log`                  | exit code passed into `IOutputFilter.Apply(raw, exitCode)` |
| `detailLevels` | all                                | array of `"Default"` and/or `"More"` — one test case per entry |
| `unityMode`    | `git-status`                      | passed to `GitStatusFilter`'s unity-meta-stripping mode |
| `hasState`     | `git-status`                      | if true, read `state.txt` and pass it as the filter's `stateRaw` |
| `command`      | `grep`                            | `"grep"` or `"rg"` (affects option parsing, not output shape here) |
| `pattern`      | `grep`                            | the search pattern, used for `GrepFilter`'s truncate-around-pattern logic |
| `flags`        | `log`                             | array passed to `LogFileFilter.Apply` (e.g. `["--errors"]`, `["--all"]`, `["--last","5"]`) |

## Normalization

Both when generating and when comparing, output is normalized (`SnapshotNormalizer.Normalize`)
so volatile substrings don't cause false-positive diffs:

- line endings → `\n`
- ANSI escape codes stripped
- `t=<duration>` → `t=<t>`
- absolute temp/repo paths (`/Users/...`, `/var/folders/...`, `/tmp/...`) → `/WORK/<filename>`
- `pid=<n>` → `pid=<pid>`
- **git fixtures only**: every distinct 7-40 char lowercase-hex hash is replaced by a
  stable placeholder keyed by first-seen order in that output (`<h1>`, `<h2>`, ...) — so the
  *value* of a hash never causes a diff, but if two hashes that used to be equal become
  different (or vice versa), that identity change still surfaces.

Fixture `input.txt` files are pre-scrubbed by hand at authoring time (absolute scratch-repo
paths replaced with `/WORK/...` before committing) so raw fixtures and their snapshots stay
consistent; the normalizer above is a safety net, not the primary scrubbing mechanism.

## Running / updating

Normal run (part of `dotnet test`): fixtures are compared against their checked-in
`expected.*.txt`; a mismatch fails with a unified-diff-style message showing the first
differing line plus context.

To (re)generate snapshots after an intentional filter change:

```
TK_UPDATE_SNAPSHOTS=1 dotnet test --filter FullyQualifiedName~Snapshots
```

This overwrites every `expected.*.txt` for every discovered fixture with the filter's current
(normalized) output — **review the diff carefully** before committing; this mode always
"passes" because it writes rather than asserts.
