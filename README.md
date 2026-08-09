# iPad Tablet Backends

Experimental Linux and Windows backends for using an iPad Pro and Apple Pencil as a low-latency
screen and OpenTabletDriver-compatible pen tablet.

The iPad client is developed separately and is not part of this public repository.

## Backends

- [LinuxBackEnd](LinuxBackEnd/README.md): Wayland/X11 capture, VA-API, WebSocket, authenticated UDP,
  USBMux and UHID/OpenTabletDriver.
- [WindowsBackEnd](WindowsBackEnd/README.md): FFmpeg hardware encoding, WebSocket, authenticated UDP,
  USBMux and a named-pipe OpenTabletDriver device hub.

Both implementations understand the same H.264, Pencil and live stream-settings protocol. The
Gaming Mode can change resolution, FPS, bitrate and CBR/VBR, or stop video completely while keeping
Pencil input active.

This project is experimental. Do not expose its unencrypted LAN ports directly to the internet.

## License

MIT; see [LICENSE](LICENSE).
