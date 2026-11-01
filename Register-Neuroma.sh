#!/bin/sh
# Add Neuroma to the "Open with" list for EPUB files on Linux desktops
# (GNOME, KDE, XFCE, ...). Per-user; no administrator access needed.
set -eu

script_directory=$(CDPATH= cd "$(dirname "$0")" && pwd)
executable="$script_directory/Neuroma"
if [ ! -x "$executable" ]; then
    if [ -x "$script_directory/dist/Neuroma" ]; then
        executable="$script_directory/dist/Neuroma"
    else
        printf '%s\n' "Neuroma was not found. Run build.sh first." >&2
        exit 1
    fi
fi

if [ "$(uname -s)" != "Linux" ]; then
    printf '%s\n' "Register-Neuroma.sh is for Linux desktops." >&2
    exit 1
fi

data_home=${XDG_DATA_HOME:-$HOME/.local/share}
applications="$data_home/applications"
mkdir -p "$applications"
desktop_file="$applications/neuroma.desktop"

# Desktop Entry Exec values quote with double quotes, escape \ " ` $ and double %.
escaped=$(printf '%s' "$executable" | sed -e 's/[\\"`$]/\\\\&/g' -e 's/%/%%/g')
cat > "$desktop_file" <<DESKTOP
[Desktop Entry]
Type=Application
Name=Neuroma
GenericName=EPUB Reader
Comment=A fast, distraction-free EPUB reader for the terminal
Exec="$escaped" %f
Terminal=true
MimeType=application/epub+zip;
Categories=Office;Viewer;ConsoleOnly;
Keywords=epub;ebook;book;reader;
NoDisplay=false
DESKTOP

if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "$applications" >/dev/null 2>&1 || true
fi
printf '%s\n' "Neuroma is registered as an EPUB reader for this Linux account ($desktop_file)."
printf '%s\n' "Use Open With on an EPUB file to choose it, or make it the default with:"
printf '%s\n' "  xdg-mime default neuroma.desktop application/epub+zip"
