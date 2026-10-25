# Keeping these docs current

Moved verbatim from the root `AGENTS.md`, which keeps the one-line rule.

Project context lives in the codebase, not in an agent's context window. **When
you change a subsystem in a way that would make its guide misleading — a moved
entry point, a changed invariant, a new gotcha — update that folder's `AGENTS.md`
in the same change.** Terse bullets with real file/type names; current facts, not
history. The `CLAUDE.md` files are one-line `@AGENTS.md` imports — never put
content in them.

**A SAFETY claim in a guide is the dangerous kind of rot, because a reader stops
looking.** Any sentence shaped *"X is bounded / validated / enforced by test T"*,
*"X is the ONLY place that does Y"* or *"X is missing"* must be re-verified
against code whenever you touch that area. **A "this is missing" note is the
worst of them** — it makes the next agent delete working code.

Mechanical backstops:

- `Infrastructure/DocReferenceLintTests` fails the gate on a dead file path, a
  dead `*Tests` name or a dead relative link in any `AGENTS.md` or
  `docs/agents/**`. Its allow-list exists for guides that name deleted things on
  purpose — **adding a name there to turn the build green, without checking the
  doc's claim, defeats the test.**
- `Infrastructure/SourceFileSizeBudgetTests` keeps files from silently growing
  back into god files.
- `Infrastructure/MethodComplexityBudgetTests` does the same for methods.
- Nothing mechanical covers the numbers or the prose. Re-measure; don't carry.
