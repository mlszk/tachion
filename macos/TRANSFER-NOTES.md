# tachion macOS folder transfer notes

Copy this whole `macos/` folder to the root of the tachion GitHub repository.

Expected repository structure after copy:

```text
tachion/
  windows/
  server/
  macos/
    TachionMac.xcodeproj
    TachionMac/
    README.md
    TRANSFER-NOTES.md
```

This source folder contains no real sync token and no real server URL.
The app stores the token in macOS Keychain at runtime.
