# SDK API TRUTH

Classification: SDK_CALLBACK_PATH_FOUND

SDK folders inspected:
- C:\Users\ceide\Documents\BossCamSuite_SDK_AND_SUPPORT_FILES
- C:\Users\ceide\Downloads\NETSDK_V1.4_SECONDARY_DEVELOPMENT_INFORMATION_FOR_CONTROLLING_IPC
- C:\Users\ceide\Downloads\NVR_SDK_v1.1.0.8
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)

Candidate DLLs/libs:
- C:\Users\ceide\Documents\BossCamSuite_SDK_AND_SUPPORT_FILES\NVR_SDK_v1.1.0.8\avlib.dll
- C:\Users\ceide\Documents\BossCamSuite_SDK_AND_SUPPORT_FILES\NVR_SDK_v1.1.0.8\HISISDK.dll
- C:\Users\ceide\Downloads\NVR_SDK_v1.1.0.8\avlib.dll
- C:\Users\ceide\Downloads\NVR_SDK_v1.1.0.8\HISISDK.dll

Candidate headers:
- C:\Users\ceide\Documents\BossCamSuite_SDK_AND_SUPPORT_FILES\NVR_SDK_v1.1.0.8\HISISDK.h
- C:\Users\ceide\Documents\BossCamSuite_SDK_AND_SUPPORT_FILES\NVR_SDK_v1.1.0.8\HisiSdkTest\HISISDK.h
- C:\Users\ceide\Documents\BossCamSuite_SDK_AND_SUPPORT_FILES\NVR_SDK_v1.1.0.8\HisiSdkTest\HisiSdkTest.h
- C:\Users\ceide\Documents\BossCamSuite_SDK_AND_SUPPORT_FILES\NVR_SDK_v1.1.0.8\HisiSdkTest\HisiSdkTestDlg.h
- C:\Users\ceide\Documents\BossCamSuite_SDK_AND_SUPPORT_FILES\NVR_SDK_v1.1.0.8\HisiSdkTest\resource.h
- C:\Users\ceide\Documents\BossCamSuite_SDK_AND_SUPPORT_FILES\NVR_SDK_v1.1.0.8\HisiSdkTest\stdafx.h
- C:\Users\ceide\Documents\BossCamSuite_SDK_AND_SUPPORT_FILES\NVR_SDK_v1.1.0.8\multiple test\multiple test\HISISDK.h
- C:\Users\ceide\Documents\BossCamSuite_SDK_AND_SUPPORT_FILES\NVR_SDK_v1.1.0.8\multiple test\multiple test\multiple test.h
- C:\Users\ceide\Documents\BossCamSuite_SDK_AND_SUPPORT_FILES\NVR_SDK_v1.1.0.8\multiple test\multiple test\multiple testDlg.h
- C:\Users\ceide\Documents\BossCamSuite_SDK_AND_SUPPORT_FILES\NVR_SDK_v1.1.0.8\multiple test\multiple test\resource.h
- C:\Users\ceide\Documents\BossCamSuite_SDK_AND_SUPPORT_FILES\NVR_SDK_v1.1.0.8\multiple test\multiple test\SdkLib.h
- C:\Users\ceide\Documents\BossCamSuite_SDK_AND_SUPPORT_FILES\NVR_SDK_v1.1.0.8\multiple test\multiple test\stdafx.h
- C:\Users\ceide\Documents\BossCamSuite_SDK_AND_SUPPORT_FILES\NVR_SDK_v1.1.0.8\multiple test\multiple test\targetver.h
- C:\Users\ceide\Downloads\NVR_SDK_v1.1.0.8\HISISDK.h
- C:\Users\ceide\Downloads\NVR_SDK_v1.1.0.8\HisiSdkTest\HISISDK.h
- C:\Users\ceide\Downloads\NVR_SDK_v1.1.0.8\HisiSdkTest\HisiSdkTest.h
- C:\Users\ceide\Downloads\NVR_SDK_v1.1.0.8\HisiSdkTest\HisiSdkTestDlg.h
- C:\Users\ceide\Downloads\NVR_SDK_v1.1.0.8\HisiSdkTest\resource.h
- C:\Users\ceide\Downloads\NVR_SDK_v1.1.0.8\HisiSdkTest\stdafx.h
- C:\Users\ceide\Downloads\NVR_SDK_v1.1.0.8\multiple test\multiple test\HISISDK.h
- C:\Users\ceide\Downloads\NVR_SDK_v1.1.0.8\multiple test\multiple test\multiple test.h
- C:\Users\ceide\Downloads\NVR_SDK_v1.1.0.8\multiple test\multiple test\multiple testDlg.h
- C:\Users\ceide\Downloads\NVR_SDK_v1.1.0.8\multiple test\multiple test\resource.h
- C:\Users\ceide\Downloads\NVR_SDK_v1.1.0.8\multiple test\multiple test\SdkLib.h
- C:\Users\ceide\Downloads\NVR_SDK_v1.1.0.8\multiple test\multiple test\stdafx.h
- C:\Users\ceide\Downloads\NVR_SDK_v1.1.0.8\multiple test\multiple test\targetver.h

Export/sample/callback clues:
- C:\Users\ceide\Downloads\NVR_SDK_v1.1.0.8\HISISDK.h:28:#define  HISI_EXCEPTION_EXMAXCHANNEL  0x8012;       //�������ͨ����
- C:\Users\ceide\Downloads\NVR_SDK_v1.1.0.8\HISISDK.h:107:    LONG Channel;                    //ͨ����,��1��ʼ
- C:\Users\ceide\Downloads\NVR_SDK_v1.1.0.8\HISISDK.h:114:    LONG Channel;                    //ͨ����,��1��ʼ
- C:\Users\ceide\Downloads\NVR_SDK_v1.1.0.8\HISISDK.h:115:    LONG Stream;
- C:\Users\ceide\Downloads\NVR_SDK_v1.1.0.8\HISISDK.h:241:	int nChannel;
- C:\Users\ceide\Downloads\NVR_SDK_v1.1.0.8\HISISDK.h:282:			int channel; //ͨ����
- C:\Users\ceide\Downloads\NVR_SDK_v1.1.0.8\HISISDK.h:295:}Frame_Head_t;
- C:\Users\ceide\Downloads\NVR_SDK_v1.1.0.8\HISISDK.h:300:    byte Channel[4];
- C:\Users\ceide\Downloads\NVR_SDK_v1.1.0.8\HISISDK.h:320:LONG __stdcall HISI_DVR_Login(char *sDVRIP,WORD wDVRPort,WORD wHttpPort, char *sUserName,char *sPassword,PHISI_DEVCEINFO lpDeviceInfo);
- C:\Users\ceide\Downloads\NVR_SDK_v1.1.0.8\HISISDK.h:323:LONG __stdcall HISI_DVR_RealPlay(LONG lUserID,PHISI_DEV_CLIENTINFO lpClientInfo);
- C:\Users\ceide\Downloads\NVR_SDK_v1.1.0.8\HISISDK.h:324:LONG __stdcall HISI_DVR_RealPlayEx(LONG lUserID,PHISI_DEV_CLIENTINFOEX lpClientInfo);
- C:\Users\ceide\Downloads\NVR_SDK_v1.1.0.8\HISISDK.h:331:BOOL __stdcall HISI_DVR_SetRealDataCallBack(LONG lRealHandle,void(CALLBACK *fRealDataCallBack) (LONG lRealHandle, DWORD dwDataType, BYTE *pBuffer,DWORD dwBufSize,DWORD dwUser),DWORD dwUser);
- C:\Users\ceide\Downloads\NVR_SDK_v1.1.0.8\HISISDK.h:342:BOOL __stdcall HISI_DVR_GetDVRConfig(LONG lUserID, DWORD dwCommand,LONG lChannel, LPVOID lpOutBuffer, DWORD dwOutBufferSize, LPDWORD lpBytesReturned);
- C:\Users\ceide\Downloads\NVR_SDK_v1.1.0.8\HISISDK.h:343:BOOL __stdcall HISI_DVR_SetDVRConfig(LONG lUserID, DWORD dwCommand,LONG lChannel, LPVOID lpInBuffer, DWORD dwInBufferSize);
- C:\Users\ceide\Downloads\NVR_SDK_v1.1.0.8\HISISDK.h:345:LONG HISI_DVR_FindFile(LONG lUserID,LONG lChannel,HISI_DVR_RECORDTYPE dwFileType,PHISI_DVR_TIME lpStartTime,PHISI_DVR_TIME lpStopTime);
- C:\Users\ceide\Downloads\NVR_SDK_v1.1.0.8\HISISDK.h:349:LONG __stdcall HISI_DVR_PlayBackByTime(LONG lUserID,LONG lChannel, PHISI_DVR_TIME lpStartTime, PHISI_DVR_TIME lpStopTime, HWND hWnd);
- C:\Users\ceide\Downloads\NVR_SDK_v1.1.0.8\HISISDK.h:355:int __stdcall HISI_DVR_GetFileByTime(int lUserID, int lChannel, PHISI_DVR_TIME lpStartTime, PHISI_DVR_TIME lpStopTime, char *sSavedFileName);
- C:\Users\ceide\Downloads\NVR_SDK_v1.1.0.8\HISISDK.h:401:BOOL  __stdcall HISI_Play_OpenStream(LONG nPort);
- C:\Users\ceide\Downloads\NVR_SDK_v1.1.0.8\HISISDK.h:402:BOOL  __stdcall HISI_Play_CloseStream(LONG nPort);
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\probe_endpoints.sh:19:  "/NetSDK/Audio/input/channels"
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\probe_endpoints.sh:20:  "/NetSDK/Audio/input/channel/1"
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\probe_endpoints.sh:21:  "/NetSDK/Audio/encode/channels"
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\probe_endpoints.sh:22:  "/NetSDK/Audio/encode/channel/1"
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\probe_endpoints.sh:24:  "/NetSDK/Video/input/channel/1"
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\probe_endpoints.sh:25:  "/NetSDK/Video/input/channel/1/sharpnessLevel"
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\probe_endpoints.sh:26:  "/NetSDK/Video/input/channel/1/brightnessLevel"
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\probe_endpoints.sh:27:  "/NetSDK/Video/input/channel/1/flip"
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\probe_endpoints.sh:28:  "/NetSDK/Video/input/channel/1/mirror"
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\probe_endpoints.sh:29:  "/NetSDK/Video/input/channel/1/saturationLevel"
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\probe_endpoints.sh:30:  "/NetSDK/Video/input/channel/1/hueLevel"
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\probe_endpoints.sh:31:  "/NetSDK/Video/input/channel/1/contrastLevel"
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\probe_endpoints.sh:32:  "/NetSDK/Video/input/channel/1/privacyMasks"
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\probe_endpoints.sh:33:  "/NetSDK/Video/input/channel/1/privacyMask/1"
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\probe_endpoints.sh:34:  "/NetSDK/Video/encode/channels"
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\probe_endpoints.sh:35:  "/NetSDK/Video/encode/channel/1"
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\probe_endpoints.sh:36:  "/NetSDK/Video/encode/channel/1/channelNameOverlay"
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\probe_endpoints.sh:37:  "/NetSDK/Video/encode/channel/1/datetimeOverlay"
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\probe_endpoints.sh:38:  "/NetSDK/Video/encode/channel/1/deviceIDOverlays"
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\probe_endpoints.sh:39:  "/NetSDK/Video/encode/channel/1/textOverlays"
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\probe_endpoints.sh:40:  "/NetSDK/Video/encode/channel/1/textOverlay/1"
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\probe_endpoints.sh:41:  "/NetSDK/Video/encode/channel/1/requestKeyFrame"
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\probe_endpoints.sh:42:  "/NetSDK/Video/encode/channel/1/snapShot"
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\probe_endpoints.sh:43:  "/NetSDK/Video/motionDetection/channels"
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\probe_endpoints.sh:44:  "/NetSDK/Video/motionDetection/channel/1"
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\probe_endpoints.sh:45:  "/NetSDK/Video/motionDetection/channel/1/status"
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\probe_endpoints.sh:46:  "/NetSDK/IO/alarmInput/channels"
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\probe_endpoints.sh:47:  "/NetSDK/IO/alarmInput/channel/1"
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\probe_endpoints.sh:48:  "/NetSDK/IO/alarmInput/channel/1/portStatus"
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\probe_endpoints.sh:49:  "/NetSDK/IO/alarmOutput/channels"
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\probe_endpoints.sh:50:  "/NetSDK/IO/alarmOutput/channel/1"
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\probe_endpoints.sh:51:  "/NetSDK/IO/alarmOutput/channel/1/portStatus"
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\probe_endpoints.sh:52:  "/NetSDK/IO/alarmOutput/channel/1/trigger"
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\probe_endpoints.sh:53:  "/NetSDK/PTZ/channels"
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\probe_endpoints.sh:54:  "/NetSDK/ PTZ/channel/1"
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\probe_endpoints.sh:55:  "/NetSDK/ PTZ/channel/1/control"
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\probe_endpoints.sh:56:  "/NetSDK/Stream/channles"
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\probe_endpoints.sh:57:  "/NetSDK/Stream/channel/1"
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:25:- **5.2.1 Audio Input Channel List**
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:27:  - Path: `/NetSDK/Audio/input/channels[/properties]`
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:29:- **5.2.2 Audio Input Channel**
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:31:  - Path: `/NetSDK/Audio/input/channel/ID[/properties]`
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:33:- **5.2.3 Audio Encode Channel List**
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:35:  - Path: `/NetSDK/Audio/encode/channels[/properties]`
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:37:- **5.2.4 Audio Encode Channel**
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:39:  - Path: `/NetSDK/Audio/encode/channel/ID[/properties]`
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:41:- **5.3.1 Video Input Channel List**
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:45:- **5.3.2 Video Input Channel**
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:47:  - Path: `/NetSDK/Video/input/channel/ID[/properties]`
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:49:- **5.3.3 Video Input Channel Sharpness Level**
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:51:  - Path: `/NetSDK/Video/input/channel/ID/sharpnessLevel`
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:53:- **5.3.4 Video Input Channel brightness Level**
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:55:  - Path: `/NetSDK/Video/input/channel/ID/brightnessLevel`
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:57:- **5.3.5 Video Input Channel flipEnabled**
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:59:  - Path: `/NetSDK/Video/input/channel/ID/flip`
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:61:- **5.3.6 Video Input Channel mirrorEnabled**
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:63:  - Path: `/NetSDK/Video/input/channel/ID/mirror`
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:65:- **5.3.7 Video Input Channel Saturation Level**
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:67:  - Path: `/NetSDK/Video/input/channel/ID/saturationLevel`
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:69:- **5.3.8 Video Input Channel Hue Level**
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:71:  - Path: `/NetSDK/Video/input/channel/ID/hueLevel`
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:73:- **5.3.9 Video Input Channel Contrast Level**
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:75:  - Path: `/NetSDK/Video/input/channel/ID/contrastLevel`
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:77:- **5.3.10 Video Input Channel Privacy Mask List**
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:79:  - Path: `/NetSDK/Video/input/channel/ID/privacyMasks[/properties]`
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:81:- **5.3.11 Video Input Channel Privacy Mask**
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:83:  - Path: `/NetSDK/Video/input/channel/ID/privacyMask/ID[/properties]`
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:85:- **5.3.12 Video Encode Channel List**
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:87:  - Path: `/NetSDK/Video/encode/channels[/properties]`
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:89:- **5.3.13 Video Encode Channel**
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:91:  - Path: `/NetSDK/Video/encode/channel/ID[/properties]`
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:93:- **5.3.14 Video Encode Channel Name Overlay**
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:95:  - Path: `/NetSDK/Video/encode/channel/ID/channelNameOverlay[/properties]`
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:99:  - Path: `/NetSDK/Video/encode/channel/ID/datetimeOverlay[/properties]`
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:103:  - Path: `/NetSDK/Video/encode/channel/ID/deviceIDOverlays[/properties]`
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:107:  - Path: `/NetSDK/Video/encode/channel/ID/textOverlays[/properties]`
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:111:  - Path: `/NetSDK/Video/encode/channel/ID/textOverlay/ID[/properties]`
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:115:  - Path: `/NetSDK/Video/encode/channel/ID/requestKeyFrame`
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:119:  - Path: `/NetSDK/Video/encode/channel/ID/snapShot`
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:121:- **5.3.21 Video Motion Detection Channel List**
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:123:  - Path: `/NetSDK/Video/motionDetection/channels[/properties]`
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:125:- **5.3.22 Video Motion Detection Channel**
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:127:  - Path: `/NetSDK/Video/motionDetection/channel/ID[/properties]`
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:129:- **5.3.23 Video Motion Detection Channel Status**
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:131:  - Path: `/NetSDK/Video/motionDetection/channel/ID/status`
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:133:- **5.4.1 IO Alarm Input Channel List**
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:135:  - Path: `/NetSDK/IO/alarmInput/channels[/properties]`
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:137:- **5.4.2 IO Alarm Input Channel**
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:139:  - Path: `/NetSDK/IO/alarmInput/channel/ID[/properties]`
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:141:- **5.4.3 IO Alarm Input Channel Port Status**
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:143:  - Path: `/NetSDK/IO/alarmInput/channel/ID/portStatus`
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:145:- **5.4.4 IO Alarm Output Channel List**
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:147:  - Path: `/NetSDK/IO/alarmOutput/channels[/properties]`
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:149:- **5.4.5 IO Alarm Output Channel**
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:151:  - Path: `/NetSDK/IO/alarmOutput/channel/ID[/properties]`
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:153:- **5.4.6 IO Alarm Output Channel Port Status**
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:155:  - Path: `/NetSDK/IO/alarmOutput/channel/ID/portStatus`
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:157:- **5.4.8 IO Alarm Output Channel Trigger**
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:159:  - Path: `/NetSDK/IO/alarmOutput/channel/ID/trigger`
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:161:- **5.5.1 PTZ Channel List**
- C:\Users\ceide\Downloads\NETSDK_V1.4_PLATINUM_PACK (1)\netsdk_platinum\NETSDK_V1.4_INTERFACE_DESCRIPTION_PLATINUM.md:163:  - Path: `/NetSDK/PTZ/channels[/properties]`

Most likely SDK call chain:
1. HISI_DVR_Init
2. HISI_DVR_Login(10.0.0.227, dvrPort, 80, admin, explicit empty password, deviceInfo)
3. HISI_DVR_RealPlayEx(userId, clientInfo with channel/stream selection)
4. HISI_DVR_SetRealDataCallBack(realHandle, callback, userData)
5. Callback receives encoded/private frames; sample feeds HISI_Play_InputData or HISI_DVR_SaveRealData
6. StopRealPlay, Logout, Cleanup

Mapping caution:
- IPCamSuite itself uses IPCamSuite NetSdk.dll/CNetClient symbols. HISISDK.dll provides a callback path, but direct ABI compatibility with the IPCamSuite 5523-W private transport has not yet been proven.
- Channel 101/102 are NetSDK encode-channel IDs. SDK preview samples commonly use logical preview channel/stream fields, so 101/102 cannot be assumed as direct RealPlay channel IDs without a live SDK login proof.
