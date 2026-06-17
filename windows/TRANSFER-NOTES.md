# tachion Windows v0.1.3 transfer notes

This is the current full Windows source package for continuing development in a new chat.

Included in this version:

- v0.1.1 locked-file retry fix
- v0.1.2 delete propagation
- v0.1.3 whole-folder copy/move upload fix
- separate startup options:
  - `Run at Windows startup` — launches tachion at Windows login
  - `Start sync when opened` — automatically starts sync when the app opens

Build:

```powershell
cd TachionWinForms
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false
```

Use the exe from:

```text
TachionWinForms\bin\Release\net8.0-windows\win-x64\publish\tachion.exe
```

- UI polish: wider default window so the `Start sync when opened` checkbox is fully visible.

- UI polish: window title, title label and tray tooltip show executable version from assembly metadata.
