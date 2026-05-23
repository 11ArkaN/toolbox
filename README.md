# Toolbox

Toolbox is a native Windows desktop utility built with C# and WinUI 3. It combines everyday media, PDF, image, and file-renaming workflows into one focused application with a collapsible sidebar and separate tool pages.

## Features

- **Audio Splitter** - splits one audio file into two parts in the background with progress reporting.
- **Slide Splitter** - extracts slide regions from PDF pages, including manual visual cropping with multiple editable rectangles per page.
- **Batch Renamer** - renames many files with readable rule-based options, live preview, numbering, dates, casing, prefixes, suffixes, and extension handling.
- **PDF Toolbox** - merges PDFs, splits every page into separate files, deletes or rotates pages, compresses PDFs, and exports pages to JPG or PNG.
- **Image Converter** - batch converts images between JPG, PNG, and WebP with resizing, aspect-ratio crop, quality settings, and output suffixes.

## Requirements

- Windows 10 version 1809 or newer.
- Windows App Runtime matching the Windows App SDK version used by the application.
- FFmpeg and FFprobe available in `PATH` for the Audio Splitter tool.

The MSI installer is self-contained for the .NET application runtime, installs Toolbox for the current Windows user by default, and creates a Start Menu shortcut.

## Build From Source

Install the .NET SDK, then build the app:

```powershell
dotnet build -c Release -r win-x64 -p:SelfContained=true -p:WindowsAppSDKSelfContained=false -p:PublishTrimmed=false -p:PublishReadyToRun=false -o artifacts\app-win-x64
```

Run the built app:

```powershell
.\artifacts\app-win-x64\Toolbox.exe
```

## Build The Installer

The installer is authored with WiX Toolset and packages the verified WinUI build output into an MSI.

```powershell
dotnet build Installer\Toolbox.Installer.wixproj -c Release -p:ProductVersion=1.0.0 -p:AppBuildDir="$PWD\artifacts\app-win-x64"
```

The MSI is produced under:

```text
Installer\bin\x64\Release\ToolboxSetup.msi
```

## Repository Layout

```text
Assets/      Application icons and Windows assets
Installer/   WiX MSI installer project
Models/      Shared data models
Pages/       WinUI tool pages
Services/    Tool implementations and file processing services
```
