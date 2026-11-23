"""Writer/reader census of reference GML variables (docs/PARITY.md, "The variable census").

Lists every site in the reference that writes or reads a variable, across
Scripts/**/*.gml and the XML-escaped event code under Objects/ (which grep
misses). Draw events are listed separately: they never feed the simulation,
but a reader there is how you tell decoration from state.

    python tools/refvars.py cloak humiliated            # sites for each name
    python tools/refvars.py --object Character --declared
                                                         # names an object's Create assigns
    python tools/refvars.py --summary cloak humiliated  # counts only

The reference root is $GG2_REFERENCE/Source/gg2, else the tests' cached clone
under %LOCALAPPDATA%/gg2-server/reference/<commit>/Source/gg2.
"""
import argparse, html, os, pathlib, re, sys

def reference_root():
    env = os.environ.get("GG2_REFERENCE")
    if env:
        return pathlib.Path(env) / "Source" / "gg2"
    base = pathlib.Path(os.environ.get("LOCALAPPDATA", os.path.expanduser("~/.local/share"))) / "gg2-server" / "reference"
    clones = sorted(base.glob("*/Source/gg2"))
    if not clones:
        sys.exit("no reference: set GG2_REFERENCE or run the suite once to clone it")
    return clones[-1]

EVENT_NAMES = {"CREATE": "Create", "DESTROY": "Destroy", "STEP": "Step", "ALARM": "Alarm",
               "COLLISION": "Collision", "DRAW": "Draw", "OTHER": "Other", "KEYBOARD": "Key",
               "MOUSE": "Mouse", "KEYPRESS": "KeyPress", "KEYRELEASE": "KeyRelease", "TRIGGER": "Trigger"}

def blocks(root):
    """Yield (label, is_draw, code) for every script and every event code action."""
    for p in sorted((root / "Scripts").rglob("*.gml")):
        yield str(p.relative_to(root)).replace("\\", "/"), False, p.read_text(encoding="utf-8", errors="replace")
    for p in sorted((root / "Objects").rglob("*.events/*.xml")):
        t = p.read_text(encoding="utf-8", errors="replace")
        m = re.search(r'<event category="(\w+)"', t)
        cat = m.group(1) if m else "?"
        # Only CODE actions; a comment action's STRING is prose, not code.
        codes = re.findall(r'<kind>CODE</kind>.*?<argument kind="STRING">(.*?)</argument>', t, re.S)
        label = str(p.relative_to(root)).replace("\\", "/")
        for i, c in enumerate(codes):
            yield (label if len(codes) == 1 else f"{label}#{i}"), cat == "DRAW", html.unescape(c)

def strip_comments(line):
    # Drop // comments outside strings; GML strings use " or '.
    out, q = [], None
    i = 0
    while i < len(line):
        ch = line[i]
        if q:
            out.append(ch)
            if ch == q:
                q = None
        elif ch in "\"'":
            q = ch; out.append(ch)
        elif line.startswith("//", i):
            break
        else:
            out.append(ch)
        i += 1
    return "".join(out)

def uncomment(code):
    code = re.sub(r"/\*.*?\*/", lambda m: "\n" * m.group(0).count("\n"), code, flags=re.S)
    return [strip_comments(l) for l in code.split("\n")]

def classify(line, name):
    """'write', 'read' or None for one line and one bare variable name."""
    pat = re.compile(r"(?<![\w$])" + re.escape(name) + r"(?!\w)")
    if not pat.search(line):
        return None
    w = re.compile(r"(?<![\w$])" + re.escape(name) + r"\s*(\[[^\]]*\])?\s*([-+*/|&^]?=)(?!=)")
    for m in w.finditer(line):
        # GML's `=` is a comparison inside a condition. Call it a write only
        # when no paren is open and the token before the (possibly dotted)
        # name ends a statement or a condition: `with Player if team != win
        # humiliated = 1` writes, `if hp = 0` and `if (a = b)` read.
        before = re.sub(r"[\w$\]\)\[.]*\.$", "", line[:m.start()]).rstrip()
        if before.count("(") > before.count(")"):
            continue
        prev = re.search(r"(\w+|\S)$", before)
        tok = prev.group(1) if prev else ""
        if tok in ("if", "while", "until", "and", "or", "not", "xor", "return", "&&", "||", "!", "=", "<", ">", "+", "-", "*", "/", ",", "?", ":", "|", "&", "^", "~"):
            continue
        return "write"
    return "read"

def census(root, names):
    res = {n: [] for n in names}
    for label, is_draw, code in blocks(root):
        for ln, line in enumerate(uncomment(code), 1):
            for n in names:
                k = classify(line, n)
                if k:
                    res[n].append((k, is_draw, label, ln, line.strip()))
    return res

def declared(root, obj):
    hits = list((root / "Objects").rglob(f"{obj}.events/Create.xml"))
    if not hits:
        sys.exit(f"no Create event for {obj}")
    names = []
    for _, _, code in ((None, None, html.unescape(c)) for c in re.findall(r'<argument kind="STRING">(.*?)</argument>', hits[0].read_text(encoding="utf-8"), re.S)):
        for line in uncomment(code):
            for m in re.finditer(r"(?:^|;)\s*([A-Za-z_]\w*)\s*(\[[^\]]*\])?\s*=(?!=)", line):
                if m.group(1) not in names and m.group(1) not in ("var", "global", "i", "j"):
                    names.append(m.group(1))
    return names

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("names", nargs="*")
    ap.add_argument("--object")
    ap.add_argument("--declared", action="store_true", help="print the names OBJECT's Create assigns")
    ap.add_argument("--summary", action="store_true")
    ap.add_argument("--no-draw", action="store_true", help="omit Draw-event sites")
    ap.add_argument("--owner", action="append", default=[],
                    help="object name or path prefix whose own events are omitted; repeatable. "
                         "What is left is the cross-object sites, the ones porting the owner never reads")
    a = ap.parse_args()
    root = reference_root()
    names = list(a.names)
    if a.declared:
        names = declared(root, a.object)
        if not a.summary:
            print("\n".join(names)); return
    def own(label):
        return any(label.startswith(o) or f"/{o}.events/" in label for o in a.owner)
    for n, sites in census(root, names).items():
        sites = [s for s in sites if not own(s[2])]
        w = [s for s in sites if s[0] == "write" and not s[1]]
        r = [s for s in sites if s[0] == "read" and not s[1]]
        d = [s for s in sites if s[1]]
        if a.summary:
            print(f"{n:28} writes {len(w):3}  reads {len(r):3}  draw {len(d):3}")
            continue
        print(f"== {n}: {len(w)} writes, {len(r)} reads, {len(d)} in Draw")
        for group, rows in (("W", w), ("R", r), ("D", [] if a.no_draw else d)):
            for _, _, label, ln, text in rows:
                print(f"  {group} {label}:{ln}  {text[:140]}")

if __name__ == "__main__":
    main()
