# NAO Dialog Recovery

A Windows utility for recovering two NAO 6 dialog faults that can stall ELIAS lessons. It checks whether dialog initialized correctly and whether an active lesson has a loaded speech recognition context. The recovery restores the lesson's language and restarts only the dialog service when it confirms a supported fault.

## Download and run

Download the Windows x64 ZIP from [Releases](https://github.com/SamiKamara/nao-dialog-recovery/releases/latest), extract it, and run **NAO Dialog Recovery.exe**. The package includes the .NET runtime. Run it on a Windows PC that can reach NAO over the local network.

1. Enter NAO's IPv4 address. Press the chest button once to hear the address if needed.
2. If ELIAS says it is listening but gives no feedback, leave the stalled lesson open while checking.
3. Click **Check only** for a read-only diagnosis or **Check & recover** to let the app restart dialog when it confirms a supported fault.
4. When the app says **Refresh ELIAS to finish**, press **Ctrl+R** in the existing ELIAS tab, reconnect if prompted, and start the lesson again.
5. Run **Check only** while the lesson is listening to verify that recognition is active.

The app remembers the address after a completed check. It writes diagnostic logs under `%LOCALAPPDATA%\NAODialogRecovery\Logs`. Use **Save log...** to export the current run. Logs can include the robot's address and active topic names.

## What it checks

- **Startup fault.** The dialog service is unavailable or has no language. Recovery checks for an installed recognition language and uses English as a fallback when neither dialog nor recognition reports a language.
- **Missing recognition context.** During an active ELIAS lesson, four samples across at least six seconds must show the recognizer running with status `ListenOn`, active listening off, no loaded contexts, and no speech output. The checks cover known lesson topics and the numbered English chat topic format.
- **Restart conditions.** Recovery verifies the dialog service identity and rechecks the lesson and language before requesting a restart. Other active dialog topics block recovery. The app reconnects to the restarted service and verifies the dialog and recognition languages before prompting for the browser refresh.

The browser refresh reconnects ELIAS to the restarted service so the lesson can reload its recognition context. This utility handles service and context faults. Improving recognition accuracy or restoring ELIAS animations requires a separate fix.

## Troubleshooting

- If the robot is unreachable, check the Ethernet or Wi-Fi connection, the announced IPv4 address, and whether the PC and NAO can reach each other on the local network.
- If the app says **Dialog ready · no active lesson**, start the lesson and check while ELIAS is listening.
- If NAO is speaking or changing lessons, wait for the next listening prompt and check again.
- If NAO refuses the restart, or listening still fails after refreshing ELIAS, save the log for diagnosis.

## Build and test

Run these commands from the repository root on Windows with the .NET 9 SDK.

```powershell
dotnet publish NaoRecovery/NaoRecovery.csproj -c Release -o publish
dotnet run --project Tests/Tests.csproj -c Release
```

The executable is `publish/NAO Dialog Recovery.exe`. The tests use a local fake robot to cover recovery, healthy and idle states, language restoration, timeouts, and unsafe restart conditions.

For a live read-only check, pass the IPv4 address of your own robot after `--live`.

```powershell
dotnet run --project Tests/Tests.csproj -c Release -- --live YOUR_NAO_IPV4_ADDRESS
```
