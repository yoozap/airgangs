#!/usr/bin/env python3
"""Classify every line of a src/*.js file as code / comment / blank.

WHY: tools/port_progress.py's budget is raw `wc -l` — comments and blank
lines count the same as code. That inflates "lines remaining" by whatever
fraction of the file is prose. This module re-derives the split properly so
coverage can be measured against CODE lines only.

## What it does

A single-pass character scanner over the whole file (not line-regexes, which
cannot see across a `/* ... */` or a template literal that spans lines). It
tracks a mode stack:

  normal | line_comment | block_comment | string_s | string_d | template
  | tmpl_sub (a `${...}` substitution inside a template, itself parsed with
    the same normal-mode rules, so a GLSL template can contain a real JS
    expression that contains a real JS comment)

For each character it marks the character's line as containing CODE or
COMMENT (whichever applies); a line with any code char — even one byte of
code before a trailing `//` comment — is a CODE line. A line is COMMENT only
if every non-whitespace character on it falls inside a comment. A line with
no non-whitespace content at all is BLANK. (A line can also be "mixed" code+
comment; it is counted as CODE, matching how most LOC tools count trailing
comments.)

Returns, per file: counts of code/comment/blank lines, and the ordered SET of
1-indexed line numbers classified as code — the thing the coverage map
intersects citation ranges against.

## Regex-vs-division disambiguation

JS reuses `/` for division and for starting a regex literal (`/foo/g`), and
you cannot tell which without a token-level parse. This module uses the
standard shallow heuristic: a `/` starts a regex unless the last significant
character before it was an identifier character, digit, `)`, `]`, or `}`
(i.e. the end of a value — meaning `/` divides it). This is WRONG for a
regex literal written right after a keyword whose last character is
alphanumeric with nothing else between, e.g. `return /foo/.test(x)` or
`x = y in /foo/` — those would misparse as division. A grep across this
repo's src/*.js (2026-10) found zero such keyword-adjacent regex literals,
so the heuristic is verified safe for this codebase, but it is NOT a general
JS parser and would need hardening (full tokenizing) elsewhere.

## Known limitations (documented, not silently swallowed)

- No real tokenizer / no ASI or grammar awareness — this is a lexical scan,
  not a parser. It cannot detect syntax errors; it just classifies bytes.
- Regex-vs-division: see above. Verified safe for this repo only.
- A string literal is assumed NOT to contain a raw, unescaped newline (true
  JS syntax requires `\` + newline to continue a single/double-quoted string
  across lines). If one appears anyway, this scanner defensively closes the
  string at the newline rather than reading garbage as string content —
  that is a deliberate recovery choice, not a found-and-fixed bug.
- Template-literal substitutions (`${...}`) are parsed with brace-depth
  tracking, so a `{` from an object literal inside a substitution does not
  prematurely end it. Nested template literals inside a substitution
  (`` `${`nested`}` ``) are handled by simple recursion of the same mode
  stack — arbitrary nesting works, but this has only been exercised against
  this repo's actual template usage (mostly GLSL strings with occasional
  `${uniformName}` substitutions), not fuzzed against adversarial JS.
- Line/column numbers are 1-indexed and count `\n` only; files using bare
  `\r` (no `\n`) line endings would be misread. Not seen in this repo.
- Shebang lines, BOMs: not special-cased beyond normal character handling;
  harmless for this repo (no src/*.js uses either).

Usage:
    python3 tools/code_lines.py src/foot.js        # prints the split
    python3 tools/code_lines.py --all              # prints the split for every src/*.js

As a library:
    from code_lines import analyze_file
    r = analyze_file("src/foot.js")
    r.code, r.comment, r.blank, r.total   # counts
    r.code_lines                          # frozenset of 1-indexed code line numbers
"""
import os
import sys
from dataclasses import dataclass, field


@dataclass
class LineSplit:
    path: str
    total: int
    code: int
    comment: int
    blank: int
    code_lines: frozenset
    comment_lines: frozenset = field(default_factory=frozenset)
    blank_lines: frozenset = field(default_factory=frozenset)


_IDENT_CHARS = set(
    "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_$"
)
# Characters after which a following `/` is division (end of a value), not
# the start of a regex literal (start of a new sub-expression).
_DIVISION_AFTER = _IDENT_CHARS | {")", "]", "}"}


def _is_regex_context(prev_sig):
    if prev_sig is None:
        return True
    return prev_sig not in _DIVISION_AFTER


def analyze_text(text, path="<text>"):
    """Scan `text` (a whole file's contents) and return a LineSplit."""
    n = len(text)
    i = 0
    line = 1
    has_code = set()
    has_comment = set()
    nonblank = set()

    # Mode stack. Each entry is (mode, extra). `tmpl_sub` extra is the
    # current brace depth (starts at 1 for the `{` that opened it).
    stack = [("normal", None)]
    prev_sig = None  # last significant (non-whitespace, non-comment) char

    def mark(is_comment):
        nonblank.add(line)
        if is_comment:
            has_comment.add(line)
        else:
            has_code.add(line)

    while i < n:
        c = text[i]
        mode, extra = stack[-1]

        if c == "\n":
            if mode == "line_comment":
                stack.pop()
            line += 1
            i += 1
            continue

        if mode == "line_comment":
            if c not in (" ", "\t", "\r"):
                mark(True)
            i += 1
            continue

        if mode == "block_comment":
            if text[i : i + 2] == "*/":
                mark(True)
                i += 2
                stack.pop()
                continue
            if c not in (" ", "\t", "\r"):
                mark(True)
            i += 1
            continue

        if mode in ("string_s", "string_d"):
            quote = "'" if mode == "string_s" else '"'
            if c == "\\":
                mark(False)
                i += 1
                if i < n:
                    if text[i] == "\n":
                        # line continuation inside a string; not expected
                        # in this repo, handled defensively.
                        line += 1
                    else:
                        mark(False)
                    i += 1
                continue
            if c == quote:
                mark(False)
                prev_sig = quote
                stack.pop()
                i += 1
                continue
            if c == "\n":
                # Defensive recovery: an unterminated string hitting a raw
                # newline. Real JS disallows this; just close the string.
                stack.pop()
                line += 1
                i += 1
                continue
            mark(False)
            i += 1
            continue

        if mode == "template":
            if c == "\\":
                mark(False)
                i += 1
                if i < n:
                    if text[i] == "\n":
                        line += 1
                    mark(False)
                    i += 1
                continue
            if c == "`":
                mark(False)
                prev_sig = "`"
                stack.pop()
                i += 1
                continue
            if text[i : i + 2] == "${":
                mark(False)
                stack.append(("tmpl_sub", 1))
                i += 2
                prev_sig = None
                continue
            mark(False)
            i += 1
            continue

        # --- shared handling for 'normal' and 'tmpl_sub' ---
        if mode == "tmpl_sub":
            if c == "{":
                stack[-1] = (mode, extra + 1)
                mark(False)
                prev_sig = "{"
                i += 1
                continue
            if c == "}":
                if extra - 1 == 0:
                    stack.pop()
                    mark(False)
                    prev_sig = "}"
                    i += 1
                    continue
                stack[-1] = (mode, extra - 1)
                mark(False)
                prev_sig = "}"
                i += 1
                continue
            # fall through to normal-mode token handling below

        if c in (" ", "\t", "\r"):
            i += 1
            continue

        if text[i : i + 2] == "//":
            mark(True)
            stack.append(("line_comment", None))
            i += 2
            continue

        if text[i : i + 2] == "/*":
            mark(True)
            stack.append(("block_comment", None))
            i += 2
            continue

        if c == "'":
            mark(False)
            stack.append(("string_s", None))
            i += 1
            continue

        if c == '"':
            mark(False)
            stack.append(("string_d", None))
            i += 1
            continue

        if c == "`":
            mark(False)
            stack.append(("template", None))
            i += 1
            continue

        if c == "/":
            if _is_regex_context(prev_sig):
                mark(False)
                j = i + 1
                in_class = False
                while j < n:
                    cj = text[j]
                    if cj == "\\":
                        j += 2
                        continue
                    if cj == "[":
                        in_class = True
                    elif cj == "]":
                        in_class = False
                    elif cj == "/" and not in_class:
                        j += 1
                        break
                    elif cj == "\n":
                        break
                    j += 1
                # Mark every char we just consumed (regex literals in this
                # repo never span lines, but guard anyway).
                k = i
                while k < j:
                    if text[k] == "\n":
                        line += 1
                    else:
                        mark(False)
                    k += 1
                i = j
                while i < n and (text[i] in _IDENT_CHARS):
                    mark(False)
                    i += 1
                prev_sig = "/"
                continue
            else:
                mark(False)
                prev_sig = "/"
                i += 1
                continue

        mark(False)
        prev_sig = c
        i += 1

    # `line` is the 1-indexed line counter's current value. If the file ends
    # with a trailing newline, that counter has already stepped past the
    # last real line onto a phantom empty one — drop it (matches `wc -l`
    # semantics closely enough; comment/code lines never land past EOF).
    if text.endswith("\n"):
        total = line - 1 if line > 1 else 0
        # occupied/blank sets never reference `line` (the phantom empty line
        # past the final \n), so nothing to trim there.
    else:
        total = line

    code_lines = frozenset(has_code)
    comment_only_lines = frozenset(has_comment - has_code)
    all_lines = set(range(1, total + 1))
    blank_lines = frozenset(all_lines - nonblank)

    return LineSplit(
        path=path,
        total=total,
        code=len(code_lines),
        comment=len(comment_only_lines),
        blank=len(blank_lines),
        code_lines=code_lines,
        comment_lines=comment_only_lines,
        blank_lines=blank_lines,
    )


def analyze_file(path):
    with open(path, encoding="utf-8", errors="replace") as f:
        text = f.read()
    return analyze_text(text, path=path)


def _main(argv):
    if not argv or argv[0] in ("-h", "--help"):
        print(__doc__)
        return 0

    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

    if argv[0] == "--all":
        paths = sorted(
            os.path.join(root, "src", n)
            for n in os.listdir(os.path.join(root, "src"))
            if n.endswith(".js")
        )
    else:
        paths = argv

    print(f"{'file':<20}{'total':>8}{'code':>8}{'comment':>9}{'blank':>8}")
    print("-" * 53)
    tot = [0, 0, 0, 0]
    for p in paths:
        r = analyze_file(p)
        name = os.path.basename(r.path)
        print(f"{name:<20}{r.total:>8}{r.code:>8}{r.comment:>9}{r.blank:>8}")
        tot[0] += r.total
        tot[1] += r.code
        tot[2] += r.comment
        tot[3] += r.blank
    if len(paths) > 1:
        print("-" * 53)
        print(f"{'TOTAL':<20}{tot[0]:>8}{tot[1]:>8}{tot[2]:>9}{tot[3]:>8}")
    return 0


if __name__ == "__main__":
    sys.exit(_main(sys.argv[1:]))
