# Neuroma

Neuroma is a fast, distraction-free EPUB reader that runs in the terminal. It
is the EPUB sibling of NeuroMD and keeps the same local, read-only philosophy.

## Features

- EPUB 2 and EPUB 3 reading order and table of contents
- Reflowing XHTML terminal rendering: headings, paragraphs, lists, quotes, and code
- Chapter/page navigation and whole-book search
- Automatic reading-position persistence
- Metadata view and plain-text output for piping
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
| `i` / `?` | Book information / help |
| `F11` | Maximize or restore the terminal window |
| `q`, Escape | Quit |

Neuroma uses a low-glare, black-background colour scheme with no solid colour
bars. Windows Terminal can also enter its native fullscreen mode with `F11` or
`Alt+Enter`, depending on its configured shortcuts.

Use `Neuroma --plain book.epub` for non-interactive output.

## Build

Install the .NET 8 SDK, then run:

```powershell
.\build.ps1
```

The script runs the dependency-free test harness and publishes a self-contained,
single-file Windows x64 executable to `dist\Neuroma.exe`.

Neuroma supports standard reflowable EPUB 2/3. Fixed-layout pages and complex
tables become readable text; images appear as descriptive placeholders. DRM is
intentionally unsupported.
