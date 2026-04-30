@"
# FULL BUILD TEST TRUTH

- dotnet restore .\BossCamSuite.sln: PASS
- dotnet build .\BossCamSuite.sln --configuration Release --no-restore: PASS, 0 warnings, 0 errors
- dotnet test .\BossCamSuite.sln --configuration Release --no-build: PASS, 64/64
- dotnet format .\BossCamSuite.sln --verify-no-changes: initially FAIL whitespace; ran dotnet format; final verify PASS
- scripts/validate-serpent-circle.ps1: PASS
- tools/Probe-5523W-HighRes.ps1 parse: PASS
