// HisiSdkTestDlg.cpp : 实现文件
//

#include "stdafx.h"
#include "HisiSdkTest.h"
#include "HisiSdkTestDlg.h"
#include "winsock2.h"
#ifdef _DEBUG
#define new DEBUG_NEW
#endif

#define PLAY_PORT		5
#define STREAM_PORT		PLAY_PORT + 1
#define HISI_FALSE		0
#define HISI_TRUE		-1
void CALLBACK OnException(DWORD dwType,LONG lUserID,LONG lHandle,	void *pUser)
{
	CString str;
	str.Format(_T("On Exception user id:%d, Handle:%d"), lUserID, lHandle);
//	AfxMessageBox(str);
}

BOOL CALLBACK MessageCallBack(LONG lCommand, char *sDVRIP, char *pBuf, DWORD dwBufLen, DWORD dwUser)
{
	CString str;
//	str.Format("Alarm:command:%d - ip:%s - buf:%s - bufflength:%d", lCommand, sDVRIP, pBuf, dwBufLen);
//	AfxMessageBox(str);
	if (0x13 == lCommand)
	{
		for (int i = 0;i < 4; i++)
		{
			for (int j = 0; j < 4; j++)
			{
				if (*(pBuf + i * 4 + j))
				{
					MTPRINTF("wireless channel %d key %d %d\n",i,j,*(pBuf + i * 4 + j));
				}
			}
		}
		MTPRINTF("==========================\n");
	}
	return FALSE;
}

void CALLBACK RealDataCallBack(LONG lRealHandle, DWORD dwDataType, BYTE *pBuffer, DWORD dwBufSize, DWORD dwUser)
{
	//((CHisiSdkTestDlg *)dwUser)->m_file.Write(pBuffer, dwBufSize);
	if ( ((CHisiSdkTestDlg *)dwUser)->m_bInputData)
	{
		BOOL bRes = ((CHisiSdkTestDlg *)dwUser)->pHISI_Play_InputData(STREAM_PORT, pBuffer, dwBufSize);
	}
	//TRACE("%d_", bRes);
}

void CALLBACK PlayDataCallBack(LONG lPlayHandle, DWORD dwDataType, BYTE *pBuffer, DWORD dwBufSize, DWORD dwUser)
{

}

// CHisiSdkTestDlg 对话框
CHisiSdkTestDlg::CHisiSdkTestDlg(CWnd* pParent /*=NULL*/)
	: CDialog(CHisiSdkTestDlg::IDD, pParent)
	, m_strESeeID(_T(""))
	, m_dwIP(0)
	, m_strDVRPort(_T(""))
	, m_strHTTPPort(_T(""))
	, m_nPlayChannel(0)
	, m_tmDate(0)
	, m_tmStartTime(0)
	, m_tmStopTime(0)
	, m_nFreePort(0)
	, m_dwBroadcastIP(0)
	, m_bInputData(0)
	, m_dwAlarmIP(0)
	, m_nAlarmPort(0)
	, m_nStream(0)
{
	m_hIcon = AfxGetApp()->LoadIcon(IDR_MAINFRAME);
	if (!m_file.Open("D:\\ts.es", CFile::modeCreate | CFile::modeWrite | CFile::typeBinary, NULL))
		AfxMessageBox("open error.");
}

void CHisiSdkTestDlg::DoDataExchange(CDataExchange* pDX)
{
	CDialog::DoDataExchange(pDX);
	DDX_Control(pDX, IDC_EDIT_LOG, m_editLog);
	DDX_Text(pDX, IDC_EDIT_ESEEID, m_strESeeID);
	DDX_IPAddress(pDX, IDC_IPADDRESS1, m_dwIP);
	DDX_Text(pDX, IDC_EDIT_DVRPORT, m_strDVRPort);
	DDX_Text(pDX, IDC_EDIT_HTTPPROT, m_strHTTPPort);
	DDX_Text(pDX, IDC_EDIT_CHANNEL, m_nPlayChannel);
	DDX_DateTimeCtrl(pDX, IDC_DTP_DATE, m_tmDate);
	DDX_DateTimeCtrl(pDX, IDC_DTP_STARTTIME, m_tmStartTime);
	DDX_DateTimeCtrl(pDX, IDC_DTP_STOPTIME, m_tmStopTime);
	DDX_Text(pDX, IDC_EDIT_FREEPORT, m_nFreePort);
	DDX_IPAddress(pDX, IDC_IP_BRDC, m_dwBroadcastIP);
	DDX_Control(pDX, IDC_CHECK_ALARM, m_chkAlarm);
	DDX_IPAddress(pDX, IDC_IP_ALARM, m_dwAlarmIP);
	DDX_Text(pDX, IDC_EDIT_ALARM_PORT, m_nAlarmPort);
	DDX_Text(pDX, IDC_EDIT1, m_nStream);
}

BEGIN_MESSAGE_MAP(CHisiSdkTestDlg, CDialog)
	ON_WM_PAINT()
	ON_WM_QUERYDRAGICON()
	//}}AFX_MSG_MAP
	ON_BN_CLICKED(IDC_BTN_INITIALIZE, &CHisiSdkTestDlg::OnBnClickedBtnInitialize)
	ON_BN_CLICKED(IDC_BTN_GETVERSION, &CHisiSdkTestDlg::OnBnClickedBtnGetversion)
	ON_BN_CLICKED(IDC_BTN_CLEAR, &CHisiSdkTestDlg::OnBnClickedBtnClear)
	ON_BN_CLICKED(IDC_BTN_LOGIN, &CHisiSdkTestDlg::OnBnClickedBtnLogin)
	ON_BN_CLICKED(IDC_BTN_LOGOUT, &CHisiSdkTestDlg::OnBnClickedBtnLogout)
	ON_BN_CLICKED(IDC_BTN_GETINFO, &CHisiSdkTestDlg::OnBnClickedBtnGetinfo)
	ON_BN_CLICKED(IDC_BTN_STARTREALPLAY, &CHisiSdkTestDlg::OnBnClickedBtnStartRealplay)
	ON_BN_CLICKED(IDC_BTN_STOPREALPLAY2, &CHisiSdkTestDlg::OnBnClickedBtnStopRealplay)
	ON_BN_CLICKED(IDC_BTN_CAPTURE, &CHisiSdkTestDlg::OnBnClickedBtnCapture)
	ON_BN_CLICKED(IDC_BTN_SAVEREALDATA, &CHisiSdkTestDlg::OnBnClickedBtnSaverealdata)
	ON_BN_CLICKED(IDC_BTN_STOPSAVE, &CHisiSdkTestDlg::OnBnClickedBtnStopsave)
	ON_BN_CLICKED(IDC_BTN_STARTPLAYBACK, &CHisiSdkTestDlg::OnBnClickedBtnStartplayback)
	ON_BN_CLICKED(IDC_BTN_PAUSE, &CHisiSdkTestDlg::OnBnClickedBtnPause)
	ON_BN_CLICKED(IDC_BTN_RESUME, &CHisiSdkTestDlg::OnBnClickedBtnResume)
	ON_BN_CLICKED(IDC_BTN_STOPPLAYBACK, &CHisiSdkTestDlg::OnBnClickedBtnStopplayback)
	ON_BN_CLICKED(IDC_BTN_PLAYBACKSAVE, &CHisiSdkTestDlg::OnBnClickedBtnPlaybackSave)
	ON_BN_CLICKED(IDC_BTN_STOPPLAYBACKSAVE2, &CHisiSdkTestDlg::OnBnClickedBtnStopplaybacksave2)
	ON_BN_CLICKED(IDC_BTN_GETFILEBYTIME, &CHisiSdkTestDlg::OnBnClickedBtnGetFileByTime)
	ON_BN_CLICKED(IDC_BTN_STOPGETFILE, &CHisiSdkTestDlg::OnBnClickedBtnStopGettingFile)
	ON_BN_CLICKED(IDC_BTN_PLAYBACKCAPTURE, &CHisiSdkTestDlg::OnBnClickedBtnPlaybackcapture)
	ON_BN_CLICKED(IDC_BTN_FINDFILE, &CHisiSdkTestDlg::OnBnClickedBtnFindfile)
	ON_BN_CLICKED(IDC_BTN_FINDNEXT, &CHisiSdkTestDlg::OnBnClickedBtnFindnext)
	ON_BN_CLICKED(IDC_BTN_CLOSEFIND, &CHisiSdkTestDlg::OnBnClickedBtnClosefind)
	ON_BN_CLICKED(IDC_BTN_PLAY_INIT, &CHisiSdkTestDlg::OnBnClickedBtnPlayInit)
	ON_BN_CLICKED(IDC_BTN_PLAY_RELEASE, &CHisiSdkTestDlg::OnBnClickedBtnPlayRelease)
	ON_BN_CLICKED(IDC_BTN_PLAY_OPEN, &CHisiSdkTestDlg::OnBnClickedBtnPlayOpen)
	ON_BN_CLICKED(IDC_BTN_PLAY_CLOSE, &CHisiSdkTestDlg::OnBnClickedBtnPlayClose)
	ON_BN_CLICKED(IDC_BTN_PLAY_PLAY, &CHisiSdkTestDlg::OnBnClickedBtnPlayPlay)
	ON_BN_CLICKED(IDC_BTN_PLAY_STOP, &CHisiSdkTestDlg::OnBnClickedBtnPlayStop)
	ON_BN_CLICKED(IDC_BTN_PLAY_PAUSE, &CHisiSdkTestDlg::OnBnClickedBtnPlayPause)
	ON_BN_CLICKED(IDC_BTN_PLAY_RESUME, &CHisiSdkTestDlg::OnBnClickedBtnPlayResume)
	ON_BN_CLICKED(IDC_BTN_FAST, &CHisiSdkTestDlg::OnBnClickedBtnFast)
	ON_BN_CLICKED(IDC_BTN_SLOW, &CHisiSdkTestDlg::OnBnClickedBtnSlow)
	ON_BN_CLICKED(IDC_BTN_PLAY_GETPOS, &CHisiSdkTestDlg::OnBnClickedBtnPlayGetpos)
	ON_BN_CLICKED(IDC_BTN_PLAY_SETPOS, &CHisiSdkTestDlg::OnBnClickedBtnPlaySetpos)
	ON_BN_CLICKED(IDC_BTN_PLAY_GETTIME, &CHisiSdkTestDlg::OnBnClickedBtnPlayGetFileTime)
	ON_BN_CLICKED(IDC_BTN_PLAY_GETPLAYTIME, &CHisiSdkTestDlg::OnBnClickedBtnPlayGetPlayedTime)
	ON_BN_CLICKED(IDC_BTN_PTZ, &CHisiSdkTestDlg::OnBnClickedBtnPtz)
	ON_BN_CLICKED(IDC_BTN_GETPORT, &CHisiSdkTestDlg::OnBnClickedBtnGetport)
	ON_BN_CLICKED(IDC_BTN_FREEPORT, &CHisiSdkTestDlg::OnBnClickedBtnFreeport)
	ON_BN_CLICKED(IDC_BTN_PLAYCAPTURE, &CHisiSdkTestDlg::OnBnClickedBtnPlaycapture)
	ON_BN_CLICKED(IDOK, &CHisiSdkTestDlg::OnBnClickedOk)
	ON_BN_CLICKED(IDC_BTN_GETCONFIG, &CHisiSdkTestDlg::OnBnClickedBtnGetconfig)
	ON_BN_CLICKED(IDC_CHK_OPENSOUND, &CHisiSdkTestDlg::OnBtnClickedChkOpensound)
	ON_BN_CLICKED(IDC_BTN_BRDC_START, &CHisiSdkTestDlg::OnBnClickedBtnBrdcStart)
	ON_BN_CLICKED(IDC_BTN_BRDC_ADD, &CHisiSdkTestDlg::OnBnClickedBtnBrdcAdd)
	ON_BN_CLICKED(IDC_BTN_BRDC_DEL, &CHisiSdkTestDlg::OnBnClickedBtnBrdcDel)
	ON_BN_CLICKED(IDC_BTN_BRDC_STOP, &CHisiSdkTestDlg::OnBnClickedBtnBrdcStop)
	ON_BN_CLICKED(IDC_BTN_CLEARLOG, &CHisiSdkTestDlg::OnBnClickedBtnClearlog)
	ON_BN_CLICKED(IDC_BTN_STREAM_OPEN, &CHisiSdkTestDlg::OnBnClickedBtnStreamOpen)
	ON_BN_CLICKED(IDC_BTN_STREAM_INPUT, &CHisiSdkTestDlg::OnBnClickedBtnStreamInput)
	ON_BN_CLICKED(IDC_BTN_STREAM_CLOSE, &CHisiSdkTestDlg::OnBnClickedBtnStreamClose)
	ON_BN_CLICKED(IDC_CHECK_ALARM, &CHisiSdkTestDlg::OnBnClickedCheckAlarm)
	ON_BN_CLICKED(IDC_BUTTON_PLAYBYNAME, &CHisiSdkTestDlg::OnBnClickedButtonPlaybyname)
	ON_BN_CLICKED(IDC_BTN_GETFILEBYNAME, &CHisiSdkTestDlg::OnBnClickedBtnGetfilebyname)
	ON_BN_CLICKED(IDC_BUTTON_GETFILEPOS, &CHisiSdkTestDlg::OnBnClickedButtonGetfilepos)
	ON_BN_CLICKED(IDC_BUTTON_PLAYBYNAME2, &CHisiSdkTestDlg::OnBnClickedButtonPlaybyname2)
	ON_BN_CLICKED(IDC_BTN_STOPPLAYBACK2, &CHisiSdkTestDlg::OnBnClickedBtnStopplayback2)
	ON_BN_CLICKED(IDC_BTN_GETFILEBYNAME2, &CHisiSdkTestDlg::OnBnClickedBtnGetfilebyname2)
	ON_BN_CLICKED(IDC_BUTTON_STEP, &CHisiSdkTestDlg::OnBnClickedButtonStep)
	ON_BN_CLICKED(IDC_BUTTON_GETPLAYPOS, &CHisiSdkTestDlg::OnBnClickedButtonGetplaypos)
	ON_BN_CLICKED(IDC_BUTTON_GETPLAYPOS2, &CHisiSdkTestDlg::OnBnClickedButtonGetplaypos2)
END_MESSAGE_MAP()


// CHisiSdkTestDlg 消息处理程序

BOOL CHisiSdkTestDlg::OnInitDialog()
{
	CDialog::OnInitDialog();

	// 设置此对话框的图标。当应用程序主窗口不是对话框时，框架将自动
	//  执行此操作
	SetIcon(m_hIcon, TRUE);			// 设置大图标
	SetIcon(m_hIcon, FALSE);		// 设置小图标

	// TODO: 在此添加额外的初始化代码
	//initialize data
	m_nUserID = -1;
	m_nPreviewPlayHandle = -1;
	m_nPlaybackPlayHandle = -1;
	memset(&m_StartTime, 0, sizeof(HISI_DVR_TIME));
	memset(&m_StopTime, 0, sizeof(HISI_DVR_TIME));
	m_tmDate = CTime::GetCurrentTime();
	m_tmStartTime = m_tmStopTime = m_tmDate;
	((CDateTimeCtrl *)GetDlgItem(IDC_DTP_DATE))->SetTime(&m_tmDate);
	((CDateTimeCtrl *)GetDlgItem(IDC_DTP_STARTTIME))->SetTime(&m_tmStartTime);
	((CDateTimeCtrl *)GetDlgItem(IDC_DTP_STOPTIME))->SetTime(&m_tmStopTime);
	m_bPTZControlStart = false;
	m_nFreePort = -1;
	m_hBroadcast = NULL;
	m_lAlarmHandle = -1;
	m_lFindHandle = -1;
	//load method address
	m_hINSTANCE = LoadLibrary(_T("HISISDK.dll"));
	if( m_hINSTANCE ==NULL )
	{  
		MessageBox(_T("无法加载HISISDK.dll"));
		return FALSE;
	}

	if( NULL == (pHISI_DVR_Init = (PHISI_DVR_Init)GetProcAddress(m_hINSTANCE,"HISI_DVR_Init")) )
	{
		FreeLibrary(m_hINSTANCE);
		MessageBox(_T("无法加载HISISDK.dll::HISI_DVR_Init"));
	}

	pHISI_DVR_Cleanup = (PHISI_DVR_Cleanup)GetProcAddress(m_hINSTANCE, "HISI_DVR_Cleanup");
	pHISI_DVR_GetSDKVersion = (PHISI_DVR_GetSDKVersion)GetProcAddress(m_hINSTANCE, "HISI_DVR_GetSDKVersion");
	pHISI_DVR_GetLastError = (PHISI_DVR_GetLastError)GetProcAddress(m_hINSTANCE, "HISI_DVR_GetLastError");
	pHISI_DVR_SetDVRMessage = (PHISI_DVR_SetDVRMessage)GetProcAddress(m_hINSTANCE, "HISI_DVR_SetDVRMessage");
	pHISI_DVR_GetConnectInfoByID = (PHISI_DVR_GetConnectInfoByID)GetProcAddress(m_hINSTANCE, "HISI_DVR_GetConnectInfoByID");
	pHISI_DVR_Login = (PHISI_DVR_Login)GetProcAddress(m_hINSTANCE, "HISI_DVR_Login");
	pHISI_DVR_Logout = (PHISI_DVR_Logout)GetProcAddress(m_hINSTANCE, "HISI_DVR_Logout");
	pHISI_DVR_RealPlay = (PHISI_DVR_RealPlay)GetProcAddress(m_hINSTANCE, "HISI_DVR_RealPlay");
	pHISI_DVR_RealPlayEx = (PHISI_DVR_RealPlayEx)GetProcAddress(m_hINSTANCE, "HISI_DVR_RealPlayEx");
	pHISI_DVR_StopRealPlay = (PHISI_DVR_StopRealPlay)GetProcAddress(m_hINSTANCE, "HISI_DVR_StopRealPlay");
	pHISI_DVR_OpenSound = (PHISI_DVR_OpenSound)GetProcAddress(m_hINSTANCE, "HISI_DVR_OpenSound");
	pHISI_DVR_CloseSound = (PHISI_DVR_CloseSound)GetProcAddress(m_hINSTANCE, "HISI_DVR_CloseSound");
	pHISI_DVR_CapturePicture = (PHISI_DVR_CapturePicture)GetProcAddress(m_hINSTANCE, "HISI_DVR_CapturePicture");
	pHISI_DVR_SetRealDataCallBack = (PHISI_DVR_SetRealDataCallBack)GetProcAddress(m_hINSTANCE, "HISI_DVR_SetRealDataCallBack");
	pHISI_DVR_SaveRealData = (PHISI_DVR_SaveRealData)GetProcAddress(m_hINSTANCE, "HISI_DVR_SaveRealData");
	pHISI_DVR_StopSaveRealData = (PHISI_DVR_StopSaveRealData)GetProcAddress(m_hINSTANCE, "HISI_DVR_StopSaveRealData");
	pHISI_DVR_SetupAlarmChan = (PHISI_DVR_SetupAlarmChan)GetProcAddress(m_hINSTANCE, "HISI_DVR_SetupAlarmChan");
	pHISI_DVR_CloseAlarmChan = (PHISI_DVR_CloseAlarmChan)GetProcAddress(m_hINSTANCE, "HISI_DVR_CloseAlarmChan");
	pHISI_DVR_SetDVRMessageCallBack = (PHISI_DVR_SetDVRMessageCallBack)GetProcAddress(m_hINSTANCE, "HISI_DVR_SetDVRMessageCallBack");
	pHISI_DVR_FindFile = (PHISI_DVR_FindFile)GetProcAddress(m_hINSTANCE, "HISI_DVR_FindFile");
	pHISI_DVR_FindNextFile = (PHISI_DVR_FindNextFile)GetProcAddress(m_hINSTANCE, "HISI_DVR_FindNextFile");
	pHISI_DVR_FindClose = (PHISI_DVR_FindClose)GetProcAddress(m_hINSTANCE, "HISI_DVR_FindClose");
	pHISI_DVR_PlayBackByTime = (PHISI_DVR_PlayBackByTime)GetProcAddress(m_hINSTANCE, "HISI_DVR_PlayBackByTime");
	pHISI_DVR_PlayBackByName = (PHISI_DVR_PlayBackByName)GetProcAddress(m_hINSTANCE, "HISI_DVR_PlayBackByName");
	pHISI_DVR_PlayBackControl = (PHISI_DVR_PlayBackControl)GetProcAddress(m_hINSTANCE, "HISI_DVR_PlayBackControl");
	pHISI_DVR_StopPlayBack = (PHISI_DVR_StopPlayBack)GetProcAddress(m_hINSTANCE, "HISI_DVR_StopPlayBack");
	pHISI_DVR_SetPlayDataCallBack = (PHISI_DVR_SetPlayDataCallBack)GetProcAddress(m_hINSTANCE, "HISI_DVR_SetPlayDataCallBack");
	pHISI_DVR_PlayBackSaveData = (PHISI_DVR_PlayBackSaveData)GetProcAddress(m_hINSTANCE, "HISI_DVR_PlayBackSaveData");
	pHISI_DVR_StopPlayBackSave = (PHISI_DVR_StopPlayBackSave)GetProcAddress(m_hINSTANCE, "HISI_DVR_StopPlayBackSave");
	pHISI_DVR_PlayBackCaptureFile = (PHISI_DVR_PlayBackCaptureFile)GetProcAddress(m_hINSTANCE, "HISI_DVR_PlayBackCaptureFile");
	pHISI_BroadcastStart = (PHISI_BroadcastStart)GetProcAddress(m_hINSTANCE, "HISI_BroadcastStart");
	pHISI_BroadcastAddClient = (PHISI_BroadcastAddClient)GetProcAddress(m_hINSTANCE, "HISI_BroadcastAddClient");
	pHISI_BroadcastDelClient = (PHISI_BroadcastDelClient)GetProcAddress(m_hINSTANCE, "HISI_BroadcastDelClient");
	pHISI_BroadcastStop = (PHISI_BroadcastStop)GetProcAddress(m_hINSTANCE, "HISI_BroadcastStop");
	pHISI_Play_Init = (PHISI_Play_Init)GetProcAddress(m_hINSTANCE, "HISI_Play_Init");
	pHISI_Play_Realese = (PHISI_Play_Realese)GetProcAddress(m_hINSTANCE, "HISI_Play_Realese");
	pHISI_Play_OpenFile = (PHISI_Play_OpenFile)GetProcAddress(m_hINSTANCE, "HISI_Play_OpenFile");
	pHISI_Play_CloseFile = (PHISI_Play_CloseFile)GetProcAddress(m_hINSTANCE, "HISI_Play_CloseFile");
	pHISI_Play_Play = (PHISI_Play_Play)GetProcAddress(m_hINSTANCE, "HISI_Play_Play");
	pHISI_Play_Stop = (PHISI_Play_Stop)GetProcAddress(m_hINSTANCE, "HISI_Play_Stop");
	pHISI_Play_Pause = (PHISI_Play_Pause)GetProcAddress(m_hINSTANCE, "HISI_Play_Pause");
	pHISI_Play_Fast = (PHISI_Play_Fast)GetProcAddress(m_hINSTANCE, "HISI_Play_Fast");
	pHISI_Play_Slow = (PHISI_Play_Slow)GetProcAddress(m_hINSTANCE, "HISI_Play_Slow");
	pHISI_Play_GetPlayPos = (PHISI_Play_GetPlayPos)GetProcAddress(m_hINSTANCE, "HISI_Play_GetPlayPos");
	pHISI_Play_SetPlayPos = (PHISI_Play_SetPlayPos)GetProcAddress(m_hINSTANCE, "HISI_Play_SetPlayPos");
	pHISI_Play_OpenStream = (PHISI_Play_OpenStream)GetProcAddress(m_hINSTANCE, "HISI_Play_OpenStream");
	pHISI_Play_CloseStream = (PHISI_Play_CloseStream)GetProcAddress(m_hINSTANCE, "HISI_Play_CloseStream");
	pHISI_Play_InputData = (PHISI_Play_InputData)GetProcAddress(m_hINSTANCE, "HISI_Play_InputData");
	pHISI_DVR_GetFileByName = (PHISI_DVR_GetFileByName)GetProcAddress(m_hINSTANCE, "HISI_DVR_GetFileByName");
	pHISI_DVR_StopGetFile = (PHISI_DVR_StopGetFile)GetProcAddress(m_hINSTANCE, "HISI_DVR_StopGetFile");
	pHISI_Play_OneByOne = (PHISI_Play_OneByOne)GetProcAddress(m_hINSTANCE, "HISI_Play_OneByOne");
	if(NULL == (pHISI_DVR_PTZControl = (PHISI_DVR_PTZControl)GetProcAddress(m_hINSTANCE, "HISI_DVR_PTZControl")))
		MessageBox("ptz");
	if(NULL == (pHISI_Play_GetPort = (PHISI_Play_GetPort)GetProcAddress(m_hINSTANCE, "HISI_Play_GetPort")))
		MessageBox("getport");
	(pHISI_Play_FreePort = (PHISI_Play_FreePort)GetProcAddress(m_hINSTANCE, "HISI_Play_FreePort"));
	pHISI_Play_CapturePicture = (PHISI_Play_CapturePicture)GetProcAddress(m_hINSTANCE, "HISI_Play_CapturePicture");
	pHISI_DVR_GetDVRConfig = (PHISI_DVR_GetDVRConfig)GetProcAddress(m_hINSTANCE, "HISI_DVR_GetDVRConfig");
	pHISI_DVR_SetDVRConfig = (PHISI_DVR_SetDVRConfig)GetProcAddress(m_hINSTANCE, "HISI_DVR_SetDVRConfig");
	if(NULL == (pHISI_Play_GetFileTime = (PHISI_Play_GetFileTime)GetProcAddress(m_hINSTANCE, "HISI_Play_GetFileTime")))
		MessageBox("GetFileTime");
	if(NULL == (pHISI_Play_GetPlayedTime = (PHISI_Play_GetPlayedTime)GetProcAddress(m_hINSTANCE, "HISI_Play_GetPlayedTime")))
		MessageBox("GetPlayedTime");


	LVCOLUMN col;
	col.mask = LVCF_SUBITEM | LVCF_TEXT | LVCF_WIDTH;
	col.iSubItem = 0;
	col.pszText = _T("File name");
	col.cchTextMax = _tcslen(col.pszText) * sizeof(TCHAR);
	col.cx = 100;
	((CListCtrl *)GetDlgItem(IDC_LIST_FINDFILE))->InsertColumn(0,&col);

	memset(&col,0,sizeof(col));
	col.mask = LVCF_SUBITEM | LVCF_TEXT | LVCF_WIDTH;
	col.iSubItem = 1;
	col.pszText = _T("Start time");
	col.cchTextMax = _tcslen(col.pszText) * sizeof(TCHAR);
	col.cx = 100;
	((CListCtrl *)GetDlgItem(IDC_LIST_FINDFILE))->InsertColumn(1,&col);

	memset(&col,0,sizeof(col));
	col.mask = LVCF_SUBITEM | LVCF_TEXT | LVCF_WIDTH;
	col.iSubItem = 2;
	col.pszText = _T("End time");
	col.cchTextMax = _tcslen(col.pszText) * sizeof(TCHAR);
	col.cx = 100;
	((CListCtrl *)GetDlgItem(IDC_LIST_FINDFILE))->InsertColumn(2,&col);

	memset(&col,0,sizeof(col));
	col.mask = LVCF_SUBITEM | LVCF_TEXT | LVCF_WIDTH;
	col.iSubItem = 3;
	col.pszText = _T("Size");
	col.cchTextMax = _tcslen(col.pszText) * sizeof(TCHAR);
	col.cx = 100;
	((CListCtrl *)GetDlgItem(IDC_LIST_FINDFILE))->InsertColumn(3,&col);

	
	

	return TRUE;  // 除非将焦点设置到控件，否则返回 TRUE
}

// 如果向对话框添加最小化按钮，则需要下面的代码
//  来绘制该图标。对于使用文档/视图模型的 MFC 应用程序，
//  这将由框架自动完成。

void CHisiSdkTestDlg::OnPaint()
{
	if (IsIconic())
	{
		CPaintDC dc(this); // 用于绘制的设备上下文

		SendMessage(WM_ICONERASEBKGND, reinterpret_cast<WPARAM>(dc.GetSafeHdc()), 0);

		// 使图标在工作矩形中居中
		int cxIcon = GetSystemMetrics(SM_CXICON);
		int cyIcon = GetSystemMetrics(SM_CYICON);
		CRect rect;
		GetClientRect(&rect);
		int x = (rect.Width() - cxIcon + 1) / 2;
		int y = (rect.Height() - cyIcon + 1) / 2;

		// 绘制图标
		dc.DrawIcon(x, y, m_hIcon);
	}
	else
	{
		CDialog::OnPaint();
	}
}

//当用户拖动最小化窗口时系统调用此函数取得光标显示。
//
HCURSOR CHisiSdkTestDlg::OnQueryDragIcon()
{
	return static_cast<HCURSOR>(m_hIcon);
}

void CHisiSdkTestDlg::SetLogText(CString &strText)
{
	int nLen = m_editLog.GetWindowTextLength();
	m_editLog.SetFocus();
	m_editLog.SetSel(nLen, nLen);
	strText += _T("\r\r\n********************\n");
	m_editLog.ReplaceSel(strText);
}

void CHisiSdkTestDlg::Faild(CString &strRes)
{
	strRes += _T("Failed.\n");	
	DWORD dwError = pHISI_DVR_GetLastError();
	CString strOut;
	strOut.Format(_T("%s\r\r\nError code:%d\n"), strRes, dwError);	
	SetLogText(strOut);
}

void CHisiSdkTestDlg::OnBnClickedBtnInitialize()
{
	// TODO: 在此添加控件通知处理程序代码
	BOOL bRes = pHISI_DVR_Init();
	CString strRes = _T("HISI_DVR_Init:");
	if (FALSE == bRes)
	{
		Faild(strRes);
		return;
	}
	else
	{
		strRes += _T("OK.\n");	
		SetLogText(strRes); 
		m_strESeeID = _T("88902615");
		UpdateData(false);

		//set DVR message
		bRes = pHISI_DVR_SetDVRMessage(::OnException, NULL);
		strRes = _T("HISI_DVR_SetDVRMessage:");
		if (FALSE == bRes)
		{
			Faild(strRes);
			return;
		}
		else
		{
			strRes += _T("OK.\n");	
			SetLogText(strRes); 
		}
	}
}

void CHisiSdkTestDlg::OnBnClickedBtnGetversion()
{
	// TODO: 在此添加控件通知处理程序代码
	DWORD dwRes = 0x00;
	CString strRes = _T("HISI_DVR_GetSdkVersion:");
	dwRes = pHISI_DVR_GetSDKVersion();
	if (0x00 == dwRes)
	{
		strRes += _T("Failed.\n");
		GetDlgItem(IDC_EDIT_VERSION)->SetWindowText(_T("-ERROR"));
	}
	else
	{
		strRes += _T("OK.\n");
		int nL = 0x0000ffff & dwRes;
		int nH = dwRes >> 16;
		CString strVersion;
		strVersion.Format(_T("%d.%d"), nH, nL);
		GetDlgItem(IDC_EDIT_VERSION)->SetWindowText(strVersion);
	}
	SetLogText(strRes);
}

void CHisiSdkTestDlg::OnBnClickedBtnClear()
{
	// TODO: 在此添加控件通知处理程序代码
	BOOL bRes = pHISI_DVR_Cleanup();
	CString strRes = _T("HISI_DVR_Clearup:");
	if (FALSE == bRes)
	{
		Faild(strRes);
		return;
	}
	else
	{
		strRes += _T("OK.\n");	
		SetLogText(strRes); 
	}
}

void CHisiSdkTestDlg::OnBnClickedBtnLogin()
{
	// TODO: 在此添加控件通知处理程序代码
	UpdateData(true);
	if (m_strDVRPort.IsEmpty() || m_strHTTPPort.IsEmpty())
	{
		MessageBox(_T("Port can not be empty."));
		return;
	}

	CString strIP;
	strIP.Format(_T("%d.%d.%d.%d"), FIRST_IPADDRESS(m_dwIP), SECOND_IPADDRESS(m_dwIP), THIRD_IPADDRESS(m_dwIP), FOURTH_IPADDRESS(m_dwIP));
	unsigned short nDVRPort = _ttoi(m_strDVRPort);
	unsigned short nHTTPPort = _ttoi(m_strHTTPPort);
	m_DeviceInfo = (PHISI_DEVCEINFO)malloc(sizeof(HISI_DEVCEINFO));
	memset(m_DeviceInfo, 0, sizeof(HISI_DEVCEINFO));

	LONG lRes = pHISI_DVR_Login(strIP.GetBuffer(0), nDVRPort, nHTTPPort, "admin", "", m_DeviceInfo);
	strIP.ReleaseBuffer();
	CString strRes = _T("HISI_DVR_Login:");
	if (-1 == lRes)
	{
		Faild(strRes);
		free(m_DeviceInfo);
		return;
	}
	else /*if(0 == lRes)*/
	{
		m_nUserID = lRes;
		CString strId;
		strId.Format("OK!\r\nUser ID:%d", m_nUserID);
		strRes += strId;
		//strRes.Format(_T("%s OK."), strRes, lRes, m_DeviceInfo->ChanNum);
		free(m_DeviceInfo);
		SetLogText(strRes);
		strRes.Format(_T("	User ID:%d Channel:%d"), lRes, m_DeviceInfo->ChanNum);
	}/*
	else
		MessageBox("return value ");*/
}

void CHisiSdkTestDlg::OnBnClickedBtnLogout()
{
	// TODO: 在此添加控件通知处理程序代码
	BOOL bRes = pHISI_DVR_Logout(m_nUserID);
	CString strRes = _T("HISI_DVR_Logout:");
	if (FALSE == bRes)
	{
		Faild(strRes);
		return;
	}
	else
	{
		m_nUserID = -1;
		strRes += _T("OK.\n");	
		SetLogText(strRes); 
	}
}

void CHisiSdkTestDlg::OnBnClickedBtnGetinfo()
{
	// TODO: 在此添加控件通知处理程序代码
	UpdateData(TRUE);
	m_ConnectInfo = (PHISI_DEVCONNECTINFO)malloc(sizeof(HISI_DEVCONNECTINFO));
	memset((void *)m_ConnectInfo, 0, sizeof(HISI_DEVCONNECTINFO));

 	BOOL bRes = pHISI_DVR_GetConnectInfoByID(m_strESeeID.GetBuffer(0), m_ConnectInfo);
	m_strESeeID.ReleaseBuffer();
	CString strRes = _T("HISI_DVR_GetConnectInfoByID:");
	if (FALSE == bRes)
	{
		Faild(strRes);
		return;
	}
	else
	{
		strRes += _T("OK.\n");	
		SetLogText(strRes);/*
		DWORD dwError = pHISI_DVR_GetLastError();
		if(HISI_DVR_NOERROR != dwError)
			strRes.Format(_T("%s\r\r\nError code:%d\n"), strRes, dwError);*/
	}

	if(m_ConnectInfo)
	{
		m_dwIP = ntohl(inet_addr(m_ConnectInfo->sIP));
		m_strHTTPPort.Format(_T("%d"), m_ConnectInfo->nHttpPort);
		m_strDVRPort.Format(_T("%d"), m_ConnectInfo->nVideoPort);
		m_nPlayChannel = 1;
		UpdateData(FALSE);
	}
	free(m_ConnectInfo);
	SetLogText(strRes); 
}

void CHisiSdkTestDlg::OnBnClickedBtnStartRealplay()
{
	// TODO: 在此添加控件通知处理程序代码
	//start realplay
	UpdateData(TRUE);
	 HISI_DEV_CLIENTINFOEX clientInfo;
	 memset(&clientInfo, 0, sizeof(HISI_DEV_CLIENTINFO));
	 clientInfo.Channel = m_nPlayChannel;
	 clientInfo.LinkMode = 1;
	 clientInfo.PlayWnd = GetDlgItem(IDC_STA_PLAYWND)->GetSafeHwnd();
	 clientInfo.Stream = m_nStream;

	 LONG lRes = pHISI_DVR_RealPlayEx(m_nUserID, &clientInfo);
	 CString strRes = _T("HISI_DVR_RealPlay:");
	 if (-1 == lRes)
	 {
		 Faild(strRes);
		 return;
	 }
	 else
	 {
		 strRes.Format(_T("%sOK.\r\r\n Play handle:%d"),  strRes, lRes);	
		 m_nPreviewPlayHandle = lRes;
		 SetLogText(strRes); 
	 }

	 //register realplay callback
	 BOOL bRes = pHISI_DVR_SetRealDataCallBack(m_nPreviewPlayHandle, ::RealDataCallBack, (DWORD)this);
	 strRes = _T("HISI_DVR_SetRealDataCallBack:");
	 if (FALSE == bRes)
	 {
		 Faild(strRes);
		 return;
	 }
	 else
	 {
		 strRes += _T("OK.\n");
		 SetLogText(strRes); 
	 }
}

void CHisiSdkTestDlg::OnBnClickedBtnStopRealplay()
{
	// TODO: 在此添加控件通知处理程序代码
	BOOL bRes = pHISI_DVR_StopRealPlay(m_nPreviewPlayHandle);
	CString strRes = _T("HISI_DVR_StopRealPlay:");
	if (FALSE == bRes)
	{
		Faild(strRes);
		return;
	}
	else
	{
		strRes += _T("OK.\n");	
		m_nPreviewPlayHandle = -1;
		SetLogText(strRes); 
	}
}

void CHisiSdkTestDlg::OnBnClickedBtnCapture()
{
	// TODO: 在此添加控件通知处理程序代码
	BOOL bRes = pHISI_DVR_CapturePicture(m_nPreviewPlayHandle, "D:\\123\\Preview.bmp");
	CString strRes = _T("HISI_DVR_CapturePicture:");
	if (FALSE == bRes)
	{
		Faild(strRes);
		return;
	}
	else
	{
		strRes += _T("OK.\n");
		SetLogText(strRes); 
	}

}

void CHisiSdkTestDlg::OnBnClickedBtnSaverealdata()
{
	// TODO: 在此添加控件通知处理程序代码
	if (-1 == m_nPreviewPlayHandle)
	{
		return;
	}

	BOOL bRes = pHISI_DVR_SaveRealData(m_nPreviewPlayHandle, "D:\\Preview.mp4");
	CString strRes = _T("HISI_DVR_SaveRealData:");
	if (FALSE == bRes)
	{
		Faild(strRes);
		return;
	}
	else
	{
		strRes += _T("OK.\n");	
		SetLogText(strRes); 
	}
}

void CHisiSdkTestDlg::OnBnClickedBtnStopsave()
{
	// TODO: 在此添加控件通知处理程序代码
	if (-1 == m_nPreviewPlayHandle)
	{
		return;
	}
	BOOL bRes = pHISI_DVR_StopSaveRealData(m_nPreviewPlayHandle);
	CString strRes = _T("HISI_DVR_StopSaveRealData:");
	if (FALSE == bRes)
	{
		Faild(strRes);
		return;
	}
	else
	{
		strRes += _T("OK.\n");
		SetLogText(strRes); 
	}
}

void CHisiSdkTestDlg::SetHisiTime(const CTime &date, const CTime &time, HISI_DVR_TIME &HisiTime)
{
	HisiTime.dwYear = (DWORD)date.GetYear();
	HisiTime.dwMonth = (DWORD)date.GetMonth();
	HisiTime.dwDay = (DWORD)date.GetDay();
	HisiTime.dwHour = (DWORD)time.GetHour();
	HisiTime.dwMinute = (DWORD)time.GetMinute();
	HisiTime.dwSecond = (DWORD)time.GetSecond();
}

void CHisiSdkTestDlg::OnBnClickedBtnStartplayback()
{
	// TODO: 在此添加控件通知处理程序代码
	UpdateData(TRUE);
	if (-1 == m_nUserID || 0 == m_nPlayChannel)
	{
		return;
	}
	SetHisiTime(m_tmDate, m_tmStartTime, m_StartTime);
	SetHisiTime(m_tmDate, m_tmStopTime, m_StopTime);

	LONG lRes = pHISI_DVR_PlayBackByTime(m_nUserID, m_nPlayChannel, &m_StartTime, &m_StopTime, GetDlgItem(IDC_STA_PLAKBACKWND)->GetSafeHwnd());
	CString strRes = _T("HISI_DVR_PlayBackByTime:");
	if (-1 == lRes)
	{
		Faild(strRes);
		return;
	}
	else
	{
		m_nPlaybackPlayHandle = lRes;
		CString str;
		str.Format(_T("%sOK.\r\r\n Play handle:%d"), strRes, lRes);	
		SetLogText(str); 

		//register playback callback
		BOOL bRes = pHISI_DVR_SetPlayDataCallBack(m_nPlaybackPlayHandle, ::PlayDataCallBack, 0);
		strRes = _T("HISI_DVR_SetPlayDataCallBack:");
		if (FALSE == bRes)
		{
			Faild(strRes);
			return;
		}
		else
		{
			strRes += _T("OK.\n");
			SetLogText(strRes); 
		}
	}
}

void CHisiSdkTestDlg::OnBnClickedBtnPause()
{
	// TODO: 在此添加控件通知处理程序代码
	if (-1 == m_nPlaybackPlayHandle)
		return;

	DWORD dwTest = 0;
	BOOL bRes = pHISI_DVR_PlayBackControl(m_nPlaybackPlayHandle, HISI_DVR_PLAYPAUSE, 0, &dwTest);
	CString strRes = _T("HISI_DVR_PlayBackControl:");
	if (FALSE == bRes)
	{
		Faild(strRes);
		return;
	}
	else
	{
		strRes += _T("OK.\n");	
		SetLogText(strRes); 
	}
}

void CHisiSdkTestDlg::OnBnClickedBtnResume()
{
	// TODO: 在此添加控件通知处理程序代码
	if (-1 == m_nPlaybackPlayHandle)
		return;

	DWORD dwTest = 0;
	BOOL bRes = pHISI_DVR_PlayBackControl(m_nPlaybackPlayHandle, HISI_DVR_PLAYRESTART, 0, &dwTest);
	CString strRes = _T("HISI_DVR_PlayBackControl:");
	if (FALSE == bRes)
	{
		Faild(strRes);
		return;
	}
	else
	{
		strRes += _T("OK.\n");	
		SetLogText(strRes); 
	}
}



void CHisiSdkTestDlg::OnBnClickedBtnPlaybackSave()
{
	// TODO: 在此添加控件通知处理程序代码
	if (-1 == m_nPlaybackPlayHandle)
		return;

	BOOL bRes = pHISI_DVR_PlayBackSaveData(m_nPlaybackPlayHandle, "D:\\playback.mp4");
	CString strRes = _T("HISI_DVR_PlayBackSaveData:");
	if (FALSE == bRes)
	{
		Faild(strRes);
		return;
	}
	else
	{
		strRes += _T("OK.\n");	
		SetLogText(strRes); 
	}
}

void CHisiSdkTestDlg::OnBnClickedBtnStopplaybacksave2()
{
	// TODO: 在此添加控件通知处理程序代码
	if (-1 == m_nPlaybackPlayHandle)
		return;

	BOOL bRes = pHISI_DVR_StopPlayBackSave(m_nPlaybackPlayHandle);
	CString strRes = _T("HISI_DVR_StopPlayBackSave:");
	if (FALSE == bRes)
	{
		Faild(strRes);
		return;
	}
	else
	{
		strRes += _T("OK.\n");	
		SetLogText(strRes); 
	}
}



void CHisiSdkTestDlg::OnBnClickedBtnGetFileByTime()
{
	// TODO: 在此添加控件通知处理程序代码
}

void CHisiSdkTestDlg::OnBnClickedBtnStopGettingFile()
{
	// TODO: 在此添加控件通知处理程序代码
}


void CHisiSdkTestDlg::OnBnClickedBtnPlaybackcapture()
{
	// TODO: 在此添加控件通知处理程序代码
	if (-1 == m_nPlaybackPlayHandle)
		return;

	BOOL bRes = pHISI_DVR_PlayBackCaptureFile(m_nPlaybackPlayHandle, "D:\\playback.bmp");
	CString strRes = _T("HISI_DVR_PlayBackCaptureFile:");
	if (FALSE == bRes)
	{
		Faild(strRes);
		return;
	}
	else
	{
		strRes += _T("OK.\n");	
		SetLogText(strRes); 
	}
}

void CHisiSdkTestDlg::OnBnClickedBtnFindfile()
{
	// TODO: 在此添加控件通知处理程序代码	
	UpdateData(TRUE);
	((CListCtrl *)GetDlgItem(IDC_LIST_FINDFILE))->DeleteAllItems();
	SetHisiTime(m_tmDate, m_tmStartTime, m_StartTime);
	SetHisiTime(m_tmDate, m_tmStopTime, m_StopTime);
	CString strRes = _T("HISI_DVR_FindFile:");
	HISI_DVR_RECORDTYPE FindType = rt_all;
	LONG FindFileChannel = (1 <<( m_nPlayChannel - 1));
	LONG lRes = pHISI_DVR_FindFile(m_nUserID, (LONG)FindFileChannel, FindType, &m_StartTime, &m_StopTime);
	if (-1 == lRes)
	{
		Faild(strRes);
		return;
	}
	else
	{
		m_lFindHandle = lRes;
		TRACE("%d\n", m_lFindHandle);
		strRes += _T("OK.\n");	
		SetLogText(strRes); 
	}
}

void CHisiSdkTestDlg::OnBnClickedBtnFindnext()
{
	// TODO: 在此添加控件通知处理程序代码
	HISI_DVR_FIND_DATA FindData;
	memset(&FindData, 0, sizeof(HISI_DVR_FIND_DATA));
	CString strRes = _T("HISI_DVR_FindNextFile:");
	int nRes = pHISI_DVR_FindNextFile(m_lFindHandle, &FindData);
	switch(nRes)
	{
	case HISI_DVR_FILE_SUCCESS:
		{
			strRes += _T("OK.\n");	
			SetLogText(strRes); 

			CString sFilename;
			CString sStarttiem;
			CString sEndtime;
			CString sSize;
			sFilename.Format("%s",FindData.sFileName);
			sStarttiem.Format("%d-%d-%d:%d:%d",
				FindData.struStartTime.dwMonth,
				FindData.struStartTime.dwDay,
				FindData.struStartTime.dwHour,
				FindData.struStartTime.dwMinute,
				FindData.struStartTime.dwSecond);
			sEndtime.Format("%d-%d-%d:%d:%d",
				FindData.struStopTime.dwMonth,
				FindData.struStopTime.dwDay,
				FindData.struStopTime.dwHour,
				FindData.struStopTime.dwMinute,
				FindData.struStopTime.dwSecond);
			sSize.Format("%d",FindData.dwFileSize);

			int nCount = ((CListCtrl *)GetDlgItem(IDC_LIST_FINDFILE))->GetItemCount();
			((CListCtrl *)GetDlgItem(IDC_LIST_FINDFILE))->InsertItem(nCount,sFilename.GetBuffer(0));
			((CListCtrl *)GetDlgItem(IDC_LIST_FINDFILE))->SetItem(nCount,1,LVIF_TEXT,sStarttiem.GetBuffer(0),0,0,0,NULL);
			((CListCtrl *)GetDlgItem(IDC_LIST_FINDFILE))->SetItem(nCount,2,LVIF_TEXT,sEndtime.GetBuffer(0),0,0,0,NULL);
			((CListCtrl *)GetDlgItem(IDC_LIST_FINDFILE))->SetItem(nCount,3,LVIF_TEXT,sSize.GetBuffer(0),0,0,0,NULL);
		}
		break;
	case HISI_DVR_FILE_NOFIND:
		{
			strRes += _T("no file be found\n");
			SetLogText(strRes);
		}
		break;
	case HISI_DVR_ISFINDING:
		{
			strRes += _T("finding,wait for a moment\n");
			SetLogText(strRes);

		}
		break;
	case HISI_DVR_NOMOREFILE:
		{
			strRes += _T("no more files,close the findfile please\n");
			SetLogText(strRes);
		}
		break;
	case HISI_DVR_FILE_EXCEPTION:
		{
			strRes += _T("a exception caused while finding files\n");
			SetLogText(strRes);
		}
		break;
	}
}

void CHisiSdkTestDlg::OnBnClickedBtnClosefind()
{
	// TODO: 在此添加控件通知处理程序代码
	BOOL bRes = pHISI_DVR_FindClose(m_lFindHandle);
	CString strRes = _T("HISI_DVR_FindClose:");
	if (FALSE == bRes)
	{
		Faild(strRes);
		return;
	}
	else
	{
		strRes += _T("OK.\n");	
		SetLogText(strRes); 
		m_lFindHandle = -1;

	}
}

void CHisiSdkTestDlg::OnBnClickedBtnPlayInit()
{
	// TODO: 在此添加控件通知处理程序代码
	BOOL bRes = pHISI_Play_Init();
	CString strRes = _T("HISI_Play_Init:");
	if (FALSE == bRes)
	{
		Faild(strRes);
		return;
	}
	else
	{
		strRes += _T("OK.\n");	
		SetLogText(strRes); 
	}
}

void CHisiSdkTestDlg::OnBnClickedBtnPlayRelease()
{
	// TODO: 在此添加控件通知处理程序代码
	BOOL bRes = pHISI_Play_Realese();
	CString strRes = _T("HISI_Play_Release:");
	if (FALSE == bRes)
	{
		Faild(strRes);
		return;
	}
	else
	{
		strRes += _T("OK.\n");	
		SetLogText(strRes); 
	}
}

void CHisiSdkTestDlg::OnBnClickedBtnPlayOpen()
{
	// TODO: 在此添加控件通知处理程序代码
	CFileDialog dlg(TRUE, NULL, NULL, 0, "*.mp4|*.mp4||");
	if (IDOK == dlg.DoModal())
	{
		CString strFilePath = dlg.GetPathName();

		BOOL bRes = pHISI_Play_OpenFile(PLAY_PORT, (LPSTR)(LPCTSTR)strFilePath);
		CString strRes = _T("HISI_Play_OpenFile:");
		if (FALSE == bRes)
		{
			Faild(strRes);
			return;
		}
		else
		{
			strRes += _T("OK.\n");	
			SetLogText(strRes); 
		}
	}
}

void CHisiSdkTestDlg::OnBnClickedBtnPlayClose()
{
	// TODO: 在此添加控件通知处理程序代码
	BOOL bRes = pHISI_Play_CloseFile(PLAY_PORT);
	CString strRes = _T("HISI_Play_CloseFile:");
	if (FALSE == bRes)
	{
		Faild(strRes);
		return;
	}
	else
	{
		strRes += _T("OK.\n");	
		SetLogText(strRes); 
	}
}

void CHisiSdkTestDlg::OnBnClickedBtnPlayPlay()
{
	// TODO: 在此添加控件通知处理程序代码
	BOOL bRes = pHISI_Play_Play(PLAY_PORT, GetDlgItem(IDC_STA_PLAYFILEWND)->GetSafeHwnd());
	CString strRes = _T("HISI_Play_Play:");
	if (FALSE == bRes)
	{
		Faild(strRes);
		return;
	}
	else
	{
		strRes += "OK.\n";	
		SetLogText(strRes); 
	}
}

void CHisiSdkTestDlg::OnBnClickedBtnPlayStop()
{
	// TODO: 在此添加控件通知处理程序代码
	BOOL bRes = pHISI_Play_Stop(PLAY_PORT);
	CString strRes = _T("HISI_Play_Stop:");
	if (FALSE == bRes)
	{
		Faild(strRes);
		return;
	}
	else
	{
		strRes += _T("OK.\n");	
		SetLogText(strRes); 
	}
}

void CHisiSdkTestDlg::OnBnClickedBtnPlayPause()
{
	// TODO: 在此添加控件通知处理程序代码
	BOOL bRes = pHISI_Play_Pause(PLAY_PORT, 1);
	CString strRes = _T("HISI_Play_Pause:");
	if (FALSE == bRes)
	{
		Faild(strRes);
		return;
	}
	else
	{
		strRes += _T("OK.\n");	
		SetLogText(strRes); 
	}
}

void CHisiSdkTestDlg::OnBnClickedBtnPlayResume()
{
	// TODO: 在此添加控件通知处理程序代码
	BOOL bRes = pHISI_Play_Pause(PLAY_PORT, 0);
	CString strRes = _T("HISI_Play_Resume:");
	if (FALSE == bRes)
	{
		Faild(strRes);
		return;
	}
	else
	{
		strRes += _T("OK.\n");	
		SetLogText(strRes); 
	}
}

void CHisiSdkTestDlg::OnBnClickedBtnFast()
{
	// TODO: 在此添加控件通知处理程序代码
	BOOL bRes = pHISI_Play_Fast(PLAY_PORT);
	CString strRes = _T("HISI_Play_Fast:");
	if (FALSE == bRes)
	{
		Faild(strRes);
		return;
	}
	else
	{
		strRes += _T("OK.\n");	
		SetLogText(strRes); 
	}
}

void CHisiSdkTestDlg::OnBnClickedBtnSlow()
{
	// TODO: 在此添加控件通知处理程序代码
	BOOL bRes = pHISI_Play_Slow(PLAY_PORT);
	CString strRes = _T("HISI_Play_Slow:");
	if (FALSE == bRes)
	{
		Faild(strRes);
		return;
	}
	else
	{
		strRes += _T("OK.\n");	
		SetLogText(strRes); 
	}
}


void CHisiSdkTestDlg::OnBnClickedBtnPlayGetpos()
{
	// TODO: 在此添加控件通知处理程序代码
	LONG lRes = pHISI_Play_GetPlayPos(PLAY_PORT);
	CString strRes = _T("HISI_Play_GetPlayPos:");
	if (-1 == lRes)
	{
		Faild(strRes);
		return;
	}
	else
	{
		strRes.Format(_T("%sOK.\r\r\n Current pos:%d"),strRes, lRes);	
		SetLogText(strRes); 
	}
}

void CHisiSdkTestDlg::OnBnClickedBtnPlaySetpos()
{
	// TODO: 在此添加控件通知处理程序代码
	BOOL bRes = pHISI_Play_SetPlayPos(PLAY_PORT, 50);
	CString strRes = _T("HISI_Play_SetPlayPos:");
	if (FALSE == bRes)
	{
		Faild(strRes);
		return;
	}
	else
	{
		strRes += _T("OK.\n");	
		SetLogText(strRes); 
	}
}

void CHisiSdkTestDlg::OnBnClickedBtnPlayGetFileTime()
{
	// TODO: 在此添加控件通知处理程序代码
	DWORD dwRes = pHISI_Play_GetFileTime(PLAY_PORT);
	CString strRes = _T("HISI_DVR_GetFileTime:");
	if (-1 == dwRes)
	{
		Faild(strRes);
		return;
	}
	else
	{
		strRes.Format(_T("%sOK.\r\r\nFile length:%ds\n"),strRes, dwRes);	
		SetLogText(strRes); 
	}
}

void CHisiSdkTestDlg::OnBnClickedBtnPlayGetPlayedTime()
{
	// TODO: 在此添加控件通知处理程序代码
	DWORD dwRes = pHISI_Play_GetPlayedTime(PLAY_PORT);
	CString strRes = _T("HISI_DVR_GetPlayedTime:");
	if (-1 == dwRes)
	{
		Faild(strRes);
		return;
	}
	else
	{
		strRes.Format(_T("%sOK.\r\r\nPlayed time:%ds\n"),strRes, dwRes);	
		SetLogText(strRes); 
	}
}

void CHisiSdkTestDlg::OnBnClickedBtnPtz()
{
	// TODO: 在此添加控件通知处理程序代码
	if (-1 == m_nPreviewPlayHandle)
		return;
	
	DWORD dwStop = !m_bPTZControlStart;
	BOOL bRes = pHISI_DVR_PTZControl(m_nPreviewPlayHandle, 24, dwStop);
	CString strRes = _T("HISI_Play_PTZControl:");
	if (FALSE == bRes)
	{
		Faild(strRes);
		return;
	}
	else
	{
		strRes.Format(_T("%sOK. start:%d\n"),strRes, m_bPTZControlStart);	
		SetLogText(strRes); 
		m_bPTZControlStart = !m_bPTZControlStart;
	}
}

void CHisiSdkTestDlg::OnBnClickedBtnGetport()
{
	// TODO: 在此添加控件通知处理程序代码
	LONG nPort = -1;
	BOOL bRes = pHISI_Play_GetPort(&nPort);
	CString strRes = _T("HISI_Play_Getport:");
	if (FALSE == bRes)
	{
		Faild(strRes);
		return;
	}
	else
	{
		strRes.Format(_T("%sOK. Available port:%d\n"),strRes, nPort);	
		SetLogText(strRes); 
	}
}

void CHisiSdkTestDlg::OnBnClickedBtnFreeport()
{
	// TODO: 在此添加控件通知处理程序代码
	UpdateData(TRUE);
	if (-1 == m_nFreePort)
	{
		MessageBox("Need to input the port to be free.");
		return;
	}

	BOOL bRes = pHISI_Play_GetPort(&m_nFreePort);
	CString strRes = _T("HISI_Play_Getport:");
	if (FALSE == bRes)
	{
		Faild(strRes);
		return;
	}
	else
	{
		strRes += _T("OK.\n");	
		SetLogText(strRes); 
	}
}

void CHisiSdkTestDlg::OnBnClickedBtnPlaycapture()
{
	// TODO: 在此添加控件通知处理程序代码
	BOOL bRes = pHISI_Play_CapturePicture(PLAY_PORT, "D:\\play.bmp");
	CString strRes = _T("HISI_Play_CapturePicture:");
	if (FALSE == bRes)
	{
		Faild(strRes);
		return;
	}
	else
	{
		strRes += _T("OK.\n");	
		SetLogText(strRes); 
	}
}

void CHisiSdkTestDlg::OnBnClickedOk()
{
	// TODO: 在此添加控件通知处理程序代码
	if ( FALSE == FreeLibrary(m_hINSTANCE))
		MessageBox("free library failed");
	m_file.Close();
	OnOK();
}

void CHisiSdkTestDlg::OnBnClickedBtnGetconfig()
{
	// TODO: 在此添加控件通知处理程序代码
	if (-1 == m_nUserID)
	{
		return;
	}
	DWORD nRcvBytes = 0;	

	PHISI_DEVINFO devInfo;
	memset(&devInfo, 0, sizeof(PHISI_DEVINFO));
	BOOL bRes = pHISI_DVR_GetDVRConfig(m_nUserID, HISI_DVR_GET_DEVICECFG, m_nPlayChannel, &devInfo, sizeof(PHISI_DEVINFO), &nRcvBytes);
	CString strRes = _T("HISI_DVR_GetDVRConfig:");
	if (FALSE == bRes)
	{
		Faild(strRes);
		return;
	}
	else
	{
		//strRes += "OK\n";	
		//SetLogText(strRes); 
	}

	//encode config
	HISI_ENCODEINFO encodeInfo;
	memset(&encodeInfo, 0, sizeof(HISI_ENCODEINFO));
	nRcvBytes = 0;
	/*bRes = */pHISI_DVR_GetDVRConfig(m_nUserID, HISI_DVR_GET_ENCODECFG, m_nPlayChannel, &encodeInfo, sizeof(HISI_ENCODEINFO), &nRcvBytes);
//	MTPRINTF("## Encode configuration:\n");
//	MTPRINTF("%s\n", encodeInfo);

	//general config
	HISI_MISCINFO miscInfo;
	memset(&miscInfo, 0, sizeof(HISI_MISCINFO));
	nRcvBytes = 0;
	/*bRes = */pHISI_DVR_GetDVRConfig(m_nUserID, HISI_DVR_GET_MISCCFG, m_nPlayChannel, &miscInfo, sizeof(HISI_MISCINFO), &nRcvBytes);

	//network config
	HISI_NETWORKINFO netInfo;
	memset(&netInfo, 0, sizeof(HISI_NETWORKINFO));
	nRcvBytes = 0;
	/*bRes = */pHISI_DVR_GetDVRConfig(m_nUserID, HISI_DVR_GET_NETCFG, m_nPlayChannel, &netInfo, sizeof(HISI_NETWORKINFO), &nRcvBytes);

	//screen config
	HISI_SCREENINFO screenInfo;
	memset(&screenInfo, 0, sizeof(HISI_SCREENINFO));
	nRcvBytes = 0;
	/*bRes = */pHISI_DVR_GetDVRConfig(m_nUserID, HISI_DVR_GET_SCREENCFG, m_nPlayChannel, &screenInfo, sizeof(HISI_SCREENINFO), &nRcvBytes);

	//ptz config
	HISI_PTZINFO ptzInfo;
	memset(&ptzInfo, 0, sizeof(HISI_PTZINFO));
	nRcvBytes = 0;
	/*bRes = */pHISI_DVR_GetDVRConfig(m_nUserID, HISI_DVR_GET_PTZCFG, m_nPlayChannel, &ptzInfo, sizeof(HISI_PTZINFO), &nRcvBytes);

	//sensor config
	HISI_SENSORINFO sensorInfo;
	memset(&sensorInfo, 0, sizeof(HISI_SENSORINFO));
	nRcvBytes = 0;
	/*bRes = */pHISI_DVR_GetDVRConfig(m_nUserID, HISI_DVR_GET_SENSORCFG, m_nPlayChannel, &sensorInfo, sizeof(HISI_SENSORINFO), &nRcvBytes);

	//detection config
	HISI_DETECTIONINFO detctInfo;
	memset(&detctInfo, 0, sizeof(HISI_DETECTIONINFO));
	nRcvBytes = 0;
	/*bRes = */pHISI_DVR_GetDVRConfig(m_nUserID, HISI_DVR_GET_DETECTIONCFG, m_nPlayChannel, &detctInfo, sizeof(HISI_DETECTIONINFO), &nRcvBytes);

	//network config
	HISI_SCHEDULEINFO scheduleInfo;
	memset(&scheduleInfo, 0, sizeof(HISI_SCHEDULEINFO));
	nRcvBytes = 0;
	/*bRes = */pHISI_DVR_GetDVRConfig(m_nUserID, HISI_DVR_GET_SCHEDULECFG, m_nPlayChannel, &scheduleInfo, sizeof(HISI_SCHEDULEINFO), &nRcvBytes);
	
}


void CHisiSdkTestDlg::OnBtnClickedChkOpensound()
{
	// TODO: 在此添加控件通知处理程序代码
	BOOL bChecked = ((CButton *)GetDlgItem(IDC_CHK_OPENSOUND))->GetCheck();
	if (BST_CHECKED == bChecked)
	{
		if (-1 == m_nPreviewPlayHandle)
		{
			CString str = "Preview handle error. \r\r\nPlease ensure you have opened a channel to preview.";
			((CButton *)GetDlgItem(IDC_CHK_OPENSOUND))->SetCheck(false);
			SetLogText(str);
			return;
		}

		CString strRes = _T("HISI_DVR_OpenSound:");		
		BOOL bRes = pHISI_DVR_OpenSound(m_nPreviewPlayHandle);
		if (HISI_FALSE == bRes)
		{
			Faild(strRes);
			((CButton *)GetDlgItem(IDC_CHK_OPENSOUND))->SetCheck(false);
			return;
		}
		else if (HISI_TRUE == bRes)
		{
			strRes += "OK.\n";
			SetLogText(strRes);
		}
	}
	else if (BST_UNCHECKED == bChecked)
	{

		CString strRes = _T("HISI_DVR_CloseSound:");		
		BOOL bRes = pHISI_DVR_CloseSound();
		if (FALSE == bRes)
		{
			Faild(strRes);
			return;
		}
		else
		{
			strRes += _T("OK.\n");	
			SetLogText(strRes); 
		}
	}
}

void CHisiSdkTestDlg::OnBnClickedBtnBrdcStart()
{
	// TODO: 在此添加控件通知处理程序代码
	CString strRes = _T("HISI_BroadcastStart:");
	int nRes = pHISI_BroadcastStart();
	if (-1 == nRes)
	{
		Faild(strRes);
		return;
	}
	else if(0 == nRes)
	{
		strRes += _T("OK.\n");	
		SetLogText(strRes); 
	}
}

void CHisiSdkTestDlg::OnBnClickedBtnBrdcAdd()
{
	// TODO: 在此添加控件通知处理程序代码
	UpdateData(true);
	if (0 == m_dwBroadcastIP)
	{
		CString str = "IP could not be empty.";
		SetLogText(str);
		return;
	}

	CString strBroadcastIP;
	strBroadcastIP.Format("%d.%d.%d.%d", FIRST_IPADDRESS(m_dwBroadcastIP), SECOND_IPADDRESS(m_dwBroadcastIP), THIRD_IPADDRESS(m_dwBroadcastIP), FOURTH_IPADDRESS(m_dwBroadcastIP));
	CString strBroadcastPort;
	GetDlgItem(IDC_EDIT_BRDC_PORT)->GetWindowText(strBroadcastPort);
	unsigned short uPort = _ttoi(strBroadcastPort);
	CString strRes = _T("HISI_BroadcastAddClient:");
	int nRes = pHISI_BroadcastAddClient(strBroadcastIP.GetBuffer(0), uPort, "", "admin", "123", m_hBroadcast);
	strBroadcastIP.ReleaseBuffer();

	if (-1 == nRes)
	{
		Faild(strRes);
		return;
	}
	else if(0 == nRes)
	{
		strRes += _T("OK.\n");	
		SetLogText(strRes); 
	}
}

void CHisiSdkTestDlg::OnBnClickedBtnBrdcDel()
{
	// TODO: 在此添加控件通知处理程序代码
	if (!m_hBroadcast)
	{
		MessageBox("");
	}
	CString strRes = _T("HISI_BroadcastDelClient:");
	int nRes = pHISI_BroadcastDelClient(m_hBroadcast);
	if (-1 == nRes)
	{
		Faild(strRes);
		return;
	}
	else if(0 == nRes)
	{
		strRes += _T("OK.\n");	
		SetLogText(strRes); 
		m_hBroadcast = NULL;
	}
}

void CHisiSdkTestDlg::OnBnClickedBtnBrdcStop()
{
	// TODO: 在此添加控件通知处理程序代码
	CString strRes = _T("HISI_BroadcastStop:");
	int nRes = pHISI_BroadcastStop();
	if (-1 == nRes)
	{
		Faild(strRes);
		return;
	}
	else if(0 == nRes)
	{
		strRes += _T("OK.\n");	
		SetLogText(strRes); 
	}
}

void CHisiSdkTestDlg::OnBnClickedBtnClearlog()
{
	// TODO: 在此添加控件通知处理程序代码
	((CEdit *)GetDlgItem(IDC_EDIT_LOG))->SetWindowText("");
}

void CHisiSdkTestDlg::OnBnClickedBtnStreamOpen()
{
	// TODO: 在此添加控件通知处理程序代码
	BOOL bRes = pHISI_Play_Init();
	if (FALSE == bRes)
	{
		MessageBox("Play initialization error.");
		return;
	}

	CString strRes = _T("HISI_Play_OpenStream:");
	bRes = pHISI_Play_OpenStream(STREAM_PORT);
	if (FALSE == bRes)
	{
		Faild(strRes);
		return;
	}
	else if(-1 == bRes)
	{
		strRes += _T("OK.\n");	
		SetLogText(strRes); 
	}
}

void CHisiSdkTestDlg::OnBnClickedBtnStreamInput()
{
	// TODO: 在此添加控件通知处理程序代码
	CString strRes = _T("InputData:");
	BOOL bRes = pHISI_Play_Play(STREAM_PORT, GetDlgItem(IDC_STA_PLAYFILEWND)->GetSafeHwnd());
	if (FALSE == bRes)
	{
		Faild(strRes);
		return;
	}
	else if(-1 == bRes)
	{
		m_bInputData = TRUE;
		strRes += _T("OK.\n");	
		SetLogText(strRes); 
	}
}

void CHisiSdkTestDlg::OnBnClickedBtnStreamClose()
{
	// TODO: 在此添加控件通知处理程序代码
	CString strRes = _T("HISI_Play_CloseStream:");
	BOOL bRes = pHISI_Play_CloseStream(STREAM_PORT);
	if (FALSE == bRes)
	{
		Faild(strRes);
		return;
	}
	else if(-1 == bRes)
	{
		m_bInputData = FALSE;
		strRes += _T("OK.\n");	
		SetLogText(strRes); 
	}
}

void CHisiSdkTestDlg::OnBnClickedCheckAlarm()
{
	// TODO: 在此添加控件通知处理程序代码
	if (m_chkAlarm.GetCheck())
	{
		//set callback function
		CString strRes = _T("HISI_DVR_SetDVRMessageCallBack:");
		BOOL bRes = pHISI_DVR_SetDVRMessageCallBack(::MessageCallBack, (DWORD)0);
		if (FALSE == bRes)
		{
			Faild(strRes);
			return;
		}
		else if(-1 == bRes)
		{
			strRes += _T("OK.\n");	
			SetLogText(strRes); 
		}

		//start alarm
		UpdateData(TRUE);
		CString strIP;
		strIP.Format("%d.%d.%d.%d", FIRST_IPADDRESS(m_dwAlarmIP), SECOND_IPADDRESS(m_dwAlarmIP), THIRD_IPADDRESS(m_dwAlarmIP), FOURTH_IPADDRESS(m_dwAlarmIP));
		strRes = _T("HISI_DVR_SetupAlarmChan:");
		LONG lRes = pHISI_DVR_SetupAlarmChan(strIP.GetBuffer(0), (WORD)m_nAlarmPort, "admin", "");
		strIP.ReleaseBuffer();
		if (-1 == lRes)
		{
			Faild(strRes);
			return;
		}
		else
		{
			m_lAlarmHandle = lRes;
			strRes += _T("OK.\n");	
			SetLogText(strRes); 
		}
	}
	else
	{
		CString strRes = _T("HISI_Play_CloseStream:");
		if (-1 == m_lAlarmHandle)
		{
			strRes += ("Alarm is disabled\n.");
			SetLogText(strRes);
			return;
		}

		BOOL bRes = pHISI_Play_CloseStream(m_lAlarmHandle);
		if (FALSE == bRes)
		{
			Faild(strRes);
			return;
		}
		else
		{
			m_lAlarmHandle = -1;
			strRes += _T("OK.\n");	
			SetLogText(strRes); 
		}
	}
}



void CHisiSdkTestDlg::OnBnClickedBtnGetfilebyname()
{
	// TODO: 在此添加控件通知处理程序代码
	POSITION Pos = ((CListCtrl *)GetDlgItem(IDC_LIST_FINDFILE))->GetFirstSelectedItemPosition();
	if (NULL == Pos)
	{
		return;
	}

	int nItem = ((CListCtrl *)GetDlgItem(IDC_LIST_FINDFILE))->GetNextSelectedItem(Pos);
	CString sFileName = ((CListCtrl *)GetDlgItem(IDC_LIST_FINDFILE))->GetItemText(nItem,0);

	LONG lRes = pHISI_DVR_GetFileByName(m_nUserID,sFileName.GetBuffer(0),"C:\\sname.mp4");

	CString strRes;
	strRes = _T("HISI_DVR_GetFileByName:");
	if (-1 == lRes)
	{
		Faild(strRes);
		return;
	}
	else
	{
		m_nGetFileHandle = lRes;
		CString str;
		str.Format(_T("%sOK.\r\r\n Download file handle:%d"), strRes, lRes);	
		SetLogText(str); 
	}
}

void CHisiSdkTestDlg::OnBnClickedButtonGetfilepos()
{
	// TODO: 在此添加控件通知处理程序代码
	DWORD dwFilePos;
	pHISI_DVR_PlayBackControl(m_nGetFileHandle,HISI_DVR_PLAYGETPOS,0,&dwFilePos);
	CString str;
	str.Format(_T("File pos:%d\r\n"),dwFilePos);
	SetLogText(str);

}

void CHisiSdkTestDlg::OnBnClickedButtonPlaybyname2()
{
	// TODO: 在此添加控件通知处理程序代码
	POSITION Pos = ((CListCtrl *)GetDlgItem(IDC_LIST_FINDFILE))->GetFirstSelectedItemPosition();
	if (NULL == Pos)
	{
		return;
	}

	int nItem = ((CListCtrl *)GetDlgItem(IDC_LIST_FINDFILE))->GetNextSelectedItem(Pos);
	CString sFileName = ((CListCtrl *)GetDlgItem(IDC_LIST_FINDFILE))->GetItemText(nItem,0);

	LONG lRes = pHISI_DVR_PlayBackByName(m_nUserID,sFileName.GetBuffer(0),GetDlgItem(IDC_STA_PLAKBACKWND2)->GetSafeHwnd());
	CString strRes = _T("HISI_DVR_PlayBackByName:");
	if (-1 == lRes)
	{
		Faild(strRes);
		return;
	}
	else
	{
		m_nPlaybackPlayHandle2 = lRes;
		CString str;
		str.Format(_T("%sOK.\r\r\n Play handle:%d"), strRes, lRes);	
		SetLogText(str); 

		//register playback callback
		BOOL bRes = pHISI_DVR_SetPlayDataCallBack(m_nPlaybackPlayHandle2, ::PlayDataCallBack, 0);
		strRes = _T("HISI_DVR_SetPlayDataCallBack:");
		if (FALSE == bRes)
		{
			Faild(strRes);
			return;
		}
		else
		{
			strRes += _T("OK.\n");
			SetLogText(strRes); 
		}
	}
}

void CHisiSdkTestDlg::OnBnClickedButtonPlaybyname()
{
	// TODO: 在此添加控件通知处理程序代码
	POSITION Pos = ((CListCtrl *)GetDlgItem(IDC_LIST_FINDFILE))->GetFirstSelectedItemPosition();
	if (NULL == Pos)
	{
		return;
	}

	int nItem = ((CListCtrl *)GetDlgItem(IDC_LIST_FINDFILE))->GetNextSelectedItem(Pos);
	CString sFileName = ((CListCtrl *)GetDlgItem(IDC_LIST_FINDFILE))->GetItemText(nItem,0);

	LONG lRes = pHISI_DVR_PlayBackByName(m_nUserID,sFileName.GetBuffer(0),GetDlgItem(IDC_STA_PLAKBACKWND)->GetSafeHwnd());
	CString strRes = _T("HISI_DVR_PlayBackByName:");
	if (-1 == lRes)
	{
		Faild(strRes);
		return;
	}
	else
	{
		m_nPlaybackPlayHandle = lRes;
		CString str;
		str.Format(_T("%sOK.\r\r\n Play handle:%d"), strRes, lRes);	
		SetLogText(str); 

		//register playback callback
		BOOL bRes = pHISI_DVR_SetPlayDataCallBack(m_nPlaybackPlayHandle, ::PlayDataCallBack, 0);
		strRes = _T("HISI_DVR_SetPlayDataCallBack:");
		if (FALSE == bRes)
		{
			Faild(strRes);
			return;
		}
		else
		{
			strRes += _T("OK.\n");
			SetLogText(strRes); 
		}
	}
}

void CHisiSdkTestDlg::OnBnClickedBtnStopplayback2()
{
	// TODO: 在此添加控件通知处理程序代码
	BOOL bRes = pHISI_DVR_StopPlayBack(m_nPlaybackPlayHandle2);
	CString strRes = _T("HISI_DVR_StopPlayBack:");
	if (FALSE == bRes)
	{
		Faild(strRes);
		return;
	}
	else
	{
		m_nPlaybackPlayHandle2 = -1;
		strRes += _T("OK.\n");	
		SetLogText(strRes); 
	}
}

void CHisiSdkTestDlg::OnBnClickedBtnStopplayback()
{
	// TODO: 在此添加控件通知处理程序代码
	BOOL bRes = pHISI_DVR_StopPlayBack(m_nPlaybackPlayHandle);
	CString strRes = _T("HISI_DVR_StopPlayBack:");
	if (FALSE == bRes)
	{
		Faild(strRes);
		return;
	}
	else
	{
		m_nPlaybackPlayHandle = -1;
		strRes += _T("OK.\n");	
		SetLogText(strRes); 
	}
}

void CHisiSdkTestDlg::OnBnClickedBtnGetfilebyname2()
{
	// TODO: 在此添加控件通知处理程序代码
	POSITION Pos = ((CListCtrl *)GetDlgItem(IDC_LIST_FINDFILE))->GetFirstSelectedItemPosition();
	if (NULL == Pos)
	{
		return;
	}

	int nItem = ((CListCtrl *)GetDlgItem(IDC_LIST_FINDFILE))->GetNextSelectedItem(Pos);
	CString sFileName = ((CListCtrl *)GetDlgItem(IDC_LIST_FINDFILE))->GetItemText(nItem,0);

	LONG lRes = pHISI_DVR_GetFileByName(m_nUserID,sFileName.GetBuffer(0),"C:\\sname2.mp4");

	CString strRes;
	strRes = _T("HISI_DVR_GetFileByName:");
	if (-1 == lRes)
	{
		Faild(strRes);
		return;
	}
	else
	{
//		m_nGetFileHandle = lRes;
		CString str;
		str.Format(_T("%sOK.\r\r\n Download file handle:%d"), strRes, lRes);	
		SetLogText(str); 
	}
}

void CHisiSdkTestDlg::OnBnClickedButtonStep()
{
	// TODO: 在此添加控件通知处理程序代码
	PHISI_Play_OneByOne(m_nPlaybackPlayHandle);
}

void CHisiSdkTestDlg::OnBnClickedButtonGetplaypos()
{
	// TODO: 在此添加控件通知处理程序代码
	// TODO: 在此添加控件通知处理程序代码
	DWORD dwFilePos;
	pHISI_DVR_PlayBackControl(m_nPlaybackPlayHandle,HISI_DVR_PLAYGETPOS,0,&dwFilePos);
	CString str;
	str.Format(_T("File pos:%d\r\n"),dwFilePos);
	SetLogText(str);
}

void CHisiSdkTestDlg::OnBnClickedButtonGetplaypos2()
{
	// TODO: 在此添加控件通知处理程序代码
	DWORD dwFilePos;
	pHISI_DVR_PlayBackControl(m_nPlaybackPlayHandle2,HISI_DVR_PLAYGETPOS,0,&dwFilePos);
	CString str;
	str.Format(_T("File pos:%d\r\n"),dwFilePos);
	SetLogText(str);
}
