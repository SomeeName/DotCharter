"""Exact text replacements from a JSON spec, keeping each file's line endings.

Why this exists: multi-line edits pushed through a shell heredoc lost their
backslashes several times (regexes, Windows paths, C# escapes), and a hand
fixed `sed -i` turned CRLF files into whole-file diffs. Write the spec with the
agent's file tool, so no shell ever sees the text, and run:

    python tools/agent/Replace-InFile.py <spec.json> [--root <dir>] [--dry-run]
    python tools/agent/Replace-InFile.py --self-test

The spec is a JSON list of edits:

    [{"file": "DotCharter.UI/Foo.cs", "old": "a\\nb", "new": "a\\nc", "count": 1}]

- `file` is relative to --root (default: the current directory).
- `old` / `new` are written with "\\n" line breaks; each is converted to the
  file's own line ending (CRLF or LF) before matching.
- `count` (default 1) is how many matches the file must hold; any other number
  fails the whole spec. Nothing is written unless EVERY edit matches.
- A UTF-8 BOM is kept if the file had one.
"""
import json
import os
import sys
import tempfile


def _read(path):
    raw = open(path, "rb").read()
    bom = raw.startswith(b"\xef\xbb\xbf")
    text = raw[3:].decode("utf-8") if bom else raw.decode("utf-8")
    newline = "\r\n" if "\r\n" in text else "\n"
    return text, bom, newline


def _write(path, text, bom):
    data = text.encode("utf-8")
    with open(path, "wb") as f:
        f.write((b"\xef\xbb\xbf" if bom else b"") + data)


def apply(spec, root, dry_run=False):
    """Returns a list of (file, replacements). Raises ValueError on any mismatch."""
    pending = {}
    for i, edit in enumerate(spec):
        rel = edit["file"]
        path = os.path.join(root, rel)
        if path not in pending:
            pending[path] = list(_read(path))
        text, bom, newline = pending[path]
        old = edit["old"].replace("\r\n", "\n").replace("\n", newline)
        new = edit["new"].replace("\r\n", "\n").replace("\n", newline)
        want = int(edit.get("count", 1))
        found = text.count(old)
        if found != want:
            preview = edit["old"][:80].replace("\n", "\\n")
            raise ValueError(f"edit {i} in {rel}: expected {want} match(es), found {found}: '{preview}'")
        pending[path][0] = text.replace(old, new)

    done = []
    for path, (text, bom, _) in pending.items():
        if not dry_run:
            _write(path, text, bom)
        done.append(os.path.relpath(path, root))
    return done


def self_test():
    with tempfile.TemporaryDirectory() as d:
        crlf = os.path.join(d, "crlf.cs")
        lf = os.path.join(d, "lf.cs")
        bom = os.path.join(d, "bom.axaml")
        open(crlf, "wb").write(b"a\r\nb \\d+\r\nc\r\n")
        open(lf, "wb").write(b"x\ny\n")
        open(bom, "wb").write(b"\xef\xbb\xbf<A/>\n")

        apply([{"file": "crlf.cs", "old": "b \\d+\nc", "new": "B \\w+\nC"},
               {"file": "lf.cs", "old": "y", "new": "Y"},
               {"file": "bom.axaml", "old": "<A/>", "new": "<B/>"}], d)
        assert open(crlf, "rb").read() == b"a\r\nB \\w+\r\nC\r\n", "CRLF or backslashes not kept"
        assert open(lf, "rb").read() == b"x\nY\n", "LF file changed endings"
        assert open(bom, "rb").read() == b"\xef\xbb\xbf<B/>\n", "BOM lost"

        before = open(lf, "rb").read()
        try:
            apply([{"file": "lf.cs", "old": "x", "new": "X"},
                   {"file": "lf.cs", "old": "missing", "new": "?"}], d)
            raise AssertionError("a missing match must fail")
        except ValueError:
            pass
        assert open(lf, "rb").read() == before, "a failed spec must write nothing"

        try:
            apply([{"file": "crlf.cs", "old": "a", "new": "z", "count": 2}], d)
            raise AssertionError("a wrong count must fail")
        except ValueError:
            pass
    print("self-test OK")


def main(argv):
    if "--self-test" in argv:
        self_test()
        return 0
    args = [a for a in argv if not a.startswith("--")]
    if not args:
        print(__doc__)
        return 2
    root = os.getcwd()
    if "--root" in argv:
        root = argv[argv.index("--root") + 1]
        args = [a for a in args if a != root]
    spec = json.load(open(args[0], encoding="utf-8"))
    try:
        done = apply(spec, root, dry_run="--dry-run" in argv)
    except ValueError as e:
        print(f"NOTHING WRITTEN: {e}", file=sys.stderr)
        return 1
    verb = "would change" if "--dry-run" in argv else "changed"
    for f in done:
        print(f"{verb} {f}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
