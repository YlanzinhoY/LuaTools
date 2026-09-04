<p align="center">
  <img height="336" alt="luatools" src="https://github.com/user-attachments/assets/54702ada-93a8-439b-ab3e-5cd73747ed46" />
</p>

# LuaTools
<p>
  <img align="right" height="250" src="https://github.com/user-attachments/assets/df083fb0-9be7-4690-9f0f-c8b0a73da881" />

  [Discord](https://discord.gg/luatools) • [Website](https://lua.tools) • [Git Mirror](https://git.lua.tools/luatools)
  
  A Windows desktop client for managing Steam manifest/lua configurations, built with WPF on .NET 8.
    
  LuaTools browses and installs manifest sources, edits `stplug-in` lua files (depot pinning,
  per-depot enable/disable), manages unlocker modes, and injects a companion plugin into Steam's
  store pages.
  
  It ships fully translated in 29 languages and auto-updates via Velopack.
  <br><sub>Found a translation error? Tell us about it over on [Discord](https://discord.gg/luatools)</sub>
</p>

## Achievement Bridge experiment

The Achievement Bridge settings include an opt-in **Steam notification (Experimental)** toggle, off by default. For new live unlocks it first asks Steam to queue the normal `StoreStats` path. If that write is protected, it requests a native `1/2` progress toast for the same achievement name and image without changing Steam state. This may contact Steam services. If Steam rejects both requests, LuaTools automatically uses its image-rich popup instead; recovered and already-synced achievements always keep the stable fallback.

The protected-achievement fallback was manually reproduced twice with Assassin's Creed IV Black Flag (`ACObsidian_Ach_10`, `permission=2`): Steam rejected `SetAchievement`, returned `progress_queued`, and visibly displayed the localized native progress toast while the Bridge completed its local sync.

## Can I Run It?

The **Can I Run It?** action on a game's Manage panel uses a small Go sidecar. It detects the local
Windows CPU, GPU, RAM, OS and free system-drive space, loads the publisher's requirements from the
Steam Store by App ID, and asks the free OpenRouter model
`inclusionai/ling-3.0-flash-fin:free` for a structured compatibility estimate. Its tool-call schema is
validated before any model text reaches the UI. Validated results are cached by
game requirements, hardware, model and UI language so identical inputs stay stable.

The OpenRouter key is entered when the action is first used. If the user chooses to remember it, it is
encrypted with Windows DPAPI for that Windows account. It is passed to the sidecar through its process
environment, never as a command-line argument. Developers and managed installs can provide
`OPENROUTER_API_KEY` instead; `OPENROUTER_MODEL` and `OPENROUTER_BASE_URL` are optional overrides.

Go 1.24 or newer is required when building the app from source. Run the backend tests independently
with `go -C src/CanIRunItBackend test ./...`.

## Statistics
<div>
  <img src="https://img.shields.io/github/downloads/madoiscool/luatools/LuaTools-win-Setup.exe?displayAssetName=true&style=for-the-badge" />
  <img src="https://img.shields.io/github/downloads/madoiscool/luatools/LuaTools-win-Portable.zip?displayAssetName=true&style=for-the-badge" />
</div>

<a href="https://www.star-history.com/?repos=madoiscool%2Fluatools&type=date&legend=top-left">
 <picture>
   <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/chart?repos=madoiscool/luatools&type=date&theme=dark&legend=top-left&sealed_token=1SX6CDP2N0Emx5IbGfQmEz4TxM11iXtfLKL9K1utRzINJPEDv55f5XEYjliBUB1No6wbcWbMs-cSzO65OC7kAlMLAHJXjqmDoeRCM6hVtW9xd7fyg8cr2DG4gATwkgym1JvgPs4_PeGi6XMAm7_2CVXU9UxRLBW_GP4-Qmd3-AosSRCM1Nkm7dEr2_Ut" />
   <source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/chart?repos=madoiscool/luatools&type=date&legend=top-left&sealed_token=1SX6CDP2N0Emx5IbGfQmEz4TxM11iXtfLKL9K1utRzINJPEDv55f5XEYjliBUB1No6wbcWbMs-cSzO65OC7kAlMLAHJXjqmDoeRCM6hVtW9xd7fyg8cr2DG4gATwkgym1JvgPs4_PeGi6XMAm7_2CVXU9UxRLBW_GP4-Qmd3-AosSRCM1Nkm7dEr2_Ut" />
   <img alt="Star History Chart" src="https://api.star-history.com/chart?repos=madoiscool/luatools&type=date&legend=top-left&sealed_token=1SX6CDP2N0Emx5IbGfQmEz4TxM11iXtfLKL9K1utRzINJPEDv55f5XEYjliBUB1No6wbcWbMs-cSzO65OC7kAlMLAHJXjqmDoeRCM6hVtW9xd7fyg8cr2DG4gATwkgym1JvgPs4_PeGi6XMAm7_2CVXU9UxRLBW_GP4-Qmd3-AosSRCM1Nkm7dEr2_Ut" />
 </picture>
</a>

## Requirements

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and
  [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) for source builds (the released
  installer checks for the .NET 8 **Desktop Runtime** used by the WPF app; the isolated libtorrent
  host is bundled self-contained and needs no separate runtime)

## Installation
You can find release builds on the [luatools website](https://lua.tools/app) or in the [releases](https://github.com/madoiscool/LuaTools/releases/latest) tab. 

## Credits / Adjacent software

- [Millennium](https://steambrew.app/): the Steam plugin framework whose injection API this app
  polyfills when Millennium isn't installed
- [Velopack](https://velopack.io/): installer and auto-update framework

## Licence

MIT. See [LICENSE](LICENSE).
