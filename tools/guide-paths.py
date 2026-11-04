#!/usr/bin/env python3
"""The game's own click paths (the tutorial framework, NodeHoleMask: m_FormName + m_Path) -> tests/fixtures/guide-paths.txt (form TAB path).
They are ground truth for widget paths: tests/GuidePaths.cs checks the UI graph and the recipes against them.
Usage: tools/guide-paths.py [~/wardsage/work/lua/src/Guide/gen] (the decompiled Lua of the game; not in the repo)."""
import glob, os, re, sys
src = sys.argv[1] if len(sys.argv) > 1 else os.path.expanduser('~/wardsage/work/lua/src/Guide/gen')
pairs = set()
for f in glob.glob(os.path.join(src, '*.lua')):
    s = open(f, encoding='utf8', errors='ignore').read()
    for m in re.finditer(r'(\w+)\.m_Path = "([^"]*)"', s):
        fn = re.findall(m.group(1) + r'\.m_FormName = "([^"]*)"', s)
        if m.group(2) and fn: pairs.add((fn[0], m.group(2)))
out = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'tests', 'fixtures', 'guide-paths.txt')
with open(out, 'w', encoding='utf8', newline='\n') as w:
    for a, b in sorted(pairs): w.write(a + '\t' + b + '\n')
print(len(pairs), 'paths ->', out)
