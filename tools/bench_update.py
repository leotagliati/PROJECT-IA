# Mede quanto custa UMA atualização da rede do FreeExplorer neste PC (o gargalo do treino: a atualização roda na
# CPU, e os jogos ficam esperando). Serve para comparar PCs e configurações sem treinar: mesma rede, mesmo lote.
#
# Uso (Anaconda Prompt, ambiente mlagents, com NENHUM treino rodando no PC, senão a medida sai inflada):
#   python tools/bench_update.py            (6 threads)
#   python tools/bench_update.py 8          (8 threads, para o notebook de 8 núcleos)
# Compare o "ms por minibatch" entre os PCs: menor = mais rápido. Em 12/10, no Ryzen 5 1600 com o treino ligado (só
# para a proporção), a v9 com 64 salas custou 2.3x a v8.1, e com 32 salas voltou ao custo da v8.1.
import sys
import time

import torch
from mlagents.trainers.settings import NetworkSettings
from mlagents.trainers.torch_entities.networks import SimpleActor, ValueNetwork
from mlagents_envs.base_env import ActionSpec, DimensionProperty, ObservationSpec, ObservationType

THREADS = int(sys.argv[1]) if len(sys.argv) > 1 else 6
torch.set_num_threads(THREADS)


def spec(shape, dims):
    return ObservationSpec(shape=shape, dimension_property=dims, observation_type=ObservationType.DEFAULT, name="o")


# Os sensores do FreeExplorer: raios (27), vetor, e as listas Rooms (x8), Keys (x7) e Events (x17).
def sensors(vector, rooms, keys, events):
    flat = (DimensionProperty.NONE,)
    buf = (DimensionProperty.VARIABLE_SIZE, DimensionProperty.NONE)
    specs = [spec((27,), flat), spec((vector,), flat), spec((rooms, 8), buf)]
    if keys:
        specs.append(spec((keys, 7), buf))
    if events:
        specs.append(spec((events, 17), buf))
    return specs


def bench(name, specs, branches, batch=1024, reps=6):
    net = NetworkSettings(normalize=False, hidden_units=256, num_layers=2)
    actor = SimpleActor(specs, net, ActionSpec(4, tuple(branches)))
    critic = ValueNetwork(["extrinsic"], specs, net)
    optimizer = torch.optim.Adam(list(actor.parameters()) + list(critic.parameters()), lr=3e-4)
    obs = [torch.rand((batch,) + s.shape) for s in specs]
    masks = torch.ones((batch, sum(branches))) if branches else None
    times = []
    for i in range(reps):
        start = time.perf_counter()
        actions = actor.get_action_and_stats(obs, masks=masks)[0]
        stats = actor.get_stats(obs, actions, masks=masks)
        values, _ = critic.critic_pass(obs)
        loss = sum(v.mean() for v in stats.values() if torch.is_tensor(v)) + values["extrinsic"].mean()
        optimizer.zero_grad()
        loss.backward()
        optimizer.step()
        if i > 0:  # a 1ª aquece
            times.append(time.perf_counter() - start)
    ms = 1000 * sum(times) / len(times)
    print(f"  {name:40s} {ms:8.1f} ms por minibatch de {batch}")
    return ms


print(f"torch {torch.__version__}, {torch.get_num_threads()} threads")
bench("v8.1 (vetor 126, 32 salas, 4 chaves)", sensors(126, 32, 4, 0), [])
bench("v9 (vetor 200, 32 salas, 8 chaves, eventos)", sensors(200, 32, 8, 8), [3, 7, 13])
