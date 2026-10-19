# Realme Buds (WinUI 3)

Native WinUI 3 front-end for Realme / OPPO earbuds: battery, noise control
(ANC / transparency / enhance voices / wind reduction / game mode) and touch gestures.

## Requirements
- Windows 10 1809+ / Windows 11
- .NET 8 SDK (https://dotnet.microsoft.com/download)
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

## Releases
Every push to `master` builds a self-contained x64 ZIP. The GitHub Action uploads
the ZIP as a workflow artifact and attaches it to a new GitHub Release with an
automatically generated tag such as `build-42`.

To create a manually versioned release instead, push a version tag such as `v1.0.0`
after changing the workflow trigger from `master` to tags:

    git tag v1.0.0
    git push origin v1.0.0

## Using it
1. Pick your earbuds in the device list and press **Connect**. The default connection mode, **Auto**, finds the
   Bluetooth COM port Windows created for those earbuds (the same transport the Python tools use) and opens it.
2. If Windows has no COM port for the earbuds, the app falls back to an RFCOMM socket and lets Windows locate the
   Serial Port service itself (no channel scanning). You can also add an *Outgoing* COM port yourself:
   Settings > Bluetooth & devices > Devices > More Bluetooth settings > COM Ports > Add.
3. You can also pick a specific COM port, or force the RFCOMM socket, in the **Connection** box.
4. Frame length mode 0 (packet + 2) is confirmed correct; the other modes are only for debugging.
   Turn on **Enable log** in the connection settings (gear icon) to show a **Log** tab with raw TX/RX hex.
5. Gesture changes read the current gestures first and send all six slots, like the Python tool.
