# Realme Buds (WinUI 3)

Native WinUI 3 front-end for Realme / OPPO earbuds: battery, noise control
(ANC / transparency / enhance voices / wind reduction / game mode) and touch gestures.

## Requirements
- Windows 10 1809+ / Windows 11
- .NET 8 Desktop Runtime (https://dotnet.microsoft.com/download/dotnet/8.0)
- Windows App SDK Runtime (https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads)
- Earbuds already paired *and connected* to this PC

## Tested devices
- Realme T310

## Build & run
    cd RealmeBuds
    dotnet run -p:Platform=x64

Or build a release exe:

    dotnet build -c Release -p:Platform=x64
    bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\RealmeBuds.exe

Visual Studio 2022: install the "Windows application development" workload, open the
folder/csproj, choose the x64 platform and press F5.

## Project structure
- `UI\` - WinUI window and its XAML code-behind
- `Services\` - Earbud protocol client, transports, and COM-port discovery
- `Configuration\` - Persisted application settings
- `Assets\` - Images and other application assets, including light and dark theme logos
- `App.xaml`, `App.xaml.cs` - Application startup and resources

## Releases
Every push to `master` builds a small x64 ZIP that uses the installed Windows and
Windows App SDK runtimes. The GitHub Action uploads the ZIP as a workflow artifact
and attaches it to a new GitHub Release with an automatically generated tag such as
`build-42`.

To create a manually versioned release instead, push a version tag such as `v1.0.0`
after changing the workflow trigger from `master` to tags:

    git tag v1.0.0
    git push origin v1.0.0

## Using it
1. Pick your earbuds in the device list and press **Connect**. The default connection mode, **Auto**, finds the
   Bluetooth COM port Windows created for those earbuds.
