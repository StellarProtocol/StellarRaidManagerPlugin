# Stellar Raid Manager Plugin

> A Stellar Mod System plugin for Blue Protocol (Star) — raid coordination tools displayed as a HUD overlay.

## Features

### ⚔ Countdown Timer
Displays a large animated countdown on the HUD, visible to all raid members running the plugin. Counts down with centisecond precision and turns red at 5 seconds remaining.

### 🔴 Raid Warning
Flashes a bold red warning message on the HUD for 5 seconds with a smooth fade-out. Ideal for calling mechanics, stack points, or incoming attacks.

Optionally uses the game's native notice banner system (Special banner with victory audio) instead of the custom overlay — toggle in Settings.

### 📍 Mark Presets
Save the dungeon markers you place and reload the whole layout in one click — as multi-step phase sequences.

- **Save a layout as a step** — place your markers, then *Save Step*. A preset is an ordered list of steps (one marker layout per phase).
- **Walk the sequence during the fight** — *Previous / Reset / Next* each clears the board and places that step's markers. Step 0 is a permanent blank "Start".
- **A preset per raid** — create and name as many as you like; *Activate* the one you're running (the button toggles to *Deactivate*). Rename a preset any time with its pencil button.
- **Step notes** — give each step a short comment (e.g. "P2 — stack west"); it shows in the window and in the pop-up when you step to it.
- **Share presets** — *Export Preset* gives a short code (with a Copy button) holding the whole preset: name, every step's markers and notes. A friend pastes it into *Import Preset* to get the same preset.
- Pop-up confirmations on each change, and **Previous / Reset / Next hotkeys** (unbound by default — assign your own in the game's key settings).

Open it from **Raid Manager → Mark Presets**. Marker placement works in dungeons.

---

## Commands

| Command | Description |
|---|---|
| `/ct <seconds>` | Start a countdown (1–30 sec). Alias: `/countdown <seconds>` |
| `/ct stop` | Cancel the running countdown. Aliases: `cancel`, `abort` |
| `/rw <text>` | Show a raid warning message for 5 seconds |

**Example**
```
/ct 10
/rw Stack on marker!
```

---

## Settings

Open **Raid Manager** from the PLUGINS launcher tile (⚔ icon).

**Countdown**
- Enable / disable the `/ct` command
- Display size — Small / Medium / Large (resolution-scaled)
- Active channels — Party, Guild, Local

**Raid Warning**
- Enable / disable the `/rw` command
- **Use In-game Warning** — when on (default), `/rw` shows a Special notice banner with victory audio via the game's native notice system; when off, shows a custom HUD overlay for 5 seconds
- Display size — Small / Medium / Large (custom overlay only)
- Active channels — Party, Guild, Local

**Preview**
- Test buttons to trigger both features without using chat

> Both overlays remain visible when the game menu (ESC) is open.

---

## Requirements

- Stellar Mod System **v2.19.0 or above**

## Installation

1. Install Stellar Mod System v1.3.0 or above
2. Copy `Stellar.RaidManager.dll` into:
   ```
   <game>\stellar\plugins\stellar-raid-manager\
   ```
3. Launch the game — **Raid Manager** appears in the PLUGINS section of the launcher

---

## Building from Source

```bash
git clone https://github.com/StellarProtocol/StellarRaidManager
cd StellarRaidManager
dotnet build -c Release
```

**Requirements**
- .NET 6 SDK or later

---

## License

[GNU Affero General Public License v3.0](LICENSE)
