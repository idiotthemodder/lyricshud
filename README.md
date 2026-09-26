# lyricshud

A Gorilla Tag mod that shows a lyrics/music HUD in-game, with a Python helper for fetching lyrics. Built and tested on Linux.

## What it does

- Shows currently playing song info and lyrics on the in-game HUD
- Lets you change songs from inside the game
- LyricsHelper.py runs locally on 127.0.0.1:8765 and feeds lyric data to the plugin

## Structure

Plugin.cs          BepInEx plugin, the mod itself
LyricsHelper.py     Python helper, serves lyrics locally
LyricsHud.csproj    project file, builds the plugin DLL

## Building

dotnet build

Drop the built DLL into your BepInEx/plugins folder.

## Notes

Built with AI-assisted help (Claude). Linux-only for now.
