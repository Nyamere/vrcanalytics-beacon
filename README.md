# VRCAnalytics Beacon

Drop-in visitor analytics for your VRChat worlds. Tracks joins, leaves, session duration, FPS, and instance size — all from a Unity package, no setup beyond pasting your dashboard key.

[**vrcanalytics.com**](https://vrcanalytics.com) · [Docs](https://vrcanalytics.com/docs)

---

## Install

### Via VCC (recommended)

**[Add to VCC](vcc://vpm/addRepo?url=https://vrcanalytics.com/vpm/index.json)** — one-click, opens VRChat Creator Companion

Or paste the repository URL manually in VCC → Settings → Packages → Add Repository:

```
https://vrcanalytics.com/vpm/index.json
```

Then find **VRCAnalytics Beacon** in your project's package list and click "+".

### Setup in Unity

1. Sign up at [vrcanalytics.com](https://vrcanalytics.com), register your world, copy the setup key.
2. In Unity: `VRCAnalytics → Enter Setup Key`. Paste the key. Save.
3. Build & Publish via the VRChat SDK control panel as you normally would.
4. The build hook bakes per-platform beacon URLs into your scene; the setup key is **never embedded** in the uploaded world.

Full walkthrough at [vrcanalytics.com/docs/unity-setup](https://vrcanalytics.com/docs/unity-setup).

---

## What does it actually send

Per player session, the beacon fires:
- **Join** with platform bucket (PC VR / Desktop / Quest) and instance-size bucket
- **Leave** with platform bucket
- **FPS sample** every 60 seconds with the FPS bucket
- **Zone enter/exit** if you've configured zones with the optional Zone Beacon component

What it does **not** collect:
- IP addresses, User-Agents, GPU/CPU/RAM info, OS, system language
- VRChat user IDs, display names, avatars, friend info, voice activity
- Any per-player position data beyond explicit zone presence

The IP at request time is hashed with a server-side salt to pair join + leave into a session, then immediately discarded — never persisted.

See the [Privacy Policy](https://vrcanalytics.com/legal/privacy) for the full list.

---

## Disclaimer

VRCAnalytics is an independent project — we are not affiliated with, endorsed by, or sponsored by VRChat Inc. "VRChat" is a trademark of VRChat Inc.

---

## License

MIT. See `LICENSE`.
