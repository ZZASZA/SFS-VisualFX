# SFS Visual FX

Launch smoke and landing impact visual effects for Spaceflight Simulator. Purely visual - no physics, collision or gameplay changes.

## Features

- Launch smoke: ignition steam burst, smoke column and sea of smoke on the pad
- Tail smoke near the ground only (stops above 15 m altitude)
- Landing: reverse-thrust dust and impact shockwave rings
- Dust and smoke colors match the planet surface (water = white mist)
- Built-in particle textures, no external assets

## Install

Put `SFSVisualFX.dll` in `Mods/SFSVisualFX/` (folder name must match the dll), together with `config.txt` and the `Textures/` folder.

## Config

Edit `config.txt` next to the dll:

| key | default | description |
|---|---|---|
| `quality` | `auto` | `low` / `medium` / `high` / `auto` |
| `intensity` | `1.0` | global effect strength |
| `launch_smoke` | `true` | toggle launch smoke |
| `reverse_dust` | `true` | toggle reverse-thrust dust |
| `landing_impact` | `true` | toggle landing impact |
| `smoke_drag` / `steam_drag` / `dust_drag` | `6 / 5 / 7` | atmospheric drag per layer |

## Build

```
dotnet build SFSVisualFX.csproj -c Debug
```
