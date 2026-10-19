# Lessons learned

These are problems I ran into while building WinTo, and how they were fixed. Most of them failed quietly, without any error message. That is why I wrote them down.

## How the app is built

| What happened | Why | What fixed it |
|---|---|---|
| Closing a tool did not free its code | Avalonia keeps a list of every control type it has ever shown and never clears it. So a tool's code can't be unloaded. | Every tool frees its devices, threads and memory when it is closed. Only the code itself stays loaded, which is a few hundred KB per tool. |
| A tool was loaded at startup without being opened | One style file in the main app pointed into a tool's DLL, and that was enough to load it. | Tools load their own styles. The self-test fails if tool code is loaded too early. |
| The error "Control cannot be converted to Control" | A second copy of Avalonia ended up in a tool's folder, so there were two different "Control" types. | Shared libraries are kept out of tool folders, and the build fails if one shows up there. |
| Clicking a tool froze the app for half a second | Starting a helper program ran on the UI thread, and the virus scanner made the first start slow. | It now starts on a background thread. The delay went from 479 ms to 41 ms. |
| The build said "success" even though a XAML file was broken | Avalonia reports its errors in a different format than the C# compiler. | The build script also looks for Avalonia's error codes. |

## Windows

| What happened | Why | What fixed it |
|---|---|---|
| There is no way to turn on Do Not Disturb | Windows has no public API for it. The only trick writes an undocumented registry value that can change with any update. | WinTo only shows the state and opens the settings page. |
| WinTo can't make itself the default photo viewer | Since Windows 8 this choice is protected with a hash that only Windows can write. | WinTo registers itself and opens the right settings page, and you do the last click. |
| The Wi-Fi list was empty, although Wi-Fi worked | Since Windows 11 24H2, listing nearby networks needs location permission. | WinTo shows the saved networks instead, and those still connect. |
| The first Wi-Fi request took 12 seconds | The Wi-Fi service only starts when it is used for the first time. | Wi-Fi is read in the background. Later requests take less than 10 ms. |
| The clipboard history never noticed new copies | Windows sends that message to a real window. A simple message loop never sees it. | The listener now has its own small hidden window. |
| A screenshot pasted into another app was completely black | The image was only created when you pasted, but by then the screenshot tool was already closed. | The image is put on the clipboard right away. |
| Keyboard shortcuts stopped working after a while | The keyboard hook ran on the UI thread. When the app was busy, Windows removed the hook. | The hook has its own thread now. |
| Some shortcuts did nothing on a PC with PowerToys | PowerToys reserves several shortcuts, even when its tools are turned off. | WinTo takes the next free shortcut and tells you which one. |
| The network page showed 50 adapters and the speed was 6 times too high | .NET 10 also lists filter drivers, and each one repeats its adapter's numbers. | Only adapters with a real connection are shown. |
| Ending Explorer froze WinTo too | WinTo started from the Start menu is a child of Explorer, so it froze itself. | WinTo never touches its own process tree. |

## Audio and video

| What happened | Why | What fixed it |
|---|---|---|
| A screen recording had a sound track, but it was silent | Recording the system sound delivers nothing while nothing is playing, and ffmpeg waits for it. | WinTo plays silence on the same device, so the stream never stops. |
| The microphone was much quieter than the game | The two sources were 28.6 dB apart. | Both are brought to the same loudness before they are mixed. |
| A video exported at double speed was 50% too long | The length option was placed after the input, so it limited the output instead of the input. | The start and length options now come before the input. |
| Clips were 16.7 seconds long instead of 15 | The video can only be split at keyframes, and the encoder only made one every 250 frames. | Keyframes are forced at the right times. |
| The graphics card encoder was listed but failed | The list shows what ffmpeg was built with, not what the graphics card can do. | WinTo tests one frame first and falls back to the normal encoder. |

## The interface

| What happened | Why | What fixed it |
|---|---|---|
| Taking a test screenshot set my real monitors to 0% brightness | A slider had a two-way binding and its limits were still 0 when it was created, so it sent 0 to the hardware. | The limits are fixed, and the hardware is only changed after you touch a slider. |
| Buttons flashed brighter for a moment on hover | The color animation started from "transparent", which is transparent white, so it passed through light gray. | WinTo uses its own color animation that blends correctly. |
| Tooltips covered the button they belonged to | The default position was right under the mouse. | Tooltips on buttons open above them. |
| The release folder was over 140 MB | Avalonia 12 includes debug files for its native libraries. | The release build leaves all debug files out. |

## Checked against real data

Some parts were tested with small test programs that use the real code on real files:

- The metadata remover deletes EXIF and GPS data, and the image data stays exactly the same byte for byte.
- The Vencord patch file is byte for byte the same as the one from the official installer.
- The storage map was checked on a user folder with about one million files and matched exactly.
- The photo viewer turns pictures correctly for all eight possible camera orientations.
- The clipboard history passed 26 of 26 checks with the real Windows clipboard.
