# Treino longo do GraphExplorer (run `graph_19`)

Estado do sistema em 19/09/2026, montado para um run de **30M steps** deixado rodando sem
supervisão. Escrito para quem vai acompanhar o TensorBoard e decidir se mexe em alguma coisa.

## 1. O que o agente tem hoje

Base = commit `2992dd5` (seta roxa com alvo sorteado, spawn em nó aleatório, pré-visitados,
regiões removidas, peso por nó) + o que entrou depois:

| Peça | Onde | O que faz |
|---|---|---|
| **Seta roxa** (dica de fronteira) | `GraphExplorationMemory.UpdateFrontier`, `NavGraph.TryFindNearestUnvisited` | Direção + distância em arestas até um primário não-visitado, **sorteado entre os 3 mais próximos** (`_frontierCandidates`) e fixo até ser visitado. Observação, shaping (`_frontierProgressReward`, `_frontierApproachReward`) e gizmo magenta escalam **juntos** por `frontier_hint`. |
| Spawn em nó aleatório | `GraphArenaController._spawnAtRandomNode` | Nasce em cima de qualquer nó ativo (102 origens). |
| Pré-visitados | `previsited_fraction` → `GraphExplorationMemory.DrawPrevisited` | Fração dos primários já nasce visitada: fora do denominador da cobertura, visível como visitada para o agente e para a BFS. Disco azul-escuro no gizmo. |
| **Variação de peso** | `weight_jitter` → `GraphExplorationMemory.DrawEpisodeWeights` | Cada nó vale `autorado × U[1−j, 1+j]`, sorteado por episódio. Soma esperada não muda. |
| **Peso na observação** | `GraphExplorerManager.CollectObservations` | Por vizinho: direção X/Z, distância, visitado, **peso relativo ao nó mais valioso do episódio**, válido. Sem isto a variação de peso seria ruído que a rede não pode usar. |
| **Ping** | `GraphPingSystem` (no agente), `ping_interval` no currículo | Um primário "toca" por 3000 steps. **Com hider ligado, o ping é o passo do hider** (toda chegada dele num primário); sem hider, sorteio aleatório a cada `ping_interval`. O agente vê: ativo, distância em arestas, quente/frio — **sem direção**. Paga 0.1 por aresta de aproximação, +2 ao chegar, −0.5 se expirar. Farol rosa no gizmo. |
| **Hider** (etapa 1 da perseguição) | `GraphHider` (GameObject `Hider` na arena), `hider_mode` no currículo | Scriptado, anda de nó em nó a 3.5 m/s, para 0–100 steps em cada nó, nasce ≥ 6 arestas do seeker. Modos: 0 nenhum, 1 parado, 2 anda, 3 foge (maximiza distância em arestas quando o seeker chega a 12 m). O seeker só recebe o rastro (pings). Linha verde-azulada no gizmo até o nó para onde vai. |
| Vetor de observação | `VectorObservationSize` no prefab | **69** = 21 globais + 8 vizinhos × 6. Bloco [13..15] = ping. Os `.onnx` antigos (61) não servem. |
| Recompensa | `GraphRewardSystem` | `_nodeCoverageReward` 0.75 × peso do nó; `_newEdgeReward` 0.05 (só primário↔primário); `_fullCoverageReward` 5; existencial −2/episódio; parede e estagnação por step; shaping de fronteira × `frontier_hint`. |

Mapa (`NodeTraining.prefab`): 102 nós, **23 primários** (peso 1) + 79 auxiliares, 120
arestas, raios 1.8 / 3, `_maxNodeDistance` 35, `_maxEpisodeSteps` 8000.

## 2. Currículo: 5 lições, 6 parâmetros, mesmos critérios

Os seis parâmetros têm `completion_criteria` **idênticos** (o ML-Agents avalia cada um
sozinho; é a única forma de avançarem juntos). Mexeu num threshold, mexa nos outros cinco.

| Lição | `coverage_target` | `frontier_hint` | `frontier_hint_steps` | `previsited_fraction` | `weight_jitter` | `ping_interval` | `hider_mode` | threshold p/ sair |
|---|---|---|---|---|---|---|---|---|
| 0 Quarter | 0.3 | 1.0 | 0 (episódio inteiro) | 0.0 | 0.0 | 0 | 0 nenhum | 8.5 |
| 1 Half | 0.6 | 0.7 | 0 | 0.25 | 0.25 | 0 | 0 | 10.5 |
| 2 Most | 0.9 | 0.4 | 4000 | 0.5 | 0.5 | 4000 (ignorado: hider) | **2 anda** | 8.0 |
| 3 MostFaded | 0.9 | 0.2 | 2000 | 0.5 | 0.5 | 2000 (ignorado) | 2 anda | 7.5 |
| **4 NoHint** | 0.9 | **0.0** | — | 0.5 | 0.5 | 2000 (ignorado) | **3 foge** | (final) |

Os sete parâmetros têm critérios idênticos. Atenção: o ping é renda nova (+até 12/episódio),
e os thresholds das lições 2–3 foram calculados sem ele — se a `Most` passar em menos de
200k steps, suba 8.0 / 7.5 para ~11 / ~10 nos seis.

- `measure: reward`, `signal_smoothing: true`, `min_lesson_length: 150` episódios,
  `require_reset: true`.
- O threshold é avaliado **dentro da lição que ele encerra** e **não cresce** de lição em
  lição: os pré-visitados tiram renda enquanto a cobertura-alvo sobe. Conta detalhada nos
  comentários do YAML (teto ≈ 11.7 / 13.9 / 11 / 10.6).
- Thresholds são **estimativa** (o run 11 mediu 11.25 na lição 0, batendo com a conta de
  11.7). Se `Lesson Number` travar numa lição por mais de ~500k steps, o threshold dela está
  alto — abaixe nos seis.

## 3. A linha roxa some — como e quando

Em duas etapas:

1. **Dentro do episódio** (lições 2 e 3): `frontier_hint_steps` corta a dica depois de N steps
   de física — 4000 (metade do episódio) e depois 2000 (os primeiros 40 s). Depois do limite
   ela vai a zero: observação, shaping e gizmo. O agente termina sozinho. A vantagem sobre
   cortar a força: a política vê os dois regimes **no mesmo episódio**.
2. **Entre lições**: a força cai 1.0 → 0.7 → 0.4 → 0.2 → **0.0**. Na lição `NoHint` não existe
   linha nenhuma — e é lá que o run de 30M passa a maior parte do tempo, porque as quatro
   anteriores avançam por desempenho (algumas centenas de milhares de steps cada).

Como saber: `Environment/Lesson Number` no TensorBoard chegou a **4** = linha sumiu de vez. No
gizmo é a mesma coisa: a linha magenta só é desenhada enquanto a dica está sendo entregue à
rede (`FrontierHintVisible`), então "sem linha na tela" = "sem linha para o agente".

Por que ela pode ir a zero agora (no run 05 cortar para 0 derrubou a recompensa de +15 para
−3): a política tem vizinhos com **visitado + peso**, cobertura global, e viu o regime sem
seta dentro dos episódios das lições 2–3. Se mesmo assim a recompensa desabar ao entrar em
`NoHint` e não recuperar em ~1M steps, o threshold da lição 3 (7.5) promoveu cedo — suba-o.

## 3b. Ping — como funciona

- Liga na lição 2. Intervalo sorteado em 0.5×–1.5× do valor (não vira relógio); primeiro ping
  nunca antes de 1000 steps do episódio. Cada ping dura 3000 steps (60 s).
- Nó sorteado entre os primários ativos a ≥ 2 arestas do agente (`_minDistance`).
- Observação [13..15]: `ativo`, `distância/20`, `quente/frio` (+1 se a última troca de nó
  aproximou, −1 se afastou, 0 senão). Sem direção de propósito: dado, não resposta.
- Recompensa: `_pingApproachReward` 0.1 × (arestas ganhas) por decisão, com o mesmo ping;
  `_pingReachedReward` +2 ao chegar; `_pingMissedPenalty` −0.5 ao expirar. Não escala com
  `frontier_hint`.
- Gizmo: esfera + farol vertical **rosa** no nó que toca; some ao chegar/expirar.
- O que esperar: nas primeiras centenas de milhares de steps com ping ele vai ignorá-lo
  (−0.5 por expiração); depois deve começar a desviar da exploração quando o ping está perto
  (distância pequena) e ignorar quando está longe — o que é o comportamento certo, não um
  bug. Se ele nunca atender, suba `_pingReachedReward` para 3–4.

## 3c. Hider — etapa 1 da perseguição (rastro)

- A partir da lição 2 um `GraphHider` anda pelo grafo. Toda vez que ele **chega num primário**,
  aquele nó pinga (substitui o ping anterior sem contar como perdido). Nascer num primário já
  pinga. O sorteio aleatório de ping fica desligado enquanto o hider está ativo.
- O seeker recebe só o rastro (ativo / distância em arestas / quente-frio) — nunca a posição.
  Perseguir = ir atrás do ping que anda. Chegar no nó do ping paga +2 mesmo que o hider já
  tenha saído (era o rastro certo).
- Lição 4: o hider **foge** — ao ver o seeker a ≤ 12 m escolhe a saída que mais aumenta a
  distância em arestas. É onde "seguir o rastro" deixa de bastar e o seeker precisa cortar
  caminho.
- Ainda não existe **captura** nem **visão**: são a etapa 2 (slots [16..20]: vendo / já viu /
  direção + distância à última posição vista; captura a < 1.5 m encerra o episódio com bônus
  grande). O episódio continua terminando por cobertura ou timeout.
- Gizmo do hider: cápsula na cena + linha verde-azulada até o nó de destino.

## 4. Variação de peso — o que esperar

- Lição 0: peso autorado (todos 1). Lições 1+: cada nó vale entre 0.75–1.25× e depois
  0.5–1.5× por episódio.
- O agente enxerga o peso do vizinho (slot 5 de cada vizinho, 0..1 relativo ao mais valioso
  do episódio). O comportamento esperado é preferir, entre duas saídas não visitadas, a que
  vale mais — visível no gizmo comparando o tamanho do ponto do nó (o `NavNode` desenha o
  ponto escalado por √peso **autorado**; o peso do episódio não é desenhado).
- Como a soma esperada dos pesos não muda, os thresholds do currículo não mudam.

## 5. Comandos

```powershell
conda activate mlagents
cd "C:\Users\Arthur Lee\Unity\PROJECT-IA"
mlagents-learn config/graph_explorer_curriculum.yaml --run-id=graph_19
# cena: Assets/Scenes/Arthur/Nodes_2.unity → Play quando aparecer "Listening on port 5004"

# outro terminal
conda activate mlagents
cd "C:\Users\Arthur Lee\Unity\PROJECT-IA"
tensorboard --logdir results        # http://localhost:6006

# retomar / sobrescrever
mlagents-learn config/graph_explorer_curriculum.yaml --run-id=graph_19 --resume
mlagents-learn config/graph_explorer_curriculum.yaml --run-id=graph_19 --force
```

Duração: 30M steps a ~270 steps/s (9 arenas; o run 11 fez 140k em 527 s) ≈ **30 h**.
Checkpoints a cada 1M (`keep_checkpoints: 10`), então dá para parar e retomar. `Ctrl+C`
exporta `results/graph_19/GraphExplorer.onnx`.

Antes do Play, no Console: `ValidateSetup` não pode reclamar de `VectorObservationSize`
(69), e o bake do `NavGraph` não pode acusar grafo desconexo.

## 6. O que olhar no TensorBoard, e o que fazer

| Sinal | Bom | Ruim | Ação |
|---|---|---|---|
| `Environment/Lesson Number` | sobe 0→4 nas primeiras ~2–3M | travado numa lição > 500k steps | abaixar o threshold daquela lição (nos 5 parâmetros) |
| `Environment/Cumulative Reward` | sobe dentro de cada lição; cai um degrau na troca e recupera | cai na troca e **não** recupera em ~1M | threshold da lição anterior promoveu cedo — subir |
| `Environment/Episode Length` | cai ao longo de cada lição (termina por cobertura, não por timeout) | cravado em 1600 (= 8000 steps / período 5) | ele não está batendo o alvo: baixar `coverage_target` da lição para 0.8 ou `previsited_fraction` para 0.4 |
| `Policy/Entropy` | cai devagar | despenca cedo | subir `beta` (0.01 → 0.02) |

Atenção com o valor absoluto do reward: a lição `NoHint` não tem renda de shaping, então o
teto dela (~10) é **menor** que o da lição 1 (~14). Reward mais baixo em `NoHint` com
`Episode Length` igual ou menor = comportamento igual ou melhor, não pior.

## 7. Armadilhas

- `previsited 0.5` + `coverage 0.9` sobre o que resta: em 23 primários restam ~11 e ele precisa
  de 10. O último nó custa caro. Se `Episode Length` cravar em 1600 nas lições 2+, baixe o alvo.
- Nascer em cima de um primário registra o nó como visitado sem pagar (1 nó "de graça").
  Aceitável.
- `_newEdgeReward` só paga aresta primário↔primário e **não existe nenhuma** neste mapa (tudo
  passa por auxiliar). Termo morto; não conta no teto.
- Mudar `_neighborSlots`, `FloatsPerNeighbor` ou `GlobalObservations` invalida todos os
  `.onnx` e exige ajustar `VectorObservationSize` no prefab.
- `docs/graph/exploracao-local.md` (do commit `db9f983`) descreve o sistema **sem** seta e
  está desatualizado; foi removido no revert.

## 8. Plano da noite (`config/graph_overnight.yaml`, run `night_02`)

Config **por relógio** (`measure: progress`), 8M steps (~8,4 h com 9 arenas), para rodar sem
supervisão. **A seta roxa fica em 1.0 em todas as etapas** — decisão de 20/09: o fade não
funciona (baixar a força só reescala a observação; a rede nunca é obrigada a olhar os vizinhos
e, quando a seta some, não tem dado suficiente para sair de um beco). A seta é tratada como
conhecimento do mapa (grafo + memória própria). A versão sem seta — valor por saída na
observação + *dropout* de episódios inteiros sem seta — fica para outra branch (pergunta do TCC).

| Etapa | termina em | cobertura | pré-visitados | peso ± | hider | velocidade | o que aprende |
|---|---|---|---|---|---|---|---|
| E0 AndarAteNo | 0.5M | 0.3 | 0 | 0 | — | — | chegar num nó, seguir a seta, virar em porta |
| E1 ExplorarMais | 1.5M | 0.6 | 0.25 | 25% | — | — | preferir saída não visitada e de peso maior |
| E2 RastroParado | 3.0M | 0.9 | 0.5 | 50% | **parado** | — | ler o ping, ir até um nó que toca uma vez |
| E3 RastroQueAnda | 4.5M | 0.9 | 0.5 | 50% | **anda** | 1.0 | seguir um ping que pula de nó em nó; **ver** e se aproximar |
| E4 RastroRapido | 6.0M | 0.9 | 0.5 | 50% | anda | **2.2** | manter o rastro de algo quase tão rápido quanto ele |
| E5 Fuga | 8.0M | 0.9 | 0.5 | 50% | **foge** | **3.2** | cortar caminho em vez de só seguir o rastro |

**Visão** (`GraphHiderPerception`, slots `[16..20]`): cone 100° / 15 m + raycast contra `Wall`;
+0.5 ao avistar (cooldown 5 s), 0.05/m de aproximação enquanto vê. Sempre ligada. Sem captura.

```powershell
mlagents-learn config/graph_overnight.yaml --run-id=night_02
```

De manhã: `Lesson Number` em 5; como a seta não some, o reward **não** deve cair de etapa em
etapa (um degrau para cima na E3 é a renda nova de ping + visão, não o agente "melhorando");
se cair na E5, é a fuga. Checkpoints a cada 500k em `results/night_02/GraphExplorer/`.
