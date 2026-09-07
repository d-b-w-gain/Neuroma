# Glyph DCT preview generator

This Windows-only development tool converts the illuminated PNG source assets
to genuine colour ASCII. It rasterizes all 95 printable ASCII characters from
the supplied monospaced font, reduces each glyph to an 8×16 coverage patch, and
computes the complete orthonormal two-dimensional DCT.

Each identically sized source patch is transformed and compared with every
glyph. For each candidate, the optimal foreground RGB value on black is solved
by least squares. The glyph and colour with the smallest complete-coefficient
residual are written to `.txt`, `.ans`, and `.png` previews.

```powershell
dotnet run --project tools\GlyphDctPreview -- `
  "C:\path\to\CascadiaMono.ttf" artifacts\ascii-previews\dct 28 14 `
  assets\dropcaps\celtic-a.png
```

The source artwork currently contains a baked near-white checkerboard, so the
generator removes that matte before matching. This preprocessing does not
select or rank glyphs.
