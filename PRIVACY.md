# Privacy Policy

Last updated: 22 November 2026

This policy covers Fluorite Launcher ("Fluorite"), the Minecraft: Java Edition launcher for Windows from https://github.com/l5z12/FluoriteLauncher and, once released, the Microsoft Store. "We" means the Fluorite Launcher developers.

## The short version

- Fluorite has no telemetry, analytics, ads or tracking. We don't run any servers, and Fluorite never sends anything to us.
- Your accounts, settings, instances and logs stay on your PC. Sign-in tokens are encrypted so that only your Windows user can read them, or kept by Windows itself for accounts you add through Windows' sign-in.
- To do its job, Fluorite connects directly to the services you'd expect: Microsoft and Mojang to sign you in and download the game, mod sites to find mods, and the mirrors and skin sites you choose. They see your IP address and what Fluorite asks them for, under their own privacy policies.
- Nothing is made public unless you ask for it, such as a log upload. The one exception is **Show on Discord**: in builds that offer it, it's on by default and shows people who can see your Discord activity what you're playing. You can turn it off in **Settings**.

## What Fluorite keeps on your PC

Everything is in Fluorite's data folder, `%LOCALAPPDATA%\FluoriteLauncher` unless you've chosen another folder in **Settings**. The one exception: for a Microsoft account you add through Windows' built-in account sign-in, Windows keeps the sign-in, outside that folder.

| File or folder | What it holds |
|---|---|
| `accounts.dat` | Your accounts: player names and UUIDs, your Xbox user ID, whether the account owns Minecraft, which skin site an account belongs to, and sign-in tokens. The whole file is encrypted with Windows' Data Protection API (DPAPI) for your Windows user. |
| `settings.json` | Your settings. |
| `logs\` | Fluorite's own logs: one file for each day you use it, and the 14 most recent are kept. They include file paths (which contain your Windows user name), instance names and the web addresses Fluorite downloaded from. Access tokens are masked in the launch command they record. If you start Fluorite with `--trace-exceptions`, it also writes every error to `exceptions.log` there, which stays until you delete it. |
| `skins\` | Skins you chose for offline accounts. |
| Instances, versions, libraries, assets, Java runtimes and caches | The game, the content you install, and cached news and metadata so Fluorite works offline. |

Skin-site passwords are sent to the skin site once, when you sign in, and are never stored.

## Who Fluorite connects to

| Service | When | What it receives |
|---|---|---|
| Microsoft sign-in (`login.microsoftonline.com`, or Windows' built-in account sign-in) and Xbox Live | You add a Microsoft account, or Fluorite refreshes its sign-in | You sign in on Microsoft's own page or dialog; Fluorite never sees your password. Fluorite then exchanges the tokens Microsoft gives it with Xbox Live. |
| Minecraft services (`api.minecraftservices.com`) | Signing in, opening an account, changing a skin or cape | Your tokens. Returns your profile, skins, capes and whether you own the game. Receives any skin you upload. |
| Mojang (`*.mojang.com`, `libraries.minecraft.net`, `resources.download.minecraft.net`, `textures.minecraft.net`) | Installing, updating and launching versions and Java; the home page's news; showing skins | Requests for game files, news and player profiles. |
| BMCLAPI (`bmclapi2.bangbang93.com`) and MCIM (`mod.mcimirror.top`) | **Download source** is set to BMCLAPI or Auto, or an official server can't be reached | The same requests that would otherwise go to the official servers. |
| Mod loader sites: Fabric, Quilt, Legacy Fabric, Forge, NeoForge, LiteLoader, OptiFine, Maven Central | Installing a mod loader | Requests for loader versions and files. |
| Modrinth and CurseForge | Browsing, installing and updating mods, other content and modpacks | Your searches. To recognise installed files and find updates, the hashes of files in your instances, never the files themselves. |
| FTB and Technic | Browsing and installing their modpacks | Your searches and requests for modpack files. |
| Skin sites you add (such as LittleSkin and Ely.by) | Adding or playing with a skin-site account | Your user name or email and password when you sign in, then tokens. |
| authlib-injector (`authlib-injector.yushi.moe`, or BMCLAPI's mirror of it) | Playing with a skin-site account, or with a custom skin on an offline account | Requests for the authlib-injector file. Never your sign-in details. |
| mclo.gs | Only when you click **Upload log** and confirm | The game session's log. See below. |

Every request Fluorite sends itself carries the header `User-Agent: FluoriteLauncher/<version> (+https://github.com/l5z12/FluoriteLauncher)`. Microsoft's sign-in page runs in your browser or in Windows' sign-in dialog, which send their own headers. Fluorite adds no identifier of its own that could track you.

## Things you choose to share

- **Upload log** sends the game session's log to mclo.gs and gives you a link that anyone can open. Before uploading, Fluorite removes your access tokens, sign-in names and Windows user name. Your player name and mod list stay in it. mclo.gs keeps the log under its own privacy policy.
- **Save diagnostics** writes a zip file to a place you pick and sends nothing. It holds the session log, the game's logs and crash reports, the launch command, your Windows version, CPU, memory and graphics cards, and your mod list, with the same details removed as for an upload.
- **Show on Discord** (in **Settings**, on by default in builds that offer it) tells the Discord app on your PC, over a local connection, the name of the instance you're playing, its Minecraft version and mod loader, and how long you've played. Discord shows that to people who can see your activity, under Discord's privacy policy. Fluorite does nothing if Discord isn't running.

## The game, mods and Windows

- Minecraft runs on its own once Fluorite starts it. It sends its own telemetry to Mojang under the Microsoft Privacy Statement.
- Mods are programs written by others and can connect to the internet on their own. Fluorite doesn't control what they do.
- If you install Fluorite from the Microsoft Store, Windows and the Store may collect diagnostic data about it under the Microsoft Privacy Statement and your Windows diagnostic data settings. Microsoft gives app publishers summary reports of installs and crashes from that data. We don't receive anything that identifies you.

## Your choices

- Turn off **Show on Discord**, or pick a different **Download source**, in **Settings**.
- Removing an account in **Accounts** deletes it and its tokens from `accounts.dat`. Fluorite then asks the skin site to sign a skin-site account out, and asks Windows to forget a Microsoft account you added through Windows' sign-in. Both are best effort: if the skin site can't be reached, its sign-in may stay valid, so sign out on the site yourself if you need to be sure. To also revoke Fluorite's access to your Microsoft account, use https://account.live.com/consent/Manage.
- Uninstalling Fluorite leaves its data folder in place, so your instances survive a reinstall. To remove everything, first remove your accounts in **Accounts**, so the sign-ins Windows keeps are removed too, then delete the folder.

Because we never receive your data, we have none to show you, export or delete: everything is on your PC, under your control.

## Children

Fluorite doesn't collect personal information from anyone, including children.

## Changes

If Fluorite starts handling data differently, we'll update this policy before that version is released. The date at the top shows the last change, and the file's history on GitHub shows every change.

## Contact

Questions about this policy: open an issue at https://github.com/l5z12/FluoriteLauncher/issues.
