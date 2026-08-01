
// multiple testDlg.h : 头文件
//

#pragma once
#include "afxcmn.h"


// CmultipletestDlg 对话框
class CmultipletestDlg : public CDialogEx
{
// 构造
public:
	CmultipletestDlg(CWnd* pParent = NULL);	// 标准构造函数

// 对话框数据
	enum { IDD = IDD_MULTIPLETEST_DIALOG };

	protected:
	virtual void DoDataExchange(CDataExchange* pDX);	// DDX/DDV 支持

	CMap<long,long &,long,long&> m_OpenMap;


// 实现
protected:
	HICON m_hIcon;

	// 生成的消息映射函数
	virtual BOOL OnInitDialog();
	afx_msg void OnPaint();
	afx_msg HCURSOR OnQueryDragIcon();
	DECLARE_MESSAGE_MAP()
public:
	afx_msg void OnBnClickedButton1();
	DWORD m_dwHttpPort;
	DWORD m_dwDataPort;
	CIPAddressCtrl m_Address;
	DWORD m_dwChannel;
	DWORD m_dwStream;
	DWORD m_dwWnd;
	afx_msg void OnBnClickedButton2();
	afx_msg void OnBnClickedButton3();
	long m_lUser;
};
