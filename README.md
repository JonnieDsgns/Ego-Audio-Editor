# Ego Audio Editor

Preview, export, and replace music, voice lines, and sound effects in **F1 25**. Create mods compatible with Simplified Launcher and keep an editable project for future changes.

## Features

- Open `.nfs` speech archives, `.bnk` sound banks, and `.wem` audio files.
- Browse audio folders and search by sound name or ID.
- Preview original audio and imported replacements.
- Export audio as WAV or WEM.
- Import WAV or compatible WEM replacements, including shorter and longer audio where supported.
- Display original speech filenames with separate ID information.
- Export Launcher-compatible mod ZIPs with a mod name, creator, and version.
- Save a companion `.egoaudio` project and reopen it to continue editing.
- Highlight modified files and sounds in **bold**.
- Preserve original game files during export.

## Installation

1. Download and extract a packaged Windows x64 release, if available.
2. Keep the `license` folder beside `EgoAudioEditor.exe`.
3. Run `EgoAudioEditor.exe`.

The single-file release includes .NET, FFmpeg, vgmstream, and the required libraries. No separate runtime installation is required. The source checkout must be built before it can be run.

## How to Use

1. Choose **Open File / Project** or **Open Audio Folder**.
2. Select the sound you want to edit and preview it.
3. Export it as WAV if you want to edit it externally.
4. Save your edited audio as **16-bit PCM WAV**, retaining the original sample rate and channel count.
5. Import it with **Replace Selected Sound** and preview the replacement.
6. Select **Export to Mod**, enter the mod name, creator, and version, and choose an output location.
7. Import the resulting ZIP into **Simplified Launcher**.

A matching `.egoaudio` project is saved beside the mod ZIP. Open it through **Open File / Project** to continue editing. Modified entries remain individually reversible.

## Supported Audio and Limitations

- Replacement duration can differ from the original where the format and control data support it.
- Music editing requires the original matching `metadata.bnk` beside `music.bnk` and the associated external streams. The mod export includes the generated companion files.
- Eight-channel Opus can be previewed and exported, but replacement is not currently supported.
- Certain special plugin media cannot be previewed or replaced.
- Preview plays individual media; it does not simulate complete Wwise events.

Keep backups before installing mods. File-level verification does not guarantee every replacement will behave correctly in-game. Game audio and other game files are not included in this repository.

## Build from Source

Requirements: **Windows x64**, the **.NET 9 SDK**, and **Git LFS**.

```powershell
git lfs install
git clone https://github.com/JonnieDsgns/Ego-Audio-Editor.git
cd Ego-Audio-Editor
git lfs pull
dotnet restore EgoAudioEditor.csproj
dotnet build EgoAudioEditor.csproj -c Release
```

Open `EgoAudioEditor.csproj` directly in Visual Studio if you prefer. This is a standalone project; the AlterEgo Toolsuite is not required.

Use a Git clone with LFS rather than GitHub's generated source ZIP: the vendored encoder, decoder, and libraries must be downloaded as real binaries rather than LFS pointers.

## Publish

```powershell
dotnet publish EgoAudioEditor.csproj -p:PublishProfile=SingleExe
```

The profile in `Properties/PublishProfiles/SingleExe.pubxml` produces a self-contained Windows x64 executable under `publish/SingleExe/`, with a separate `license/` folder. Bundled helper tools are extracted temporarily by the .NET runtime when needed.

The GitHub Actions workflow builds the release and uploads it as a workflow artifact. Build output, personal settings, and audio projects are excluded from Git.

## License

This project is licensed under the **GNU General Public License v3.0 only** (`GPL-3.0-only`). See [LICENSE](LICENSE) for the full terms.

You may use, study, modify, and redistribute the software. If you distribute modified versions or a combined work derived from this code, you must comply with GPLv3, including providing the corresponding source code under GPLv3 and retaining the required notices. Private modifications do not have to be published, and using this editor to create an audio mod does not by itself place that mod under the GPL.

Bundled third-party components retain their respective licenses. Their license texts and notices are included in `Distribution/` and, where applicable, `vendor/`. Published releases place the notices in `license/` beside the executable.
