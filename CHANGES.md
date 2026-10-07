# CHANGES

This fork (by NPi2Loup) extends the original [SubmersedVR](https://github.com/Okabintaro/SubmersedVR) (by Okabintaro) with the features below. Everything is in-game, driven from the **"Submersed VR"** tab of the options menu (the SubnauticaMap bridge is the exception: it is always on when installed). Installation is unchanged: extract the release zip into your Subnautica installation directory.

## 0.3.0

- **VR sonar**: added the **hologram map (3D)** mode — the game's own sonar hologram (the Seaglide / map room look) rendered on the real terrain, revealed by the ping front and fading out, synced with the on-screen blue wave. Added the **Ping Range (m)** and **Sonar Color** (preset) options.
- **Teleport**: new option fixing the precursor teleport's eye strain in VR (see the Teleport section below).
- **PDA**: the hand-angle sliders now start from the calibrated defaults, so a fresh install opens the PDA well-oriented.
- **Robustness**: fixed a batch of null-reference crashes (PDA scaling on the title screen / menus, ambient occlusion, laser reticle).

## VR sonar (new)

The game's sonar is a screen-space effect drawn once for the whole stereo frame, which in VR shows up as a double grid that follows the head. This fork offers a **"Sonar screen effect"** option with four modes:

- **legacy** — the game's own effect, left completely untouched (100% original behavior).
- **legacy (3D fixed)** — the game's sonar grid, stereo-corrected per eye with per-eye projection terms + per-eye pose (no more double grid, stable while the head moves).
- **blue wave** — a new lagoon-blue sonar wave that sweeps out from the player on each sonar ping, leaves a fading trail and reveals the terrain relief (facing-based shading, distance fade).
- **hologram map (3D)** — the blue wave over the whole view (objects and monsters glow) plus the game's own sonar hologram rendered on the real terrain, revealed by the ping front and fading out, synced with the screen wave.

The ping range and the sonar color (preset) are configurable; all other look parameters are frozen in code. The replacement screen modes swap the screen effect's material before the game's blit runs, so the game keeps driving its own ping animation.

## SubnauticaMap bridge (new)

A standalone BepInEx plugin (`bridges/SubnauticaMap/`, own csproj/DLL — no change to the main mod) that makes the third-party [SubnauticaMap](https://www.nexusmods.com/subnautica/mods/12) map mod usable in VR:

- **Map panning**: hold the trigger and move the laser — the drag engages once the laser hit point on the map has moved past 2 mm (movement decides click vs drag, so clicks are untouched).
- **Map zoom**: the right stick drives the mod's own private `Zoom()` (the only path that also recomputes the map scale and icons); the native scroll delta is clamped to ±0.99 while the map is open so the mod's native "exactly one wheel notch" double-zoom gate stays closed.
- **Note creation**: a trigger click on the map creates a note exactly at the click position (the mod's own `CreateNote`, with its cursor synced from the click's raycast).
- **Scan-range circle fix**: the mod's spin coroutine writes a world-space euler rotation; in VR (PDA screen tilted) that snaps the distance disc out of the map plane. A Harmony transpiler redirects the loop's getter and setter to local euler, and a `LateUpdate` corrector keeps the disc in the plane as a safety net.

The mod is only addressed at runtime through reflection (no compile-time reference to its assembly): every lookup is signature-checked and every call guarded, so a future mod release degrades to a warning + disabled feature, never a crash. With the mod absent the plugin stays idle. No new in-game options: installing the plugin enables it.

## Knife (new)

- **Physical knife swing**: a fast controller motion (speed threshold configurable) triggers the game's knife swing, so the weapon can be used with a real cutting motion.

## PDA (new)

- **PDA reach zone**: the PDA opens in the shoulder or in the hip zone (configurable).
- **PDA hand angle**: per-axis (X/Y/Z) calibration of the PDA orientation in the hand.

## Reticle (changed)

- **Reticle mode**: choose between the legacy layout, the whole reticle following the laser dot, or the target info (action texts, icon, progress) projected on the laser hit point.
- **Billboarded reticle**: the hand reticle always faces the viewer (HMD), readable no matter how the tool model is held.
- **Split reticle**: in the target-info mode, the texts, icon and progress are projected on the laser hit point (offset 5 cm, laser dot hidden while aiming); the reticle is re-applied automatically after a save/load recreation.

## Comfort (new)

- **Delayed vehicle recenter**: after entering a Seamoth / Cyclops / PRAW the view recenters after a configurable delay, so you can straighten your head first.

## Teleport (fixed)

- **Teleport swirl**: the precursor teleport's 2D head-locked swirl causes eye strain in VR. A new option (on by default) replaces it with a world-locked 3D sphere using the game's own door material; the original swirl stays available as the alternative.

## Movement (changed)

- **Movement modes**: head based, or right/left hand based.
- **Snap turning** with a configurable angle (22.5°/30°/45°/90°).
- **Hand movement pitch offset** and a session-only **movement laser** visual aid for calibrating the movement axis.

## In-game keyboard (fixed)

- The virtual keyboard (used in Subnautica UI) now works correctly in VR (issue #133 on the fork).

## Camera mode (fixed)

- Drone camera mode: the native tool-cycle actions are disabled while in camera mode, and camera cycling is bound to the thumbsticks / long-press of the camera exit button (issue #24 on the fork).

## SteamVR input (changed)

- SteamVR actions are regenerated from source (camera cycle actions added).
- Additional controller bindings, including Oculus Touch.

## Options

All of the above is exposed in the new **"Submersed VR"** tab of the in-game options, alongside the original mod's settings.
