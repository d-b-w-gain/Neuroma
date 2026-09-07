# Glyph DCT preview generator

This Windows-only development tool converts the illuminated PNG source assets
to genuine colour terminal glyph art. It rasterizes the supported single-cell
characters from printable ASCII plus the Unicode arrows, technical, box-drawing,
block, geometric, and Braille ranges in the supplied monospaced font. Missing-
glyph fallback boxes are discarded. Every surviving glyph is reduced to an
8×16 coverage patch and transformed with the complete orthonormal 2D DCT.

Each identically sized source patch is transformed and compared with every
glyph. For each candidate, the optimal foreground RGB value on black is solved
by least squares. The glyph and colour with the smallest complete-coefficient
residual are written to `.txt`, `.ans`, and `.png` previews.

```powershell
dotnet run --project tools\GlyphDctPreview -- `
  "C:\path\to\CascadiaMono.ttf" artifacts\ascii-previews\dct 28 14 `
  assets\dropcaps\celtic-a.png
```

The source artwork may contain a baked near-white checkerboard, so the generator
removes that matte before matching. This preprocessing does not select or rank
glyphs. The chosen symbol is still the direct least-error DCT match; no hand-
authored character ramp or density guess is involved.
