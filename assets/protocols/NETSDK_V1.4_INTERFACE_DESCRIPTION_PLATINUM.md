# NETSDK V1.4 Interface Description — Platinum Version

Chinese preserved. Rebuilt for Codex accuracy, endpoint mining, client generation, and live firmware validation.

## Contents
- Endpoint Index
- Endpoint Detail Blocks
- Raw Data Structures
- Fact / Inference Boundaries

## Endpoint Index

- **5.1.1 Device Information**
  - Tag: `System`
  - Path: `/NetSDK/System/deviceInfo`
  - Methods: `GET, PUT`
- **5.1.2 System Local Time**
  - Tag: `System`
  - Path: `/NetSDK/System/time/localTime`
  - Methods: `GET, PUT`
- **5.1.3 System Time Ntp**
  - Tag: `System`
  - Path: `/NetSDK/System/time/ntp[/properties]`
  - Methods: `GET, PUT`
- **5.2.1 Audio Input Channel List**
  - Tag: `Audio`
  - Path: `/NetSDK/Audio/input/channels[/properties]`
  - Methods: `GET`
- **5.2.2 Audio Input Channel**
  - Tag: `Audio`
  - Path: `/NetSDK/Audio/input/channel/ID[/properties]`
  - Methods: `GET, PUT`
- **5.2.3 Audio Encode Channel List**
  - Tag: `Audio`
  - Path: `/NetSDK/Audio/encode/channels[/properties]`
  - Methods: `GET`
- **5.2.4 Audio Encode Channel**
  - Tag: `Audio`
  - Path: `/NetSDK/Audio/encode/channel/ID[/properties]`
  - Methods: `GET, PUT, POST`
- **5.3.1 Video Input Channel List**
  - Tag: `Video`
  - Path: `/NetSDK/Video/input/channles[/properties]`
  - Methods: `GET`
- **5.3.2 Video Input Channel**
  - Tag: `Video`
  - Path: `/NetSDK/Video/input/channel/ID[/properties]`
  - Methods: `GET, PUT`
- **5.3.3 Video Input Channel Sharpness Level**
  - Tag: `Video`
  - Path: `/NetSDK/Video/input/channel/ID/sharpnessLevel`
  - Methods: `GET, PUT`
- **5.3.4 Video Input Channel brightness Level**
  - Tag: `Video`
  - Path: `/NetSDK/Video/input/channel/ID/brightnessLevel`
  - Methods: `GET, PUT`
- **5.3.5 Video Input Channel flipEnabled**
  - Tag: `Video`
  - Path: `/NetSDK/Video/input/channel/ID/flip`
  - Methods: `GET, PUT`
- **5.3.6 Video Input Channel mirrorEnabled**
  - Tag: `Video`
  - Path: `/NetSDK/Video/input/channel/ID/mirror`
  - Methods: `GET, PUT`
- **5.3.7 Video Input Channel Saturation Level**
  - Tag: `Video`
  - Path: `/NetSDK/Video/input/channel/ID/saturationLevel`
  - Methods: `GET, PUT`
- **5.3.8 Video Input Channel Hue Level**
  - Tag: `Video`
  - Path: `/NetSDK/Video/input/channel/ID/hueLevel`
  - Methods: `GET, PUT`
- **5.3.9 Video Input Channel Contrast Level**
  - Tag: `Video`
  - Path: `/NetSDK/Video/input/channel/ID/contrastLevel`
  - Methods: `GET, PUT`
- **5.3.10 Video Input Channel Privacy Mask List**
  - Tag: `Video`
  - Path: `/NetSDK/Video/input/channel/ID/privacyMasks[/properties]`
  - Methods: `GET`
- **5.3.11 Video Input Channel Privacy Mask**
  - Tag: `Video`
  - Path: `/NetSDK/Video/input/channel/ID/privacyMask/ID[/properties]`
  - Methods: `GET, PUT`
- **5.3.12 Video Encode Channel List**
  - Tag: `Video`
  - Path: `/NetSDK/Video/encode/channels[/properties]`
  - Methods: `GET`
- **5.3.13 Video Encode Channel**
  - Tag: `Video`
  - Path: `/NetSDK/Video/encode/channel/ID[/properties]`
  - Methods: `GET, PUT`
- **5.3.14 Video Encode Channel Name Overlay**
  - Tag: `Video`
  - Path: `/NetSDK/Video/encode/channel/ID/channelNameOverlay[/properties]`
  - Methods: `GET, PUT`
- **5.3.15 Video Encode Datetime Overlay**
  - Tag: `Video`
  - Path: `/NetSDK/Video/encode/channel/ID/datetimeOverlay[/properties]`
  - Methods: `GET, PUT`
- **5.3.16 Video Encode Deivce ID Overlay**
  - Tag: `Video`
  - Path: `/NetSDK/Video/encode/channel/ID/deviceIDOverlays[/properties]`
  - Methods: `GET, PUT`
- **5.3.17 Video Encode Text Overlay List**
  - Tag: `Video`
  - Path: `/NetSDK/Video/encode/channel/ID/textOverlays[/properties]`
  - Methods: `GET`
- **5.3.18 Video Encode Text Overlay**
  - Tag: `Video`
  - Path: `/NetSDK/Video/encode/channel/ID/textOverlay/ID[/properties]`
  - Methods: `GET, PUT`
- **5.3.19 Video Encode Request Key Frame**
  - Tag: `Video`
  - Path: `/NetSDK/Video/encode/channel/ID/requestKeyFrame`
  - Methods: `PUT`
- **5.3.20 Video Encode Snap Shot**
  - Tag: `Video`
  - Path: `/NetSDK/Video/encode/channel/ID/snapShot`
  - Methods: `GET`
- **5.3.21 Video Motion Detection Channel List**
  - Tag: `Video`
  - Path: `/NetSDK/Video/motionDetection/channels[/properties]`
  - Methods: `GET`
- **5.3.22 Video Motion Detection Channel**
  - Tag: `Video`
  - Path: `/NetSDK/Video/motionDetection/channel/ID[/properties]`
  - Methods: `GET, PUT`
- **5.3.23 Video Motion Detection Channel Status**
  - Tag: `Video`
  - Path: `/NetSDK/Video/motionDetection/channel/ID/status`
  - Methods: `GET, DELETE`
- **5.4.1 IO Alarm Input Channel List**
  - Tag: `IO`
  - Path: `/NetSDK/IO/alarmInput/channels[/properties]`
  - Methods: `GET`
- **5.4.2 IO Alarm Input Channel**
  - Tag: `IO`
  - Path: `/NetSDK/IO/alarmInput/channel/ID[/properties]`
  - Methods: `GET, PUT`
- **5.4.3 IO Alarm Input Channel Port Status**
  - Tag: `IO`
  - Path: `/NetSDK/IO/alarmInput/channel/ID/portStatus`
  - Methods: `GET`
- **5.4.4 IO Alarm Output Channel List**
  - Tag: `IO`
  - Path: `/NetSDK/IO/alarmOutput/channels[/properties]`
  - Methods: `GET`
- **5.4.5 IO Alarm Output Channel**
  - Tag: `IO`
  - Path: `/NetSDK/IO/alarmOutput/channel/ID[/properties]`
  - Methods: `GET, PUT`
- **5.4.6 IO Alarm Output Channel Port Status**
  - Tag: `IO`
  - Path: `/NetSDK/IO/alarmOutput/channel/ID/portStatus`
  - Methods: `GET, DELETE`
- **5.4.8 IO Alarm Output Channel Trigger**
  - Tag: `IO`
  - Path: `/NetSDK/IO/alarmOutput/channel/ID/trigger`
  - Methods: `PUT`
- **5.5.1 PTZ Channel List**
  - Tag: `PTZ`
  - Path: `/NetSDK/PTZ/channels[/properties]`
  - Methods: `GET`
- **5.5.2 PTZ Channel**
  - Tag: `PTZ`
  - Path: `/NetSDK/ PTZ/channel/ID[/properties]`
  - Methods: `GET, PUT`
- **5.5.3 PTZ Channel Control**
  - Tag: `PTZ`
  - Path: `/NetSDK/ PTZ/channel/ID/control`
  - Methods: `PUT`
- **5.6.1 Network Transport Stream Channel List**
  - Tag: `Stream`
  - Path: `/NetSDK/Stream/channles`
  - Methods: `GET`
- **5.6.2 Network Transport Stream Channel**
  - Tag: `Stream`
  - Path: `/NetSDK/Stream/channel/ID`
  - Methods: `GET`
- **5.7.1 Network Interface List**
  - Tag: `Network`
  - Path: `/NetSDK/Network/interfaces[/properties]`
  - Methods: `GET`
- **5.7.2 Network Interface**
  - Tag: `Network`
  - Path: `/NetSDK/Network/interfaces/ID[/properties]`
  - Methods: `GET, PUT`
- **5.7.3 Network Interface Lan**
  - Tag: `Network`
  - Path: `/NetSDK/Network/interfaces/ID/lan[/properties]`
  - Methods: `GET, PUT`
- **5.7.4 Network Interface Pppoe**
  - Tag: `Network`
  - Path: `/NetSDK/Network/interfaces/ID/pppoe[/properties]`
  - Methods: `GET, PUT`
- **5.7.5 Network Interface Dns**
  - Tag: `Network`
  - Path: `/NetSDK/Network/interfaces/ID/ddns[/properties]`
  - Methods: `GET, PUT`
- **5.7.6 Network Interface Wireless**
  - Tag: `Network`
  - Path: `NetSDK/Network/interfaces/ID/wireless[/properties]`
  - Methods: `GET, PUT`
- **5.7.7 Network Port List**
  - Tag: `Network`
  - Path: `/NetSDK/Network/Ports[/properties]`
  - Methods: `GET`
- **5.7.8 Network Port**
  - Tag: `Network`
  - Path: `/NetSDK/Network/Port/ID[/properties]`
  - Methods: `GET, PUT, POST`
- **5.7.9 Network Dns**
  - Tag: `Network`
  - Path: `/NetSDK/Network/Dns[/properties]`
  - Methods: `GET, PUT, POST`
- **5.7.10 Network Esee**
  - Tag: `Network`
  - Path: `/NetSDK/Network/Esee`
  - Methods: `GET, PUT, POST`
- **5.8.1 SDCard media search**
  - Tag: `SDCard`
  - Path: `/NetSDK/SDCard/media/search`
  - Methods: `GET`
- **5.8.2 SDCard media playbackFLV**
  - Tag: `SDCard`
  - Path: `/NetSDK/SDCard/media/playbackFLV`
  - Methods: `GET`
- **5.8.3 SDCard format**
  - Tag: `SDCard`
  - Path: `/NetSDK/SDCard/format`
  - Methods: `GET`
- **5.8.4 SDCard status**
  - Tag: `SDCard`
  - Path: `/NetSDK/SDCard/status`
  - Methods: `GET`
- **5.9.1 Image**
  - Tag: `Image`
  - Path: `/NetSDK/Image[/properties]`
  - Methods: `GET, PUT, POST`
- **5.9.2 IrCutFilter**
  - Tag: `Image`
  - Path: `/NetSDK/Image/irCutFilter[/properties]`
  - Methods: `GET, PUT, POST`
- **5.9.3 ManualSharpness**
  - Tag: `Image`
  - Path: `/NetSDK/Image/manualSharpness[/properties]`
  - Methods: `GET, PUT, POST`
- **5.9.4 Denoise3D**
  - Tag: `Image`
  - Path: `/NetSDK/Image/denoise3d[/properties]`
  - Methods: `GET, PUT, POST`
- **5.9.5 WDR**
  - Tag: `Image`
  - Path: `/NetSDK/Image/wdr[/properties]`
  - Methods: `GET, PUT, POST`
- **5.9.6 AF**
  - Tag: `Image`
  - Path: `/NetSDK/Image/AF`
  - Methods: `GET, DELETE`

## 5.1.1 Device Information

**Path:** `/NetSDK/System/deviceInfo`

**Group (inferred):** `System`

### Method: GET
- Description: 用于获取设备信息。
- Query: None.
- Content: None.
- Success Return: DeviceInfo JSON Form.

### Method: PUT
- Description: 用于设置设备信息。
- Query: None.
- Content: DeviceInfo JSON Form.
- Success Return: Response JSON Form
- Notes: :

---

## 5.1.2 System Local Time

**Path:** `/NetSDK/System/time/localTime`

**Group (inferred):** `System`

### Method: GET
- Description: 用于获取系统本地时间信息。
- Query: None.
- Content: None.
- Success Return: ISO8601 Date-Time Format String.
- Notes: 2

### Method: PUT
- Description: 用于更新系统本地时间信息。
- Query: None.
- Content: ISO8601 Date-Time Format String.
- Success Return: Response JSON Form
- Notes: : 如北京时间 2013 年 8 月 16 日下午 5 点 30 分 8 秒，可以表示为 2004-05-03T17:30:08+08:00。

---

## 5.1.3 System Time Ntp

**Path:** `/NetSDK/System/time/ntp[/properties]`

**Group (inferred):** `System`

### Method: GET
- Description: 用于请求同步国际标准时间。
- Query: None.
- Content: None.
- Success Return: TimeNtp JSON Form

### Method: PUT
- Description: 用于设置 ntp 配置信息。
- Query: None.
- Content: TimeNtp JSON Form
- Success Return: Response JSON Form
- Notes: :

---

## 5.2.1 Audio Input Channel List

**Path:** `/NetSDK/Audio/input/channels[/properties]`

**Group (inferred):** `Audio`

### Method: GET
- Description: 用于获取所有音频输入通道配置。
- Query: None.
- Content: None.
- Success Return: AudioInputChannel Array JSON Form.
- Notes: :

---

## 5.2.2 Audio Input Channel

**Path:** `/NetSDK/Audio/input/channel/ID[/properties]`

**Group (inferred):** `Audio`

### Method: GET
- Description: 用于获取指定音频输入通道配置。
- Query: None.
- Content: None.
- Success Return: AudioInputChannel JSON Form.

### Method: PUT
- Description: 用于更新指定音频输入通道配置。
- Query: None.
- Content: AudioInputChannel JSON Form.
- Success Return: Response JSON Form.
- Notes: :

---

## 5.2.3 Audio Encode Channel List

**Path:** `/NetSDK/Audio/encode/channels[/properties]`

**Group (inferred):** `Audio`

### Method: GET
- Description: 用于获取音频编码通道配置列表。
- Query: None.
- Content: None.
- Success Return: AudioEncodeChannel Array JSON Form.
- Notes: :

---

## 5.2.4 Audio Encode Channel

**Path:** `/NetSDK/Audio/encode/channel/ID[/properties]`

**Group (inferred):** `Audio`

### Method: GET
- Description: 用于获取指定音频编码通道配置。
- Query: None.
- Content: None.
- Success Return: AudioEncodeChannel JSON Form.

### Method: PUT/POST
- Description: 用于更新指定音频编码通道配置。
- Query: None.
- Content: AudioEncodeChannel JSON Form.
- Success Return: Response JSON Form.
- Notes: : 4

---

## 5.3.1 Video Input Channel List

**Path:** `/NetSDK/Video/input/channles[/properties]`

**Group (inferred):** `Video`

### Method: GET
- Description: 用于获取视频输入通道配置列表。
- Query: None.
- Content: None.
- Success Return: VideoInputChannel Array JSON Form.
- Notes: :

---

## 5.3.2 Video Input Channel

**Path:** `/NetSDK/Video/input/channel/ID[/properties]`

**Group (inferred):** `Video`

### Method: GET
- Description: 用于获取视频输入通道配置列表。
- Query: None.
- Content: None.
- Success Return: VideoInputChannel JSON Form.

### Method: PUT
- Description: 用于更新视频输入通道配置列表。
- Query: None.
- Content: VideoInputChannel JSON Form.
- Success Return: Response JSON Form.
- Notes: :

---

## 5.3.3 Video Input Channel Sharpness Level

**Path:** `/NetSDK/Video/input/channel/ID/sharpnessLevel`

**Group (inferred):** `Video`

### Method: GET
- Description: 用于获取视频输入通道锐度配置值。
- Query: None.
- Content: None.
- Success Return: Interger.

### Method: PUT
- Description: 用于更新视频输入通道锐度配置值。
- Query: None.
- Content: Interger.
- Success Return: Response JSON Form.
- Notes: : 5 Range from 0 to 100.

---

## 5.3.4 Video Input Channel brightness Level

**Path:** `/NetSDK/Video/input/channel/ID/brightnessLevel`

**Group (inferred):** `Video`

### Method: GET
- Description: 用于获取视频输入通道亮度配置值。
- Query: None.
- Content: None.
- Success Return: Interger.

### Method: PUT
- Description: 用于更新视频输入通道亮度配置值。
- Query: None.
- Content: Interger.
- Success Return: Response JSON Form.
- Notes: :

---

## 5.3.5 Video Input Channel flipEnabled

**Path:** `/NetSDK/Video/input/channel/ID/flip`

**Group (inferred):** `Video`

### Method: GET
- Description: 用于获取视频输入通道 flip 配置值。
- Query: None.
- Content: None.
- Success Return: Boolean

### Method: PUT
- Description: 用于更新视频输入通道 flip 配置值。
- Query: None.
- Content: Boolean
- Success Return: Response JSON Form.
- Notes: :

---

## 5.3.6 Video Input Channel mirrorEnabled

**Path:** `/NetSDK/Video/input/channel/ID/mirror`

**Group (inferred):** `Video`

### Method: GET
- Description: 用于获取视频输入通道 mirror 配置值。
- Query: None.
- Content: None.
- Success Return: Boolean

### Method: PUT
- Description: 用于更新视频输入通道 mirror 配置值。
- Query: None.
- Content: Boolean
- Success Return: Response JSON Form.
- Notes: 6 :

---

## 5.3.7 Video Input Channel Saturation Level

**Path:** `/NetSDK/Video/input/channel/ID/saturationLevel`

**Group (inferred):** `Video`

### Method: GET
- Description: 用于获取视频输入通道饱和度配置值。
- Query: None.
- Content: None.
- Success Return: Interger.

### Method: PUT
- Description: 用于更新视频输入通道饱和度配置值。
- Query: None.
- Content: Interger.
- Success Return: Response JSON Form.
- Notes: : Range from 0 to 100.

---

## 5.3.8 Video Input Channel Hue Level

**Path:** `/NetSDK/Video/input/channel/ID/hueLevel`

**Group (inferred):** `Video`

### Method: GET
- Description: 用于获取视频输入通道色度配置值。
- Query: None.
- Content: None.
- Success Return: Interger.

### Method: PUT
- Description: 用于更新视频输入通道色度配置值。
- Query: None.
- Content: Interger.
- Success Return: Response JSON Form.
- Notes: : Range from 0 to 100.

---

## 5.3.9 Video Input Channel Contrast Level

**Path:** `/NetSDK/Video/input/channel/ID/contrastLevel`

**Group (inferred):** `Video`

### Method: GET
- Description: 用于获取视频输入通道对比度配置值。
- Query: None.
- Content: None.
- Success Return: Interger.

### Method: PUT
- Description: 用于更新视频输入通道对比度配置值。
- Query: None.
- Content: Interger.
- Success Return: Response JSON Form.
- Notes: 7 : Range from 0 to 100.

---

## 5.3.10 Video Input Channel Privacy Mask List

**Path:** `/NetSDK/Video/input/channel/ID/privacyMasks[/properties]`

**Group (inferred):** `Video`

### Method: GET
- Description: 用于获取指定视频输入通道隐私区域配置列表。
- Query: None.
- Content: None.
- Success Return: VideoPrivacyMask Array JSON Form.
- Notes: :

---

## 5.3.11 Video Input Channel Privacy Mask

**Path:** `/NetSDK/Video/input/channel/ID/privacyMask/ID[/properties]`

**Group (inferred):** `Video`

### Method: GET
- Description: 用于获取指定视频输入通道上指定隐私区域配置。
- Query: None.
- Content: None.
- Success Return: VideoPrivacyMask JSON Form.

### Method: PUT
- Description: 用于更新指定视频输入通道上指定隐私区域配置。
- Query: None.
- Content: VideoPrivacyMask JSON Form.
- Success Return: Response JSON Form.
- Notes: :

---

## 5.3.12 Video Encode Channel List

**Path:** `/NetSDK/Video/encode/channels[/properties]`

**Group (inferred):** `Video`

### Method: GET
- Description: 用于获取视频编码通道配置列表。
- Query: None.
- Content: None.
- Success Return: VideoEncodeChannel Array JSON Form.
- Notes: : 8

---

## 5.3.13 Video Encode Channel

**Path:** `/NetSDK/Video/encode/channel/ID[/properties]`

**Group (inferred):** `Video`

### Method: GET
- Description: 用于获取指定视频编码通道配置。
- Query: None.
- Content: None.
- Success Return: VideoEncodeChannel JSON Form.

### Method: PUT
- Description: 用于更新指定视频编码通道配置。
- Query: None.
- Content: VideoEncodeChannel JSON Form.
- Success Return: Response JSON Form.
- Notes: : 为了支持多个视频输入设备，在 URL 中的视频输入通道 ID 需要包含视频输入通道 ID，因 此定义为：视频编码码流 ID+视频输入通道 ID*100，例如，视频输入通道 1 的第一个视频 编码码流对应的 URL 应该为/NetSDK/Video/encode/channel/101，第二个视频编码码流对应 的 URL 应该为/NetSDK/Video/encode/channel/102。

---

## 5.3.14 Video Encode Channel Name Overlay

**Path:** `/NetSDK/Video/encode/channel/ID/channelNameOverlay[/properties]`

**Group (inferred):** `Video`

### Method: GET
- Description: 用于获取指定视频编码通道上通道名称叠加层配置。
- Query: None.
- Content: None.
- Success Return: VideoChanelNameOverlay JSON Form.

### Method: PUT
- Description: 用于更新指定视频编码通道上通道名称叠加层配置。
- Query: None.
- Content: VideoChanelNameOverlay JSON Form.
- Success Return: Response JSON Form.
- Notes: :

---

## 5.3.15 Video Encode Datetime Overlay

**Path:** `/NetSDK/Video/encode/channel/ID/datetimeOverlay[/properties]`

**Group (inferred):** `Video`

### Method: GET
- Description: 用于获取指定视频编码通道上日期时间叠加层配置。
- Query: None.
- Content: None.
- Success Return: VideoDatetimeOverlay JSON Form.

### Method: PUT
- Description: 用于更新指定视频编码通道上日期时间叠加层配置。
- Query: None.
- Content: VideoDatetimeOverlay JSON Form.
- Success Return: Response JSON Form.
- Notes: 9 :

---

## 5.3.16 Video Encode Deivce ID Overlay

**Path:** `/NetSDK/Video/encode/channel/ID/deviceIDOverlays[/properties]`

**Group (inferred):** `Video`

### Method: GET
- Description: 用于获取指定视频编码通道上设备序号叠加层配置列表。
- Query: None.
- Content: None.
- Success Return: VideoDeviceIDOverlay JSON Form.

### Method: PUT
- Description: 用于更新指定视频编码通道上设备序号叠加层配置列表。
- Query: None.
- Content: VideoDeviceIDOverlay JSON Form.
- Success Return: Response JSON Form.
- Notes: :

---

## 5.3.17 Video Encode Text Overlay List

**Path:** `/NetSDK/Video/encode/channel/ID/textOverlays[/properties]`

**Group (inferred):** `Video`

### Method: GET
- Description: 用于获取指定视频编码通道上文本叠加层配置列表。
- Query: None.
- Content: None.
- Success Return: VideoTextOverlay Array JSON Form.
- Notes: : 该接口保留。

---

## 5.3.18 Video Encode Text Overlay

**Path:** `/NetSDK/Video/encode/channel/ID/textOverlay/ID[/properties]`

**Group (inferred):** `Video`

### Method: GET
- Description: 用于获取指定视频编码通道上指定文本叠加层配置。
- Query: None.
- Content: None.
- Success Return: VideoTextOverlay JSON Form.

### Method: PUT
- Description: 用于更新指定视频编码通道上指定文本叠加层配置。
- Query: None.
- Content: VideoTextOverlay JSON Form.
- Success Return: Response JSON Form.
- Notes: 10 : 该接口保留。

---

## 5.3.19 Video Encode Request Key Frame

**Path:** `/NetSDK/Video/encode/channel/ID/requestKeyFrame`

**Group (inferred):** `Video`

### Method: PUT
- Description: 用于对指定视频编码通道请求一个关键帧。
- Query: None.
- Content: None.
- Success Return: Response JSON Form.
- Notes: :

---

## 5.3.20 Video Encode Snap Shot

**Path:** `/NetSDK/Video/encode/channel/ID/snapShot`

**Group (inferred):** `Video`

### Method: GET
- Description: 用于获取指定视频编码通道快照图片。
- Query: resolutionWidth integer
- Content: None.
- Success Return: JPEG over HTTP。
- Notes: resolutionHeight integer :

---

## 5.3.21 Video Motion Detection Channel List

**Path:** `/NetSDK/Video/motionDetection/channels[/properties]`

**Group (inferred):** `Video`

### Method: GET
- Description: 用于获取视频运动侦测通道配置列表。
- Query: None.
- Content: None.
- Success Return: VideoMotionDetectionChannel Array JSON Form.
- Notes: :

---

## 5.3.22 Video Motion Detection Channel

**Path:** `/NetSDK/Video/motionDetection/channel/ID[/properties]`

**Group (inferred):** `Video`

### Method: GET
- Description: 用于获取指定视频运动侦测通道配置。
- Query: None.
- Content: None.
- Success Return: VideoMotionDetectionChannel JSON Form.
- Notes: 11

### Method: PUT
- Description: 用于更新指定视频运动侦测通道配置。
- Query: None.
- Content: VideoMotionDetectionChannel JSON Form.
- Success Return: Response JSON Form.
- Notes: :

---

## 5.3.23 Video Motion Detection Channel Status

**Path:** `/NetSDK/Video/motionDetection/channel/ID/status`

**Group (inferred):** `Video`

### Method: GET
- Description: 用于获取指定视频运动侦测通道状态。
- Query: None.
- Content: None.
- Success Return: Boolean

### Method: DELETE
- Description: 用于清除指定视频运动侦测通道状态。
- Query: None.
- Content: None.
- Success Return: Response JSON Form.
- Notes: :

---

## 5.4.1 IO Alarm Input Channel List

**Path:** `/NetSDK/IO/alarmInput/channels[/properties]`

**Group (inferred):** `IO`

### Method: GET
- Description: 用于获取报警输入通道配置列表。
- Query: None.
- Content: None.
- Success Return: AlarmInputChannel Array JSON Form.
- Notes: : 12

---

## 5.4.2 IO Alarm Input Channel

**Path:** `/NetSDK/IO/alarmInput/channel/ID[/properties]`

**Group (inferred):** `IO`

### Method: GET
- Description: 用于获取指定报警输入通道配置。
- Query: None.
- Content: None.
- Success Return: AlarmInputChannel JSON Form.

### Method: PUT
- Description: 用于更新指定报警输入通道配置。
- Query: None.
- Content: AlarmInputChannel JSON Form.
- Success Return: Response JSON Form.
- Notes: :

---

## 5.4.3 IO Alarm Input Channel Port Status

**Path:** `/NetSDK/IO/alarmInput/channel/ID/portStatus`

**Group (inferred):** `IO`

### Method: GET
- Description: 用于获取指定报警输入通道状态。
- Query: None.
- Content: None.
- Success Return: AlarmIOPortStatus JSON Form.
- Notes: :

---

## 5.4.4 IO Alarm Output Channel List

**Path:** `/NetSDK/IO/alarmOutput/channels[/properties]`

**Group (inferred):** `IO`

### Method: GET
- Description: 用于获取报警输出通道配置列表。
- Query: None.
- Content: None.
- Success Return: AlarmOutputChannel Array JSON Form.
- Notes: :

---

## 5.4.5 IO Alarm Output Channel

**Path:** `/NetSDK/IO/alarmOutput/channel/ID[/properties]`

**Group (inferred):** `IO`

### Method: GET
- Description: 用于获取指定报警输出通道配置。
- Query: None.
- Content: None.
- Success Return: AlarmOutputChannel JSON Form.
- Notes: 13

### Method: PUT
- Description: 用于更新指定报警输出通道配置。
- Query: None.
- Content: AlarmOutputChannel JSON Form.
- Success Return: Response JSON Form.
- Notes: :

---

## 5.4.6 IO Alarm Output Channel Port Status

**Path:** `/NetSDK/IO/alarmOutput/channel/ID/portStatus`

**Group (inferred):** `IO`

### Method: GET
- Description: 用于获取指定报警输出通道状态。
- Query: None.
- Content: None.
- Success Return: AlarmIOPortStatus JSON Form.

### Method: DELETE
- Description: 用于清除指定报警输出通道状态。
- Query: None.
- Content: None.
- Success Return: Response JSON Form.
- Notes: :

---

## 5.4.8 IO Alarm Output Channel Trigger

**Path:** `/NetSDK/IO/alarmOutput/channel/ID/trigger`

**Group (inferred):** `IO`

### Method: PUT
- Description: 用于手动触发指定报警输出通道输出。
- Query: outputState low
- Content: None.
- Success Return: Response JSON Form.
- Notes: high pulse : 14

---

## 5.5.1 PTZ Channel List

**Path:** `/NetSDK/PTZ/channels[/properties]`

**Group (inferred):** `PTZ`

### Method: GET
- Description: 用于获取云台通道配置列表。
- Query: None.
- Content: None.
- Success Return: PTZChannel Array JSON Form.
- Notes: :

---

## 5.5.2 PTZ Channel

**Path:** `/NetSDK/ PTZ/channel/ID[/properties]`

**Group (inferred):** `PTZ`

### Method: GET
- Description: 用于获取指定云台通道配置。
- Query: None.
- Content: None.
- Success Return: PTZChannel JSON Form.

### Method: PUT
- Description: 用于更新指定云台通道配置。
- Query: None.
- Content: PTZChannel JSON Form.
- Success Return: Response JSON Form.
- Notes: :

---

## 5.5.3 PTZ Channel Control

**Path:** `/NetSDK/ PTZ/channel/ID/control`

**Group (inferred):** `PTZ`

### Method: PUT
- Description: 用于控制指定云台通道。
- Query: command:string pan_left
- Content: None.
- Success Return: Response JSON Form.
- Notes: pan_right tilt_up tilt_down up_left up_right down_left down_right zoom_in zoom_out focus_near focus_far iris_open 15 iris_close auto_pan set_preset clear_preset goto_preset mode:string start stop speed:integer 0-100 presetNo:integer :

---

## 5.6.1 Network Transport Stream Channel List

**Path:** `/NetSDK/Stream/channles`

**Group (inferred):** `Stream`

### Method: GET
- Description: 用于获取网络传输码流通道配置列表。
- Query: None.
- Content: None.
- Success Return: StreamChannel Array JSON Form.
- Notes: :

---

## 5.6.2 Network Transport Stream Channel

**Path:** `/NetSDK/Stream/channel/ID`

**Group (inferred):** `Stream`

### Method: GET
- Description: 用于获取指定网络传输码流通道配置。
- Query: None.
- Content: None.
- Success Return: StreamChannel JSON Form.
- Notes: : 16

---

## 5.7.1 Network Interface List

**Path:** `/NetSDK/Network/interfaces[/properties]`

**Group (inferred):** `Network`

### Method: GET
- Description: 用于获取网络接口配置列表。
- Query: None.
- Content: None.
- Success Return: NetworkInterfaceList JSON Form.
- Notes: :

---

## 5.7.2 Network Interface

**Path:** `/NetSDK/Network/interfaces/ID[/properties]`

**Group (inferred):** `Network`

### Method: GET
- Description: 用于获取指定网络接口配置。
- Query: None.
- Content: None.
- Success Return: NetworkInterfaceList JSON Form.

### Method: PUT
- Description: 用于更新指定网络接口配置。
- Query: None.
- Content: NetworkInterfaceList JSON Form.
- Success Return: Response JSON Form.
- Notes: :列表有 4 个网络接口，其中 id 为 1 的是本地 IP 配置，id 为 2 的是保留接口，id 为 3 的是默认 IP 配置，id 为 4 的是 wifi 配置，请留意 interfaceName 属性

---

## 5.7.3 Network Interface Lan

**Path:** `/NetSDK/Network/interfaces/ID/lan[/properties]`

**Group (inferred):** `Network`

### Method: GET
- Description: 用于获取指定网络接口 LAN 配置。
- Query: None.
- Content: None.
- Success Return: NetworkInterfaceLan JSON Form.

### Method: PUT
- Description: 用于更新指定网络接口 LAN 配置。
- Query: None.
- Content: NetworkInterfaceLan JSON Form.
- Success Return: Response JSON Form.
- Notes: : 17

---

## 5.7.4 Network Interface Pppoe

**Path:** `/NetSDK/Network/interfaces/ID/pppoe[/properties]`

**Group (inferred):** `Network`

### Method: GET
- Description: 用于获取指定网络接口配置。
- Query: None.
- Content: None.
- Success Return: NetworkInterfacePppoe JSON Form.

### Method: PUT
- Description: 用于更新指定网络接口配置。
- Query: None.
- Content: NetworkInterfacePppoe JSON Form.
- Success Return: Response JSON Form.
- Notes: :

---

## 5.7.5 Network Interface Dns

**Path:** `/NetSDK/Network/interfaces/ID/ddns[/properties]`

**Group (inferred):** `Network`

### Method: GET
- Description: 用于获取指定网络接口 DDNS 配置。
- Query: None.
- Content: None.
- Success Return: NetworkInterfaceDdns JSON Form.

### Method: PUT
- Description: 用于更新指定网络接口 DDNS 配置。
- Query: None.
- Content: NetworkInterfaceDdns JSON Form.
- Success Return: Response JSON Form.
- Notes: :

---

## 5.7.6 Network Interface Wireless

**Path:** `NetSDK/Network/interfaces/ID/wireless[/properties]`

**Group (inferred):** `Network`

### Method: GET
- Description: 用于获取指定网络接口 WIRELESS 配置。
- Query: None.
- Content: None.
- Success Return: NetworkInterfaceWireless JSON Form.

### Method: PUT
- Description: 用于更新指定网络接口 WIRELESS 配置。
- Query: None.
- Content: NetworkInterfaceWireless JSON Form.
- Success Return: Response JSON Form.
- Notes: 18 :

---

## 5.7.7 Network Port List

**Path:** `/NetSDK/Network/Ports[/properties]`

**Group (inferred):** `Network`

### Method: GET
- Description: 用于获取所有网络端口配置列表。
- Query: None.
- Content: None.
- Success Return: NetworkPortListJSON Form.
- Notes: : web/data 端口只是用 ID 为 1 的，其余为保留接口

---

## 5.7.8 Network Port

**Path:** `/NetSDK/Network/Port/ID[/properties]`

**Group (inferred):** `Network`

### Method: GET
- Description: 用于获取指定网络端口配置。
- Query: None.
- Content: None.
- Success Return: NetworkPortListJSON Form.

### Method: PUT/POST
- Description: 用于更新指定网络端口配置。
- Query: None.
- Content: NetworkPortList JSON Form.
- Success Return: Response JSON Form.
- Notes: :

---

## 5.7.9 Network Dns

**Path:** `/NetSDK/Network/Dns[/properties]`

**Group (inferred):** `Network`

### Method: GET
- Description: 用于获取网络 DNS 配置。
- Query: None.
- Content: None.
- Success Return: NetworkDns JSON Form.

### Method: PUT/POST
- Description: 用于更新网络 DNS 配置。
- Query: None.
- Content: NetworkDns JSON Form.
- Success Return: Response JSON Form.
- Notes: : 19

---

## 5.7.10 Network Esee

**Path:** `/NetSDK/Network/Esee`

**Group (inferred):** `Network`

### Method: GET
- Description: 用于获取 ESEE 配置。
- Query: None.
- Content: None.
- Success Return: NetworkEsee JSON Form.

### Method: PUT/POST
- Description: 用于更新 ESEE 配置。
- Query: None.
- Content: NetworkEsee JSON Form.
- Success Return: Response JSON Form.
- Notes: :

---

## 5.8.1 SDCard media search

**Path:** `/NetSDK/SDCard/media/search`

**Group (inferred):** `SDCard`

### Method: GET
- Description: 用于获取 SDCard media
- Query: None.
- Content: None.
- Success Return: SDCardDbMedia JSON Form.
- Notes: : 按 beginUTC, endUTC 获取 SDCsrd db media

---

## 5.8.2 SDCard media playbackFLV

**Path:** `/NetSDK/SDCard/media/playbackFLV`

**Group (inferred):** `SDCard`

### Method: GET
- Description: 用于回放 SDCard media
- Query: None.
- Content: None.
- Success Return: Integer 0.
- Notes: :

---

## 5.8.3 SDCard format

**Path:** `/NetSDK/SDCard/format`

**Group (inferred):** `SDCard`

### Method: GET
- Description: 用于格式化 SDCard
- Query: None.
- Content: None.
- Success Return: Integer 0.
- Notes: 20 :

---

## 5.8.4 SDCard status

**Path:** `/NetSDK/SDCard/status`

**Group (inferred):** `SDCard`

### Method: GET
- Description: 用于获取 SDCard 状态
- Query: None.
- Content: None.
- Success Return: Sdcard status.
- Notes: : Sdcard status:1、work 2、ejected 3、fserror 4、formatting 5、zombie

---

## 5.9.1 Image

**Path:** `/NetSDK/Image[/properties]`

**Group (inferred):** `Image`

### Method: GET
- Description: 用于获取图像处理配置
- Query: None.
- Content: None.
- Success Return: Image JSON Form.

### Method: PUT/POST
- Description: 用于设置图像处理配置
- Query: None.
- Content: Image JSON Form.
- Success Return: Response JSON Form.
- Notes: : 查询用get，设置用put，设置成功后返回ok的json信息。

---

## 5.9.2 IrCutFilter

**Path:** `/NetSDK/Image/irCutFilter[/properties]`

**Group (inferred):** `Image`

### Method: GET
- Description: 用于获取红外配置
- Query: None.
- Content: None.
- Success Return: IrCutFilter JSON Form.

### Method: PUT/POST
- Description: 用于设置红外配置
- Query: None.
- Content: IrCutFilter JSON Form.
- Success Return: Response JSON Form.
- Notes: 21 : 查询用 get，设置用 put，设置成功后返回 ok 的 json 信息。

---

## 5.9.3 ManualSharpness

**Path:** `/NetSDK/Image/manualSharpness[/properties]`

**Group (inferred):** `Image`

### Method: GET
- Description: 用于获取手动锐度配置
- Query: None.
- Content: None.
- Success Return: ManualSharpness JSON Form.

### Method: PUT/POST
- Description: 用于设置手动锐度配置
- Query: None.
- Content: ManualSharpness JSON Form.
- Success Return: Response JSON Form.
- Notes: : 查询用 get，设置用 put，设置成功后返回 ok 的 json 信息。

---

## 5.9.4 Denoise3D

**Path:** `/NetSDK/Image/denoise3d[/properties]`

**Group (inferred):** `Image`

### Method: GET
- Description: 用于获取 3D 去噪配置
- Query: None.
- Content: None.
- Success Return: Denoise3D JSON Form.

### Method: PUT/POST
- Description: 用于设置 3D 去噪配置
- Query: None.
- Content: Denoise3D JSON Form.
- Success Return: Response JSON Form.
- Notes: : 查询用 get，设置用 put，设置成功后返回 ok 的 json 信息。

---

## 5.9.5 WDR

**Path:** `/NetSDK/Image/wdr[/properties]`

**Group (inferred):** `Image`

### Method: GET
- Description: 用于获取宽动态配置
- Query: None.
- Content: None.
- Success Return: WDR JSON Form.
- Notes: 22

### Method: PUT/POST
- Description: 用于设置宽动态配置
- Query: None.
- Content: WDR JSON Form.
- Success Return: Response JSON Form.
- Notes: : 查询用 get，设置用 put，设置成功后返回 ok 的 json 信息。

---

## 5.9.6 AF

**Path:** `/NetSDK/Image/AF`

**Group (inferred):** `Image`

### Method: GET
- Description: 用于获取自动对焦统计
- Query: None.
- Content: None.
- Success Return: Unsigned short: Focus Metries Value（text/html）

### Method: DELETE
- Description: 用于清除自动对焦统计推送
- Query: None.
- Content: None
- Success Return: Response JSON Form
- Notes: : 查询用 get，get 后设备会对该 socket 进行主动推送 HTTP 数据包，推送失败 100 次会自动 清除推送状态。主动清除用 delete，清除成功后返回 ok 的 json 信息。 Appendix I Data Structure DeviceInfo { /* rw, string, */ "deviceName", /* rw, integer, "1-128", */ 23 "deviceAddress", /* ro, string, */ "description", /* ro, string, */ "model", /* ro, string, */ "serialNumber", /* ro, string, */ "macAddress", /* ro, string, */ "firmwareVersion", /* ro, string, */ "firmwareReleaseDate", /* ro, string, */ "firmwareVersion", /* ro, string, */ "hardwareVersion", } TimeNtp { /* rw, boolean, [true, false] */ " ntpEnabled", /* rw, string, "max": 64, */ " ntpServerDomain", } AudioInputChannel { /* ro, integer, */ "id", /* ro, string, "input,output", */ "workMode", /* rw, integer, "8000", */ "sampleRate", /* rw, integer, "16", */ "sampleBitWidth", /* rw, integer, "0-100", */ "inputVolume", /* rw, integer, "0-100", */ "outputVolume", } AudioEncodeChannel 24 { /* ro, integer, */ "id", /* ro, integer, */ "audioInputChannelID", /* rw, boolean, */ "enabled", /* rw, string, "G.711alaw,G.711ulaw", */ "codecType", } VideoPrivacyMask { /* ro, integer, */ "id", /* ro, boolean, */ "enabled", /* ro, float, "0-100", the relative percentage to capture size */ "regionX", "regionY", "regionWidth", "regionHeight", /* ro, string, the mask color, RGB888 format, such as red "ff0000" */ "regionColor", } VideoInputChannel 25 { /* ro, integer, */ "id", /* ro, integer, "50,60", */ "powerLineFrequencyMode", /* ro, integer, the video capture size, */ "captureWidth", "captureWidthProperty", /* ro, integer, depends on the <powerLineFrequencyMode>, */ "captureFrameRate", /* ro, integer, "0-100", */ "brightnessLevel", "contrastLevel", "sharpnessLevel", "saturationLevel", "hueLevel", /* video privacy mask list, */ "privacyMask" : [], } VideoChannelNameOverlay { /* rw, boolean, indicates the overlay shows or not, */ "enabled", /* rw, float, "0-100", indicates the location of overlay, */ /* the relative percentage of video size, */ "regionX", "regionY", } VideoDatetimeOverlay 26 { /* rw, boolean, indicates the overlay shows or not, */ "enabled", "regionX", "regionY", /* rw, string, */ /* "YYYY/MM/DD,MM/DD/YYYY,DD/MM/YYYY", */ /* "YYYY-MM-DD,MM-DD-YYYY,DD-MM-YYYY", */ /* indicates the date format to display, */ "dateFormat", /* rw, integer, "12,24", indicates the time format to display, */ "timeFormat", /* rw, boolean, */ "displayWeek", } VideoDeviceIDOverlay { /* rw, boolean, indicates the overlay shows or not, */ "enabled", "regionX", "regionY", } VideoTextOverlay { /* rw, boolean, indicates the overlay shows or not, */ "enabled", "regionX", "regionY", /* rw, string, */ "message", } VideoEncodeChannel { /* ro, integer, */ "id", /* rw, string, */ "channelName", /* rw, boolean, */ "enabled", /* ro, integer, refers to </NetSDK/Video/input/channel/ID>, */ "videoInputChannelID", 27 /* rw, string, "H.264", */ "codecType", /* rw, string, "baseline,main,high", */ "h264Profile", /* rw, string, format <width x height>, */ "resolution", /* rw, boolean, */ "freeResolution", /* rw, integer, indicates the video size when <freeResolution> true, */ "resolutionWidth", "resolutionHeight", /* rw, string, "CBR,VBR", */ "bitRateControlType", /* rw, integer, */ "constantBitRate", /* rw, integer, */ "frameRate", /* rw, integer, */ "keyFrameInterval", /* rw, string, "JPEG" */ "snapShotImageType", /* the channel name overlay of this encoding, */ "channelNameOverlay", /* the datetime overlay of this encoding, */ "datetimeOverlay", /* the device ID overlay of this encoding, */ "deviceIDOverlay", /* the text overlay of this encoding, */ "textOverlay" : [], } VideoMotionDetectionGrid { /* ro, integer, */ "rowGranularity", /* ro, integer, */ "columnGranularity ", /* rw, a boolean <rowGranularity> by <columnGranularity> matrix, */ "granularity", /* ro, integer, "0-100" */ "sensitivityLevel", } VideoMotionDetectionRegion 28 { /* ro, integer, */ "id", /* rw, boolean, */ "enabled", /* rw, float, "0-100" */ "regionX", "regionY", "regionWidth", "regionHeight", /* ro, integer, "0-100" */ "sensitivityLevel", } VideoMotionDetectionChannel { /* ro, integer, */ "id", /* rw, boolean, */ "enabled", /* rw, string, "grid,region", */ " detectionType", /* refers to <VideoMotionDetectionGrid>, */ " detectionGrid", /* refers to <VideoMotionDetectionRegion>, */ " detectionRegion" : [], } AlarmInputChannel { /* ro, integer, */ "id", "active" : { /* rw, string, "high,low", */ "defaultState", /* rw, string, "high,low", */ "activeState", } } AlarmOutputChannel { /* ro, integer, */ "id", 29 "active" : { /* rw, string, "high,low", */ "defaultState", "activeState", }, /* rw, string, "continuous,low", */ "powerOnState", /* rw, integer, "1000-", in millisecond */ "pulseDuration", } AlarmIOPortStatus { /* ro, string, "input,output", the type of this alarm IO port */ "type", /* ro, string, "high,low", the current state of this alarm IO port */ "state", /* ro, boolean, */ "active", } PTZChannel { /* ro, integer, */ "id", /* ro, integer, */ "serialPortID", "videoInputID", /* ro, string, "half,full", */ "duplexMode", /* ro, string, "internal,external", */ "controlType, "externalControl" : { /* rw, string, */ "protocol", /* rw, integer, */ "address", }, } StreamTransportRTSP { /* ro, boolean, */ 30 "enabled", /* ro, boolean, */ "authorization", /* rtsp://<ipAddress>[:<port>]/<absolutePath>, */ /* ro, string, */ "absolutePath", /* ro, integer, "1-60000", */ "port", } StreamTransportRTSPoverHTTP { /* ro, boolean, */ "enabled", /* ro, boolean, */ "authorization", /* ro, string, "GET", */ "requestMethod", /* http://<ipAddress>[:<port>]/<absolutePath>, */ /* ro, string, */ "absolutePath", /* ro, integer, "1-60000", */ "port", } StreamTransportFLVoverHTTP { /* ro, boolean, */ "enabled", /* ro, boolean, */ "authorization", /* ro, string, "GET", */ "requestMethod", /* http://<ipAddress>[:<port>]/<absolutePath>, */ /* ro, string, */ "absolutePath", /* ro, integer, "1-60000", */ "port", } StreamTransportRTMP { /* ro, boolean, */ "enabled", 31 /* ro, boolean, */ "authorization", /* rtmp://<ipAddress>[:<port>]/<absolutePath>, */ /* ro, string, */ "absolutePath", /* ro, integer, "1-60000", */ "port", } StreamChannel { /* ro, ineteger, */ "id", /* ro, ineteger, refers to </NetSDK/audio/encode/channel/ID>, */ "audioEncodeChannelID", /* ro, ineteger, refers to </NetSDK/video/encode/channel/ID>, */ "videoEncodeChannelID", /* refers to <StreamTransportRTSP>, */ "transportRTSP", /* refers to <StreamTransportRTSPoverHTTP>, */ "transportRTSPoverHTTP", /* refers to <StreamTransportFLVoverHTTP>, */ "transportFLVoverHTTP", /* refers to <StreamTransportRTMP>, */ "transportRTMP", } NetworkInterfaceList { /* ro, integer, */ "id", /* ro, string, */ "interfaceName", /* refers to <NetworkInterfaceLan>, */ "lan", /* refers to <NetworkInterfaceUpnp>, */ "upnp", /* refers to <NetworkInterfacePppoe>, */ "pppoe", /* refers to <NetworkInterfaceDdns>, */ "ddns", /* refers to <NetworkInterfaceWireless>, */ "wireless", /* refers to <NetworkInterfaceAccessPointMode>, */ 32 "accessPointMode", /* refers to <NetworkInterfaceDhcpServer>, */ "dhcpServer", } NetworkInterfaceLan { /* rw, string, */ " ipVersion", /* rw, string, ["static", "dynamic"] */ " addressingType", /* rw, string, */ " staticIP", /* rw, string, */ " staticNetmask", /* rw, string, */ " staticGateway", } NetworkInterfaceUpnp { /* ro, boolean, */ "enabled", } NetworkInterfacePppoe { /* ro, boolean, */ "enabled", /* rw, string, "max": 64 * "pppoeUserName", /* rw, string, "max": 64 * "pppoePassword", } NetworkInterfaceDns { /* ro, boolean, */ "enabled", /*rw,string, "DYNDDNS","NOIP","3322", "CHANGEIP","POPDVR","SKYBEST","DVRTOP"*/ "ddnsProvider", /* rw, string, "max": 64 */ "ddnsUrl", 33 /* rw, string, "max": 64 */ "ddnsUserName", /* rw, string, "max": 64 */ "ddnsPassword", } NetworkInterfaceWireless { /* rw, string, ["none","accessPoint","stationMode"] */ "wirelessMode", /* refers to < stationMode >, */ "stationMode", } NetworkInterfaceStationMode { /* ro, string, */ "wirelessStaMode", /* rw, string, "max": 48 */ "wirelessApBssId", /* rw, string, "max": 32 */ "wirelessApEssId", /* rw, string, "min": 8,"max": 63 */ "wirelessApPsk", } NetworkInterfaceAccessPointMode { /* ro, string, */ "wirelessBssId", /* rw, string, "max": 32 */ "wirelessEssId", /* rw, string, "min": 8,"max": 63 */ "wirelessPsk", /* rw, string, ["802.11b","802.11g","802.11n","802.11bg","802.11bgn"] */ "wirelessApMode", /*rw,string, ["Auto","1","2","3","4","5", "6","7","8","9","10","11","12","13","14"]*/ "wirelessApMode80211nChannel", /* ro,boolean, */ "wirelessEssIdBroadcastingEnabled", /* rw, string, ["WPA_PSK","WPA2_PSK"] */ "wirelessWpaMode", } 34 NetworkInterfaceDhcpServer { /* ro, boolean, */ "enabled", /* ro, boolean, */ "dhcpAutoSettingEnabled", /* rw, string, "max": 64 */ "dhcpIpRange", /* rw, string, "max": 32 */ "dhcpIpNumber", /* rw, string, "max": 32 */ "dhcpIpDns", /* rw, string, "max": 32 */ "dhcpIpGateway", } NetworkPortList { /* ro, integer, */ "id", /* ro, string, */ "portName", /* rw, integer, "min" : 1, "max" 60000 */ "value", } NetworkDNS { /* ro, string, */ "preferredDns", /* ro, string, */ "staticAlternateDns", } NetworkEsee { /* ro, boolean, */ "enabled", } SDCardDbMedia { /* rw, integer, */ 35 "sessionID" /* rw, integer, */ "channelID". /* rw, integer, */ "beginUTC" /* rw, integer, */ "endUTC" /* rw, strng, */ "type" }, Image { /* rw, string, [ "auto", "indoor", "outdoor"], */ "sceneMode" /* refers to <ImageIrCutFilter>, */ "irCutFilter" /* refers to < ImageManualSharpness>, */ "manualSharpness" /* refers to <ImageDenoise3d>, */ "denoise3d" /* refers to <ImageWDR>, */ "WDR" /* rw, string, ["auto", "bright", "dark"]*/ "exposureMode" /* rw, string, ["auto", "indoor", "outdoor"]*/ "awbMode" /* rw, string, ["close", "only night", "day-night", "auto"]*/ "lowlightMode" } IrCutFilter { /* rw, string, ["hardware", "software"] */ "irCutControlMode" /* rw, string, ["auto", "daylight", "night"]*/ "irCutMode" } ManualSharpness { /* ro, boolean, */ "enabled" 36 /* rw, integer, "min" : 0, "max" : 255, "def" : 170*/ "sharpnessLevel" } Denoise3d { /* ro, boolean, */ "enabled" /* rw, integer, "min" : 1, "max" : 5, "def" : 1*/ "denoise3dStrength" } WDR { /* ro, boolean, */ "enabled" /* rw, integer, "min" : 1, "max" : 5, "def" : 3 */ "WDRStrength" } 37 - Many endpoints support GET for read and PUT/POST for write. - Preserve exact path capitalization when testing firmware. - Some PDFs contain OCR errors; validate against live device responses. - Use authenticated HTTP requests if device requires login/session. 1. Generate Python client from endpoint list. 2. Probe live device and compare supported routes. 3. Build ONVIF + NETSDK bridge. 4. Auto-discover writable image/night-vision settings.

---

## Raw Data Structures

## Fact / Inference Boundaries

## Directly known from extracted text
- Endpoint paths as printed in the PDF text extraction
- HTTP methods where printed near each endpoint
- Nearby description/query/content/success-return text
- Raw schema appendix blocks

## Inferred
- Tag names per endpoint group
- OpenAPI `operationId` names
- Converting `ID` placeholders into generic path parameters
- Linking request/response bodies to schema names when the PDF wording appeared to match
- Primitive response types where the PDF said Integer / Boolean / ISO8601 / JPEG

## Design-unsound vs unproven
- **Unproven:** Any endpoint behavior not validated against a live device
- **Unproven:** Whether every PUT/POST route actually accepts JSON exactly as drafted
- **Design-unsound:** Treating this draft OpenAPI as production truth without probing firmware