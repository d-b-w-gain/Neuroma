# Neuroma

Neuroma is a fast, distraction-free EPUB reader that runs in the terminal. It
is the EPUB sibling of NeuroMD and keeps the same local, read-only philosophy.

## Features

- EPUB 2 and EPUB 3 reading order and table of contents
- Reflowing XHTML terminal rendering: headings, paragraphs, lists, quotes, code, and images
- Two-row terminal drop caps for EPUB small-cap scene openings
- Gentle semantic colour for dialogue, emphasis, links, code, and quotations
- Optional paragraph-focus colour mode with no character-attribution guesses
- Chapter/page navigation and whole-book search
- Automatic reading-position persistence
- Metadata view and plain-text output for piping
- Kokoro read-aloud from the current page with timed word highlighting
- One-segment Kokoro prefetching for continuous playback between sections
- Helpful malformed-file and DRM errors
- Embedded Neuroma application icon for Windows shortcuts and file associations
- No runtime package dependencies

## Use

```powershell
.\dist\Neuroma.exe "C:\Books\book.epub"
```

Run `Register-Neuroma.cmd` after building to add Neuroma to Windows' **Open
with** list for `.epub` files. This is per-user and needs no administrator access.

| Key | Action |
| --- | --- |
| `j` / `k`, arrows | Scroll |
| Space, Page Down / Page Up | Change page |
| `h` / `l`, left / right | Change chapter |
| `g` / `G` | Chapter start / end |
| `t` | Table of contents |
| `/`, then `n` / `N` | Search; next / previous result |
| `s` | Start or stop Kokoro narration |
| `c` | Cycle Gentle, Focus, and Plain colour modes |
| `i` / `?` | Book information / help |
| `F11` | Maximize or restore the terminal window |
| `q`, Escape | Quit |

Neuroma uses a low-glare, black-background colour scheme with no solid colour
bars. Windows Terminal can also enter its native fullscreen mode with `F11` or
`Alt+Enter`, depending on its configured shortcuts.
The header and footer use darker neutral greys so the book remains the clearest
and brightest layer on screen.

Gentle colour mode is enabled by default. It gives quoted speech a muted sage
foreground while leaving dialogue attribution as neutral prose, and preserves
EPUB emphasis, strong text, code, links, blockquotes, headings, and scene
dividers as restrained semantic accents. Focus mode keeps the paragraph near
the reading position—or the paragraph currently spoken by Kokoro—bright while
dimming surrounding prose. Plain mode restores the original 16-colour display.
Set the standard `NO_COLOR` environment variable to start in Plain mode.

Use `Neuroma --plain book.epub` for non-interactive output.

## Read aloud with Kokoro

Press `s` to read from the first visible line through the rest of the book, and
press `s` again to stop. Neuroma uses Kokoro's captioned-speech endpoint when it
is available, highlighting each spoken word and following narration into the
next chapter. It automatically falls back to `/v1/audio/speech` with estimated
word timing on older Kokoro servers. While one segment plays, Neuroma generates
and buffers the next segment and its timestamps to minimize pauses.

Copy `neuroma.example.json` to `neuroma.json` beside `Neuroma.exe` (or keep it
in the working directory) and set the same endpoint, voice, and speed used by
NeuroMD:

```json
{
  "kokoroUrl": "http://127.0.0.1:8880",
  "voice": "af_bella",
  "speed": 1.0
}
```

You can override these per launch with `--kokoro-url URL`, `--voice NAME`, and
`--speed RATE`. The supported speed range is `0.25` to `4`.

## Build

Install the .NET 8 SDK, then run:

```powershell
.\build.ps1
```

The script runs the dependency-free test harness and publishes a self-contained,
single-file Windows x64 executable to `dist\Neuroma.exe`.

Neuroma supports standard reflowable EPUB 2/3. PNG, JPEG, GIF, BMP, and WebP
illustrations are resized to the available page and rendered as true-colour
terminal cells. Unsupported images retain a descriptive text fallback. Fixed-layout
pages and complex tables become readable text. DRM is intentionally unsupported.

Image decoding and terminal-cell rendering use
[Spectre.Console.ImageSharp](https://spectreconsole.net/console/widgets/canvas-image/).
