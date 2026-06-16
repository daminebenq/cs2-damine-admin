# Damine Admin — CounterStrikeSharp admin plugin

A lightweight, **self-contained** admin / moderation plugin for
[CounterStrikeSharp](https://docs.cssharp.dev/) (Counter-Strike 2), built for the
MUS SOU MANO fleet.

It uses **only built-in CSSharp APIs** — the built-in chat menus and the built-in
admin/permission system — with **no external shared assemblies and no
NuGet-resolved dependencies**. That means it registers reliably with
`PluginResolveNugetPackages=false` and never conflicts with stacks like
GameModeManager / MenuManagerAPI / PlayerSettings. Every command body is wrapped
and all entity actions are deferred to the next frame, so a handler error can
never crash the game server.

## Features

- **Moderation** — `kick`, `ban` / `addban` / `unban` (file-backed),
  timed `gag` / `mute` / `silence`, `slay`, `slap`, `freeze` / `unfreeze`,
  `noclip`, `hp`, team moves.
- **Maps** — workshop `map` change (with a short delay) and an in-chat **map vote**.
- **Admin menu** — `!admin` built-in chat menu surfacing the common actions.
- **Timed advertisements** — rotating feature-discovery messages on a timer.
- **`!web` / `!stats` / `!profile`** — public command (no permission required)
  that DMs the player the stats-site URL and a direct link to their own
  `player.php?id=<steamid>` profile, plus `!rank` / `!store` hints.

## Commands

Chat (`!`) and console (`css_`) variants are both registered, e.g. `!kick`,
`!ban`, `!gag 10 <name>`, `!map`, `!vote`, `!admin`, `!web`. Admin commands are
gated by CSSharp permission flags; `!web` is open to everyone.

## Build

Requires the .NET SDK matching your CSSharp API version.

```bash
dotnet build -c Release
```

The build output `DamineAdmin.dll` goes in
`addons/counterstrikesharp/plugins/DamineAdmin/` on the server.

## Install

1. Copy `DamineAdmin.dll` into `addons/counterstrikesharp/plugins/DamineAdmin/`.
2. Configure admins/flags via CSSharp's `admins.json` / `admin_groups.json`.
3. `meta load` CSSharp (or restart) and the plugin registers on next map.

## License

[MIT](LICENSE) © daminebenq (MUS SOU MANO)
