# Prefab object marker regression

Runs deterministic distribution checks against the production `DeltaV.dll`: density-to-count
conversion, annular area sampling, weighted prefab selection, minimum spacing, stable seeds and
rerolls. It does not create a RimWorld Map or execute full `SpawnSetup`, so marker expansion,
preview drawing and later prefab materialization still require an in-game new-save check.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/PrefabMarkerRegression/run.ps1
```
