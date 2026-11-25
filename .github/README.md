# AddonGenie

An addon framework for the XCOM 2 War of the Chosen SDK's Unreal Editor. Addons are single `.cs` files that add tools
to the editor's Addons menu.

## Download

**[Download AddonGenie](https://github.com/SurferJay/AddonGenie/releases/latest/download/AddonGenie.zip)**

## Install

1. Extract the zip into `XCOM 2 War of the Chosen SDK\Binaries\Win64`.
2. Run `X2EditorAddonLauncher.bat`. Use it from now on to start the editor.

The first time, Windows may warn that the file came from the internet. Confirm to run it (on a blue "Windows protected
your PC" screen: **More info**, then **Run anyway**).

Needs Windows and the War of the Chosen SDK. Nothing in the SDK is changed.

## Uninstall

Delete `X2EditorAddonLauncher.bat`, `X2EditorAddonFramework.dll`, `X2EditorAddonFramework.stamp` and the `Addons`
folder from `Binaries\Win64`.

## Addons

Put addon `.cs` files in `Binaries\Win64\Addons`. Subfolders aren't loaded, so you can park addons in one.

**Sharing an addon?** It has to be open, like AddonGenie: anyone may use, change and reshare it. Just put this line at
the top of your `.cs` file:

```
// License: GPL-3.0-or-later
```

Addons you keep to yourself don't need it.

## License

AddonGenie is free software: anyone may use, change and reshare it, as long as what they share stays open too. The
full terms are the GPL v3 or later, in [LICENSE](https://github.com/SurferJay/AddonGenie/blob/main/.github/LICENSE).

## Credits

Icon: ["Genie" by Magnific, from Flaticon](https://www.flaticon.com/free-icon/genie_3821133).
