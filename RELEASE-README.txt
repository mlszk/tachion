tachion Windows client

1. Run tachion.exe.
2. Choose your local sync folder.
3. Enter your server URL, for example:
   wss://tachion.example.com/ws
4. Enter a unique device name.
5. Enter your sync token.
6. Click Save, then Start sync.

Requires .NET 8 Desktop Runtime.

Settings are stored in:
%APPDATA%\tachion\tachion.config.json

The token is encrypted using Windows DPAPI and is not stored as plain text.
