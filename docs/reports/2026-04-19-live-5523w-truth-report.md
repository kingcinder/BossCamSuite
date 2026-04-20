# 5523-w Live Truth Report (April 19, 2026)

Target cameras:
- `10.0.0.4`
- `10.0.0.29`
- `10.0.0.227`

Test mode:
- LAN direct HTTP
- Auth candidate set tested; successful control auth is `Basic admin:<blank>`
- Safe write + immediate readback + rollback for representative top-group fields

## Proven Auth Modes
- `basic admin:<blank>` accepted on all three cameras for `/netsdk/*` and `/user/*` paths.
- Digest attempts did not provide additional capability for these tested flows.

## Proven Readable Endpoints
- `/netsdk/video/input/channel/1`
- `/netsdk/video/encode/channel/101/properties`
- `/netsdk/network/esee`
- `/user/user_list.xml?username=admin&password=`

## Proven Writable Endpoints + Semantic Readback (All Three Cameras)
- Video/Image:
  - `/netsdk/video/input/channel/1` with `brightnessLevel` mutation (`+1`) accepted and readback-verified.
  - `/netsdk/video/input/channel/1` with `contrastLevel`, `saturationLevel`, `sharpnessLevel` mutations accepted and readback-verified.
  - `/netsdk/video/encode/channel/101/properties` with `constantBitRate` mutation accepted and readback-verified when payload excludes `*Property` keys.
  - `/netsdk/video/encode/channel/101/properties` with `frameRate` mutation accepted and readback-verified on `10.0.0.4` and `10.0.0.227` (no-change on `10.0.0.29` in this pass).
- Network/Wireless:
  - `/netsdk/network/esee` with `enabled` toggle accepted and readback-verified.
- Users/Maintenance:
  - `/user/set_pass.xml?username=admin&password=&content=<...>` accepted for blank→blank pass update and post-write auth read remains valid.

## Contract Notes Promoted From Live Evidence
- `video/input/channel/1` accepts full object writes for tested brightness flow.
- `video/encode/channel/101/properties` rejects naive full readback payloads containing read-only `*Property` fields; sanitized payload is required.
- `network/esee` accepts minimal `{ \"enabled\": bool }` style payload.
- `user/set_pass.xml` is querystring-driven (`username`, `password`, `content`) and returns XML status.

## Blocked/Unverified From This Pass
- Full field-set validation for every top-group field (all enum/range combinations) was not completed in this pass.
- Persistence-after-reboot verification is still pending for these writes.
- Wireless AP/range-extension parameter write matrix is not yet fully proven.
- `network/interface/1` fields `upnp.enabled`, `ddns.enabled`, `pppoe.enabled` returned transport success (`200`) but no semantic value change in this pass (treated as accepted-no-observable-change).

## Next Exploit/Validation Path
1. Expand proven video fields beyond brightness/bitrate (`contrastLevel`, `saturationLevel`, `sharpnessLevel`, `frameRate`) with same sanitized write discipline.
2. Validate wireless/AP write paths under `/netsdk/network/interface/4` with explicit reconnect guardrails.
3. Run delayed + reboot persistence checks and promote `PersistsAfterReboot` truth for each proven field.
