# 📸 **FinalShot**  

FinalShot is a powerful Rainmeter plugin written in **C#** that lets you capture screenshots directly from your Rainmeter skins! 🌟  
Whether it's a full-screen capture, custom region, or predefined area, **FinalShot** has got you covered!  
Plus, it works seamlessly with multi-monitor setups—even when monitors have different DPI scaling! 💻🖥️  

---

## ✨ **Features**  

- 🖥️ **Full-Screen Capture:**  
  Capture the **entire virtual desktop** across all monitors.  

- ✂️ **Custom Region Capture:**  
  Drag and select a custom area on the screen with a **bold dashed red border**—clean and precise!  

- 🗺️ **Predefined Region Capture:**  
  Capture a specific area defined by coordinates in your skin's configuration.  

- 🖥️🖥️ **Multi-Monitor Support:**  
  Designed to work with **multiple monitors**, even with different DPI settings!  
  The plugin composites multiple captures into a single image when needed.  

- 🐞 **Debug Logging:**  
  Optional **debug logging** helps troubleshoot DPI scaling and coordinate conversion issues.  
  Enable debugging via your skin's `.ini` file.  

---

## 🛠️ **Installation**  

1. **🔧 Build the Plugin:**  
   - Open the **FinalShot solution** in Visual Studio.  
   - Build the project (choose **Debug** or **Release**).  
   - The compiled DLL (`FinalShot.dll`) will be located in your output directory.  

2. **📂 Copy the DLL:**  
   - Place `FinalShot.dll` in Rainmeter’s plugins directory:  
     ```
     %USERPROFILE%\Documents\Rainmeter\Plugins
     ```
   - Or, use your **custom plugins folder** if configured.  

3. **🎨 Setup Your Skin:**  
   - Create a new folder for your skin (e.g., `FinalShotSkin`) in your Rainmeter skins directory.  
   - Copy the **sample skin** into that folder.  
   - Make sure that any folder referenced in the `SavePath` exists.  

---

## 🚀 **Usage**  

### 🌈 In Your Rainmeter Skin  
Define a measure that loads the plugin and configures the screenshot options:  

```ini
[MeasureScreenshot]
Measure=Plugin
Plugin=FinalShot
;!Note supported Image extension are .png(default),jpg,.jpeg,.tiff,.bmp.
SavePath=#@#Screenshots\screenshot.png
PredefX=100
PredefY=100
PredefWidth=400
PredefHeight=300
DebugLog=1
DebugLogPath=#@#FinalShotDebug.log
;Capture Mouse Cursor in Screenshot.
ShowCursor=1
```

### 📸 **Screenshot Modes:**  
- **🖥️ Full Screen:** `[!CommandMeasure "MeasureScreenshot" "-fs"]`  
- **🔲 Custom Region:** `[!CommandMeasure "MeasureScreenshot" "-cs"]`  
- **📐 Predefined Region:** `[!CommandMeasure "MeasureScreenshot" "-ps"]`  

---

## 📝 **Debugging**  

To enable debug logging, add these lines to your skin’s variables:  

```ini
DebugLog=1
DebugLogPath=#@#FinalShotDebug.log
```

Debug logs help identify DPI scaling and coordinate conversion issues during **custom region capture**.  

---

## 💪 **Contributing**  

Contributions are always welcome! 🌟  
- 🐛 **Found a bug?** Open an issue!  
- 💡 **Got an improvement?** Feel free to submit a pull request!  

---

## 📜 **License**  

This project is licensed under the **[APACHE License](LICENSE)**.  
Feel free to use, modify, and distribute as per the license terms.  

---

💙 **Enjoy capturing screenshots with FinalShot!** 💙  
Developed with ❤️ by **NS Tech Bytes**  
