# BlurInput

**BlurInput** is a Rainmeter plugin that provides a transparent and interactive input field for users. Designed for seamless integration into your Rainmeter skins, it allows for text input with various validation options and clipboard controls, all while maintaining a stylish and modern translucent appearance.

---

## Features 🚀

- 🌟 **Transparent Input Field**: A visually appealing input box with a blurred or translucent background.
- ⌨️ **Custom Input Types**:
  - Strings
  - Integers
  - Letters
  - Alphanumeric
  - Hexadecimal
  - Float
  - Email
  - Custom character sets
- 🔄 **Undo/Redo Support**: Easily revert or redo changes while typing.
- 📝 **Clipboard Operations**: Supports **Copy**, **Paste**, and **Cut** operations.
- ⚡ **User-Friendly Commands**: Trigger actions like starting/stopping input, clearing text, and showing a context menu via Rainmeter bangs.
- 🛡️ **Input Validation**: Ensures data integrity with built-in validators for different input types.
- ✨ **Customizable**: Supports character limits, dynamic input length, and text cursor behavior.

---

## Installation 📥

1. Download the latest release from the [Releases](https://github.com/NSTechBytes/BlurInput/releases) section.
2. Copy the `BlurInput.dll` file to the **Plugins** folder of your Rainmeter installation:
   ```
   C:\Program Files\Rainmeter\Plugins
   ```
3. Add the `BlurInput` plugin to your Rainmeter skin.

---

## Usage 🛠️

To integrate the **BlurInput** plugin into your Rainmeter skin, use the following template:

### Basic Example

```ini
[Rainmeter]
Update=1000

[Metadata]
Name=BlurInput Test Skin
Author=NS TEch Bytes
Information=Usage example to use plugin.
Version=Version 1.5
License=Creative Commons BY-NC-SA 3.0

[Variables]
Text=Type Here

;===================================================================================================================;
[InputHandler]
Measure=Plugin
Plugin=BlurInput
MeterName=Text_1
;Cursor=|
;Password=1   
Multiline=1 
;FormatMultiline=1
;InputLimit=0   
;ViewLimit=0   
DefaultValue=#Text#
InputType=String
;AllowedCharacters=abc
UnFocusDismiss=1
;ShowErrorDialog=1
;FormBackgroundColor=255,250,250
;FormButtonColor=136,132,132
;FormTextColor=6,6,6
;SetInActiveValue=0
;InActiveValue=Any Value
OnDismissAction=[!Log "Dismiss"]
;ForceValidInput=1
OnEnterAction=[!Log """[InputHandler]"""][!WriteKeyValue Variables Text """[InputHandler]"""]
OnInvalidAction=[!Log  "UnValidInput Type" "Debug"]
OnESCAction=[!Log """[InputHandler]"""]
DynamicVariables=1
;RegExpSubstitute=1
;Substitute="\n":"#*CRLF*#"
;===================================================================================================================;

[BackGround]
Meter=Shape 
Shape=Rectangle 0,0,800,400,8 |StrokeWidth 0 | FillColor 22,22,22,200
DynamicVariables=1

[Text_1]
Meter=String
Text=#Text#
FontSize=22
FontColor=255,255,255
X=10
Y=10
Antialias=1
FontWeight=200
stringAlign = Left
FOntFace=Arial
DynamicVariables=1
LeftMouseUpAction=[!CommandMeasure InputHandler "Start"]




```

---

### Plugin Bangs (Commands)

The following bangs can be used to interact with the input field:

| Bang                            | Description                     |
| ------------------------------- | ------------------------------- |
| `!CommandMeasure "start"`     | Starts the input field.         |
| `!CommandMeasure "stop"`      | Stops the input field.          |
| `!CommandMeasure "cleartext"` | Clears all text in the field.   |
| `!CommandMeasure "copy"`      | Copies text to the clipboard.   |
| `!CommandMeasure "paste"`     | Pastes text from the clipboard. |
| `!CommandMeasure "cut"`       | Cuts text to the clipboard.     |
| `!CommandMeasure "redo"`      | Redoes the last action.         |
| `!CommandMeasure "undo"`      | Undoes the last action.         |
| `!CommandMeasure "context"`   | Opens the context menu.         |

---

### Custom Input Validation 🛡️

You can validate input with specific types:

- **String**: Allows all characters.
- **Integer**: Allows numbers and a negative sign at the start.
- **Letters**: Accepts only alphabetical characters.
- **Alphanumeric**: Accepts letters and numbers.
- **Hexadecimal**: Allows hexadecimal characters (A-F, 0-9).
- **Float**: Accepts floating-point numbers.
- **Email**: Ensures valid email format.
- **Custom**: Use the `AllowedCharacters` property to specify accepted characters.

Example:

```ini
InputType=Custom
AllowedCharacters=abc123
```

---

## General Keyboard Actions

| **Action**      | **Key** | **Description**                                                                     |
| --------------------- | ------------- | ----------------------------------------------------------------------------------------- |
| **Backspace**   | 8             | Removes the character before the cursor position.                                         |
| **Delete**      | 46            | Removes the character at the cursor position.                                             |
| **Enter**       | 13            | Submits the input (if not in multiline mode) or inserts a newline (if in multiline mode). |
| **Esc**         | 27            | Executes the assigned `OnESCAction` and stops input.                                    |
| **Arrow Left**  | 37            | Moves the cursor one position to the left.                                                |
| **Arrow Right** | 39            | Moves the cursor one position to the right.                                               |
| **Home**        | 36            | Moves the cursor to the beginning of the text.                                            |
| **End**         | 35            | Moves the cursor to the end of the text.                                                  |
| **Tab**         | 9             | Inserts a tab space at the current cursor position.                                       |
| **Caps Lock**   | 20            | Toggles the Caps Lock state.                                                              |

## Ctrl + Keyboard Shortcuts

These shortcuts require the **Ctrl** key to be pressed along with a specific key:

| **Action**          | **Shortcut** | **Description**                                                                                                 |
| ------------------------- | ------------------ | --------------------------------------------------------------------------------------------------------------------- |
| **Copy**            | Ctrl + C           | Copy the current text to the clipboard.                                                                               |
| **Paste**           | Ctrl + V           | Paste text from the clipboard.                                                                                        |
| **Cut**             | Ctrl + X           | Cut the current text to the clipboard.                                                                                |
| **Undo**            | Ctrl + Z           | Undo the last change.                                                                                                 |
| **Redo**            | Ctrl + Y           | Redo the last undone change.                                                                                          |
| **Execute OnEnter** | Ctrl + Enter       | Executes the action assigned to the `OnEnterAction` only when `Multiline=1`, otherwise uses only **Enter**. |

## Available Options

| Name                | Default Value | Require      | Description                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                      |
| :------------------ | :------------ | :----------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| **MeterName** | None          | Must Provide | The `MeterName` is a mandatory configuration parameter in the BlurInput plugin. It specifies the name of the <br />Rainmeter meter that will visually display the text input managed by the plugin.<br />The MeterName is used to dynamically update the meter's text with the input provided by the user. The text<br /> displayed can be the raw input, a masked version (e.g., for passwords), or modified using substitution rules.                                                                                                                                                                                                        |
| **Cursor**    | **\|**  | Optional     | The Cursor marks the location where the next character will be inserted or deleted within the text buffer.<br />By default, the Cursor is represented by a vertical bar but it can be customized.                                                                                                                                                                                                                                                                                                                                                                                                                                               |
| Password            | 0             | Optional     | When Password is enabled `Password=1`, the text entered by the user is not shown directly in the text meter. Instead, each<br /> character is replaced with a masking symbol `*`.                                                                                                                                                                                                                                                                                                                                                                                                                                                            |
| Multiline           | 0             | Optional     | When Multiline is enabled `Multiline=1`, the plugin treats the text input as multi-line. This means users can insert line<br /> breaks using the `Enter key`, and the text buffer will handle multiple lines.                                                                                                                                                                                                                                                                                                                                                                                                                                |
| FormatMultiline     | 0             | Optional     | When `FormatMultiline=1`, the plugin replaces all line breaks \r\n or \n in the TextBuffer with a single space , effectively <br />flattening the text into a single line.<br />If `FormatMultiline=0`, no changes are made to the text buffer, and it retains its multi-line format.                                                                                                                                                                                                                                                                                                                                                        |
| InputLimit          | 0             | Optional     | When InputLimit is set to a positive integer e.g.,`InputLimit=50`, it limits the maximum number of characters that can be<br /> entered into the text buffer.<br />If a user attempts to type beyond this limit, the input is ignored.                                                                                                                                                                                                                                                                                                                                                                                                         |
| ViewLimit           | 0             | Optional     | Control how much of the input text is visible in the meter, essentially defining the maximum width of the text displayed<br />to the user. This is particularly useful when you want to display a portion of the text or manage the overflow within a <br />fixed-width display area.<br />If `ViewLimit=0`, the full length of the TextBuffer is displayed, regardless of how many characters <br />it contains. There is no truncation, and the text appears as entered.                                                                                                                                                                     |
| DefaultValue        | Null          | Optional     | Defines the default text that appears in the input field when it is first loaded or when it is cleared. This value provides<br />users with a hint, example, or placeholder text, guiding them on what type of input is expected.                                                                                                                                                                                                                                                                                                                                                                                                                |
| InputType           | String        | Optional     | Define the type of data that the input field will accept. It determines the kind of validation applied to the user's input, ensuring<br /> that only the specified type of data is allowed. This setting allows for multiple predefined input types or a custom set of allowed<br /> characters, providing flexibility based on the desired input requirements. It supports a range of predefined types like <br />`String`, `Integer`, `Float`, `Letters`, `Alphanumeric`, `Hexadecimal`, and `Email`, as well as a `Custom` option for more specific <br />validation. This flexibility allows for a tailored input experience |
| AllowedCharacters   | Null          | Optional     | The AllowedCharacters setting defines a list of characters that are allowed for input when the ` InputType` is set to ` "Custom"`.<br /> If you want to allow only numeric digits and alphabetic letters (both lowercase and uppercase), you would set the <br />AllowedCharacters to ` "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"`.                                                                                                                                                                                                                                                                                 |
| UnFocusDismiss      | 0             | Optional     | When `UnFocusDismiss` is set to `1`, the input field will be dismissed automatically when it loses focus.<br />This is particularly useful for situations where you want the input to be transient, only visible while the user is interacting <br />with it.<br />If `UnFocusDismiss` is set to `0`, the input field will remain visible and active even when it loses focus.                                                                                                                                                                                                                                                           |
| ShowErrorDialog     | 0             | Optional     | If `ShowErrorDialog` is set to 1, the plugin will display a dialog box such as a pop-up containing an error message when<br />invalid input is entered, or an error condition occurs.<br />The Dialog will be displayed after submitting text.                                                                                                                                                                                                                                                                                                                                                                                                 |
| SetInActiveValue    | 0             | Optional     | if `ShowErrorDialog` is set to 1,then the plugin set the value of  `InActiveValue` instead of` DefaultValue` .when the input field dismiss or other <br />action is performed.                                                                                                                                                                                                                                                                                                                                                                                                                                                          |
| InActiveValue       | Null          | Optional     | The text of InActiveValue will be defined in that option.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        |
| OnDismissAction     | Null          | Optional     | Defines the action or behavior that occurs when the input field is dismissed or loses focus. This can happen, for example,<br />when the user clicks away from the input field or when any action is performed that causes the input field to no longer be <br />active.                                                                                                                                                                                                                                                                                                                                                                         |
| OnEnterAction       | Null          | Optional     | Defines the action or behavior that occurs when the user presses the `Enter key` while interacting with the input field. <br />This setting is useful for triggering specific events, such as submitting the input, running a script, or switching focus to <br />another element, based on the user's input submission.                                                                                                                                                                                                                                                                                                                       |
| OnInvalidAction     | Null          | Optional     | When a user enters invalid data such as text when only Integers are allowed, the validation function detects the<br /> failure and triggers the `OnInvalidAction` after submitting text.                                                                                                                                                                                                                                                                                                                                                                                                                                                       |
| OnESCAction         | Null          | Optional     | Defines the action or behavior that occurs when the user presses the `ESC key` while interacting with the input field.<br />This setting is useful for triggering specific events, such as submitting the input, running a script, or switching focus to <br />another element, based on the user's input submission.                                                                                                                                                                                                                                                                                                                         |
| ForceValidInput     | 0             | Optional     | If `ForceValidInput` is set to ` 1`, the plugin will enforce strict validation rules for the user input based on the InputType.<br />For example, if the InputType is set to `Integer`, the plugin ensures that only digits and possibly a negative sign at the <br />start are allowed.<br />If the user enters an invalid character based on the validation rules, the input will be rejected.                                                                                                                                                                                                                                           |
| FormBackgroundColor | 30,30,30      | Optional     | The FormBackgroundColor refers to the background color of the form in your ` ContextForm` and the `ErrorDialog` class.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                       |
| FormButtonColor     | 70,70,70      | Optional     | The FormButtonColor refers to the button color of the form in your `ContextForm` and the ` ErrorDialog` class.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                               |
| FormTextColor       | 255,255,255   | Optional     | The FormTextColor refers to the text color of the form in your `ContextForm` and the `ErrorDialog` class.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    |

## Contributing 🤝

Contributions are welcome! If you have ideas for enhancements, bug fixes, or additional features, please submit an issue or pull request.

---

## License 📄

This project is licensed under the Apache License. See the [LICENSE](LICENSE) file for details.

---

## Credits 💡

- Developed by **[Nasir Shahbaz]**.
- Inspired by the need for a cleaner, transparent input field in Rainmeter.

---

Enjoy using **BlurInput** and make your Rainmeter skins more interactive and stylish! 🎨
