; Remote VoiceIME per-user Windows installer (NSIS 3, Unicode). Built by
; scripts/package-windows.ps1 from a verified dist folder; never run directly.
; Installs for the current user only: no administrator rights, no service,
; no autostart. Settings in %LOCALAPPDATA%\RemoteVoiceInput are kept on uninstall.
Unicode true
ManifestDPIAware true
SetCompressor /SOLID lzma

!macro Require NAME
  !ifndef ${NAME}
    !error "Run scripts/package-windows.ps1; it defines PRODUCT_NAME, VERSION, BUILD, SOURCE_DIR, OUTFILE and ICON."
  !endif
!macroend
!insertmacro Require PRODUCT_NAME
!insertmacro Require VERSION
!insertmacro Require BUILD
!insertmacro Require SOURCE_DIR
!insertmacro Require OUTFILE
!insertmacro Require ICON

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"

!define EXE "${PRODUCT_NAME}.exe"
; Same identity as the app's single-instance mutex (kept from 0.1 for old settings).
!define INSTANCE_MUTEX "Local\RemoteVoiceInput.Windows"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\RemoteVoiceIME"

Name "${PRODUCT_NAME}"
Caption "${PRODUCT_NAME} ${VERSION} 安装"
UninstallCaption "${PRODUCT_NAME} 卸载"
OutFile "${OUTFILE}"
InstallDir "$LOCALAPPDATA\Programs\${PRODUCT_NAME}"
InstallDirRegKey HKCU "${UNINSTALL_KEY}" "InstallLocation"
RequestExecutionLevel user
BrandingText "${PRODUCT_NAME} ${VERSION}（构建 ${BUILD}）"

VIProductVersion "${VERSION}.${BUILD}"
VIFileVersion "${VERSION}.${BUILD}"
VIAddVersionKey /LANG=2052 "ProductName" "${PRODUCT_NAME}"
VIAddVersionKey /LANG=2052 "ProductVersion" "${VERSION}.${BUILD}"
VIAddVersionKey /LANG=2052 "FileVersion" "${VERSION}.${BUILD}"
VIAddVersionKey /LANG=2052 "FileDescription" "${PRODUCT_NAME} 安装程序"
VIAddVersionKey /LANG=2052 "LegalCopyright" "Copyright (C) 2026 jacksonlin-infra"

!define MUI_ICON "${ICON}"
!define MUI_UNICON "${ICON}"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE "安装 ${PRODUCT_NAME} ${VERSION}"
!define MUI_WELCOMEPAGE_TEXT "在本机用豆包听写，把文字投送到 UU 远程或远程桌面中的远端输入框。$\r$\n$\r$\n只为当前用户安装，不需要管理员权限，不安装服务，不开机自启。$\r$\n$\r$\n如果旧版本正在系统托盘运行，请先从托盘退出。"
!define MUI_FINISHPAGE_RUN "$INSTDIR\${EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "运行 ${PRODUCT_NAME}"
!define MUI_FINISHPAGE_TEXT "${PRODUCT_NAME} 已安装。运行后驻留在系统托盘，首次使用会显示准备清单。"
!define MUI_UNCONFIRMPAGE_TEXT_TOP "将卸载 ${PRODUCT_NAME}。设置（%LOCALAPPDATA%\RemoteVoiceInput）会保留，重新安装后继续使用。"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"

; Replacing the EXE of a running tray instance would fail or leave the old
; process listening; ask the user to quit it first (silent mode just stops).
!macro WaitForExit
  retry:
    System::Call 'kernel32::OpenMutexW(i 0x00100000, i 0, w "${INSTANCE_MUTEX}") p .r0'
    ${If} $0 <> 0
      System::Call 'kernel32::CloseHandle(p r0)'
      IfSilent +2
      MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "${PRODUCT_NAME} 正在系统托盘运行。$\r$\n请在托盘图标的菜单中选择“退出”，再点“重试”。" IDRETRY retry
      SetErrorLevel 2
      Quit
    ${EndIf}
!macroend

Function .onInit
  !insertmacro WaitForExit
FunctionEnd

Function un.onInit
  !insertmacro WaitForExit
FunctionEnd

Section "${PRODUCT_NAME}（必需）" SecMain
  SectionIn RO
  SetOutPath "$INSTDIR"
  ; A just-closed tray instance can keep the EXE locked for a moment. Silent mode
  ; would skip a locked file and still report success, leaving the old version.
  StrCpy $1 0
  replace_exe:
    Delete "$INSTDIR\${EXE}"
    IfFileExists "$INSTDIR\${EXE}" 0 exe_free
    IntOp $1 $1 + 1
    IntCmp $1 20 exe_locked
    Sleep 250
    Goto replace_exe
  exe_locked:
    IfSilent exe_failed
    MessageBox MB_ICONSTOP "无法替换旧的 ${EXE}（文件仍被占用）。请从托盘退出 ${PRODUCT_NAME} 后重新运行安装程序。"
    Goto exe_failed
  exe_free:
  ClearErrors
  File "${SOURCE_DIR}\${EXE}"
  IfErrors 0 exe_written
    IfSilent exe_failed
    MessageBox MB_ICONSTOP "无法写入 ${EXE}，安装未完成。"
  exe_failed:
    SetErrorLevel 3
    Abort
  exe_written:
  File "${SOURCE_DIR}\build-info.json"
  File "${SOURCE_DIR}\*.md"
  File "${SOURCE_DIR}\*.txt"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateShortcut "$SMPROGRAMS\${PRODUCT_NAME}.lnk" "$INSTDIR\${EXE}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "${PRODUCT_NAME}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${VERSION}（构建 ${BUILD}）"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "jacksonlin-infra"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\${EXE}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "URLInfoAbout" "https://github.com/jacksonlin-infra/remote-voiceime"
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "EstimatedSize" $0
SectionEnd

Section "桌面快捷方式" SecDesktop
  CreateShortcut "$DESKTOP\${PRODUCT_NAME}.lnk" "$INSTDIR\${EXE}"
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SecMain} "程序、使用指南与 .NET 运行时许可证（内置运行时，无需另装 .NET）。"
  !insertmacro MUI_DESCRIPTION_TEXT ${SecDesktop} "在桌面放一个启动图标。开始菜单中始终会有。"
!insertmacro MUI_FUNCTION_DESCRIPTION_END

Section "Uninstall"
  Delete "$INSTDIR\${EXE}"
  Delete "$INSTDIR\build-info.json"
  Delete "$INSTDIR\*.md"
  Delete "$INSTDIR\*.txt"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  Delete "$SMPROGRAMS\${PRODUCT_NAME}.lnk"
  Delete "$DESKTOP\${PRODUCT_NAME}.lnk"
  DeleteRegKey HKCU "${UNINSTALL_KEY}"
SectionEnd
