#pragma once
#include "HISISDK.h"


class CSdkLib
{
public:
	CSdkLib(void);
	~CSdkLib(void);

public:
	BOOL (WINAPI *PHISI_DVR_Init) ();
	BOOL (WINAPI *PHISI_DVR_Cleanup) ();
	LONG (WINAPI *PHISI_DVR_Login) (char *sDVRIP,WORD wDVRPort,WORD wHttpPort, char *sUserName,char *sPassword,PHISI_DEVCEINFO lpDeviceInfo);
	BOOL (WINAPI *PHISI_DVR_Logout) (LONG lUserID);
	LONG (WINAPI *PHISI_DVR_RealPlayEx) (LONG lUserID,PHISI_DEV_CLIENTINFOEX lpClientInfo);
	BOOL (WINAPI *PHISI_DVR_StopRealPlay) (LONG lRealHandle);
	LONG (WINAPI *PHISI_DVR_PlayBackByTime) (LONG lUserID,LONG lChannel, PHISI_DVR_TIME lpStartTime, PHISI_DVR_TIME lpStopTime, HWND hWnd);

private:
	HMODULE m_hLib;
};
extern CSdkLib g_SdkLib;


