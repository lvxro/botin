@echo off
chcp 65001 >nul
title Compilando Botín
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo Falta el SDK de .NET 10: https://dotnet.microsoft.com/download/dotnet/10.0
  pause
  exit /b 1
)

echo Compilando (1 o 2 minutos la primera vez)...
dotnet publish src\Botin\Botin.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist
if errorlevel 1 (
  echo La compilación falló. Mirá el mensaje de arriba.
  pause
  exit /b 1
)
echo.
echo Listo: %~dp0dist\Botin.exe
explorer dist
