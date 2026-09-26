# Desktop integration checks (interactive Windows session)

Run `powershell -ExecutionPolicy Bypass -File scripts/Smoke.ps1 -Fullscreen -DesktopInput`
with Explorer owning a visible desktop. The smoke process uses an isolated database.
Then check the following on Windows 10 and 11. For each row, confirm that the widget
is absent from Alt+Tab and never has `WS_EX_TOPMOST` in the debug hierarchy log.

| Environment | Action | Expected result |
| --- | --- | --- |
| Standard wallpaper | Cover a widget with Chrome, then minimize Chrome | Chrome covers it; widget reappears immediately without a visibility change |
| Standard wallpaper | Press Win+D twice | Widgets and icons are available on the desktop; applications cover them again |
| Wallpaper Engine | Start before and after DesktopPlanner; stop it | Animated wallpaper stays behind widgets; icons and applications work |
| Lively Wallpaper | Start before and after DesktopPlanner; stop it | Same behavior as Wallpaper Engine |
| Explorer | Restart Explorer while widgets are visible | Widgets recover once, at the saved coordinates, without floating windows |
| Interaction | Toggle Ctrl+Shift+Space, then drag, resize, edit and use the calendar | Unlocked controls work; locked clicks pass through |
| Displays | Move widgets between monitors with different DPI; change resolution | Positions and sizes remain usable and persist after restart |
| Fullscreen | Open maximized, borderless and exclusive fullscreen applications | Widgets remain below applications and games |
| Game overlays | Open NVIDIA, Xbox Game Bar or Steam overlay | DesktopPlanner does not change their Z-order |

The desktop layer is selected from the Explorer window that directly contains
`SHELLDLL_DefView`. On some versions this is `Progman`; on others it is a `WorkerW`.
The widgets are children of that same window and sit above the icon view. Animated
wallpaper normally occupies a lower desktop window. Explorer may use a different
undocumented hierarchy on other builds, so validate the ordering visually.
