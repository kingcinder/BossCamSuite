# IPCAMSUITE BINARY TRUTH

IPCamSuite directory: C:\Program Files\IPCamSuite

Binaries inspected:
- C:\Program Files\IPCamSuite\IPCamSuite.exe present=True
- C:\Program Files\IPCamSuite\NetSdk.dll present=True
- C:\Program Files\IPCamSuite\hi_h264dec_w.dll present=True
- C:\Program Files\IPCamSuite\HW_H265dec_Win32D.dll present=True

Config/log files inspected:
- C:\Program Files\IPCamSuite\CHS.INI
- C:\Program Files\IPCamSuite\ENG.INI
- C:\Program Files\IPCamSuite\IPCamSuite.log
- C:\Program Files\IPCamSuite\MAINSET.INI

MAINSET.INI preview truth:
- csIp=10.0.0.227
- csDevId=Z7C34781634738
- csPort=80
- csUsername=admin
- csPasswd=
- bMainStream=1

Stream-related strings:
- 	          Ordinal   Hint Name
- 	          Ordinal  Address  Type
- 	[   0] +base[   1]  0000 ??0CNetClient@@QAE@ABV0@@Z
- 	[   0] +base[   1] 00001003 Export RVA
- 	[   1] +base[   2]  0001 ??0CNetClient@@QAE@XZ
- 	[   1] +base[   2] 00001027 Export RVA
- 	[   2] +base[   3]  0002 ??1CNetClient@@QAE@XZ
- 	[   2] +base[   3] 0000106c Export RVA
- 	[   3] +base[   4]  0003 ??4CNetClient@@QAEAAV0@ABV0@@Z
- 	[   3] +base[   4] 00001018 Export RVA
- 	[   4] +base[   5]  0004 ??_7CNetClient@@6B@
- 	[   4] +base[   5] 00011150 Export RVA
- 	[   5] +base[   6]  0005 ?BindClient@CNetClient@@QAEHI@Z
- 	[   5] +base[   6] 000010d4 Export RVA
- 	[   6] +base[   7]  0006 ?CloseAll@CNetClient@@QAEHXZ
- 	[   6] +base[   7] 000010e3 Export RVA
- 	[   7] +base[   8]  0007 ?ConnectToServer@CNetClient@@QAEHPBDGH@Z
- 	[   7] +base[   8] 00001080 Export RVA
- 	[   8] +base[   9]  0008 ?ConvertIP@@YAPADK@Z
- 	[   8] +base[   9] 00003410 Export RVA
- 	[   9] +base[  10]  0009 ?GetConnectStatus@CNetClient@@QAE_NXZ
- 	[   9] +base[  10] 000010eb Export RVA
- 	[  10] +base[  11]  000a ?GetStreamDes@CNetClient@@QAEHAAU_tagStreamDes@1@@Z
- 	[  10] +base[  11] 00001195 Export RVA
- 	[  11] +base[  12]  000b ?OnCommandDataEx@CNetClient@@UAEXKPADKEEK00@Z
- 	[  11] +base[  12] 00001000 Export RVA
- 	[  12] +base[  13]  000c ?OpenId@CNetClient@@QAEHHH@Z
- 	[  12] +base[  13] 00001097 Export RVA
- 	[  13] +base[  14]  000d ?OpenStreamEx@CNetClient@@QAEHHHH@Z
- 	[  13] +base[  14] 000010aa Export RVA
- 	[  14] +base[  15]  000e ?PauseReplay@CNetClient@@QAEHXZ
- 	[  14] +base[  15] 0000117d Export RVA
- 	[  15] +base[  16]  000f ?PrepairStream@CNetClient@@QAEXHH@Z
- 	[  15] +base[  16] 000010c1 Export RVA
- 	[  16] +base[  17]  0010 ?RegDisConnectCallback@CNetClient@@QAEXP6A_NHHPAX@Z0@Z
- 	[  16] +base[  17] 000010f3 Export RVA
- 	[  17] +base[  18]  0011 ?RegReconnectCallback@CNetClient@@QAEXP6AXPAX@Z0@Z
- 	[  17] +base[  18] 00001106 Export RVA
- 	[  18] +base[  19]  0012 ?ResumeReplay@CNetClient@@QAEHXZ
- 	[  18] +base[  19] 00001185 Export RVA
- 	[  19] +base[  20]  0013 ?SendCmdToServer@CNetClient@@QAEHUtagPacketMsg@@@Z
- 	[  19] +base[  20] 00001119 Export RVA
- 	[  20] +base[  21]  0014 ?SendDataToServer@CNetClient@@QAEHUtagPacketData@@@Z
- 	[  20] +base[  21] 0000112c Export RVA
- 	[  21] +base[  22]  0015 ?SendRecCmdToServer@CNetClient@@QAEHDPADH@Z
- 	[  21] +base[  22] 00001148 Export RVA
- 	[  22] +base[  23]  0016 ?StartReplay@CNetClient@@QAEHPAHHHJJ@Z
- 	[  22] +base[  23] 0000115f Export RVA
- 	[  23] +base[  24]  0017 ?StopReplay@CNetClient@@QAEHXZ
- 	[  23] +base[  24] 0000118d Export RVA
- 	[Name Pointer/Ordinal] Table	00000018
- 	00011000  <none>  016d  GetTickCount
- 	00011004  <none>  00aa  FlushFileBuffers
- 	00011008  <none>  027c  SetStdHandle
- 	0001100c  <none>  01c2  LoadLibraryA
- 	00011010  <none>  0131  GetOEMCP
- 	00011014  <none>  00b9  GetACP
- 	00011018  <none>  00bf  GetCPInfo
- 	0001101c  <none>  0156  GetStringTypeW
- 	00011020  <none>  0296  Sleep
- 	00011024  <none>  01aa  InitializeCriticalSection
- 	00011028  <none>  0055  DeleteCriticalSection
- 	0001102c  <none>  0066  EnterCriticalSection
- 	00011030  <none>  01c1  LeaveCriticalSection
- 	00011034  <none>  022f  RtlUnwind
- 	00011038  <none>  019f  HeapFree
- 	0001103c  <none>  0199  HeapAlloc
- 	00011040  <none>  020b  RaiseException
- 	00011044  <none>  00ca  GetCommandLineA
- 	00011048  <none>  0174  GetVersion
- 	0001104c  <none>  01ad  InterlockedDecrement
- 	00011050  <none>  01b0  InterlockedIncrement
- 	00011054  <none>  011a  GetLastError
- 	00011058  <none>  004a  CreateThread
- 	0001105c  <none>  00fa  GetCurrentThreadId
- 	00011060  <none>  02a5  TlsSetValue
- 	00011064  <none>  02a4  TlsGetValue
- 	00011068  <none>  007e  ExitThread
- 	0001106c  <none>  02a2  TlsAlloc
- 	00011070  <none>  02a3  TlsFree

Suspected DLLs:
- NetSdk.dll: private CNetClient transport and stream open/export clues.
- hi_h264dec_w.dll: H.264 decoder used by IPCamSuite.
- HW_H265dec_Win32D.dll: H.265 decoder present.

Suspected protocol/endpoints:
- /livestream/%d?action=play&media=%s
- /bubble/live?ch=0&stream=1
- /bubble/live?ch=%d&stream=0
- flv-application/octet-stream / text/HDP private payload markers

Conclusion:
- IPCamSuite appears to use SDK/private DLL stream callbacks or a private NetSdk CNetClient transport, not a standard reusable RTSP/HTTP URL.
- SDK callback/private transport evidence found: True
