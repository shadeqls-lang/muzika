# muzika

![muzika](https://github.com/shadeqls-lang/muzika/blob/main/muzika.png?raw=true)

> **beta**: muzika is still in beta. expect bugs and rough edges. it keeps getting updates, and it updates itself.

a lightweight soundcloud desktop client for windows. it's a standalone `.exe`, not a browser tab, so you can keep music on while gaming without your browser eating your ram.

---

## features

- **library**: your soundcloud likes, feed and playlists, plus everything you saved in muzika
- **radio**: start an endless queue of similar tracks from any song
- **saved + playlists**: the heart and the **+** keep tracks in muzika itself, no soundcloud captcha
- **find**: search soundcloud or paste any link (track, playlist, profile, someone's likes)
- **profiles**: artist pages with banner, stats, tracks, likes, playlists and reposts
- **instant start**: tracks stream piece by piece and the next ones load ahead of time
- **crossfade**: smooth transitions between tracks, no click at the start
- **equalizer**: 10 bands, preamp and presets
- **themes**: noir, paper, ash, blood, frost
- **stats**: minutes listened by day and hour, top tracks and artists, streaks
- **discord**: show what you're listening to in your status
- **tray + games**: closes to the tray, draws nothing while you're in a game, media keys work anywhere
- **auto-updates**: new versions download in the background, one click to restart into them
- **design**: dark liquid glass, sf pro display, all lowercase, no white title bar

## planned

- uploading your own tracks
- more ui and ux polish

## install

1. download `muzika.exe` from the [latest release](https://github.com/shadeqls-lang/muzika/releases/latest)
2. put it in its own folder and run it (it keeps its data in `muzika_data` next to the exe)
3. press **link soundcloud** and log in

after that it updates itself. moving to a new folder? settings → **move from another folder** → import data.

needs windows 10/11 with the microsoft edge webview2 runtime (already there on most pcs).

## build it yourself

run `build.bat`. it uses the c# compiler that ships with windows (.net framework 4) and the webview2 sdk in `lib/`. you get one standalone `muzika.exe`.

## releasing (for me)

1. bump the number in `version.txt` (and commit)
2. push a tag: `git tag v0.2.1 && git push origin v0.2.1`
3. github actions builds `muzika.exe` and publishes the release; every installed muzika picks it up within a few hours

## feedback

found a bug or have an idea?

- telegram: [@emopunk](https://t.me/emopunk)
- discord: [join the server](https://discord.gg/VzGdZZ5H8)

---

<sub>muzika is an unofficial fan-made client. not affiliated with or endorsed by soundcloud. soundcloud is a trademark of soundcloud global limited & co. kg.</sub>


