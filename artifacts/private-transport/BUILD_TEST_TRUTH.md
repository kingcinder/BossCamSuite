# BUILD / TEST TRUTH

Input: artifacts/private-transport/BLUE_IRIS_SURFACE_TRUTH.md

Commands run:
- pwsh -NoProfile -ExecutionPolicy Bypass -File .\tools\Start-5523W-BubbleRelay.ps1 -Ip 10.0.0.227 -Username admin -Password "" -StopExisting
- pwsh -NoProfile -Command '$null = [scriptblock]::Create((Get-Content .\tools\Start-5523W-BubbleRelay.ps1 -Raw)); ''SCRIPT_PARSE_OK'''
- pwsh -NoProfile -Command '$null = [scriptblock]::Create((Get-Content .\tools\Inspect-5523W-PrivateTransport.ps1 -Raw)); ''INSPECT_PARSE_OK'''
- dotnet build .\BossCamSuite.sln --configuration Release
- dotnet test .\BossCamSuite.sln --configuration Release --no-build

Results:
- relay smoke: PASS_HIGHRES_BUBBLE_RELAY (main h264 2560x1920 15/1; sub h264 704x480 15/1)
- script parse: PASS
- inspect parse: PASS
- build: PASS (0 warnings, 0 errors)
- test: PASS (64 passed, 0 failed, 0 skipped)
