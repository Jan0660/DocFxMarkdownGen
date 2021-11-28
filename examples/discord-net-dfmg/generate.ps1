#!/usr/bin/pwsh

cd Discord.Net
dotnet build
cd ..
docfx metadata
dfmg
