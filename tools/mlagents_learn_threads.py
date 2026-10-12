# O mesmo "mlagents-learn", com mais threads do torch na atualização da rede.
#
# Por quê (12/10, v9): o ML-Agents limita o torch a min(núcleos lógicos / 2, 4) threads (mlagents/torch_utils/
# cpu_utils.py), e o gargalo do treino é a atualização da rede na CPU: medido no v9_zero_01, o treinador em ~400%
# (as 4 threads cheias) e os 10 jogos somando ~12%, esperando. Com o cérebro da v9 (planta de 64 salas, eventos,
# 8 chaves) a atualização ficou ~2.5x mais cara que na v8.1 (0.38 contra 0.9 M steps/h). O Ryzen 5 1600 tem 6
# núcleos físicos: 6 threads usam todos sem disputar com os jogos, que quase não pedem CPU.
# Só muda a VELOCIDADE: a matemática do PPO é a mesma; dá para trocar no meio de um run (Ctrl+C e --resume).
#
# Uso (Anaconda Prompt, ambiente mlagents, na raiz do projeto), com os MESMOS argumentos do mlagents-learn:
#   python tools/mlagents_learn_threads.py config/free_v9.yaml --run-id=v9_zero_01 --env=Builds/V9/PROJECT-IA.exe --no-graphics --num-envs=10 --base-port=5010 --resume
# Outro número de threads: variável TORCH_THREADS (set TORCH_THREADS=5 antes).
import os
import sys

import torch
from mlagents.trainers.learn import main

if __name__ == "__main__":
    # Depois do import do ML-Agents, que já chamou o set_num_threads(4): este vale.
    threads = int(os.environ.get("TORCH_THREADS", "6"))
    torch.set_num_threads(threads)
    print(f"[mlagents_learn_threads] torch com {torch.get_num_threads()} threads")
    sys.exit(main())
