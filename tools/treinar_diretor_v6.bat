@echo off
rem Treino do diretor v6 no Anaconda Prompt (ambiente mlagents). Ctrl+C para parar: grava o Director.onnx.
rem Continuar depois de onde parou:  tools\treinar_diretor_v6.bat --resume
call C:\miniconda\Scripts\activate.bat C:\miniconda
call conda activate mlagents
cd /d "%~dp0.."
title Diretor v6 - treino (Ctrl+C para parar)
python tools/mlagents_learn_threads.py config/diretor_v6.yaml --run-id=diretor_v6_01 --env="Builds/Diretor_v6/PROJECT-IA.exe" --no-graphics --num-envs=8 --base-port=5030 %*
