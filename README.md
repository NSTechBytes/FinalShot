# FinalShot

FinalShot is a Rainmeter plugin written in C# that enables you to take screenshots directly from your Rainmeter skins. It supports both full-screen captures and custom region selections, making it a versatile tool for desktop customization.

## Features

- **Full Screen Screenshot:** Capture the entire screen with a single command.
- **Custom Region Screenshot:** Allow users to select a specific area of the screen to capture.
- **Post-Capture Actions:** Execute a finish action after capturing a screenshot (e.g., refresh the skin).
- **PNG Output:** Screenshots are saved in high-quality PNG format.

## Installation

1. **Compile the Plugin:**
   - Open the FinalShot solution in Visual Studio.
   - Build the project (Debug or Release configuration as needed).
   - The compiled DLL (`FinalShot.dll`) will be located in your output directory.

2. **Place the DLL:**
   - Copy the `FinalShot.dll` file into Rainmeter’s plugin directory:
     ```
     %USERPROFILE%\Documents\Rainmeter\Plugins
     ```
   - Alternatively, use your custom Rainmeter plugins folder if specified.

3. **Prepare a Skin:**
   - Create a new folder for your skin (e.g., `FinalShotSkin`) in your Rainmeter skins directory.
   - Save the provided sample skin file (see below) into this folder.
   - Ensure the folder specified in the `SavePath` exists (or update it to a valid path).

## Usage

In your Rainmeter skin, define a measure to load the plugin and set the required configuration options. For example:

```ini
[Rainmeter]
Update=1000

[Metadata]
Name=Screenshot Skin
Author=NS Tech Bytes
Version=1.2
Description=This Rainmeter skin allows you to take full screen or custom region screenshots.
License=Appache 2.0

[mFinalShot]
Measure=Plugin
Plugin=Finalshot.dll
SavePath="D:\screenshot.png" 
ScreenshotFinishAction=[!Log "Picture Saved"] 


[BackGroundShape]
Meter=Shape
Shape=Rectangle 0,0,400,200,8 | StrokeWidth 0 | FillColor 255,255,255,100
DyamicVariables=1

; --- [Full Screen Screenshot Button] ---
[FullScreenButton]
Meter=Shape
Shape=Rectangle 0,0,150,50,8 | StrokeWidth 0 | FillColor 10,10,10,150
X=20
Y=16r
LeftMouseUpAction=[!CommandMeasure mFinalShot "-fs"]
[Full_Ins]
Meter=String
X=((150)/2)r
Y=(50/2)r
stringAlign = CenterCenter
Text=FullScreen
FontColor=10,10,10
FontSize=14
Antialias=1
; --- [Custom Region Screenshot Button] ---
[CustomRegionButton]
Meter=Shape
Shape=Rectangle 0,0,150,50,8 | StrokeWidth 0 | FillColor 10,10,10,150
X=30R
Y=-25r
LeftMouseUpAction=[!CommandMeasure mFinalShot "-cs"]

[Custom_Ins]
Meter=String
X=((150)/2)r
Y=(50/2)r
stringAlign = CenterCenter
Text=Select Region
FontColor=10,10,10
FontSize=14
Antialias=1
; --- [Instructions Text] ---
[Instructions]
Meter=String
X=200
Y=100
stringAlign = Center
Text="Click on 'Full Screen' or 'Select Region' to capture a screenshot.#crlf#SavePath:"D:\screenshot.png" "
FontColor=10,10,10
FontSize=14
W=380
clipString = 2
Antialias=1


```

## Troubleshooting

- **Invalid SavePath Error:**  
  If you see an error regarding an invalid save path, ensure that the `SavePath` variable in your skin points to a valid directory with write permissions.

- **Custom Region Selection:**  
  The plugin safely handles cases where no valid region is selected (i.e., if you click without dragging). In such cases, the screenshot will not be captured, and the form will close without error.

## Contributing

Contributions and improvements are welcome! If you find a bug or have suggestions for new features, please open an issue or submit a pull request.

## License

This project is licensed under the [APACHE License](LICENSE).

---

Enjoy using FinalShot to enhance your Rainmeter experience with easy screenshot capabilities!