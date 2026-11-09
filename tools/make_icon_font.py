import re, os
from fontTools.fontBuilder import FontBuilder
from fontTools.pens.ttGlyphPen import TTGlyphPen

# Generates src/Perch/Assets/Fonts/PerchIcons.ttf (needs fontTools): one glyph, the dinosaur, at U+F0000. Perch looks icons up there when Segoe Fluent Icons has none.
# the dinosaur's silhouette from the generated collision mask (one cell per game pixel)
src = open(r'D:\Projects\Perch\src\Perch\Services\Games\DinoMasks.cs', encoding='utf-8').read()
m = re.search(r'\["DinoRun1"\] = \((\d+), (\d+), "([0-9a-f]+)"\)', src)
w, h, hexbits = int(m.group(1)), int(m.group(2)), m.group(3)
bits = []
for ch in hexbits:
    v = int(ch, 16)
    bits += [(v >> 3) & 1, (v >> 2) & 1, (v >> 1) & 1, v & 1]
bits = bits[:w * h]
rows = [bits[y * w:(y + 1) * w] for y in range(h)]
# a 16-row pixel icon: at the 17 px icon size one cell is about one screen pixel, so the shape stays bright and crisp
H2 = 24
W2 = round(w * H2 / h)
src_rows, sw, sh = rows, w, h
rows = []
for Y in range(H2):
    y0, y1 = Y * sh / H2, (Y + 1) * sh / H2
    row = []
    for X in range(W2):
        x0, x1 = X * sw / W2, (X + 1) * sw / W2
        filled = total = 0.0
        for y in range(int(y0), min(sh, int(y1) + 1)):
            for x in range(int(x0), min(sw, int(x1) + 1)):
                cover = (min(y + 1, y1) - max(y, y0)) * (min(x + 1, x1) - max(x, x0))
                if cover <= 0: continue
                total += cover; filled += cover * src_rows[y][x]
        row.append(1 if total and filled / total >= 0.30 else 0)
    rows.append(row)
w, h = W2, H2

# a little thicker than the one-pixel outline so the shape stays readable at 17 px: also fill cells touching a filled cell horizontally
unit = 960 / h              # font units per cell: the glyph fills nearly the whole 1000 unit em, as it must to look as large as the line icons
left = (1000 - w * unit) / 2
base = 10                   # feet just above the baseline

pen = TTGlyphPen(None)
for y, row in enumerate(rows):
    x = 0
    while x < w:
        if row[x]:
            x0 = x
            while x < w and row[x]: x += 1
            # the cell run as one rectangle (clockwise, TrueType's filled direction); y grows upward in a font
            top = base + (h - y) * unit
            bottom = top - unit
            l = left + x0 * unit
            r = left + x * unit
            pen.moveTo((round(l), round(bottom)))
            pen.lineTo((round(l), round(top)))
            pen.lineTo((round(r), round(top)))
            pen.lineTo((round(r), round(bottom)))
            pen.closePath()
        else:
            x += 1
glyph = pen.glyph()

fb = FontBuilder(1000, isTTF=True)
fb.setupGlyphOrder(['.notdef', 'dino'])
fb.setupCharacterMap({0xF0000: 'dino'})
empty = TTGlyphPen(None).glyph()
fb.setupGlyf({'.notdef': empty, 'dino': glyph})
fb.setupHorizontalMetrics({'.notdef': (1000, 0), 'dino': (1000, 0)})
fb.setupHorizontalHeader(ascent=900, descent=-200)
fb.setupNameTable({'familyName': 'Perch Icons', 'styleName': 'Regular'})
fb.setupOS2(sTypoAscender=900, sTypoDescender=-200, usWinAscent=900, usWinDescent=200)
fb.setupPost()
os.makedirs(r'D:\Projects\Perch\src\Perch\Assets\Fonts', exist_ok=True)
fb.save(r'D:\Projects\Perch\src\Perch\Assets\Fonts\PerchIcons.ttf')
print('font written', os.path.getsize(r'D:\Projects\Perch\src\Perch\Assets\Fonts\PerchIcons.ttf'), 'bytes;', sum(1 for r in rows for c in r if c), 'filled cells')
