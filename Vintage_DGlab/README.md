# Coyote Health Link for Vintage Story

Monitors player health and drives [DG-Lab Coyote](https://www.dungeon-lab.com/) device output via WebSocket. Takes damage → output intensifies. Heals → output fades. Stable → output holds.

**Target version: Vintage Story 1.22.3**

## Features

- **Health-Driven Output** — player health changes control the device:
  - 🔻 Health decreasing (damage) → output gradually **increases**
  - 🔺 Health increasing (healing) → output gradually **decreases**
  - ➡ Health stable → output **stays the same**
- **Player Auto-Tracking** — Gimbal Sensor automatically tracks the nearest player within 32 blocks
- **DG-Lab WebSocket Server** — built-in server implementing the full DG-Lab Coyote v3.0 protocol
- **QR Code Pairing** — scan QR code from in-game GUI to pair with DG-Lab phone app
- **Chat Commands** — `/dglab connect`, `/dglab disconnect`, `/dglab status`, `/dglab port`
- **Random Waveform** — auto-generates pulse waveforms mapped to signal strength
- **A/B Channel** — independent strength control for both output channels
- **Visual GUI** — real-time health display, signal bar with trend indicator, connection status, channel output

## Usage

### Commands

| Command | Description |
|---------|-------------|
| `/dglab connect` | Start WebSocket server and wait for DG-Lab app |
| `/dglab disconnect` | Stop WebSocket server |
| `/dglab status` | Show connection status and channel info |
| `/dglab port <1024-65535>` | Change server port (default: 8877) |

### How It Works

1. Place a **Gimbal Sensor** block in the world
2. Stand near it (within 32 blocks) — it auto-tracks the nearest player
3. Use `/dglab connect` to start the server
4. Scan the QR code (right-click the block) with your DG-Lab app
5. **When you take damage** → signal rises → device output intensifies
6. **When you heal** → signal falls → device output decreases
7. **When health is stable** → signal holds → output stays constant
8. No player nearby → signal fades to 0

### Signal Mechanics

```
Health Change      →  Signal Change      Output Effect
──────────────────────────────────────────────────────
Damage taken       →  Signal ↑ (1.5×)   Output ↑
Healing received   →  Signal ↓ (1.0×)   Output ↓
Health unchanged   →  Signal holds       Output holds
No player nearby   →  Signal fades → 0   Output → 0
```

- Each 1 HP of damage ≈ +1~2 signal points (max +3/tick)
- Each 1 HP of healing ≈ -1 signal point (max -3/tick)
- Signal range: 0–15
- Max signal change per tick: 3

## Building

### Prerequisites

- [.NET 8.0 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
- Vintage Story 1.22.3 installed
- `VINTAGE_STORY` environment variable set to game directory

### Setup

```powershell
# One-time template install
dotnet new install VintageStory.Mod.BasicTemplate
```

### Build

```bash
dotnet build
```

Output goes to `bin/` directory. Copy `CoyoteMod.dll`, `modinfo.json`, and `assets/` to your game's `Mods/CoyoteMod/` folder.

### Dependencies

- [QRCoder](https://github.com/codebude/QRCoder) v1.6.0 — Pure C# QR code generation
- VintageStory API DLLs — referenced from game install

## Project Structure

```
CoyoteMod/
├── modinfo.json                          # Mod metadata (game: 1.22.3)
├── CoyoteMod.csproj                      # .NET 8 project
├── src/
│   ├── CoyoteModSystem.cs                # ModSystem entry point
│   ├── block/
│   │   ├── GimbalSensorBlock.cs          # Custom block (interact, register)
│   │   └── GimbalSensorBlockEntity.cs    # BE (health tracking, signal, NBT)
│   ├── command/
│   │   └── DGLabCommand.cs               # /dglab commands
│   ├── config/
│   │   └── ModConfigData.cs              # Mod config model
│   ├── gui/
│   │   └── SensorGuiDialog.cs            # GUI (QR, health, signal bar, trend)
│   ├── util/
│   │   ├── QRCodeGenerator.cs            # QR code generation (QRCoder)
│   │   └── WaveformGenerator.cs          # Random waveform hex data
│   └── websocket/
│       ├── CoyoteWebSocketServer.cs       # Lightweight WebSocket server
│       └── SimpleJson.cs                 # Minimal JSON parser
└── assets/
    └── coyotemod/
        ├── blocktypes/gimbal_sensor.json  # Block definition
        ├── itemtypes/gimbal_sensor.json   # Item definition
        ├── lang/en.json                   # English localization
        ├── shapes/block/gimbal_sensor.json # 3D block model
        ├── textures/block/gimbal_sensor.png # Block texture
        └── patches/creative_tab.json      # Creative tab registration
```

## DG-Lab Protocol

The WebSocket server implements the [DG-Lab Coyote v3.0 protocol](https://github.com/DG-LAB-OPENSOURCE/DG-LAB-OPENSOURCE/blob/main/README.md):

1. App connects to `ws://<host>:<port>/`
2. Server sends `bind` request
3. App responds with `bind` + `DGLAB` message
4. Server sends heartbeat every 60s
5. Pulse commands: `clear-X`, `pulse-X:[...]`, `strength-X+X+<value>`

### Message Flow

```
[App] → ws://host:port/ → [CoyoteMod]
         ← bind(clientId, targetId, "targetId")
         → bind(appClientId, generatedClientId, "DGLAB")
         ← bind(FIXED_ID, appClientId, "200")  ← BOUND
         ← heartbeat ... (every 60s)
         → heartbeat ...
         ← clear-1, clear-2, pulse-A, pulse-B
         ← strength-1+2+<val>, strength-2+2+<val>
         → strength-1+2+<maxA>+<maxB>  (app reports limits)
```

## Configuration

Config file is stored at `ModConfig/coyotemod.json` in your game data folder:

```json
{
  "WebSocketPort": 8877,
  "SensorTickInterval": 4,
  "WaveformRefreshInterval": 5
}
```

## License

MIT
