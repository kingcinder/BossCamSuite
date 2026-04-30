# Contributing

Every change must follow [SERPENT_CIRCLE.md](SERPENT_CIRCLE.md).

Before editing, inspect the existing implementation and identify what is verified, candidate-only, or blocked. Before final response or PR, report what was observed, what was verified, what drift was found, what was persisted, and what tests were run.

Required local validation:

```powershell
dotnet build BossCamSuite.sln
dotnet test BossCamSuite.sln
powershell -ExecutionPolicy Bypass -File scripts/validate-serpent-circle.ps1
```

Do not promote candidates to verified truth without live evidence. Do not collapse ONVIF-declared stream metadata into playback-probed metadata. Do not enable mechanical PTZ from service presence alone.
