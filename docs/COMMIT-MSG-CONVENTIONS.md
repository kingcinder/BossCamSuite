# BossCamSuite Commit Message Conventions

Subject line < 72 chars, imperative mood, scoped to what the commit actually
changes. Plain language. No placeholder titles survive review.

## Forbidden subject patterns

The following patterns are placeholders. PR review rejects them and the next
commit on the same branch must call out the oversight + amend before push.

```
grep -E '^(describe what changed here|wip|WIP|update|tmp|fix|untitled)\s*$'
```

(Match against the first line of `git log -1 --pretty=%s`.)

## Recommended shape

```
P0/P1/P2 #N: short imperative summary

- One bullet describing what changes (who/what/where)
- Optionally: a second bullet for the test or PR description change
```

Existing examples that comply:

- `RecordingService: thread RecordingHandle + IRecordingPipeline through pipeline lifecycle`
- `SqliteApplicationStore: switch dispatch to enum + pre-written CommandText`

These are bad:

- `RecordingService: tmp`
- `describe what changed here`
- `fix`

## Optional pre-push guard (commit-msg hook)

Drop this into `.git/hooks/commit-msg` (mode 0700) to enforce the forbidden
list above. Bypassing the hook is acceptable for hot-fix / squash merges —
simply amend on the next commit if it slips through.

```sh
#!/usr/bin/env bash
set -euo pipefail
SUBJECT="$(git log -1 --pretty=%s -F --no-merges 2>/dev/null || true)"
PATTERN='^(describe what changed here|wip|WIP|update|tmp|fix|untitled)\s*$'
if echo "${SUBJECT:-}" | grep -E "${PATTERN}" >/dev/null; then
  echo "commit-msg: rejected placeholder subject '${SUBJECT}'." >&2
  echo "  See docs/COMMIT-MSG-CONVENTIONS.md for the approved shape." >&2
  exit 1
fi
```

## Workflow rule

Before pushing a branch:

```
git log --oneline @{u}..  # last 3-N subjects you are pushing
```

Skim the last few — if any are placeholders, `git commit --amend` to fix the most
recent one before push.

## Why this exists

The codebase is the working memory for "what did we actually do" — when a
commit subject is generic, the diff + the conversation history is the only way
to recover intent, and that's brittle after a squash or a force-push. The hook
is a one-time setup that keeps the conversation short.
