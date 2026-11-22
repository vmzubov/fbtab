#!/usr/bin/env bash
# Builds fbtab as one file that needs no .NET installed: out/win-x64/fbtab.exe and out/linux-x64/fbtab.
set -euo pipefail
cd "$(dirname "$0")/.."
for rid in win-x64 linux-x64; do
  dotnet publish src/Fbtab.Cli -c Release -r "$rid" --self-contained true \
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none \
    -o "out/$rid"
done
ls -l out/*/fbtab*
