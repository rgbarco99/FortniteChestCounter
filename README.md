<p align="center"><img src="docs/icon.png" width="96" alt=""></p>

<h1 align="center">Fortnite Chest Counter</h1>

<p align="center"><b>English</b> · <a href="LEEME.md">Español</a></p>

Counts the chests you open in Fortnite (PC), match after match, and shows them in a dashboard in
your browser: chests per match and per minute, special and spirit chests, kills, place, your
squad, the friends you play with most, and the odds of each weekly treasure.

It only reads the `.replay` files Fortnite saves on your PC. It does not touch the game, does not
read its memory and does not use AI. Free and open source.

> Not affiliated with or endorsed by Epic Games. Fortnite is a trademark of Epic Games, Inc.

## Getting started

1. In Fortnite, turn on **Settings → Game → Replays → Record Replays**.
2. Download `FortniteChestCounter.exe` from the latest release on the **Releases** page and put it
   in a folder of your own (Desktop, Documents…), not in Program Files.
3. Open it. The first time Windows may show “Windows protected your PC” because the program is not
   signed: click **More info → Run anyway**.
4. The dashboard opens in your browser at `http://127.0.0.1:5380`. There is no window to keep
   open: the program sits in an icon next to the clock while you play, and each match shows up by
   itself when it ends. Double-click the icon to open the dashboard again, right-click it to quit.
   Opening the program again while it is running just brings up its dashboard.

   Windows 11 hides new tray icons behind the **^** chevron next to the clock; drag it out of
   there to keep it in sight.

Languages: English, Spanish and French (EN / ES / FR buttons in the header).

## Updates

The program checks GitHub for a new version when it starts and every few hours. When there is
one, a bar with an **Update** button appears: it downloads the new `.exe`, replaces the current
one and restarts; the page reconnects by itself. Your matches and settings live in
`%LOCALAPPDATA%\FortniteChestCounter` and are kept.

A **What's new** panel then opens once at the top of the page with what went into that version.
The **What's new** button in the header brings it back at any time, with the earlier versions
folded underneath.

## What counts as a chest

- A chest you open with the interact key, or break open. Each chest counts once per match.
- A chest that breaks because you destroyed what held it up counts as yours (“broken
  indirectly”): the game does not record who broke it, so the program uses where its loot landed.
- Chests the game refills during a match count again when you open them again.
- Special (blue) chests are recognised by the loot tier the game gives them.
- Counts arrive when Fortnite closes the replay: a few seconds after you return to the lobby, or
  when the next match starts if you chose “Play again”. Fortnite encrypts the replay while you
  play, so there is no live chest count; the **Live** tab shows the match clock, your usual pace
  and the summary as soon as it is available.
- Your own replay only contains chests opened within about 300 m of you, so teammates' chest
  counts are not shown.

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

- `start.cmd` builds and starts the program.
- `publish.cmd` builds `dist\FortniteChestCounter.exe`, a single file that does not need .NET.
- `start.cmd --dump "path\to\file.replay"` prints what is read from one replay, as JSON.

Builds made by hand never update themselves. Only the release workflow writes the repository
name into the executable.

### Publishing a new version (maintainers)

First say what changed: add an entry at the top of `ARR_NEWS`, in `src/ChestTracker/ui/index.html`
(version, date and one line per change, written out in Spanish, English and French). That list is
what the **What's new** panel shows.

Then on GitHub: **Releases → Draft a new release**, type a new tag such as `v1.2.0`, publish. Or with
git: `git tag v1.2.0` and `git push origin v1.2.0`. Either way the **Release** workflow builds `FortniteChestCounter.exe` on Windows and attaches it to a new
GitHub release. Installed copies pick it up within a few hours, or straight away with
**Settings → Updates → Check for updates**.

## Code map

```
src/ChestTracker/
  Program.cs         command line, local web server, API
  ReplayParser.cs    replay reader: containers, players, positions, header
  ReplayWatcher.cs   watches the replay folder, follows the match in progress
  GameLogTail.cs     follows Fortnite's log: match finished, searching for a match
  Summarizer.cs      per-match figures for the dashboard
  Updater.cs         checks GitHub releases and replaces the executable
  TrayIcon.cs        the icon next to the clock: dashboard, read the folder, quit
  Instance.cs        one copy at a time: a second one hands over to the first
  Store.cs           settings and matches on disk
  ui/index.html      the whole interface (one file, no external dependencies)
vendor/FortniteReplayDecompressor/   replay decoding library (MIT, by Shiqan)
```

## License

MIT. The bundled replay library keeps its own MIT license.
