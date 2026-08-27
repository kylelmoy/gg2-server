import sys, re, html, pathlib
for p in sys.argv[1:]:
    t = pathlib.Path(p).read_text(encoding='utf-8')
    print("===== %s =====" % p)
    for m in re.finditer(r'<argument kind="STRING">(.*?)</argument>', t, re.S):
        print(html.unescape(m.group(1)))
