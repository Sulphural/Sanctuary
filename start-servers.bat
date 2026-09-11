@echo off
setlocal

rem Configuration to run: Debug (default, matches what's been tested) or Release
set "CONFIG=Debug"
set "TFM=net9.0"

set "ROOT=%~dp0"
set "SRC=%ROOT%src"

rem The database lives one level above this repo, not inside it.
pushd "%ROOT%.."
set "DBDIR=%CD%"
popd

set "DOTNET_ROLL_FORWARD=Major"
set "DOTNET_ENVIRONMENT=Production"
set "Database__Provider=Sqlite"
set "Database__ConnectionString=Data Source=%DBDIR%\sanctuary.db;"

echo Building solution (%CONFIG%)...
dotnet build "%SRC%\Sanctuary.slnx" -c %CONFIG% --nologo -v minimal
if errorlevel 1 (
    echo Build failed.
    pause
    exit /b 1
)

echo Starting Sanctuary.Login...
start "Sanctuary.Login" cmd /k "cd /d ""%SRC%\Sanctuary.Login\bin\%CONFIG%\%TFM%"" && dotnet Sanctuary.Login.dll"

rem Login runs the database migration on startup. Gateway checks the database
rem once with no retry and gives up if it loses that race, so wait for the
rem migration to finish before starting Gateway.
timeout /t 5 /nobreak >nul

echo Starting Sanctuary.Gateway...
start "Sanctuary.Gateway" cmd /k "cd /d ""%SRC%\Sanctuary.Gateway\bin\%CONFIG%\%TFM%"" && dotnet Sanctuary.Gateway.dll"

echo Starting Sanctuary.WebAPI...
start "Sanctuary.WebAPI" cmd /k "cd /d ""%SRC%\Sanctuary.WebAPI\bin\%CONFIG%\%TFM%"" && dotnet Sanctuary.WebAPI.dll"

echo.
echo All three servers are starting in separate windows.
echo Close a window (or Ctrl+C inside it) to stop that server.
endlocal
