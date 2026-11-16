@echo off
setlocal
set "DOTNET_ROOT_X64=%~dp0runtime"
set "DOTNET_ROOT=%~dp0runtime"
set "DOTNET_MULTILEVEL_LOOKUP=0"
set "PIXELCANVAS_OUTPUT_DIR=%~dp0output"
start "" "%~dp0app\PixelCanvas.exe"
endlocal
