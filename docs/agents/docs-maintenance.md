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

## Writing style for user docs

Applies to `README.md` and `docs/guide/` (and the callout texts in `docs/guide/manifests/*.json`,
which generate the numbered lists in the guide pages: edit the manifest, then the page).
Readers are guitar and bass players and chart makers, many not native English speakers.

- Main point first in every section, details after. Answer what this is, what to do, what happens.
- Short sentences, under 20 to 25 words, one idea each. Active voice, present tense, "you" for the reader.
- Everyday words. Explain a technical term the first time it appears, in one short clause.
- Full sentences, not telegraph style. Tables and lists may stay short.
- Say why when the reader would wonder. Numbered steps for procedures, one action per step.
- One name per thing throughout. Contractions are fine. American spelling in prose.
- Never change code, setting names, file names or menu labels.

Sound ordinary, not polished. Avoid clauses glued with colons or semicolons, parenthetical asides in
running prose, em dashes, bold lead-ins on every bullet, "In short:", "Note that", "seamlessly", "out of
the box", "under the hood", reflexive "X, not Y" contrasts, rule-of-three lists and saying the same
thing twice. Don't expand sentences that are already clear.

A rewrite must not change facts (numbers, versions, defaults, shortcuts, menu paths, names, commands,
URLs), code blocks, inline code, link targets or heading text. Compare the file with `main` before
opening the PR: inline code spans, fenced blocks, link targets and headings must match.
