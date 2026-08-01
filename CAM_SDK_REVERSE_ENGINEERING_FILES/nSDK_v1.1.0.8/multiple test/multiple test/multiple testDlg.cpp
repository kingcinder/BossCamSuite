
// multiple testDlg.cpp : 实现文件
//

#include "stdafx.h"
#include "multiple test.h"
#include "multiple testDlg.h"
#include "afxdialogex.h"
#include "SdkLib.h"

#ifdef _DEBUG
#define new DEBUG_NEW
#endif


// CmultipletestDlg 对话框




CmultipletestDlg::CmultipletestDlg(CWnd* pParent /*=NULL*/)
	: CDialogEx(CmultipletestDlg::IDD, pParent)
	, m_dwHttpPort(0)
	, m_dwDataPort(0)
	, m_dwChannel(0)
	, m_dwStream(0)
	, m_dwWnd(0)
	, m_lUser(-1)
{
	m_hIcon = AfxGetApp()->LoadIcon(IDR_MAINFRAME);
}

void CmultipletestDlg::DoDataExchange(CDataExchange* pDX)
{
	CDialogEx::DoDataExchange(pDX);
	DDX_Text(pDX, IDC_EDIT1, m_dwHttpPort);
	DDX_Text(pDX, IDC_EDIT2, m_dwDataPort);
	DDX_Control(pDX, IDC_IPADDRESS1, m_Address);
	DDX_Text(pDX, IDC_EDIT3, m_dwChannel);
	DDX_Text(pDX, IDC_EDIT4, m_dwStream);
	DDX_Text(pDX, IDC_EDIT5, m_dwWnd);
}

BEGIN_MESSAGE_MAP(CmultipletestDlg, CDialogEx)
	ON_WM_PAINT()
	ON_WM_QUERYDRAGICON()

	ON_BN_CLICKED(IDC_BUTTON1, &CmultipletestDlg::OnBnClickedButton1)

	ON_BN_CLICKED(IDC_BUTTON2, &CmultipletestDlg::OnBnClickedButton2)
	ON_BN_CLICKED(IDC_BUTTON3, &CmultipletestDlg::OnBnClickedButton3)
END_MESSAGE_MAP()


// CmultipletestDlg 消息处理程序

BOOL CmultipletestDlg::OnInitDialog()
{
	CDialogEx::OnInitDialog();

	// 设置此对话框的图标。当应用程序主窗口不是对话框时，框架将自动
	//  执行此操作
	SetIcon(m_hIcon, TRUE);			// 设置大图标
	SetIcon(m_hIcon, FALSE);		// 设置小图标

	// TODO: 在此添加额外的初始化代码
	g_SdkLib.PHISI_DVR_Init();

	return TRUE;  // 除非将焦点设置到控件，否则返回 TRUE
}

// 如果向对话框添加最小化按钮，则需要下面的代码
//  来绘制该图标。对于使用文档/视图模型的 MFC 应用程序，
//  这将由框架自动完成。

void CmultipletestDlg::OnPaint()
{
	if (IsIconic())
	{
		CPaintDC dc(this); // 用于绘制的设备上下文

		SendMessage(WM_ICONERASEBKGND, reinterpret_cast<WPARAM>(dc.GetSafeHdc()), 0);

		// 使图标在工作区矩形中居中
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
		CDialogEx::OnPaint();
	}
}

//当用户拖动最小化窗口时系统调用此函数取得光标
//显示。
HCURSOR CmultipletestDlg::OnQueryDragIcon()
{
	return static_cast<HCURSOR>(m_hIcon);
}




void CmultipletestDlg::OnBnClickedButton1()
{
	// TODO: 在此添加控件通知处理程序代码
	UpdateData(TRUE);
	if (m_dwWnd > 6 || -1 == m_lUser)
	{
		return;
	}
	HISI_DEV_CLIENTINFOEX cinfoex;
	cinfoex.Channel = m_dwChannel;
	cinfoex.Stream = m_dwStream;
	cinfoex.LinkMode = 0;
	cinfoex.PlayWnd = GetDlgItem(IDC_PLAYWND1 + m_dwWnd - 1)->GetSafeHwnd();
	long player = g_SdkLib.PHISI_DVR_RealPlayEx(m_lUser,&cinfoex);
	if (-1 == player)
	{
		TRACE("open stream failed\r\n");
	}
}



//LONG (__stdcall *)(LONG lUserID,LONG lChannel, PHISI_DVR_TIME lpStartTime, PHISI_DVR_TIME lpStopTime, HWND hWnd)

void CmultipletestDlg::OnBnClickedButton2()
{
	// TODO: 在此添加控件通知处理程序代码
	UpdateData(TRUE);
	HISI_DVR_TIME startTime;
	HISI_DVR_TIME endTime;
	SYSTEMTIME timeTemp;
	((CDateTimeCtrl *)GetDlgItem(IDC_DATETIMEPICKER2))->GetTime(&timeTemp);
	startTime.dwYear = endTime.dwYear = timeTemp.wYear;
	startTime.dwMonth = endTime.dwMonth = timeTemp.wMonth;
	startTime.dwDay = endTime.dwDay = timeTemp.wDay;
	((CDateTimeCtrl *)GetDlgItem(IDC_DATETIMEPICKER3))->GetTime(&timeTemp);
	startTime.dwHour = timeTemp.wHour;
	startTime.dwMinute = timeTemp.wMinute;
	startTime.dwSecond = timeTemp.wSecond;
	((CDateTimeCtrl *)GetDlgItem(IDC_DATETIMEPICKER4))->GetTime(&timeTemp);
	endTime.dwHour = timeTemp.wHour;
	endTime.dwMinute = timeTemp.wMinute;
	endTime.dwSecond = timeTemp.wSecond;
	g_SdkLib.PHISI_DVR_PlayBackByTime(m_lUser,m_dwChannel,&startTime,&endTime,GetDlgItem(IDC_PLAYWND1 + m_dwWnd - 1)->GetSafeHwnd());
}


void CmultipletestDlg::OnBnClickedButton3()
{
	// TODO: 在此添加控件通知处理程序代码
	UpdateData(TRUE);
	HISI_DEVCEINFO info;
	BYTE address[4] = {0};
	char sAddress[32] = {0};
	m_Address.GetAddress(address[0],address[1],address[2],address[3]);
	sprintf_s(sAddress,sizeof(sAddress),"%d.%d.%d.%d",address[0],address[1],address[2],address[3]);
	m_lUser = g_SdkLib.PHISI_DVR_Login(sAddress,(WORD)m_dwDataPort,(WORD)m_dwHttpPort,"admin","",&info);
	if (-1 == m_lUser)
	{
		AfxMessageBox("Login failed");
	}
}
