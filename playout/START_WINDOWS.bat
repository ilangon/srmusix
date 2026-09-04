@echo off
title SR MUSIX HD Playout Setup
cd /d "%~dp0"
where node >nul 2>nul || (echo Node.js is required. Install from https://nodejs.org & pause & exit /b 1)
if not exist node_modules call npm install
call npm start
