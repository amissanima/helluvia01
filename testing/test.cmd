@echo off
setlocal
if exist "%LocalAppData%\Programs\Python\Python314\python.exe" (
    "%LocalAppData%\Programs\Python\Python314\python.exe" -B "%~dp0run.py" %*
) else (
    py -3 -B "%~dp0run.py" %*
)
exit /b %errorlevel%
