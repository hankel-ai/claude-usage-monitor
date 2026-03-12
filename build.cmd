@echo off
set DOTNET=%LOCALAPPDATA%\dotnet\dotnet.exe

if not exist "%DOTNET%" (
    echo ERROR: .NET 8 SDK not found at %DOTNET%
    echo Install it: https://dotnet.microsoft.com/download/dotnet/8.0
    exit /b 1
)

echo Using SDK: %DOTNET%
"%DOTNET%" %*
