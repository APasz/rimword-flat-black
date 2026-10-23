# Flat Black

A standalone black interface with red and purple highlights for RimWorld 1.7. Enable Harmony before Flat Black. The package ID remains `APasz.FlatBlack`; `About/PublishedFileId.txt` selects the Workshop item updated by the uploader.

The core skin uses Oxanium headings and Exo 2 body text by default, and preserves health/status colours, disabled text, menu placement, and window behaviour. Buttons have charcoal fills, grey outlines, brighter hover states, and accent outlines when pressed. Unavailable right-click actions use a neutral dark background. Loading screens and animations are deferred; unused original artwork is preserved in `Art/Deferred`, outside the game's load folders.

Checkboxes use tick, dash, and empty states; radio buttons use a dot inside a circular outline. Window and panel borders remain visible against black backgrounds, and selected options use a dark accent fill with a brighter edge. Green and gold control indicators retain their state colours when custom accents are enabled. The shared control artwork lives in `Art/Controls.svg`; regenerate its PNGs with `python3 Tools/render_controls.py` (requires `rsvg-convert`).

Open **Options → Mod settings → Flat Black** to adjust window and panel/menu opacity, toggle backgrounds, interface icons, or selection highlights, and choose custom primary and secondary accent colours. Settings apply immediately and are shared across saves. Font selections are saved explicitly; older settings without a font selection use the current default. Reset defaults restores Oxanium + Exo 2, the original red/purple appearance and 90% window opacity; saved custom values remain in effect until reset. Tooltips explain each option. Accent changes preserve neutral colours and semantic icon colours, including battle/status and work priority textures.

The **UI font** dropdown offers **Vanilla** or **Oxanium + Exo 2** (the default). The themed preset uses Oxanium Medium for headings and Exo 2 Regular for small and tiny text, including standard text input fields. It retains the game's font sizes and native character fallback, updates line measurements immediately, and reuses registered fonts when switching. Measurements are prepared before changing shared styles, so a failed measurement preserves the active fonts. Vanilla restores the original fonts and measurements; Reset defaults restores Oxanium + Exo 2; later font/style replacements from other mods take precedence. Both fonts and their SIL Open Font Licences ship in `1.7/Fonts`; no system font installation is needed.

The live preview shows the heading font; all button, checkbox, and radio states; selected and unselected options; and meaningful text colours. Disabled buttons use the game's inactive tint and disabled-text colour. Samples wrap into fewer columns in narrow windows. The preview stays visible while scrolling, uses at most half the settings area, and scrolls independently when needed.

Build with the .NET 10 SDK:

```sh
dotnet build Source/FlatBlack.csproj -c Release
```

The default references use the Linux Steam installation and Harmony's Workshop folder. Override `RimWorldDir` and `HarmonyAssemblyPath` with MSBuild `-p:Name=/path` arguments for other installations. The output is `1.7/Assemblies/FlatBlack.dll`; game and Harmony assemblies are referenced locally and are not bundled.

Package a release after building:

```sh
python3 Tools/package.py
```

The output is `dist/FlatBlack`, containing real copies of the metadata, mod DLL, definitions, translations, fonts and their licences, and source textures. Deferred artwork, backups, source code, tests, and generated asset packs are excluded. Use `--output /path/to/new-directory` for another destination; an existing directory is never overwritten. Install the resulting folder under RimWorld's `Mods` directory to upload it, with any other local copy of this package outside `Mods` for that launch. RimWorld 1.7's default uploader bakes textures into `Assets.zip` and strips the source textures from the uploaded copy.

For development, link only `About`, `1.7`, and `LoadFolders.xml` into a local mod directory. Keep `Art/Deferred` in the repository. When the repository is outside Steam's runtime filesystem, expose it through the game's launch options with `STEAM_COMPAT_MOUNTS="/absolute/path/to/repository" %command%`. The uploader can update `About/PublishedFileId.txt` through the link.

[Theme.xml](1.7/Defs/Theme.xml) owns the default palette, accent colours, component groups, opacity channels, and texture bindings. The typed definition validates these against the installed game. Settings defaults derive from this definition, and texture customisation caches its own variants without modifying source pixels. Sliced button and tooltip atlases disable mipmaps to prevent borders bleeding across short controls. Copies use the engine's pixel reader to preserve packed texture channel mappings and colours.

Texture copies preserve source sampler settings and are prepared only when needed. Neutral textures skip accent recolouring, and font-only changes leave texture and palette caches intact. Failed loading releases new copies and retains the active resources. The settings UI restores the caller's text wrapping, font, alignment, and colour.

Textures use the private `FlatBlack/` namespace, so turning off a component can restore its original resources immediately. Another mod loaded later can still replace the corresponding vanilla texture path; that replacement takes precedence over Flat Black, including custom accents and opacity. Small transpilers resolve specific vanilla resource reads while retaining the original drawing code, GUI state handling, and other Harmony patches. Later changes to source fields from other mods take precedence. Generic methods and static constructors retain their original code. Another mod's custom UI and texture paths remain under that mod's control; theme coverage there depends on whether it uses vanilla widgets and resources.

Run the managed checks against the installed game and Harmony assemblies:

```sh
dotnet run --project Tests/FlatBlack.Tests.csproj -c Release
python3 -m unittest discover -s Tests -p 'test_*.py'
```

The Linux smoke test boots the game with Harmony, Flat Black, installed DLCs, and a test fixture. A second boot adds a later texture replacement. It verifies live settings changes, restoration of original resources, failed-load cleanup, override precedence, semantic icon colours, atlas sampling and packed greyscale colours, pixel orientation, reuse of texture variants, Reset defaults, font switching/restoration, native fallback glyphs, and restoration of the caller's GUI state. It stages a temporary game directory, isolates saves/configuration/logs, and exits automatically. Hidden rendering also captures the menu, sample vanilla controls, settings with default/custom/disabled components, and both ends of the compact preview's scroll range:

```sh
python3 Tests/run_smoke.py --headless
python3 Tests/run_smoke.py --x11
python3 Tests/run_smoke.py --packed --x11
python3 Tests/run_smoke.py --packed --x11 --gameplay
```

Use `--game`, `--harmony`, or `--output` to override paths. `--x11` avoids a 0×0 hidden-window issue observed with Wayland. `--packed` packages a temporary copy, invokes the native texture baker, removes its loose textures and baking caches, then runs both cases against the resulting `Assets.zip`. A broader gameplay review with a real mod list remains separate from these boot, rendering, and patch compatibility checks.

`--gameplay` generates a temporary quick-test colony, pauses it, and captures the native work, health, research, inventory, and right-click menu screens. The fixture adds a minor injury and a disabled menu sample for readability checks. Existing saves are not loaded or modified.
