@echo off
setlocal
cd /d "%~dp0\TachionWinForms"
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false
endlocal
