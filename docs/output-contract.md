# tk output contract

tk's filters compact noisy tool output (dotnet/git/grep/find process transcripts, ASP.NET log
files) into a few dense lines for an agent to read. The dominant bug class found by auditing
real transcripts was **input silently disappearing**: a conflict diff producing `f=0 +0 -0`, a
non-log file reporting `ok n=0`, an unrecognized log-entry shape vanishing with no trace. This
contract makes that bug class structurally hard to reintroduce.

## Input units

Every filter is defined over a sequence of **input units** — the smallest thing the filter is
responsible for accounting for one-by-one:

| domain                              | input unit                                              |
|--------------------------------------|----------------------------------------------------------|
| process filters (dotnet/git/grep/find) | one physical line of process output. stdout and stderr are counted as separate streams, before `ProcessOutput.Combine` and before any trimming — a filter must not lose a line to `Combine`'s whitespace trim without accounting for it. |
| `tk log`                              | one physical line of the log file                        |
| `tk tree` / `tk files`                | one filesystem entry (file, directory, or unreadable/symlink diagnostic) |
| `tk view` / `tk focus`                | mode-dependent: file lines for range/preview rendering; symbol records for symbol lists and summaries; match records for `focus`'s ranked hits |

## Classification API — the conservation invariant

A filter that ledger-izes its accounting routes every input unit through a `Tk.Common.UnitLedger`
(`Common/UnitLedger.cs`) into **exactly one** of four categories:

- **Kept** — rendered verbatim or near-verbatim (e.g. truncated for length, but recognizably the
  same line).
- **Summarized** — not rendered itself, but represented by an aggregate output line (a count, a
  `top=`/`+N more` line, a dedup `(x3)` marker, a collapsed HTTP request/response pair).
- **Hidden** — recognized and intentionally omitted (blank lines, known startup/framework noise,
  a capped-off tail beyond a display limit). Hidden is only legal for content the filter actually
  recognized — see "alien input" below.
- **Unparsed** — the filter did not recognize this line's shape at all.

`UnitLedger` exposes `Keep(...)`, `Summarize(...)`, `Hide(...)`, `Unparsed(...)` (each accepting
either a unit for single-line call sites or an explicit `count` for bulk classification) and
read-only totals `Kept`/`Summarized`/`Hidden`/`UnparsedCount`/`Total`. There is no free-form
counter escape hatch — a filter that wants to classify a line must go through one of these four
calls, so `Total` is definitionally the sum of everything classified.

`ledger.AssertConserves(inputUnitCount)` throws when `Total != inputUnitCount` — this is what the
test harness uses to enforce the invariant per fixture (see below), and it is also how "classified
more units than existed" fails loud rather than silently producing a wrong count.

### Alien input

The ledger has no way to enforce that a filter's *intent* was honest (it can't tell "hidden
because I recognized this as noise" from "hidden because I gave up and didn't want to admit it").
The test harness enforces intent instead: **every filter fixture set includes at least one
ALIEN-INPUT fixture** — garbage the parser cannot know the shape of — and the assertion for that
fixture is that the alien lines land in `Unparsed`, never in `Hidden`.

## Footer grammar

One shared renderer, `Common/OutputFooter.cs`, replaces the ad-hoc footer-string assembly that
used to be duplicated across `Program.cs`, `GitCommand.cs`, and `LogFileFilter`. It emits tokens
in this fixed order, each omitted when not applicable:

```
hid=<hidden>/<total>   unparsed=<n>   raw=<path>   (--more, --raw)
```

- `hid=<hidden>/<total>` — semantics unchanged from the pre-existing `HiddenLinesFooter`: hidden
  lines were rendered output lines that didn't make it into the shown text (independent of ledger
  categorization; this predates the ledger and stays byte-compatible).
- `unparsed=<n>` — only emitted when `n > 0`; the ledger's `UnparsedCount` for filters that have
  been ledger-ized. New token; the reason existing snapshots gain a line when a fixture has alien
  input.
- `raw=<path>` — a saved copy of the raw pre-filter output, when one exists (see exit-code policy
  below).
- the escalation hint, `(--more, --raw)` or `(--raw)` under `--more`, exactly as before.

The whole line is omitted only when none of `hid=`, `unparsed=`, or `raw=` apply.

`LogFileFilter` keeps its own pre-existing `unparsed=<n>` disclosure in its summary header line
(not the footer) — that predates this contract and already serves the same purpose for that
command, so its footer call passes `unparsedCount: 0` to avoid a duplicate token.

## Exit-code policy

- `unparsed > 0` never changes a command's exit code. It only forces disclosure (the `unparsed=`
  footer token) and, for process commands, a `raw=` reference to the saved pre-filter output so
  the agent can go look at what wasn't understood.
- For non-process commands, `raw=` only appears when a pre-filter rendering already exists at a
  stable path — `tk log` points `raw=` at the source file path itself (no copy needed). `tree` and
  `view` disclose `unparsed` (when applicable) without a `raw=` reference.
- Permission-denied / symlink / broken-link entries encountered during `tree`/`files` traversal
  are **Kept diagnostics** — shown, not treated as `Unparsed`, and not command failures — unless
  the root path itself is unreadable (which is a hard failure as today).

## What's ledger-ized in this phase

Fully ledger-ized: `DotnetBuildFilter`, `DotnetTestFilter`, `DotnetRestoreFilter`,
`GitStatusFilter`, `GitDiffFilter`, `GitLogFilter`, `GitCompactFilter`, `GrepFilter`, `FindFilter`,
`LogFileFilter`.

Not ledger-ized this phase (shared `OutputFooter` renderer only, `unparsedCount` always 0):
`tk tree`, `tk view`, `tk focus`. Ledgerizing these mode-dependent, multi-unit-shape commands is
left as a TODO for a follow-up phase — see the parent architecture plan.

## Rule-table line classification

`GitDiffFilter`, `LogFileFilter`, and `DotnetBuildFilter` classify each physical line via an
ordered table of `(Match, Apply)` rules instead of an if/else ladder: the first matching rule
wins, and a mandatory final catch-all rule handles anything unrecognized. Parse state (current
file diff, current log entry, etc.) lives in a small per-filter `ParseState`/`Ctx` type that rule
handlers read and mutate; the table itself is private to each filter (no shared base class) since
each filter's state shape differs. Rule order encodes real precedence (e.g. a diff's structural
metadata lines must be recognized before the +/-/space content-line rules, or they'd be misread
as additions/deletions) — see the comments above each filter's rule table for the specific
order-sensitive cases.

## Process-filter stream accounting note

Filters classify the single already-combined `raw` string they are handed (this is what the
snapshot fixtures exercise, and what `HiddenLinesFooter`'s `hid=` already measured). Production
callers (`Program.cs`, `GitCommand.cs`) additionally know the true pre-`Combine` stdout/stderr
line counts; the (usually zero, occasionally a trailing-blank-line) gap between that true total
and the combined text's line count is folded into the ledger as `Hide(gap)` — recognized
whitespace trimmed by `ProcessOutput.Combine` — right after the filter returns, so the ledger's
conservation invariant holds against the real per-stream total in production, not just against
the combined text.
