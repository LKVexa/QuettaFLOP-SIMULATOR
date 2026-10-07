@echo off
setlocal EnableExtensions EnableDelayedExpansion
title Lunar127 / QuettaFLOP-SIMULATOR - GitHub Publisher
color 0B

rem ============================================================
rem Lunar127 1.1.4 - GitHub Publisher
rem Repository: https://github.com/LKVexa/QuettaFLOP-SIMULATOR
rem
rem Put this .cmd in the ROOT of the repository you want to push.
rem It always operates from its own folder (%~dp0).
rem It does NOT change your global Git identity.
rem ============================================================

cd /d "%~dp0"
set "ROOT=%CD%"
set "REPO_URL=https://github.com/LKVexa/QuettaFLOP-SIMULATOR.git"
set "BRANCH=main"
set "LOG=%ROOT%\GITHUB_PUBLISH.log"

> "%LOG%" echo ============================================================
>>"%LOG%" echo QuettaFLOP-SIMULATOR GitHub Publisher
>>"%LOG%" echo Started: %DATE% %TIME%
>>"%LOG%" echo Root: %ROOT%
>>"%LOG%" echo Repository: %REPO_URL%
>>"%LOG%" echo ============================================================

echo.
echo ============================================================
echo  QUETTAFLOP-SIMULATOR - GitHub Publisher
echo ============================================================
echo  Repository:
echo    %REPO_URL%
echo.
echo  Local root:
echo    %ROOT%
echo.
echo  This window stays open on success or failure.
echo  Log:
echo    "%LOG%"
echo ============================================================
echo.

rem ------------------------------------------------------------
rem 1. Verify Git
rem ------------------------------------------------------------
where git >nul 2>&1
if errorlevel 1 (
    echo [FAIL] Git was not found in PATH.
    echo [FAIL] Install Git for Windows, then run this publisher again.
    >>"%LOG%" echo [FAIL] Git was not found in PATH.
    goto :FAIL
)

for /f "delims=" %%G in ('git --version 2^>nul') do set "GIT_VERSION=%%G"
echo [OK] !GIT_VERSION!
>>"%LOG%" echo [OK] !GIT_VERSION!

rem ------------------------------------------------------------
rem 2. Initialize repository if needed
rem ------------------------------------------------------------
if not exist ".git\" (
    echo [INFO] Initializing Git repository...
    >>"%LOG%" echo [INFO] Initializing Git repository...
    git init >>"%LOG%" 2>&1
    if errorlevel 1 (
        echo [FAIL] git init failed.
        goto :FAIL
    )
) else (
    echo [OK] Existing .git repository detected.
    >>"%LOG%" echo [OK] Existing .git repository detected.
)

rem ------------------------------------------------------------
rem 3. Configure commit identity locally only
rem ------------------------------------------------------------
for /f "usebackq delims=" %%A in (`git config --local user.name 2^>nul`) do set "AUTHOR_NAME=%%A"
for /f "usebackq delims=" %%A in (`git config --local user.email 2^>nul`) do set "AUTHOR_EMAIL=%%A"

if not defined AUTHOR_NAME (
    echo.
    echo Git needs a commit author name for THIS repository only.
    set /p "AUTHOR_NAME=Git author name: "
    if not defined AUTHOR_NAME (
        echo [FAIL] Author name cannot be blank.
        >>"%LOG%" echo [FAIL] Author name was blank.
        goto :FAIL
    )
    git config --local user.name "!AUTHOR_NAME!" >>"%LOG%" 2>&1
    if errorlevel 1 (
        echo [FAIL] Could not set repository-local author name.
        goto :FAIL
    )
)

if not defined AUTHOR_EMAIL (
    echo.
    echo Git needs a commit author email for THIS repository only.
    echo You may use your GitHub noreply email if preferred.
    set /p "AUTHOR_EMAIL=Git author email: "
    if not defined AUTHOR_EMAIL (
        echo [FAIL] Author email cannot be blank.
        >>"%LOG%" echo [FAIL] Author email was blank.
        goto :FAIL
    )
    git config --local user.email "!AUTHOR_EMAIL!" >>"%LOG%" 2>&1
    if errorlevel 1 (
        echo [FAIL] Could not set repository-local author email.
        goto :FAIL
    )
)

echo [OK] Commit author: !AUTHOR_NAME! ^<!AUTHOR_EMAIL!^>
>>"%LOG%" echo [OK] Commit author: !AUTHOR_NAME! ^<!AUTHOR_EMAIL!^>

rem ------------------------------------------------------------
rem 4. Set / repair origin
rem ------------------------------------------------------------
git remote get-url origin >nul 2>&1
if errorlevel 1 (
    echo [INFO] Adding GitHub remote origin...
    >>"%LOG%" echo [INFO] Adding origin %REPO_URL%
    git remote add origin "%REPO_URL%" >>"%LOG%" 2>&1
    if errorlevel 1 (
        echo [FAIL] Could not add origin.
        goto :FAIL
    )
) else (
    for /f "usebackq delims=" %%R in (`git remote get-url origin 2^>nul`) do set "CURRENT_ORIGIN=%%R"
    if /I not "!CURRENT_ORIGIN!"=="%REPO_URL%" (
        echo [INFO] Existing origin points somewhere else:
        echo        !CURRENT_ORIGIN!
        echo [INFO] Repointing origin to:
        echo        %REPO_URL%
        >>"%LOG%" echo [INFO] Repointing origin from !CURRENT_ORIGIN! to %REPO_URL%
        git remote set-url origin "%REPO_URL%" >>"%LOG%" 2>&1
        if errorlevel 1 (
            echo [FAIL] Could not update origin.
            goto :FAIL
        )
    ) else (
        echo [OK] origin already points to the requested repository.
        >>"%LOG%" echo [OK] origin already correct.
    )
)

rem ------------------------------------------------------------
rem 5. Force local branch name to main
rem ------------------------------------------------------------
git branch -M "%BRANCH%" >>"%LOG%" 2>&1
if errorlevel 1 (
    echo [FAIL] Could not select branch "%BRANCH%".
    goto :FAIL
)
echo [OK] Branch: %BRANCH%

rem ------------------------------------------------------------
rem 6. Stage every file/folder recursively
rem ------------------------------------------------------------
echo.
echo [INFO] Staging repository files and folders recursively...
>>"%LOG%" echo [INFO] git add -A
git add -A >>"%LOG%" 2>&1
if errorlevel 1 (
    echo [FAIL] git add failed.
    goto :FAIL
)

rem Show staged paths so the user can see that individual files are included.
echo.
echo ---------------- STAGED FILES ----------------
git diff --cached --name-status
echo ------------------------------------------------
echo.
git diff --cached --name-status >>"%LOG%" 2>&1

rem ------------------------------------------------------------
rem 7. Commit only when something changed
rem ------------------------------------------------------------
git diff --cached --quiet
if errorlevel 1 (
    set "COMMIT_MSG=Lunar127 1.1.4 - Run Simulation randomized raster seed"
    echo [INFO] Creating commit:
    echo        !COMMIT_MSG!
    >>"%LOG%" echo [INFO] Creating commit: !COMMIT_MSG!
    git commit -m "!COMMIT_MSG!" >>"%LOG%" 2>&1
    if errorlevel 1 (
        echo [FAIL] Git could not create the commit.
        echo [INFO] See "%LOG%" for Git's exact message.
        goto :FAIL
    )
) else (
    echo [INFO] No new local changes to commit.
    >>"%LOG%" echo [INFO] No new local changes to commit.
)

rem ------------------------------------------------------------
rem 8. Push main
rem ------------------------------------------------------------
echo.
echo [INFO] Pushing "%BRANCH%" to GitHub...
echo [INFO] Git Credential Manager may open a browser for sign-in.
>>"%LOG%" echo [INFO] git push -u origin %BRANCH%

git push -u origin "%BRANCH%" >>"%LOG%" 2>&1
if errorlevel 1 (
    echo.
    echo [FAIL] GitHub push failed.
    echo.
    echo Common causes:
    echo   - GitHub authentication was cancelled or expired
    echo   - Large file exceeds GitHub's per-file size limit
    echo   - Remote contains commits not present locally
    echo   - Network access is unavailable
    echo.
    echo Exact Git output is in:
    echo   "%LOG%"
    goto :FAIL
)

rem ------------------------------------------------------------
rem 9. Success
rem ------------------------------------------------------------
echo.
echo ============================================================
echo  PUBLISH COMPLETE
echo ============================================================
echo  Repository:
echo    https://github.com/LKVexa/QuettaFLOP-SIMULATOR
echo.
echo  Branch:
echo    %BRANCH%
echo.
echo  Local root:
echo    %ROOT%
echo.
echo  Log:
echo    "%LOG%"
echo ============================================================
>>"%LOG%" echo [PASS] Publish complete: %DATE% %TIME%
echo.
pause
exit /b 0

:FAIL
echo.
echo ============================================================
echo  PUBLISH FAILED
echo ============================================================
echo  No global Git identity settings were changed.
echo  Review the log for the exact failure:
echo    "%LOG%"
echo ============================================================
>>"%LOG%" echo [FAIL] Publisher stopped: %DATE% %TIME%
echo.
pause
exit /b 1
