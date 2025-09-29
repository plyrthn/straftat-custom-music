# STRAFTAT Custom Music Mod

A BepInEx mod that adds custom MP3/OGG/WAV files to STRAFTAT's music playlist. Drop your music files in a folder and they get mixed into the game's soundtrack with proper metadata parsing.

## What it does

- Scans a CustomMusic folder for audio files on game startup
- Reads artist info from ID3 tags, JSON files, or filenames
- Extends the original (as of Sep2025) 35-track playlist or replaces it entirely
- Handles the game's shuffle system properly
- Updates the in-game music UI to show your custom tracks

## Installation

1. Install BepInEx 5.x in your STRAFTAT game folder
2. Download the latest release and extract `STRAFTATCustomMusic.dll` to `BepInEx/plugins/`
3. Launch the game once to generate the CustomMusic folder
4. Add your music files to `BepInEx/plugins/CustomMusic/`
5. Restart the game

## Music formats

Supports MP3, OGG, and WAV files. The mod tries to get artist info in this order:

1. **ID3 tags** (recommended) - Use any music tagger like Mp3tag
2. **JSON metadata** - Create `SongName.json` with `{"artist": "Artist Name"}`
3. **Text files** - Create `SongName.txt` with just the artist name
4. **Filename parsing** - Format as `Song Title - Artist Name.mp3`
5. **Folder structure** - Put files in `Artist Name/Song Title.mp3`

## Configuration

Edit `BepInEx/config/com.matchabrew.straftat.custommusic.cfg`:

- `CustomOnlyMode = false` - Mix custom tracks with original soundtrack (default)
- `CustomOnlyMode = true` - Replace all original music with only your tracks

## File structure example

```
BepInEx/plugins/CustomMusic/
├── My Song - Cool Artist.mp3
├── Another Track.mp3
├── Another Track.json
└── Daft Punk/
    └── One More Time.mp3
```

## Building from source

Requirements:
- .NET Standard 2.1
- BepInEx 5.4.21+ references
- Unity 2021.3.16+ references (from STRAFTAT game folder)

The project targets .NET Standard 2.1 and uses Harmony for runtime patching. Reference assemblies are expected in a `libs/` folder.

## How it works

Uses Harmony to patch `SetMenuMusicVolume` at startup, extending the game's audioClips and trackNames arrays before the MusicTrack objects get created. Custom files are loaded asynchronously with UnityWebRequestMultimedia to avoid blocking the game.

## Troubleshooting

Check `BepInEx/LogOutput.log` for error messages. Common issues:

- **No custom tracks playing**: Make sure files are in the right folder and supported formats
- **Artist names not showing**: Try using proper ID3 tags or the filename format
- **Config not working**: Delete the config file and restart to regenerate it

## License

This is just a game mod, do whatever you want with it.