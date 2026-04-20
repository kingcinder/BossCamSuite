# 5523-w Image Control Expansion Report

Date: 2026-04-20

## 10.0.0.4 (c57f6a60-957f-4dcf-a57d-72869f1cc1d3)

- Writable: bitrate, brightness, contrast, frameRate, saturation, sharpness
- Readable: (none)
- Blocked: (none)
- TransportSuccessNoSemanticChange: (none)
- HiddenAdjacentCandidate: awb, codec, dayNight, denoise, exposure, flip, gamma, hue, infrared, irCut, keyframeInterval, mirror, osd, profile, resolution, wdr, whiteLight
- Uncertain: irMode

### Writable Test Set

- brightness: baseline=50 candidates=51
- contrast: baseline=50 candidates=51
- saturation: baseline=50 candidates=51
- sharpness: baseline=50 candidates=51
- frameRate: baseline=13 candidates=14
- bitrate: baseline=5120 candidates=4992

### Behavior Maps

- bitrate: safe=4992-4992 thresholds=[] cliffs=[] trigger=none-observed
- brightness: safe=51-51 thresholds=[] cliffs=[] trigger=none-observed
- contrast: safe=51-51 thresholds=[] cliffs=[] trigger=none-observed
- frameRate: safe=14-14 thresholds=[] cliffs=[] trigger=none-observed
- keyframeInterval: safe=unverified thresholds=[] cliffs=[] trigger=none-observed
- saturation: safe=51-51 thresholds=[] cliffs=[] trigger=none-observed
- sharpness: safe=51-51 thresholds=[] cliffs=[] trigger=none-observed

## 10.0.0.29 (ebd28ed0-d031-4311-8771-5990b69452d0)

- Writable: bitrate, brightness, contrast, saturation, sharpness
- Readable: (none)
- Blocked: (none)
- TransportSuccessNoSemanticChange: (none)
- HiddenAdjacentCandidate: awb, codec, dayNight, denoise, exposure, flip, frameRate, gamma, hue, infrared, irCut, keyframeInterval, mirror, osd, profile, resolution, wdr, whiteLight
- Uncertain: irMode

### Writable Test Set

- brightness: baseline=60 candidates=61
- contrast: baseline=50 candidates=51
- saturation: baseline=50 candidates=51
- sharpness: baseline=50 candidates=51
- bitrate: baseline=5120 candidates=4992

### Behavior Maps

- bitrate: safe=4992-4992 thresholds=[] cliffs=[] trigger=none-observed
- brightness: safe=61-61 thresholds=[] cliffs=[] trigger=none-observed
- contrast: safe=51-51 thresholds=[] cliffs=[] trigger=none-observed
- frameRate: safe=unverified thresholds=[] cliffs=[] trigger=none-observed
- keyframeInterval: safe=unverified thresholds=[] cliffs=[] trigger=none-observed
- saturation: safe=51-51 thresholds=[] cliffs=[] trigger=none-observed
- sharpness: safe=51-51 thresholds=[] cliffs=[] trigger=none-observed

## 10.0.0.227 (4b044a04-68e2-4aaa-97af-11bfc97172cf)

- Writable: bitrate, brightness, contrast, frameRate, keyframeInterval, saturation, sharpness
- Readable: (none)
- Blocked: (none)
- TransportSuccessNoSemanticChange: (none)
- HiddenAdjacentCandidate: awb, codec, dayNight, denoise, exposure, flip, gamma, hue, infrared, irCut, mirror, osd, profile, resolution, wdr, whiteLight
- Uncertain: irMode

### Writable Test Set

- brightness: baseline=50 candidates=51
- contrast: baseline=50 candidates=51
- saturation: baseline=50 candidates=51
- sharpness: baseline=50 candidates=51
- frameRate: baseline=10 candidates=11
- keyframeInterval: baseline=20 candidates=20
- bitrate: baseline=5120 candidates=4992

### Behavior Maps

- bitrate: safe=4992-4992 thresholds=[] cliffs=[] trigger=none-observed
- brightness: safe=51-51 thresholds=[] cliffs=[] trigger=none-observed
- contrast: safe=51-51 thresholds=[] cliffs=[] trigger=none-observed
- frameRate: safe=11-11 thresholds=[] cliffs=[] trigger=none-observed
- keyframeInterval: safe=20-20 thresholds=[] cliffs=[] trigger=none-observed
- saturation: safe=51-51 thresholds=[] cliffs=[] trigger=none-observed
- sharpness: safe=51-51 thresholds=[] cliffs=[] trigger=none-observed

## Notes

- Behavior maps used fixture promotion when live semantic writes were unavailable in the current session.
- Operational image metrics are currently value-proxy metrics unless direct snapshot histogram capture is available.
