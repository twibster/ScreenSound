# Regression checks

The fast console harness links the production `MonitorService` and replaces only
its Win32 boundary. It has no test-framework dependencies and returns a nonzero
exit code on failure:

```powershell
dotnet run --project tests/MonitorRegression/MonitorRegression.csproj
```

It checks restored/minimized windows, tray hiding, a secondary lyrics window,
destroyed window handles, minimized startup, and a disconnected monitor.

For a real Win32 check, run the following from an interactive Windows desktop
with two connected monitors. It creates transparent, nonactivating test windows;
it does not move another application's windows or change any audio settings.

```powershell
dotnet run --project tests/NativeWindowRegression/NativeWindowRegression.csproj
```

The native check is intentionally excluded from hosted CI because it requires
an interactive session with two actual monitors. It exits with code 2 when that
precondition is missing. A player first discovered after it has already hidden
its window to the tray has no recorded screen; restore it once before testing.
