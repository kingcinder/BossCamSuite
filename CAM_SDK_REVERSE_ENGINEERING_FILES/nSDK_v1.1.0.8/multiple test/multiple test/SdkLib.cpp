#include "StdAfx.h"
#include "SdkLib.h"

CSdkLib g_SdkLib;

CSdkLib::CSdkLib(void)
{
	m_hLib = LoadLibrary("HISISDK.dll");
	if (NULL != m_hLib)
	{
		PHISI_DVR_Init = GetProcAddress(m_hLib,"HISI_DVR_Init");
		PHISI_DVR_Cleanup = GetProcAddress(m_hLib,"HISI_DVR_Cleanup");
		PHISI_DVR_Login = (long (__stdcall *)(char *,WORD,WORD,char *,char *,PHISI_DEVCEINFO))GetProcAddress(m_hLib,"HISI_DVR_Login");
		PHISI_DVR_Logout = (BOOL (__stdcall *)(LONG))GetProcAddress(m_hLib,"HISI_DVR_Logout");
		PHISI_DVR_RealPlayEx = (LONG (__stdcall *)(LONG,PHISI_DEV_CLIENTINFOEX))GetProcAddress(m_hLib,"HISI_DVR_RealPlayEx");
		PHISI_DVR_StopRealPlay = (BOOL (__stdcall *)(LONG))GetProcAddress(m_hLib,"HISI_DVR_StopRealPlay");
		PHISI_DVR_PlayBackByTime = (LONG (__stdcall *)(LONG lUserID,LONG lChannel, PHISI_DVR_TIME lpStartTime, PHISI_DVR_TIME lpStopTime, HWND hWnd))GetProcAddress(m_hLib,"HISI_DVR_PlayBackByTime");
	}
}


CSdkLib::~CSdkLib(void)
{
}
