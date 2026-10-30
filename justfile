set windows-shell := ["powershell.exe", "-NoProfile", "-Command"]

default:
    @just --list

# Build REF first, fetch the reference DLL, then build Delta-V.
build version="1.7":
    powershell.exe -NoProfile -File build/Build.ps1 -Version {{version}}

# Build when available, then package current 1.6 and 1.7 assemblies outside the repository.
release:
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/Release.ps1

test-ref:
    just --justfile ../RimExodusFramework/justfile test
