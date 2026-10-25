# Sanctum Helper

An Egg Heist companion for **Aniimo**. It shows the whole Lost Sanctum layout, including rooms
still under fog, every egg-nest spot, the key rooms and their doors, the Limited-Time Challenge
room and loot, and tracks where you are. It does the same out on the Egg Heist island, with
its entrances and every Egg Boat landing.

> Unofficial fan tool, not affiliated with or endorsed by Pawprint Studio. Map art, icons and
> game terms come from the game's own files and belong to their owners.

## Install

1. Download `SanctumHelper-Setup-<version>.exe` from the
   [latest release](../../releases/latest) and run it (no admin rights needed).
   Windows may show "Windows protected your PC" because the file isn't code-signed:
   click **More info → Run anyway**.
2. Sanctum Helper sits in the system tray (bottom-right, by the clock).
3. Play Aniimo in **Borderless** or **Windowed** mode with HDR off.

To uninstall, use Windows **Settings → Apps → Installed apps → Sanctum Helper**.

## Use

- **Difficulty:** open the Egg Heist party screen once. The difficulty is read from it
  automatically (or set it by hand).
- **Layout:** recognised from your minimap as you explore the Sanctum, or straight away from
  the door icons when you open the full map.
- **Full-map overlay:** with the map open (M), hold **Left Ctrl** (or a mouse side button, or a
  controller button) to line the whole layout up on the game's map.
- **On-screen map:** with the map closed, a live map follows you. Choose a corner map, a round
  minimap, a radar, a compass strip or a see-through (Diablo-style) view, and drag it
  anywhere. Hold the key with the map closed for a big zoom map.
- **Island:** outside the Sanctum the map switches to the island, with Sanctum entrances and
  the possible Egg Boat spots.
- **Notifications** in the top-right corner when the difficulty or the Sanctum is found.

Everything is in the tray icon's **Settings** window, including a browser for every Lost
Sanctum layout.

## Languages

The app speaks all 13 languages the game ships: English, 简体中文, 繁體中文, 한국어, 日本語,
Tiếng Việt, Русский, Deutsch, Français, Español, Português, Bahasa Indonesia and ไทย. By default
it follows the game's own language setting, and it can be set separately from the game. Game
terms use the game's official wording. Reading the party screen in other languages needs that
language's text recognition installed in Windows (Settings → Time & language → Language).

## Fair play

- It only **looks at screen pixels**, the same way a screenshot or streaming tool does. It
  never reads or writes the game's memory, never injects anything and never touches the
  game's files.
- It can read the game's own text log (`Player.log`) to know at once when you step out to the
  island or back into the Sanctum. That's read-only and can be turned off.
- The overlay is click-through and never takes focus from the game.

## Building

Requirements: Windows 10/11, .NET 9 SDK, and for the data tools Python 3 with `UnityPy` and
`Pillow`.

```
tools/build_data.py        # map art, layouts and markers from the game install
tools/extract_icons.py     # the game's map icons
tools/extract_text.py      # game terms in every language
tools/lang/build_lang.py   # app translations (checks every string is covered)
build.ps1                  # app + installer -> dist/SanctumHelper-Setup-<version>.exe
```

Developer checks: `SanctumHelper.exe --analyze <out> <screenshots...>` renders the overlay on
saved screenshots; `--settings-shot <out> <lang>` renders the settings window.
