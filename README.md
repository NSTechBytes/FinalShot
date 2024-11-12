
# Finalshot Rainmeter Plugin

This Rainmeter plugin, **Finalshot**, allows users to capture screenshots of the entire screen or a custom-selected region using the mouse. The screenshots are saved in PNG format at a user-specified path. After capturing the screenshot, you can execute custom actions, such as activating other Rainmeter skins.

## Features

- **Full-screen screenshot**: Capture the entire screen.
- **Custom region screenshot**: Select a region of the screen using the mouse to capture.
- **Configurable save path**: Set the path where the screenshot will be saved.
- **Post-capture action**: Trigger a custom action (e.g., activating a Rainmeter skin) once the screenshot is taken.

## Installation

1. **Download and Setup**:
   - Download or clone this repository.
   - Place the `Finalshot.dll` plugin file into the `Plugins` directory of your Rainmeter installation.
   - Ensure that the folder structure is correct and that Rainmeter recognizes the plugin.

2. **Configure the Plugin**:
   - In your Rainmeter skin folder, create or update the `.ini` file to use the `Finalshot.dll` plugin.

## Configuration Example

In your `.ini` file, configure the `Finalshot.dll` plugin as follows:

```ini
[mScreenshot]
Measure=Plugin
Plugin=Finalshot.dll
SavePath="D:\\screenshot.png"    ; Define your custom path for the screenshot
ScreenshotFinishAction=[!Log "Screenshot Saved"]  ; Action to execute after screenshot
```

### Parameters:
- `SavePath`: The path where the screenshot will be saved (must be a valid file path). The screenshot will always be saved in PNG format.
- `ScreenshotFinishAction`: An optional action to execute after the screenshot is taken. You can, for example, activate a specific Rainmeter skin.

### Example of capturing a full screenshot:

```ini
[mScreenshot]
Measure=Plugin
Plugin=Finalshot.dll
SavePath="C:\\Users\\YourUsername\\Pictures\\screenshot.png"
ScreenshotFinishAction=[!Log "Screenshot Saved"]
```

### Example of capturing a custom region:

When using a custom region, the user will click and drag to select the area of the screen to capture. Once the selection is made and the mouse is released, the screenshot will be saved at the specified `SavePath`.

### Customization:

- The `SavePath` is fully customizable. You can set any location you wish for saving the screenshot.
- The plugin will always save the screenshot in PNG format.

## License

This plugin is open-source software licensed under the [Appache General Public License, Version 2](https://github.com/NSTechBytes/FinalShot/blob/main/LICENSE).

Feel free to modify and redistribute the code under the terms of the GPLv2.

## Disclaimer

This software is provided "as is" without warranty of any kind, either express or implied, including but not limited to the implied warranties of merchantability and fitness for a particular purpose. The authors shall not be held liable for any damages or issues arising from the use of this software.

---

For more information, visit the [Rainmeter](https://www.rainmeter.net/) website.
