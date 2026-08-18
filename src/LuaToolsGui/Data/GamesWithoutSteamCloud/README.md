# GamesWithoutSteamCloud modules

Each JSON file owns one franchise or launcher. Put every supported Steam AppID in `appIds`.
Add a matching item to `games` when its executables and save locations are known.

`saveLocations` accepts more than one entry. Supported Windows bases are `UserProfile`,
`LocalAppData`, `RoamingAppData`, `Documents`, `ProgramFiles`, `ProgramFilesX86` and
`CommonAppData`. A `*` path segment matches launcher-specific account directories.
Use `includePatterns` and `recursive` to keep unrelated files and manual backup subfolders out of a snapshot.

Example:

```json
"saveLocations": [
  {
    "platform": "windows",
    "base": "ProgramFilesX86",
    "relativePath": "Ubisoft/Ubisoft Game Launcher/savegames/*/GAME_FOLDER"
  },
  {
    "platform": "windows",
    "base": "LocalAppData",
    "relativePath": "Publisher/Game/save"
  }
]
```
