# Social Space Mod Menu

A MelonLoader mod for Social Space (Unity 6, IL2CPP) with an in-headset menu.

## Features

- World-space menu panel that follows your head (VR and flat mode)
- Player list with distances, FPS, position, nameplate toggle
- Save the current space to a file and load its items into another space
- Gorilla-style arm locomotion with hand-tap sounds and vibration, at a lower player height; your visible hands stop at surfaces instead of clipping through
- Web swinging (trigger to fire, grip to reel)
- Player and hand collision against other players
- Grip to grab
- Speed/jump boost
- Notifications for mailbox items and save/load progress
- Creatool highlights: outlines what the Creatool points at (white) and has selected (blue)
- Lighting presets (default / bright / fullbright / dim) and a shadows toggle, local only
- Post processing (local): bloom, color grade (vivid/warm/cool/mono), tonemapping, vignette
- FPS cap / unlocker (desktop; in VR the headset sets the frame rate)
- Creatool noclip: fly through everything while flying with the Creatool
- Ride players: grip another player's head, body or hand to hang on and be carried (let go to be thrown)
- Hats other mod users can see (top hat, party hat, crown, halo, horns, cat ears; 10 colors). Sent as an extra key in your player data, which the game relays; players without the mod don't see anything
- Spawnable mirrors (VRChat style) using the game's own mirror: 3 sizes, 3 qualities, up to 4; grip an edge to carry one. Local only
- Recolor primitives on place: new primitives you place with the Creatool get the Creatool's current Recolor color/material, or a custom/random color (synced like a normal recolor)
- Pictures: hang your own .png / .jpg files (from `UserData/SocialSpaceModMenu/Pictures`) with a black, white, wood, gold or silver frame. They sync to mod users and save with the space; move and scale them with the Creatool. Players without the mod see a dark panel
- Sky presets (clear day, sunset, dusk, night, overcast, alien, void) synced to mod users in the space and saved with it, via a tiny marker object
- Your own hat shows in every mirror (spawned ones and the ones built into spaces), never in your normal view
- In the game's own menu: a **Mods** button in the Settings tab opens the whole mod menu, and a **Saved spaces** button in the Home tab saves, loads, renames (with the game's VR keyboard) and clears spaces
- Clear all objects in a space (networked delete, asks for a second click)
- Teleport to 0, 0, 0
- Player scale (0.25x to 5x): your view, reach and walking speed scale together; everyone sees your tracked height, and mod users also see your avatar resized
- Big space optimizations (Visuals): far objects stop drawing and mid-distance ones stop casting shadows, automatically once a space has over 300 objects; local only
- Shirt material (any of the Creatool's materials), shirt colors, visor, hand and head colors, seen by other mod users like the hats
- Discord Rich Presence ("In <space name>, with 3 others" / "In the menu" + play time); toggle in Settings
- Web swinging is networked: other mod users see your ropes and hear the fire/release sounds
- Desktop mode (auto when no headset): WASD move, Shift sprint, Space jump, right mouse or Tab to look, Esc releases the cursor

## Auto update

The mod installs a small plugin, `Plugins/SocialSpaceModMenu.Updater.dll`. At each game launch it
checks the latest GitHub release and, if it is newer, replaces `Mods/SocialSpaceModMenu.dll` before
the game loads it. The download is checked against the release's SHA-256 and version; the previous DLL
is kept in `UserData/SocialSpaceModMenu/`. While playing, a notification says when a newer release is
out. Turn it off with the menu's "Auto update" item (it creates `UserData/SocialSpaceModMenu/no-autoupdate`).

## Controls

The menu opens on six categories (Movement, Visuals, Players, World, Spaces, Settings); selecting one opens its submenu.


| Input | Action |
| --- | --- |
| Both thumbsticks together | Show / hide the panel |
| Left thumbstick click | Next menu item |
| Right thumbstick click | Activate the selected item (loading asks twice) |
| F1 / F2 / F3 / F4 | Panel / nameplates / next item / activate (keyboard) |
| Backspace | Back to the main page (keyboard); in VR select "< Back", the first item of every submenu |

## Building

Requires the .NET 8+ SDK and a game install with MelonLoader 0.7.3 that has run once
(so `MelonLoader/Il2CppAssemblies` exists).

```
cd ModMenu
dotnet build -c Release
```

The build copies `SocialSpaceModMenu.dll` into the game's `Mods` folder. If the game is installed
somewhere else, pass `-p:GameDir="path\to\Social Space"`.

## Files

- Saved spaces: `<game>/UserData/SavedSpaces/*.json`
- Custom tap sounds: `<game>/UserData/GorillaLocomotion/Sounds/*.wav`
- Settings: `<game>/UserData/MelonPreferences.cfg` (category `SocialSpaceModMenu`)

## Installing

* Install melonloader
* put the main dll into mods
* put the updater into plugins

## Third-party code

`GorillaLocomotion.cs` is adapted from
[Another-Axiom/GorillaLocomotion](https://github.com/Another-Axiom/GorillaLocomotion), MIT licensed.
See `ModMenu/ThirdParty/GorillaLocomotion/LICENSE`.
