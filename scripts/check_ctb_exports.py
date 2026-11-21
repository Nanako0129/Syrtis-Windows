#!/usr/bin/env python3
"""Check that include/ctb.h declares exactly the tb_* function names the crate exports.

Names only: parameter lists and return types are not compared.

Usage: check_ctb_exports.py [--self-test]
Exit 1 and print both differences when the sets differ.
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# Exported only behind `feature = "test-support"`; never in the shipping DLL or header.
TEST_SUPPORT = {"tb_test_set_cursor_sync"}

HEADER_RE = re.compile(r"^[A-Za-z_][\w\s\*]*?\b(tb_\w+)\s*\(", re.M)
# `#[no_mangle]` or the edition-2024 `#[unsafe(no_mangle)]`, then any further
# attributes or `///` doc lines before the item.
EXPORT_RE = re.compile(
    r'#\[(?:unsafe\()?no_mangle\)?\]\s*(?:(?:#\[[^\]]*\]|///[^\n]*)\s*)*'
    r'pub\s+(?:unsafe\s+)?extern\s+"C"\s+fn\s+(tb_\w+)'
)


def header_names(text):
    # One left-to-right pass, so a `/*` inside a `//` comment opens nothing.
    text = re.sub(r"//[^\n]*|/\*.*?\*/", "", text, flags=re.S)
    return set(HEADER_RE.findall(text))


def export_names(sources):
    return {n for text in sources for n in EXPORT_RE.findall(text)} - TEST_SUPPORT


def diff(header_text, sources):
    declared, exported = header_names(header_text), export_names(sources)
    return sorted(exported - declared), sorted(declared - exported)


def report(missing, extra):
    for n in missing:
        print(f"exported but not declared in ctb.h: {n}")
    for n in extra:
        print(f"declared in ctb.h but not exported: {n}")
    return 1 if missing or extra else 0


def self_test():
    header = "char *tb_a(void);\n// char *tb_commented(void);\nvoid tb_free(char *p);\n"
    src = [
        '#[no_mangle]\npub extern "C" fn tb_a() {}\n'
        '#[no_mangle]\npub unsafe extern "C" fn tb_free(p: *mut u8) {}\n'
        '#[cfg(feature = "test-support")]\n#[no_mangle]\npub unsafe extern "C" fn tb_test_set_cursor_sync() {}\n'
    ]
    assert diff(header, src) == ([], []), "matching pair must pass"
    assert diff(header.replace("void tb_free(char *p);", ""), src) == (["tb_free"], [])
    assert diff(header + "char *tb_ghost(void);\n", src) == ([], ["tb_ghost"])
    # Block comments are ignored; a `/*` inside a `//` line opens nothing.
    # The `/*` in the `//` line must not swallow tb_b up to the later `*/`.
    commented = header + "// see /* below\nchar *tb_b(void);\n/* char *tb_hidden(void); */\n"
    src_b = src + ['#[no_mangle]\npub extern "C" fn tb_b() {}\n']
    assert diff(commented, src_b) == ([], []), "comments must not hide or add names"
    # Return type on the line before the name.
    assert diff(header + "char *\ntb_c(void);\n", src) == ([], ["tb_c"])
    # Edition-2024 attribute and a doc line between attribute and item.
    src_u = src + ['#[unsafe(no_mangle)]\n/// doc\npub extern "C" fn tb_new() {}\n']
    assert diff(header, src_u) == (["tb_new"], [])
    print("self-test ok")
    return 0


def main():
    if sys.argv[1:] == ["--self-test"]:
        return self_test()
    if sys.argv[1:]:
        print(__doc__)
        return 2
    header = (ROOT / "include" / "ctb.h").read_text(encoding="utf-8")
    sources = [p.read_text(encoding="utf-8") for p in sorted((ROOT / "crates" / "tb_core_ffi" / "src").rglob("*.rs"))]
    missing, extra = diff(header, sources)
    rc = report(missing, extra)
    if rc == 0:
        print(f"ctb.h matches the exports ({len(header_names(header))} functions)")
    return rc


if __name__ == "__main__":
    sys.exit(main())
