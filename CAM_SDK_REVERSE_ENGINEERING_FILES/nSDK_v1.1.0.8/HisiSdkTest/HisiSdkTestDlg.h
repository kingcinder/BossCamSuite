// HisiSdkTestDlg.h : 头文件
//

#pragma once
#include "HISISDK.h"
#include "afxwin.h"
//#import "HISISDK.dll"
// CHisiSdkTestDlg 对话框

typedef BOOL (WINAPI *PHISI_DVR_Init) ();
typedef BOOL (WINAPI *PHISI_DVR_Cleanup) ();

typedef DWORD (WINAPI *PHISI_DVR_GetSDKVersion) ();

typedef DWORD (WINAPI *PHISI_DVR_GetLastError) ();

typedef BOOL (WINAPI *PHISI_DVR_SetDVRMessage) (void (CALLBACK* fExceptionCallBack)(DWORD dwType, LONG lUserID, LONG lHandle, void *pUser),void *pUser);

typedef BOOL (WINAPI *PHISI_DVR_GetConnectInfoByID) (char *eSeeID, PHISI_DEVCONNECTINFO connectInfo);
//用户登录
typedef LONG (WINAPI *PHISI_DVR_Login) (char *sDVRIP,WORD wDVRPort,WORD wHttpPort, char *sUserName,char *sPassword,PHISI_DEVCEINFO lpDeviceInfo);
typedef BOOL (WINAPI *PHISI_DVR_Logout) (LONG lUserID);
//实时预览
typedef LONG (WINAPI *PHISI_DVR_RealPlay) (LONG lUserID,PHISI_DEV_CLIENTINFO lpClientInfo);
typedef LONG (WINAPI *PHISI_DVR_RealPlayEx) (LONG lUserID,PHISI_DEV_CLIENTINFOEX lpClientInfo);
typedef BOOL (WINAPI *PHISI_DVR_StopRealPlay) (LONG lRealHandle);
//开启声音
typedef BOOL (WINAPI *PHISI_DVR_OpenSound)(LONG lRealHandle);
typedef BOOL (WINAPI *PHISI_DVR_CloseSound)();
//抓图
typedef BOOL (WINAPI *PHISI_DVR_CapturePicture) (LONG lRealHandle,char *sPicFileName);//bmp
typedef BOOL (WINAPI *PHISI_DVR_SetRealDataCallBack) (LONG lRealHandle,void(CALLBACK *fRealDataCallBack) (LONG lRealHandle, DWORD dwDataType, BYTE *pBuffer,DWORD dwBufSize,DWORD dwUser),DWORD dwUser);
//实时预览捕获数据
typedef BOOL (WINAPI *PHISI_DVR_SaveRealData) (LONG lRealHandle,char *sFileName);
typedef BOOL (WINAPI *PHISI_DVR_StopSaveRealData) (LONG lRealHandle);
//报警
typedef LONG (WINAPI *PHISI_DVR_SetupAlarmChan) (char *pServerIP,WORD wServerPort,char *pUserName,char *pUserPassword);
typedef BOOL (WINAPI *PHISI_DVR_CloseAlarmChan) (LONG lAlarmHandle);
typedef BOOL (WINAPI *PHISI_DVR_SetDVRMessageCallBack) (BOOL (CALLBACK *fMessageCallBack)(LONG lCommand,char *sDVRIP,char *pBuf,DWORD dwBufLen, DWORD dwUser), DWORD dwUser);
//云台控制相关接口
typedef BOOL (WINAPI *PHISI_DVR_PTZControl) (LONG lRealHandle,DWORD dwPTZCommand,DWORD dwStop);
//参数配置 
typedef BOOL  (WINAPI *PHISI_DVR_GetDVRConfig) (LONG lUserID, DWORD dwCommand,LONG lChannel, LPVOID lpOutBuffer, DWORD dwOutBufferSize, LPDWORD lpBytesReturned);
typedef BOOL (WINAPI *PHISI_DVR_SetDVRConfig) (LONG lUserID, DWORD dwCommand,LONG lChannel, LPVOID lpInBuffer, DWORD dwInBufferSize);
//录像文件查找
typedef LONG (WINAPI *PHISI_DVR_FindFile) (LONG lUserID,LONG lChannel,DWORD dwFileType,PHISI_DVR_TIME lpStartTime,PHISI_DVR_TIME lpStopTime);
typedef LONG (WINAPI *PHISI_DVR_FindNextFile)(LONG lFindHandle,PHISI_DVR_FIND_DATA lpFindData);
typedef BOOL (WINAPI *PHISI_DVR_FindClose)(LONG lFindHandle);

//录像回放
typedef LONG (WINAPI *PHISI_DVR_PlayBackByTime) (LONG lUserID,LONG lChannel, PHISI_DVR_TIME lpStartTime, PHISI_DVR_TIME lpStopTime, HWND hWnd);
typedef BOOL (WINAPI *PHISI_DVR_PlayBackControl) (LONG lPlayHandle,DWORD dwControlCode,DWORD dwInValue,DWORD *LPOutValue);
typedef BOOL (WINAPI *PHISI_DVR_StopPlayBack) (LONG lPlayHandle);
typedef LONG (WINAPI *PHISI_DVR_PlayBackByName)(LONG lUserID,char *sPlayBackFileName,HWND hWnd);
//录像数据捕获
typedef BOOL (WINAPI *PHISI_DVR_SetPlayDataCallBack) (LONG lPlayHandle,void(CALLBACK *fPlayDataCallBack) (LONG lPlayHandle, DWORD dwDataType, BYTE *pBuffer,DWORD dwBufSize,DWORD dwUser), DWORD dwUser);
//录像下载
typedef BOOL (WINAPI *PHISI_DVR_PlayBackSaveData) (LONG lPlayHandle,char *sFileName);
typedef BOOL (WINAPI *PHISI_DVR_StopPlayBackSave) (LONG lPlayHandle);
//录像回放抓图
typedef BOOL (WINAPI *PHISI_DVR_PlayBackCaptureFile) (LONG lPlayHandle,char *sFileName);
//语音广播
typedef BOOL (WINAPI *PHISI_DVR_ClientAudioStart) ();
typedef BOOL (WINAPI *PHISI_DVR_ClientAudioStop) ();
typedef BOOL (WINAPI *PHISI_DVR_AddDVR) (LONG lUserID);
typedef BOOL (WINAPI *PHISI_DVR_DelDVR) (LONG lUserID);
typedef int	 (WINAPI *PHISI_BroadcastStart)();
typedef int  (WINAPI *PHISI_BroadcastAddClient)(
	char *pServerIP,
	WORD wServerPort,
	char *pDeviceName,
	char *pUserName,
	char *pUserPassword,
	HANDLE	&hBrdClient);
typedef int  (WINAPI *PHISI_BroadcastDelClient)(HANDLE hBrdClent);
typedef int  (WINAPI *PHISI_BroadcastStop)();
//文件播放
//初始化
typedef BOOL (WINAPI *PHISI_Play_Init) ();
typedef BOOL (WINAPI *PHISI_Play_Realese) ();
//辅助函数
typedef BOOL (WINAPI *PHISI_Play_GetPort) (LONG* nPort);
typedef BOOL (WINAPI *PHISI_Play_FreePort) (LONG nPort);
//文件操作
typedef BOOL (WINAPI *PHISI_Play_OpenFile) (LONG nPort,LPSTR sFileName);
typedef BOOL (WINAPI *PHISI_Play_CloseFile) (LONG nPort);
//播放控制
typedef BOOL (WINAPI *PHISI_Play_Play) (LONG nPort, HWND hWnd);
typedef BOOL (WINAPI *PHISI_Play_Stop) (LONG nPort);
typedef BOOL (WINAPI *PHISI_Play_Pause) (LONG nPort,DWORD nPause);
typedef BOOL (WINAPI *PHISI_Play_Fast) (LONG nPort);
typedef BOOL (WINAPI *PHISI_Play_Slow) (LONG nPort);
typedef BOOL (WINAPI *PHISI_Play_SetPlayPos) (LONG nPort,LONG nRelativePos);
typedef LONG  (WINAPI *PHISI_Play_GetPlayPos) (LONG nPort);
typedef BOOL (WINAPI *PHISI_Play_SetVolume) (LONG nPort,WORD nVolume);
typedef BOOL (WINAPI *PHISI_Play_PlaySound) (LONG nPort);
typedef BOOL (WINAPI *PHISI_Play_StopSound) ();
//流操作
typedef BOOL (WINAPI *PHISI_Play_OpenStream) (LONG nPort);
typedef BOOL (WINAPI *PHISI_Play_CloseStream) (LONG nPort);
typedef BOOL (WINAPI *PHISI_Play_InputData) (LONG nPort,PBYTE pBuf,DWORD nSize);
//获取播放信息
typedef DWORD  (WINAPI *PHISI_Play_GetFileTime) (LONG nPort);
typedef DWORD  (WINAPI *PHISI_Play_GetPlayedTime) (LONG nPort);
//抓图
typedef BOOL (WINAPI *PHISI_Play_CapturePicture) (LONG nPort, char *sPicFileName);
//下载
typedef LONG (WINAPI *PHISI_DVR_GetFileByName)(LONG lUserID,char *sDVRFileName,char *sSavedFileName);
typedef BOOL (WINAPI *PHISI_DVR_StopGetFile)(int lFileHandle);

typedef	BOOL (WINAPI *PHISI_Play_OneByOne)(LONG nPort);

class CHisiSdkTestDlg : public CDialog
{
// 构造
public:
	CHisiSdkTestDlg(CWnd* pParent = NULL);	// 标准构造函数

// 对话框数据
	enum { IDD = IDD_HISISDKTEST_DIALOG };

	protected:
	virtual void DoDataExchange(CDataExchange* pDX);	// DDX/DDV 支持

// 实现
protected:
	HICON m_hIcon;

	// 生成的消息映射函数
	virtual BOOL OnInitDialog();
	afx_msg void OnPaint();
	afx_msg HCURSOR OnQueryDragIcon();
	DECLARE_MESSAGE_MAP()
public:
	afx_msg void OnBnClickedBtnInitialize();
private:
	CEdit m_editLog;
	DWORD m_dwIP;
	CString m_strDVRPort;
	CString m_strHTTPPort;
	CString m_strESeeID;
	int m_nPlayChannel;
	CTime m_tmDate;
	CTime m_tmStartTime;
	CTime m_tmStopTime;

	HINSTANCE m_hINSTANCE;
	PHISI_DEVCEINFO m_DeviceInfo;
	PHISI_DEVCONNECTINFO m_ConnectInfo;
	LONG	m_nUserID;					//return value of Login()
	LONG	m_nPreviewPlayHandle;		//return value of RealPlay()
	LONG	m_nPlaybackPlayHandle;		//return value of PlaybackByTime()
	LONG	m_nPlaybackPlayHandle2;
	LONG	m_nGetFileHandle;			// return value of GetFileByName of GetFileByTime()
	HISI_DVR_TIME	m_StartTime;
	HISI_DVR_TIME	m_StopTime;
	bool			m_bPTZControlStart;
	DWORD	m_dwBroadcastIP;
	CButton	m_chkAlarm;
	DWORD	m_dwAlarmIP;
	int		m_nAlarmPort;
	HANDLE	m_hBroadcast;
	LONG	m_lAlarmHandle;
	LONG	m_lFindHandle;
	PHISI_DVR_Init					pHISI_DVR_Init;
	PHISI_DVR_Cleanup				pHISI_DVR_Cleanup;
	PHISI_DVR_GetSDKVersion			pHISI_DVR_GetSDKVersion;
	PHISI_DVR_GetLastError			pHISI_DVR_GetLastError;
	PHISI_DVR_SetDVRMessage			pHISI_DVR_SetDVRMessage;
	PHISI_DVR_GetConnectInfoByID	pHISI_DVR_GetConnectInfoByID;
	PHISI_DVR_Login					pHISI_DVR_Login;
	PHISI_DVR_Logout				pHISI_DVR_Logout;
	PHISI_DVR_RealPlay				pHISI_DVR_RealPlay;
	PHISI_DVR_RealPlayEx			pHISI_DVR_RealPlayEx;
	PHISI_DVR_StopRealPlay			pHISI_DVR_StopRealPlay;
	PHISI_DVR_OpenSound				pHISI_DVR_OpenSound;
	PHISI_DVR_CloseSound			pHISI_DVR_CloseSound;
	PHISI_DVR_CapturePicture		pHISI_DVR_CapturePicture;
	PHISI_DVR_SetRealDataCallBack	pHISI_DVR_SetRealDataCallBack;
	PHISI_DVR_SaveRealData			pHISI_DVR_SaveRealData;
	PHISI_DVR_StopSaveRealData		pHISI_DVR_StopSaveRealData;
	//云台控制相关接口
	PHISI_DVR_PTZControl			pHISI_DVR_PTZControl;
	//参数配置
	PHISI_DVR_GetDVRConfig			pHISI_DVR_GetDVRConfig;
	PHISI_DVR_SetDVRConfig			pHISI_DVR_SetDVRConfig;
	//录像文件查找
	PHISI_DVR_FindFile				pHISI_DVR_FindFile;
	PHISI_DVR_FindNextFile			pHISI_DVR_FindNextFile;
	PHISI_DVR_FindClose				pHISI_DVR_FindClose;
	//录像回放
	PHISI_DVR_PlayBackByTime		pHISI_DVR_PlayBackByTime;
	PHISI_DVR_PlayBackControl		pHISI_DVR_PlayBackControl;
	PHISI_DVR_StopPlayBack			pHISI_DVR_StopPlayBack;
	PHISI_DVR_PlayBackByName		pHISI_DVR_PlayBackByName;
	//录像数据捕获
	PHISI_DVR_SetPlayDataCallBack	pHISI_DVR_SetPlayDataCallBack;
	PHISI_DVR_PlayBackSaveData		pHISI_DVR_PlayBackSaveData;	
	PHISI_DVR_StopPlayBackSave		pHISI_DVR_StopPlayBackSave;
	//报警
	PHISI_DVR_SetupAlarmChan		pHISI_DVR_SetupAlarmChan;
	PHISI_DVR_CloseAlarmChan		pHISI_DVR_CloseAlarmChan;
	PHISI_DVR_SetDVRMessageCallBack	pHISI_DVR_SetDVRMessageCallBack;

	//录像回放抓图
	PHISI_DVR_PlayBackCaptureFile	pHISI_DVR_PlayBackCaptureFile;
	//语音广播
	PHISI_BroadcastStart			pHISI_BroadcastStart;
	PHISI_BroadcastAddClient		pHISI_BroadcastAddClient;
	PHISI_BroadcastDelClient		pHISI_BroadcastDelClient;
	PHISI_BroadcastStop				pHISI_BroadcastStop;
	//文件播放
	//初始化
	PHISI_Play_Init					pHISI_Play_Init;
	PHISI_Play_Realese				pHISI_Play_Realese;
	//辅助函数
	PHISI_Play_GetPort				pHISI_Play_GetPort;
	PHISI_Play_FreePort				pHISI_Play_FreePort;
	//文件操作
	PHISI_Play_OpenFile				pHISI_Play_OpenFile;
	PHISI_Play_CloseFile			pHISI_Play_CloseFile;
	//播放控制
	PHISI_Play_Play					pHISI_Play_Play;
	PHISI_Play_Stop					pHISI_Play_Stop;
	PHISI_Play_Pause				pHISI_Play_Pause;
	PHISI_Play_Fast					pHISI_Play_Fast;
	PHISI_Play_Slow					pHISI_Play_Slow;
	PHISI_Play_SetPlayPos			pHISI_Play_SetPlayPos;
	PHISI_Play_GetPlayPos			pHISI_Play_GetPlayPos;
	//PHISI_Play_SetVolume			pHISI_Play_SetVolume;
	//PHISI_Play_PlaySound			pHISI_Play_PlaySound;
	//PHISI_Play_StopSound			pHISI_Play_StopSound;
	//流操作
	PHISI_Play_OpenStream			pHISI_Play_OpenStream;
	PHISI_Play_CloseStream			pHISI_Play_CloseStream;
	//获取播放信息
	PHISI_Play_GetFileTime			pHISI_Play_GetFileTime;
	PHISI_Play_GetPlayedTime		pHISI_Play_GetPlayedTime;
	//抓图
	PHISI_Play_CapturePicture		pHISI_Play_CapturePicture;
	//下载
	PHISI_DVR_GetFileByName			pHISI_DVR_GetFileByName;
	PHISI_DVR_StopGetFile			pHISI_DVR_StopGetFile;

	PHISI_Play_OneByOne				pHISI_Play_OneByOne;

	

public:
	BOOL	m_bInputData;
	CFile	m_file;
	PHISI_Play_InputData			pHISI_Play_InputData;
	void	SetLogText(CString &strText);
	void	Faild(CString &strRes);
	void	SetHisiTime(const CTime &date, const CTime &time, HISI_DVR_TIME &HisiTime);
	afx_msg void OnBnClickedBtnGetversion();
	afx_msg void OnBnClickedBtnClear();
	afx_msg void OnBnClickedBtnLogin();
	afx_msg void OnBnClickedBtnLogout();
	afx_msg void OnBnClickedBtnGetinfo();
	afx_msg void OnBnClickedBtnStartRealplay();
	afx_msg void OnBnClickedBtnStopRealplay();
	afx_msg void OnBnClickedBtnCapture();
	afx_msg void OnBnClickedBtnSaverealdata();
	afx_msg void OnBnClickedBtnStopsave();
	afx_msg void OnBnClickedBtnStartplayback();
	afx_msg void OnBnClickedBtnPause();
	afx_msg void OnBnClickedBtnResume();
	afx_msg void OnBnClickedBtnStopplayback();
	afx_msg void OnBnClickedBtnPlaybackSave();
	afx_msg void OnBnClickedBtnStopplaybacksave2();
	afx_msg void OnBnClickedBtnGetFileByTime();
	afx_msg void OnBnClickedBtnStopGettingFile();
	afx_msg void OnBnClickedBtnPlaybackcapture();
	afx_msg void OnBnClickedBtnFindfile();
	afx_msg void OnBnClickedBtnFindnext();
	afx_msg void OnBnClickedBtnClosefind();
	afx_msg void OnBnClickedBtnPlayInit();
	afx_msg void OnBnClickedBtnPlayRelease();
	afx_msg void OnBnClickedBtnPlayOpen();
	afx_msg void OnBnClickedBtnPlayClose();
	afx_msg void OnBnClickedBtnPlayPlay();
	afx_msg void OnBnClickedBtnPlayStop();
	afx_msg void OnBnClickedBtnPlayPause();
	afx_msg void OnBnClickedBtnPlayResume();
	afx_msg void OnBnClickedBtnFast();
	afx_msg void OnBnClickedBtnSlow();
	afx_msg void OnBnClickedBtnPlayGetpos();
	afx_msg void OnBnClickedBtnPlaySetpos();
	afx_msg void OnBnClickedBtnPlayGetFileTime();
	afx_msg void OnBnClickedBtnPlayGetPlayedTime();
	afx_msg void OnBnClickedBtnPtz();
	afx_msg void OnBnClickedBtnGetport();
	afx_msg void OnBnClickedBtnFreeport();
private:
	LONG m_nFreePort;
public:
	afx_msg void OnBnClickedBtnPlaycapture();
	afx_msg void OnBnClickedOk();
	afx_msg void OnBnClickedBtnGetconfig();
	afx_msg void OnBtnClickedChkOpensound();
	afx_msg void OnBnClickedBtnBrdcStart();
	afx_msg void OnBnClickedBtnBrdcAdd();
	afx_msg void OnBnClickedBtnBrdcDel();
	afx_msg void OnBnClickedBtnBrdcStop();
	afx_msg void OnBnClickedBtnClearlog();
	afx_msg void OnBnClickedBtnStreamOpen();
	afx_msg void OnBnClickedBtnStreamInput();
	afx_msg void OnBnClickedBtnStreamClose();
	afx_msg void OnBnClickedCheckAlarm();
	afx_msg void OnBnClickedButtonPlaybyname();
	afx_msg void OnBnClickedBtnGetfilebyname();
	afx_msg void OnBnClickedButtonGetfilepos();
	afx_msg void OnBnClickedButtonPlaybyname2();
	afx_msg void OnBnClickedBtnStopplayback2();
	afx_msg void OnBnClickedBtnGetfilebyname2();
	afx_msg void OnBnClickedButtonStep();
	afx_msg void OnBnClickedButtonGetplaypos();
	afx_msg void OnBnClickedButtonGetplaypos2();
	int m_nStream;
};
