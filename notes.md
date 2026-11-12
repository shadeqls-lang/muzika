## fixes
- updates no longer depend on the github api. it has a limit per ip, and on shared or vpn connections it runs out, so muzika couldn't see new versions ("404"). now it reads the latest release straight from github.com

## in 0.4.0
- **likes are one list**: the heart in muzika is a like. muzika keeps its own likes (soundcloud refuses likes from apps), and library → likes shows them together with your soundcloud likes, newest first
- un-hearting a soundcloud like hides it from likes in muzika
- **covers view**: library can show tracks as a list or as covers, like on soundcloud. the switch is next to the tabs
- **update screen**: after an update the new version number builds itself and the notes follow, like this

## 0.4.0 fixes
- library opens with the right tab lit, and tabs switch instantly: what they loaded is kept and refreshed quietly
- "what's new" always works, the notes ship inside the app
- settings are grouped into look, playback and app, cards line up without gaps
- spacing cleaned up across pages
