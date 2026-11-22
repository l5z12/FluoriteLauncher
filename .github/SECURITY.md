# Security policy

## Supported versions

Fluorite Launcher hasn't had a release yet. Fixes go into `main` and the next build.

## Reporting a vulnerability

Please don't open a public issue. Report it privately: on this repository's **Security** tab, choose **Report a vulnerability**.

Say what's affected, how to reproduce it, what an attacker could do with it, and the version or commit you tested.

## What to report

For example:

- Account tokens, the encrypted account store or skin-site passwords leaking: into logs, uploaded logs, other files or the network.
- Downloads (game files, Java, mod loaders, mods, modpacks) that can be swapped or that are used without their hash being checked.
- A modpack, import or `fluorite://` link that writes outside its instance or data folder, or runs code without the user starting the game.
- Fluorite's CurseForge API key going anywhere other than `api.curseforge.com`.

Not in scope: malicious mods or packs themselves (report those to Modrinth or CurseForge), and vulnerabilities in Minecraft or Java.
