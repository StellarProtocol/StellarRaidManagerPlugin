# Stellar Raid Manager Plugin

> A Stellar Mod System plugin for Blue Protocol (Star) — raid coordination tools displayed as a HUD overlay.

## Features

### ⚔ Countdown Timer
Displays a large animated countdown on the HUD, visible to all raid members running the plugin. Counts down with centisecond precision and turns red at 5 seconds remaining.

### 🔴 Raid Warning
Flashes a bold red warning message on the HUD for 5 seconds with a smooth fade-out. Ideal for calling mechanics, stack points, or incoming attacks.

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
- Display size — Small / Medium / Large (independent from countdown)
- Active channels — Party, Guild, Local

**Preview**
- Test buttons to trigger both features without using chat

> Both overlays remain visible when the game menu (ESC) is open.

---

## Requirements

- Stellar Mod System **v1.1.0 or above**

## Installation

1. Install Stellar Mod System v1.1.0 or above
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
