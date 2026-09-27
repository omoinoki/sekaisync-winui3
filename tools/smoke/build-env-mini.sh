#!/usr/bin/env bash
exec env PROGRAMDATA="${PROGRAMDATA:-C:/ProgramData}" APPDATA="${APPDATA:-$USERPROFILE/AppData/Roaming}" LOCALAPPDATA="${LOCALAPPDATA:-$USERPROFILE/AppData/Local}" "$@"
