@echo off
rem Treino do diretor v4 no Anaconda Prompt (ambiente mlagents). Ctrl+C para parar: grava o Director.onnx.
rem Continuar depois de onde parou:  tools\treinar_diretor_v4.bat --resume
call C:\miniconda\Scripts\activate.bat C:\miniconda
call conda activate mlagents
cd /d "%~dp0.."
title Diretor v4 - treino (Ctrl+C para parar)
python tools/mlagents_learn_threads.py config/diretor_v4.yaml --run-id=diretor_v4_01 --env="Builds/Diretor_v4/PROJECT-IA.exe" --no-graphics --num-envs=8 --base-port=5030 %*
