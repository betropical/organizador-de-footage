@echo off
rem Organizador de Footage (Premiere) - @betoviedo
rem Instala el panel en modo desarrollo (Windows): lo copia a las extensiones de Adobe
rem y habilita extensiones sin firmar (PlayerDebugMode). Para desinstalar: instalar-dev.bat /quitar

set "DEST=%APPDATA%\Adobe\CEP\extensions\com.betoviedo.organizadorfootage"

if /i "%~1"=="/quitar" (
  rmdir /s /q "%DEST%" 2>nul
  echo Panel quitado.
  goto :fin
)

for %%v in (8 9 10 11 12 13) do reg add "HKCU\Software\Adobe\CSXS.%%v" /v PlayerDebugMode /t REG_SZ /d 1 /f >nul
rmdir /s /q "%DEST%" 2>nul
xcopy "%~dp0*" "%DEST%\" /E /I /Q /Y >nul
echo Listo. Reinicia Premiere y abri Ventana ^> Extensiones ^> Organizador de Footage.

:fin
pause
