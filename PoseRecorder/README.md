# PoseRecorder

A BepInEx mod for Rec Room that records your head and arm positions relative to your character's center when you press Ctrl+Alt+Super+5, and saves the recording to BepInEx/config/CustomEmote.cfg in the KeyframesJson format used by the CustomEmote mod.

Ai slop readme 🙂

## Building

To build this mod, you need:

- .NET 6.0 SDK  
- The Rec Room game installed with BepInEx  
- The reference DLLs from the BepInEx folder (should be present if you have the game)

### Steps

1. Open a terminal in this directory (Mod/PoseRecorder).  
2. Run `dotnet restore` to restore NuGet packages (may require internet access).  
3. Run `dotnet build` to build the DLL.  
4. The resulting DLL will be in `bin/Debug/net6.0/PoseRecorder.dll`.  
5. Copy the DLL to your BepInEx plugins folder (usually `BepInEx/plugins/`), or leave it in the Mod/PoseRecorder folder if BepInEx is configured to look there.

## Configuration

The keybind is configurable in the code:

- Keybind: Ctrl \+ Alt \+ Super \+ 5 (change the properties `_ctrlPressed`, `_altPressed`, `_superPressed`, `_key5Pressed` if needed)  
- ~~Frame rate: 15 FPS (change `_recordInterval` to 1f / desiredFPS)~~ why does ai think there is frame rate in config?

## Usage

- Press Ctrl+Alt+Super+5 to start recording.  
- Press Ctrl+Alt+Super+5 again to stop recording and save to CustomEmote.cfg.  
- The recording will be available in the CustomEmote mod as a custom emote.

## Notes

- This mod overwrites the KeyframesJson field in CustomEmote.cfg while preserving the PlaybackMode.  
- Ensure the CustomEmote mod is installed if you wish to play back the recorded emote.