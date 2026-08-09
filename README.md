# iPad Tablet Backends

Linux and Windows 11 backends for using an iPad Pro and Apple Pencil as a low-latency display and an
OpenTabletDriver-compatible pen tablet. The iPad client is developed in a separate private repository
and is not included here.

## Choose a backend

- [Linux backend](LinuxBackEnd/README.md): Wayland/X11 capture, VA-API, encrypted UDP, USBMux,
  UHID and automatic OpenTabletDriver configuration.
- [Windows backend](WindowsBackEnd/README.md): desktop capture through FFmpeg, AMD/NVIDIA/Intel
  hardware encoding, encrypted UDP, USBMux, an OpenTabletDriver device hub, and a WPF launcher.

The backends use the same protocol and support live Gaming Mode changes for resolution, 60/120 FPS,
bitrate, CBR/VBR and a video-off tablet-only mode. LAN transport is encrypted UDP only.

## Network security

LAN traffic uses two UDP ports: `8766` for video and metadata, and `8767` for Pencil and control data.
Every datagram is protected with AES-256-GCM. Directional keys are derived from the configured token
with HKDF-SHA256; headers are authenticated and recently seen nonces are rejected. Tokens must contain
at least 16 UTF-8 bytes. Use a randomly generated token and do not expose either port to the public
internet.

USB uses the trusted usbmuxd pairing instead and deliberately does not require or transmit a token.

## Status

This is a release candidate, not a promise of latency-free operation on every GPU, compositor or
network. Test the complete iPad/backend/OTD chain on your hardware before relying on it professionally.
Bug reports should include the backend log, OS version, GPU, encoder, compositor and OTD version.

## License

MIT; see [LICENSE](LICENSE).
