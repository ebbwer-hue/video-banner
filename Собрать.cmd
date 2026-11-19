@echo off
chcp 65001 >nul
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /out:ВидеоБаннер.exe /win32manifest:src\VideoBanner.manifest /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll src\Engine.cs src\App.cs
if errorlevel 1 pause
