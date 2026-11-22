# Third-party notices

Fluorite Launcher is licensed under the GNU General Public License, version 3 (see `LICENSE`). It includes the
following third-party material.

## Prism Launcher instance icons

`src/Fluorite.App/Assets/InstanceIcons/` holds Prism Launcher's built-in instance icons, so that Prism and MultiMC
instances that use them (`iconKey` in `instance.cfg`) show the same icon in Fluorite. They are unmodified copies of
`launcher/resources/multimc/*/instances/` from https://github.com/PrismLauncher/PrismLauncher (commit `7cbd473`). For
each icon, the scalable version is used where there is one, otherwise the largest PNG.

    Prism Launcher - Minecraft Launcher
    Copyright (C) 2022-2026 Prism Launcher Contributors

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, version 3.

The `*_legacy` icons, and others that predate Prism, come from MultiMC:

    Copyright 2013-2021 MultiMC Contributors

    Licensed under the Apache License, Version 2.0 (the "License");
    you may not use this file except in compliance with the License.
    You may obtain a copy of the License at

        http://www.apache.org/licenses/LICENSE-2.0

    Unless required by applicable law or agreed to in writing, software
    distributed under the License is distributed on an "AS IS" BASIS,
    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
    See the License for the specific language governing permissions and
    limitations under the License.

The icons named `fabricmc`, `quiltmc`, `neoforged`, `forge`, `liteloader`, `modrinth`, `ftb_logo`, `ftb_glow` and
`prismlauncher` show the logos of those projects, which remain their owners' trademarks. Fluorite shows them only for
instances that already use them.
