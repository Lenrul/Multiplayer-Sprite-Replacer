# Multiplayer Sprite Replacer 2.0

A from-scratch rewrite for the full release of *Casualties: Unknown* (game 1.0.1, BepInEx 5.4.23.5).
Swaps the player's sprites for custom ones and, when **Casualties: Together** (`CasualtiesMP`) is
installed, shows every player's skin to everyone else. Together is optional: without it the mod
runs as a plain singleplayer skin mod.

## Install

Copy `bin/MultiplayerSpriteReplacer.dll` into `BepInEx/plugins/` (any sub-folder).
Skins go in `BepInEx/plugins/CustomSprites/<any name>/`.

## Skin folders

One folder per skin, any name (the old `st0`..`st9` folders still work, they are just names now):

```
CustomSprites/
  Pink Fluffy/
    Body/        limbs, tail            experimentUpTorso.png, experimentTail.png ...
    Head/        head, eyes, nosebleed  experimentHeadBack.png, experimentEyeOpen.png ...
    WoundView/   health panel diagram   headoutline.png, thoraxMiddle.png, experimentBody.png ...
    Minigame/    hands / cursors + art  handGraspIdle1.png, dislocationBoneBase.png ...
    Blood.png    optional: its dominant colour becomes the blood colour
    skin.json    optional: { "name": "Pink Fluffy", "author": "...", "bloodColor": "#c81e3c" }
    preview.png  optional: picture for the Skins tab (otherwise one is assembled from the body parts)
```

* A PNG replaces the game sprite with the same name. `Body/` and `Head/` are interchangeable.
* Skins use the Experiment sprite names. With "Fit skins to any character" on they are also mapped
  onto Milky and Dune by body part.
* Bandage wraps, ash, broken bone, injection / self-harm marks, splint and tourniquet icons are
  replaced too (`bandageWrap`, `alginateWrap`, `ashSprite`, `musharmwrap`, `brokenBone`,
  `injectionWound`, `selfHarmWound`, `suicideWound`, `splintIcon`, `tourniquetIcon`). They can sit in
  `Body/`, `WoundView/` or, as in older packs, `Minigame/`.
* Blood colour recolours fur stains, drips, and wall / floor splatters of that body only.

## Skins tab

Settings -> **Skins** (main menu and pause menu).

* Left: every skin folder. Click a tile to apply it (checkbox), click again for vanilla.
  The badge in the corner is the hotkey it is pinned to: click it, then press `1`-`9` or `0`
  (`Backspace` unpins).
* Right: paste a `.zip` link and press Enter / Download; the archive is unpacked into
  `CustomSprites/<name>/` (several skins in one archive become several folders). Only `.png` and
  `skin.json` are ever written to disk.
* Below: the key for each hotkey (default Keypad 1-9, 0), next / previous / vanilla keys, and the
  feature toggles. Click a key, press the new one; `Esc` clears it. "Reset to default" on this tab
  only resets this mod.

Keys and toggles are stored in `BepInEx/config/lenrui.casualtiesunknown.multiplayerspritereplacer.cfg`,
the active skin and pins in `BepInEx/config/MultiplayerSpriteReplacer.state.json`.

## Multiplayer

Uses Together's named custom messages (`MSR2.Up`, `MSR2.Down`, `MSR2.Req`). Body, wound view and
blood colour are shared; minigame sprites are local only. Skins are sent in 16 KB chunks so they
stay inside the Steam transport's message and buffer limits. The host files uploads under the
connection they arrive on and relays them; players without the mod are unaffected. Not compatible
with the network format of MSR 1.x.

## Build

```
dotnet build -c Release
```

Paths default to `..\..\Casualties Unknown` (game) and the Together 5.5.0 folder next to this one;
override with `-p:GameDir=...` / `-p:TogetherDir=...`. The build copies the DLL into the game's
`BepInEx/plugins/MultiplayerSpriteReplacer/` unless `-p:Deploy=false`.

## Source map

| File | What it does |
| --- | --- |
| `Plugin.cs` | Entry point, persistent runner (hotkeys, periodic re-apply) |
| `Config.cs` | Key mappings / toggles, active skin + pins |
| `Skins.cs` | Skin folders, loading, previews, network package, who-wears-what |
| `Applier.cs` | Limbs, tail, face, eyes, limb overlays; restores vanilla |
| `Blood.cs` | Blood colour: limb shader, drip particles, stains |
| `UiSkins.cs` | Health panel diagram and minigame hands / art |
| `SettingsTab.cs` | The Skins tab |
| `Downloader.cs` | Download and unpack skin archives |
| `Mp.cs` | Optional Together bridge and sync protocol |
