# Security Policy

Please do not disclose a suspected vulnerability in a public issue. Contact the maintainer through
the email address shown on the GitHub profile for `Davidio1777` and include reproduction steps,
affected versions and the expected impact.

Only the latest release candidate is supported. LAN mode is designed for a trusted private network,
uses AES-256-GCM authenticated encryption and requires a token of at least 16 UTF-8 bytes. USB-only
mode trusts Apple's local usbmuxd pairing. Do not expose UDP 8766 or 8767 to the public internet.
