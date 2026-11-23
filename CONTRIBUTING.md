# Contributing to Fluorite Launcher

For what Fluorite does, see the [README](README.md).

## Prerequisites

- Windows 10 2004+ or Windows 11
- .NET SDK 10.0.403+ (see `global.json`)
- Visual Studio 2026 with the **WinUI** workload (F5 deploy, MSIX packaging) and **Desktop development with C++** (Release builds use Native AOT)

## Build and test

```powershell
dotnet build FluoriteLauncher.slnx
dotnet test --project tests/Fluorite.Core.Tests/Fluorite.Core.Tests.csproj
dotnet test --project tests/Fluorite.Auth.Tests/Fluorite.Auth.Tests.csproj
dotnet test --project tests/Fluorite.Modding.Tests/Fluorite.Modding.Tests.csproj
```

## Run

**Packaged:** in Visual Studio, set `Fluorite.App` as the startup project and press F5. This needs Developer Mode or a trusted signing certificate.

**Unpackaged** (no Developer Mode needed):

```powershell
dotnet build src/Fluorite.App/Fluorite.App.csproj -p:Platform=x64 -p:Unpackaged=true
$env:FLUORITE_ROOT = "$PWD\.fluorite-dev"   # optional: separate dev data folder
.\src\Fluorite.App\bin\unpackaged\x64\Debug\net10.0-windows10.0.26100.0\FluoriteLauncher.exe
```

A custom data root gets its own single-instance key, so a dev build runs alongside your normal launcher.

**Release (Native AOT):**

```powershell
$env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"   # avoids "'vswhere.exe' is not recognized"
dotnet publish src/Fluorite.App/Fluorite.App.csproj -c Release -r win-x64 -p:Platform=x64 -p:Unpackaged=true -o publish\win-x64
```

The app accepts `--page Home|Library|Browse|Activity|Accounts|Settings` and `--launch <instance-id>`, plus `fluorite://launch/<id>`, `fluorite://instance/<id>` and `fluorite://page/<tag>` links.

## Developer CLI

`tools/Fluorite.Cli` runs the core without the UI. `fluorite --help` and `fluorite <command> --help` list every command and option.

```powershell
$cli = "tools\Fluorite.Cli\bin\Debug\net10.0-windows10.0.26100.0\fluorite.exe"
& $cli smoke 26.3 --root .fluorite-dev                  # install, launch, wait for the renderer, stop
& $cli instance create "Fabric Test" --version 1.21.4 --loader fabric --root .fluorite-dev
& $cli content install sodium --instance "Fabric Test" --root .fluorite-dev
& $cli instance smoke "Fabric Test" --root .fluorite-dev
& $cli pack import modrinth:fabulously-optimized --root .fluorite-dev
& $cli account add-offline Steve --root .fluorite-dev   # dev only: skips the ownership check
```

Scripts:

| Script | What it does |
|---|---|
| `pwsh tools/smoke-matrix.ps1 -Root .fluorite-dev` | Checks that every Minecraft era launches |
| `pwsh tools/loader-matrix.ps1 -Root .fluorite-dev` | Checks that every mod loader installs and launches |
| `powershell -File tools/ui-drive.ps1 -Exe <FluoriteLauncher.exe> -Steps "invoke:Settings;expect:Behavior;capture:C:\tmp\settings.png"` | UI smoke checks through UI Automation |
| `uv run tools/generate-assets.py` | Renders the logo into every app icon and logo in `src/Fluorite.App/Assets` |

## Project layout

| Project | Contents |
|---|---|
| `src/Fluorite.Core` | Launch engine: versions, libraries, assets, downloads and mirrors, Java, arguments, logs, instance layouts |
| `src/Fluorite.Auth` | Microsoft, skin-site (authlib-injector) and offline accounts; encrypted account store |
| `src/Fluorite.Modding` | Mod loaders, Modrinth/CurseForge content, modpacks, other-launcher import |
| `src/Fluorite.Platform.Windows` | Game process launch (MSIX breakaway, job object), DPAPI, Windows account sign-in (WAM) |
| `src/Fluorite.App` | WinUI 3 app (MVVM, Generic Host DI), packaged as MSIX |
| `tools/Fluorite.Cli` | Headless developer CLI |
| `tests/` | xUnit v3 tests |
| `website/` | The website, https://fluorite.l5z12.dev (see below) |

## Website

`website/` is an [Astro](https://astro.build) site that builds to static files. It follows WinUI's look (`src/styles/fluent.css`), and its text is in English and Simplified Chinese (`src/i18n/en.json`, `src/i18n/zh-hans.json`; both files must have the same keys). The privacy and terms pages render `PRIVACY.md` and `TERMS.md` from the repo root, so edit those files, not the site. It needs Node.js 22.23.4+ and pnpm.

```powershell
cd website
pnpm install
pnpm dev        # live preview at http://localhost:4321
pnpm check      # type check (CI runs it too)
pnpm preview    # build, then serve dist/ the way Cloudflare does (headers, 404 page, redirects)
```

When Fluorite is released, set `storeUrl` in `website/src/site.ts`. Home then links to the Store instead of saying Fluorite isn't released yet.

## Configuration

> **Don't use Fluorite's Microsoft client ID** (`MicrosoftAuthOptions.FluoriteClientId`) in your own launcher, fork or tool. It belongs to Fluorite Launcher. Register your own Azure app, get it approved for the Minecraft API at https://aka.ms/mce-reviewappid, and use its ID instead.

| Setting | How |
|---|---|
| CurseForge API key | Never commit a key. Release builds get Fluorite's own key from the CI secret `FLUORITE_CURSEFORGE_API_KEY`. For local testing, set `FluoriteCurseForgeApiKey` in a git-ignored `Directory.Build.local.props` at the repo root, or set the `FLUORITE_CURSEFORGE_API_KEY` env var. Builds made with a borrowed key are for your machine only. Without a key, CurseForge is turned off and Fluorite uses Modrinth alone. |
| Discord Rich Presence | `-p:FluoriteDiscordAppId=<id>` or the `FLUORITE_DISCORD_APP_ID` env var. Without it, the setting is hidden. The Discord app needs an art asset named `logo`. |
| Offline accounts in Debug | `FLUORITE_DEV_ALLOW_OFFLINE=1`. Release builds always need an owned Microsoft account first. |
| Data root | Default `%LOCALAPPDATA%\FluoriteLauncher`. Change it in Settings or with `FLUORITE_ROOT`. |
| Microsoft client ID | Built in: `4fe34dcd-b6fe-4757-ad7c-76593988eda7` (`MicrosoftAuthOptions.FluoriteClientId`), for Fluorite Launcher only (see above). The Azure app is approved for the Minecraft API and has the redirect URIs `http://localhost` (browser sign-in) and `ms-appx-web://microsoft.aad.brokerplugin/4fe34dcd-b6fe-4757-ad7c-76593988eda7` (WAM; without it, sign-in falls back to the browser). |

## Ground rules

- **MSIX must not break mods.** File-system and registry write virtualization are off, so the launcher sees the real `%APPDATA%\.minecraft` and other launchers' folders. The game starts with the desktop-app breakaway policy, so `javaw` runs outside the package container and survives the launcher closing or updating.
- **The shared store** (`versions/ libraries/ assets/ runtime/`) stays compatible with the official launcher.
- **Other launchers' folders are guests.** Fluorite's data about them lives in `<data root>/external/…`, never in their folders. Prism/MultiMC write-back is opt-in per folder, and Fluorite never deletes another launcher's instance.
- **Modpack updates keep user changes.** `pack.json` records each installed file's SHA-1. Files the user edited are kept, and disabled mods stay disabled.
- **CurseForge files that block launchers** come from Modrinth when it has the byte-identical file. Otherwise Fluorite opens the CurseForge page and installs the file from Downloads once its SHA-1 matches. The API key is only ever sent to `api.curseforge.com`.
- **Skin-site passwords** are sent once and never stored.
- **Updates** never move a file to a less stable channel than the one installed.
