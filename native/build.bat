@echo off
setlocal
rem Builds WotRDLSS.dll (native NGX bridge for WotRDLSS). Needs the Visual Studio C++ build tools and NVIDIA's DLSS SDK, which is not
rem included in this repository (github.com/NVIDIA/DLSS). Optional overrides, set before running:
rem   DLSS_SDK  folder of the DLSS SDK           (default: ..\ThirdParty\DLSS, next to this folder)
rem   VCVARS    full path of vcvars64.bat        (default: the latest Visual Studio found with vswhere)
pushd "%~dp0"

if not defined DLSS_SDK set "DLSS_SDK=%~dp0..\..\ThirdParty\DLSS"
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not defined VCVARS if exist "%VSWHERE%" for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VCVARS=%%i\VC\Auxiliary\Build\vcvars64.bat"

if not defined VCVARS goto novs
if not exist "%VCVARS%" goto novs
if not exist "%DLSS_SDK%\include\nvsdk_ngx.h" goto nosdk

call "%VCVARS%" >nul || goto fail
if not exist bin mkdir bin
cl /nologo /LD /O2 /MD /EHsc /W3 /I"%DLSS_SDK%\include" WotRDLSS.cpp /Fe:bin\WotRDLSSNative.dll /Fo:bin\ /link /LIBPATH:"%DLSS_SDK%\lib\Windows_x86_64\x64" nvsdk_ngx_d.lib d3d11.lib dxgi.lib advapi32.lib user32.lib d3dcompiler.lib || goto fail
echo BUILD OK
popd
exit /b 0

:novs
echo Visual Studio C++ build tools not found. Install them, or set VCVARS to the path of vcvars64.bat.
goto fail
:nosdk
echo NVIDIA DLSS SDK not found at "%DLSS_SDK%". Download it from github.com/NVIDIA/DLSS and set DLSS_SDK to its folder.
:fail
popd
exit /b 1
