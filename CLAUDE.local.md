If you can create sub-agents to do simple task, use them, use less powerfull models to do simple work
# PROJECT-IA

## Visão geral para quem não programa

Este projeto é um **jogo em Unity** (um escritório meio sombrio, visto em primeira pessoa) usado como laboratório
para **ensinar personagens controlados por computador a se comportar sozinhos**, sem ninguém programar cada passo.
A técnica é *aprendizado por reforço*: o personagem (chamamos de **agente**) começa sem saber nada, tenta coisas,
ganha "pontos" quando faz algo bom e perde quando faz algo ruim. Depois de milhões de tentativas ele descobre
sozinho uma estratégia que rende mais pontos. O resultado desse treino é um arquivo (`.onnx`) — o "cérebro"
treinado — que depois é colocado dentro do jogo para o personagem agir em tempo real.

O treino acontece em duas metades que conversam entre si:

- **Unity (o jogo)** — simula o mundo: paredes, física, o corpo do agente, e calcula os pontos. Para treinar mais
  rápido, a cena tem 9 cópias do mesmo mapa rodando ao mesmo tempo.
- **Python (o treinador, via ML-Agents)** — recebe do Unity o que o agente "vê" e os pontos que ganhou, ajusta o
  cérebro e devolve a próxima ação. Roda no terminal (Anaconda PowerShell) enquanto o jogo está em Play.

Existem dois agentes aqui:

1. **Seeker** (versão anterior) — um "pegador": procura e persegue outro personagem (o Hider) pelo mapa.
2. **GraphExplorer** (versão atual, em desenvolvimento) — um explorador: o objetivo é **conhecer o mapa inteiro**.
   Para ajudá-lo, o mapa é descrito por uma rede de **pontos de referência** (nós) ligados entre si, colocados à
   mão nas salas e corredores, como um mapa de metrô. Os pontos nos vãos são **portas**, e o mapa é lido como
   **salas ligadas por portas**. O agente ganha pontos ao explorar 80% de uma sala (toda sala vale igual), ao
   passar por uma porta (cada vez que repete a mesma porta vale metade) e ao sair de uma sala já explorada; perde
   pontos por encostar em paredes, ficar parado ou demorar. Ele vê só a sala em que está e as portas dela, sem
   seta. Um "currículo" torna a tarefa gradualmente mais difícil: no começo basta explorar 20% das salas, no fim
   80%.

O que está em cada lugar: o código C# dos agentes fica em `Assets/Scripts/`, as cenas de treino em
`Assets/Scenes/Arthur/`, as regras de treino (hiperparâmetros e currículo) em `config/`, e os cérebros treinados
são os `.onnx` na raiz de `Assets/`.

## Treinar: comandos no Anaconda PowerShell

O Python do ML-Agents fica **fora do repositório**: ambiente conda `mlagents` (Miniconda em `C:\miniconda`,
Python 3.10.12) e um clone do ML-Agents em `C:\Users\Arthur Lee\ml-agents` (release_21, pacote `mlagents 1.0.0`).

**Preparar o ambiente (só na primeira vez, ou quando o ambiente estiver vazio):**

Use o **Anaconda Prompt** (menu Iniciar). Todo comando abaixo só funciona com `(mlagents)` no início do prompt —
janela nova exige `conda activate mlagents` de novo. Os comandos funcionam tanto no cmd quanto no PowerShell.

```powershell
conda create -n mlagents python=3.10.12 -y
conda activate mlagents
pip install numpy==1.21.6
# O ml-agents-envs do clone pina numpy==1.21.2, que NÃO tem pacote pronto para Python 3.10 no Windows: o pip
# tenta compilar do zero e falha por falta do Visual C++. A linha abaixo afrouxa o pin para a série 1.21-1.23.
python -c "p=r'C:\Users\Arthur Lee\ml-agents\ml-agents-envs\setup.py'; s=open(p,encoding='utf-8').read().replace('numpy==1.21.2','numpy>=1.21.2,<1.24'); open(p,'w',encoding='utf-8').write(s)"
cd "C:\Users\Arthur Lee\ml-agents"
python -m pip install ./ml-agents-envs
python -m pip install ./ml-agents
pip install setuptools==77.0.3                          # versões novas quebram o mlagents-learn
pip install onnxscript protobuf==3.20.3 "numpy<1.24"     # exportador do .onnx; onnxscript exige onnx>=1.17, então
                                                         # NÃO pine onnx==1.15.0 (conflito insolúvel no pip)
mlagents-learn --help                                    # se imprimir a ajuda, está pronto
```

O pip termina com um aviso vermelho de que `mlagents` pede `onnx==1.12.0` e `protobuf<3.20` — é esperado e não
impede nada; as versões instaladas são as que o exportador precisa. Ambiente montado assim em set/2026:
torch 2.14.0, onnx 1.17.0, onnxscript 0.7.2, numpy 1.21.6, protobuf 3.20.3.

**Treinar (toda vez):**

```powershell
conda activate mlagents
cd "C:\Users\Arthur Lee\Unity\PROJECT-IA"

# GraphExplorer (agente atual) — v5.2 (caça herdando o 50M da v5.1): mais rápido, rastro de 3 s, rota NavMesh,
# previsão ao perder de vista, pressa, hider fora do centro e em cantos sem nó. Build refeito antes (mudou código).
# Cena de treino: Assets/Scenes/Arthur/V6 - Training.unity (5 arenas "NodeTraining - V6 Training", com NavMeshSurface
# assado; monstro "SeekerAgentV2 - V5 training" + hider "PlayerDummy - V5 training"). Build: perfil "V6 - Training"
# (Assets/Settings/Build Profiles) -> Builds/V6/PROJECT-IA.exe. A V5 - Training (sem NavMesh) é da v5.1.
mlagents-learn config/graph_v5.2_caca.yaml --run-id=v5.2_caca_01 --initialize-from=v5.1_zero_01 --env="Builds/V6/PROJECT-IA.exe" --no-graphics --num-envs=3
# v5.1 (do zero, terminou em 50M em 07/10): config/historico/graph_v5.1_zero.yaml, run v5.1_zero_01

# Seeker
mlagents-learn config/seeker_curriculum.yaml --run-id=seeker_04

# continuar um run interrompido / sobrescrever um run com o mesmo nome
mlagents-learn config/graph_v5.1_zero.yaml --run-id=v5.1_zero_01 --resume
mlagents-learn config/graph_v5.1_zero.yaml --run-id=v5.1_zero_01 --force
```

Quando aparecer `Listening on port 5004. Start training by pressing the Play button in the Unity Editor`,
abra a cena de treino (`Assets/Scenes/Arthur/V5 - Training.unity` para o GraphExplorer) e aperte **Play**. Para parar,
`Ctrl+C` no terminal (o `.onnx` é exportado nesse momento) e depois Stop no Unity.

**Acompanhar o treino (em outro terminal):**

```powershell
conda activate mlagents
cd "C:\Users\Arthur Lee\Unity\PROJECT-IA"
tensorboard --logdir results
# abrir http://localhost:6006
```

Saída de cada run: `results/<run-id>/` (checkpoints, logs do TensorBoard, `configuration.yaml` usado) e o cérebro
final em `results/<run-id>/GraphExplorer.onnx` (ou `Seeker.onnx`). Copie o `.onnx` para `Assets/` e arraste no
campo *Model* do `Behavior Parameters` do prefab para usá-lo no jogo.

---

## Para quem vai mexer no código

Jogo de escritório/horror em primeira pessoa (Unity) com **agentes de RL treinados via ML-Agents**. O código de
gameplay (movimento, áudio, highlight) é pequeno; o peso do projeto está nos agentes e na infraestrutura de treino.

Duas linhas de agente convivem no repo:

| Linha | Pasta | Config de treino | Estado |
|---|---|---|---|
| **GraphExplorer** — explora o mapa sobre um grafo de nós autorados à mão | `Assets/Scripts/Graph/` | `config/graph_v5.1_zero.yaml` | **Atual** (branch `feature/v5-training`) |
| **Seeker** — persegue o `HiderAgent`, memória em grade regular | `Assets/Scripts/Seeker/` | `config/seeker_curriculum.yaml` | Anterior, ainda funcional |

`Assets/Scripts/SeekerAgent.cs` (na raiz de Scripts) é um protótipo antigo e **não** é o agente atual — ignore-o.

Repo de time (`leotagliati/PROJECT-IA`), trabalho em branches `feature/*`, PRs para `main`. Comentários, commits e
nomes de menus são em **português** — mantenha assim.


## Stack

- Unity **6000.0.75f1**, URP 17, **ML-Agents 4.0.3** (pacote C#), Input System 1.19, ProBuilder 6.
- Um único `Assembly-CSharp` (sem asmdef). Sem testes automatizados, sem scripts de build/CI.
- O lado Python do ML-Agents (`mlagents-learn`) fica **fora do repo**, no ambiente conda `mlagents` — não está no
  PATH por padrão; `conda activate mlagents` antes de treinar (comandos completos na seção acima).
- `*.csproj`/`*.sln`, `Library/`, `Temp/`, `Logs/`, `results/` e `Assets/ML-Agents/Timers/` são gitignored.

## Mapa do repositório

```
Assets/
  Scripts/
    Graph/          GraphExplorer, por assunto (estrutura em docs/graph/arquitetura.md):
      Agent/        GraphExplorerManager (orquestra), GraphObservations (layout 188 + planta), GraphBodyTracker
                    (parede/batida/jitter/parado), GraphRewardSystem, GraphStepContext, GraphLocomotion, GraphAnimationSystem, GraphStamina (hider),
                    WorldAlignedSensor
      Memory/       GraphExplorationMemory (camada de nós + GraphRoomMemory, a camada de salas, serializada dentro)
      Hunt/         IGraphTarget, GraphHider, GraphPlayerTarget, GraphHiderPerception, GraphPingSystem, GraphSuspicionMap
      Arena/        GraphArenaController + GraphEpisodeSettings (o currículo do episódio)
      Map/          NavGraph (partial: .Rooms .Paths .Validation .Gizmos .Editor), NavNode, NavBlockedArea, GraphGizmos;
                    Placer/ = NavGraphPlacer (ferramenta de autoria)
    Seeker/         Seeker: SeekerManager, SeekerPerceptionSystem, SeekerExplorationMemory, SeekerRewardSystem,
                    SeekerStepContext, SeekerArenaController, SeekerMovementSystem (reusado pelo GraphExplorer)
    HiderAgent.cs   NPC scriptado (anda em cardinais, vira ao bater em parede) — presa do Seeker
    AudioSystem/    Pool estático de AudioSources 3D: AudioSystem.Play("id", x, z); AudioEmitter dispara por trigger
    Highlight/      Outline por raycast (ObjectHighlighter + HighlightTarget), shader em Assets/Shaders
    InputSystem/    PlayerInputActions (gerado pelo Input System) + MouseController
    MovementSystem/ Controle FPS do jogador (PlayerMovement, PlayerCamera, CameraJuice)
  Scenes/Arthur/    V5 - Training.unity = cena de treino atual (4 arenas "NodeTraining - V5 training", mapa v4);
                    Node_5/Node_6 = cenas da v4.x;
                    Nodes*, Node_0..4 = versões anteriores;
                    Map_1..8 = mapas do escritório / seeker. Leo_* na raiz de Scenes = testes de áudio do Leo
  Prefabs/          NodeTraining.prefab (arena completa do GraphExplorer), SeekerAgent, HiderAgent, Maps/, mobília
  *.onnx            Modelos exportados: GraphExplorer_v4.0 / _v4.1 (atual), GraphExplorer_node4_*, SeekerV1..V3
config/             YAMLs do mlagents-learn em uso (graph_v5.0_zero, seeker_curriculum); historico/ = currículos antigos,
                    só registro (o gerador escreve lá tudo que não é a versão atual)
results/            Saída do treino (gitignored): checkpoints, TensorBoard, configuration.yaml de cada run
```

## Arquitetura dos agentes (vale para as duas linhas)

Cada agente é um conjunto de componentes com uma responsabilidade cada, ligados no Inspector (com fallback por
`GetComponentInChildren`; no GraphExplorer a busca é no `Awake` do Manager, e podem estar no agente ou em filhos):

```
*Manager : Agent            único que conhece os callbacks do ML-Agents; ordem do step:
                            sentir -> observar -> agir -> avaliar -> terminar. NÃO calcula recompensa.
   -> monta *StepContext    struct com tudo que a recompensa precisa saber daquele step (Graph: preenchida por nome)
   -> *RewardSystem         calculadora PURA: EvaluateStep(context) -> delta. Todo o tuning mora aqui.
   *ExplorationMemory       estado de episódio, uma instância POR AGENTE (o que já visitei)
   *ArenaController         dono do ambiente, um POR ARENA: spawns, feedback (chão verde/vermelho),
                            lê o currículo via Academy.Instance.EnvironmentParameters.GetWithDefault
   SeekerMovementSystem     driver de Rigidbody sem regra de agente (Move / ResetMovement) — compartilhado
```

Regras derivadas disso:
- Novo sinal de recompensa = novo campo no `*StepContext` + termo no `*RewardSystem`. Nunca `AddReward` solto no Manager.
  No GraphExplorer até os bônus de término (captura, cobertura) são campos do contexto (`HiderCaught`/`CoverageReached`);
  no Seeker antigo o `HiderFoundReward` ainda é somado à parte.
- Novo parâmetro de currículo (GraphExplorer) = campo no `GraphEpisodeSettings` + `_defaultX` + uma linha no
  `GraphArenaController.ApplyCurriculum()` (o nome do YAML é constante ali). O Manager repassa `Settings` a quem usa.
  (Seeker antigo: campo `_xParameterName` + default no `SeekerArenaController`.)
- A cena de treino tem **várias cópias da arena** (9). Tudo é relativo à arena: grade do seeker em coordenadas
  locais, um `NavGraph` por cópia. Nunca use estado estático nem referências entre cópias.

## GraphExplorer em detalhe

- **SALAS E PORTAS (mapa v4, desde 01/10; plano e decisões em `docs/graph/salas-e-portas.md`)** — a exploração paga
  por SALA, não por nó. **Toda sala vale igual** (o peso por área/raio do nó saiu). `NodeKind.Door` ("Porta", era
  o Primary, valor 0) = o vão entre duas salas; `Auxiliary` = chão de sala; `Ping` = chão que pode tocar (legado).
  O `NavGraph` tira as portas no bake e cada pedaço conexo vira uma sala (`RoomOf`, `DoorsOfRoom`, `RoomsOfDoor`,
  `OtherRoom`); no NodeTraining5 são 27 salas e 37 portas (o anel S24 foi cortado em S24 + S26 em 05/10). Toda porta tem que ligar EXATAMENTE duas salas (o bake
  avisa). Menu de contexto do NavGraph (e "11." do placer): **Relatório de salas e portas**.
- **GraphRoomMemory** (camada de salas DENTRO da `GraphExplorationMemory`, não é componente): sala concluída com
  `room_complete_threshold` dos nós pisados ou VISTOS (v5: 1.0 = a sala inteira, em degrau 0.8 → 0.9 → 1.0 nas 3 primeiras lições; era 0.8); **novidade** da porta 1 → 0.5 → 0 por travessia (a 3ª não paga, `_doorPaidCrossings`) (sair por onde
  entrou = 2ª travessia = metade; sala de 1 porta: ida + volta = uma); **liberação** (`release_fraction`, 0 = off):
  com essa fração usada, a porta e a sala mais antigas voltam valendo `_releasedValue` 0.5; "quanto resta" por saída
  só DENTRO da sala. **Nada paga aproximar-se de alvo escolhido por algoritmo** (pedido do Arthur: a IA não pode
  "seguir um raio"); só eventos que o agente causa. `previsited_fraction` = fração das SALAS que já nasce concluída.
- **Recompensa de exploração** (`GraphRewardSystem`, "Salas e portas"; v5.1): sala paga UMA vez, ao concluir (0.5, sem
  fatias nem cauda), porta e saída de sala concluída só na 1ª travessia (0.1 e 0.15; beco: a volta paga igual),
  +5 ao concluir `coverage_target` (fração de SALAS). Tudo × `discovery_reward_scale`. Teto ~25 no mapa todo.
  **Valor crescente (v5, 04/10):** cada sala vale × (1 + `_progressValueGain` × fração concluída), 1 → ~2 — as que
  sobram por último são as caras que o run 02 pulava; meta das lições 2–4 = TODAS as salas. SAÍRAM: peso por nó, `weight_jitter`, patrulha por tempo, tédio de sala, seta (`frontier_hint*`), `_newEdgeReward`.
- **NavGraph** — um por arena. Bake idempotente (`EnsureBaked`), adjacência, salas, Dijkstra em metros (com versão
  "só dentro da sala": `SearchRoom`, `ScoreBeyond(..., room)`), validação de ligações contra a layer `Wall` com
  SphereCast (`_linkClearance` = metade da largura do agente). Menus de contexto: **Coletar nós filhos**,
  **Auto-ligar por linha de visão**, **Validar ligações**, **Relatório de salas e portas**.
  **Âncora** = o nó em que o agente "está" (`CurrentNodeIndex`, disco amarelo); as saídas observadas são as dela.
  `_anchorHysteresis` (0.5 m, Retângulo): a âncora só troca depois de o agente entrar 0.5 m no vizinho (ou metade
  dele, pelos nós de porta estreitos) — sem isso ela piscava na borda comum de dois ladrilhos.
- **Observações (188 floats, v5; até a v4.x eram 182)** = 36 globais + 8 vizinhos × 10 + 8 portas × 9. Globais: [7] fração de salas
  concluídas; [8] encostado em parede; [9..12] sala atual (progresso, concluída, estou num vão, nº de portas/8);
  [13..15] **ping**; [16..20] **visão**; [21] calor da sala (0 até a fase de ping); [22..23] para onde o corpo olha;
  [24..25] velocidade do hider; [26] **velocidade do meu estado** ÷ 10.2; [27..29] **procura**; [30..35] **meu corpo,
  estado e tempo** (velocidade própria X/Z ÷ 10.2, em perseguição, alerta restante, fração do episódio, tempo sem
  progresso ÷ 2500). Por vizinho: direção, distância, visitado,
  **quanto resta** e **quão perto** (dentro da sala), vim daqui, quantas vezes, suspeita, válido. Por porta da sala
  atual (ordem por ângulo em volta do centro da sala): direção, distância, distância pelo grafo, **novidade**,
  entrei por aqui, sala do outro lado concluída, calor, válido. Layout no cabeçalho de `GraphObservations.cs`.
  Raios de parede num filho `RaysWorld` com `WorldAlignedSensor` (presos ao mundo, 360°).
- **Captura** (hider a < 2.5 m): +20 + `_hiderCaughtEarlyBonus` (25) × fração do episódio que sobrava — pegar encerra o
  episódio e cortaria a renda de patrulha. Caça: `config/graph_node4_hunt.yaml` com `--initialize-from` do run de patrulha.
- **Revisita precoce** (loop, sempre ligada): chegada em PORTA pisada há < 15 s; custa −0.05 só a partir da 4ª
  seguida (`_earlyRevisitGrace`), para não punir voltar de beco.
- **Parede e suavidade** (`GraphRewardSystem`): contínuo 0.0006/step encostado (v5; era 0.0015 com o episódio de 8000) + **batida** (início de contato,
  `GraphBodyTracker`) 0.06 × batidas nos últimos 5 s (até ×5) + **suavidade** 0.0002 × |Δação|² por decisão (contra o
  giro de "beyblade"; preferido a pagar por ir reto, que seria farmável). Métricas `Exploration/WallHits`,
  `Movement/ActionJitter`; o olhar tem a própria suavidade (0.0001 × |Δolhar|², `Movement/LookJitter`).
  **Layers (v5, 06/10):** tudo com collider na arena é **Wall** (paredes, cantos, móveis E as peças `Door_Hole`:
  custo cheio). Até 06/10 as `Door_Hole` ficavam na layer **Door** com contato a 25% (`_doorPenaltyScale`), e o
  monstro raspava o batente e ficava preso na porta; a layer `Door`/`_doorLayer` segue no código, mas nada a usa.
  Chão e teto ficam fora (na layer de parede o teste de corpo do NavGraph bloquearia tudo). Botão:
  `GraphArenaController` ⋮ → "Ajustar layers das paredes (Wall)". Tudo por LAYER (visão, raios, grafo, contato);
  tag só identifica o jogador (`Goal`). `WallContactFraction` mede parede (`DoorContactFraction` fica em 0).
  `Movement/IdleFraction` = fração do episódio abaixo de 0.5 m/s (parar é permitido; a métrica pega o "fico quieto").
  `Exploration/AnchorFlicker` = pisca-pisca de âncora na borda de ladrilho; `_logLoopNodes` loga os nós mais repetidos.
- **Procura** (`GraphSuspicionMap`, um por agente, só com hider; `docs/graph/procura-e-ping.md`): crença de onde o
  hider está (probabilidade por nó, soma 1). Espalha pelas arestas na velocidade suposta dele, zera nos nós que o
  seeker vê (`GraphHiderPerception.CanSeePoint`) ou pisa, concentra 90% no nó do ping; zerou tudo → uniforme no que
  não está vendo. **Vendo o hider (06/10), a crença fica ZERADA** (nada paga, nenhuma sala reabre); ao perder de
  vista ela nasce no anel de nós mais próximo FORA da vista a partir de onde ele sumiu, com peso extra na direção
  em que corria (`_headingBias` 2), e espalha daí. Antes ficava 100% no nó dele e as salas vizinhas reabriam
  valendo até 8x: renda maior que pegar (reward ~495 e Rooms/Completed 146 na HiderParado do v5.1_zero_01). Paga `_suspicionClearedReward` 2 × massa zerada (carência de 10 s por
  nó contra ficar olhando o mesmo lugar). Gizmo: barras vermelho-escuras. `hider_noise` (currículo) = chance de
  cada chegada do hider virar ping; `ping_reward_scale`/`discovery_reward_scale` escalam ping e descoberta por
  config (0 e 0.3 na procura: ping só informa). Config: `config/graph_node4_search.yaml` (gerado).
- **Histórico de runs**: `docs/graph/historico-de-treinos.md` (uma entrada por fase: mudanças, config, resultado;
  LOCAL, gitignored). Resumo legível e versionado para o time: `docs/graph/fases-de-treino.md` — ao fechar uma
  fase/etapa, atualize os dois; tentativa que falhou ou foi abandonada entra na seção "O que não deu certo". Todo o resto de `docs/`, `CLAUDE.md` e `.claude/` é só local.
  **Versões**: mapa v4 = família v4.x (ajuste na mesma tarefa = v4.x; v5 só com mapa ou forma de treinar nova).
  v4.0 = `v4_s1_01` + `v4_noite_01`; v4.1 = planta de salas (`v4.1_noite_01`, ex-"v4b", modelo atual
  `GraphExplorer_v4.1.onnx`); v4.2 = do zero contra a parede (`v4.2_noite_01`, ex-"v5", abandonada); v4.3 = caça
  herdando a v4.1 (`config/graph_v4.3_caca.yaml`, modelo `GraphExplorer_v4.3.onnx`); v4.4 = corrida herdando a v4.3 (`config/graph_v4.4_corrida.yaml`); v4.5 = fuga com hider de mais estamina herdando a v4.4 (`config/graph_v4.5_fuga.yaml`). v4.4 e v4.5 NÃO rodaram: viraram a **v5.0** (`config/historico/graph_v5.0_zero.yaml`, run `v5.0_zero_02`, do zero, prefab novo, corpo do jogador, vetor 188; abandonada em 3.15M por decorar um loop de salas baratas). **v5.1** (`config/graph_v5.1_zero.yaml`, run `v5.1_zero_01`, do zero, noite de 04/10): sala e porta pagam uma vez, meta = todas as salas, valor crescente, gamma 0.998. Config `graph_vX.Y_<tarefa>.yaml`, run `vX.Y_<tarefa>_NN`,
  modelo `Assets/GraphExplorer_vX.Y.onnx`.
  Os currículos são GERADOS por `tools/gen_full_curriculum.py` — edite lá. **Caminho atual: v5.1** (`V5_1_STAGES`,
  um run só do zero: Inicio → Meio → Completo → Patrulha → Ping → Hider parado/anda/foge/rápido/jogador; critério no
  cabeçalho do YAML). O gerador escreve tudo que não é a versão atual em `config/historico/` (só registro).
  Treino rápido: build standalone em `Builds/V5/PROJECT-IA.exe` (gitignored, perfil `Assets/Settings/Build Profiles/
  V5 Training`) com `--env="Builds/V5/PROJECT-IA.exe" --no-graphics --num-envs=3` (~41 s/10k vs ~78 s no Editor);
  mudou código/prefab = build de novo. Ctrl+C para parar (grava a lição para o `--resume`; fechar no X perde).
- **Corpo medido**: `NavGraph._useAgentBodySize` tira a folga de passagem/spawn do `CapsuleCollider` do agente da
  arena (raio × maior escala X/Z + margem). `_linkClearance`/`_spawnClearance` só valem com ele desligado.
- **Visão** (`GraphHiderPerception`, um por agente): cone `_viewAngle` 100° / `_viewDistance` 15 m + raycast contra
  `Wall` (só parede bloqueia). **Visão do ALVO (06/10, `CanSeeTarget`; nós seguem com `CanSeePoint`):** já vendo, o
  cone abre para 160° / 20 m (`_lockedView*`); a < 4 m vê sem cone (`_closeSenseDistance`); 3 raios (centro + ombros,
  `_targetHalfWidth` 0.35); **rastro (09/10)**: depois de ver, segue "vendo" (posição real) sem linha livre por 3 s (`_trackMemorySeconds`); "sentir" através de parede foi recusado; era 0.5 s. Os raios do alvo
  saem do olho (`_eyeHeight` 2.78 na arena: a cápsula do monstro lá vai de 0.25 a 3.25 m) e DESCEM até cabeça e
  peito do collider do alvo (`_targetHeadOffset`); mesa não esconde, armário alto esconde. Nós seguem com a linha
  horizontal na altura do olho. Raios de parede do sensor (`RaysWorld`): só Wall/Obstacle/Door (648), o jogador
  (Default) não aparece mais como obstáculo. Paga `_hiderSpottedReward` 2 ao avistar (cooldown `_respotCooldownSteps` 250) e
  `_hiderApproachReward` 0.4/m enquanto vê (só comparável se via nas duas decisões). Sempre ligada. Sem captura.
- **Ping** (`GraphPingSystem`, um por agente): a cada `ping_interval` steps de física (×U[0.5,1.5]) um primário
  aleatório a ≥ 2 arestas "toca" por 3000 steps; o agente vê ativo/distância/quente-frio, **sem direção**, e é
  pago por aresta de aproximação (0.1), chegada (+2) e expiração (−0.5). Não escala com a lição (é objetivo, não
  muleta). Farol rosa no gizmo. Distância via `NavGraph.TryFindPathTo`. **Com o alvo à vista (06/10) o ping não
  existe:** o ativo some sem contar como perdido (`Ping/Silenced`), as chegadas do hider não tocam e o calor da
  sala é apagado (`GraphRoomMemory.ClearHeat`); por isso a visão roda antes do ping no `FixedUpdate` do Manager.
  **Audição (06/10):** alvo CORRENDO (`IGraphTarget.IsRunning`; jogador: correr/pular) a até `_runHearingMeters` 30 m
  PELO GRAFO é ouvido: o ping vai para o nó dele e o segue enquanto é ouvido, e o seeker entra em Perseguição
  (`HeardRunning` → `GraphLocomotion.NotifyChaseCue`). Ping de alvo NÃO expira: ESMAECE (`Strength`, observação [13],
  meia-vida `_targetPingHalfLifeSteps` 500 = 10 s desde a última renovação) e só some quando outro toca, o seeker chega
  ou o alvo é visto (o aleatório segue com força 1 e expira em 3000). Métrica `Ping/Heard`.
  **Foco no alvo (`GraphObservations.FocusLevel`):** vendo = exploração e pistas zeradas; perdeu de vista há < 20 s
  (`GraphHiderPerception.IsSearching`, `_searchFocusSeconds`) = exploração zerada, suspeita/calor/ping valem; senão tudo.
- **Hider** (`GraphHider`, um por arena, **scriptado** — só o seeker treina): anda de nó em nó pelo grafo; ao
  chegar num primário deixa `PendingArrival`, que o `GraphPingSystem` consome e transforma em ping (o rastro).
  Com hider ligado o ping aleatório fica desligado. `hider_mode` no currículo: 0 nenhum / 1 parado / 2 anda /
  3 foge (maximiza distância em arestas ao seeker quando ele chega a `_fleeRadius`). Nasce ≥ 6 arestas do seeker.
  O seeker nunca recebe a posição dele. Não use o `HiderAgent.cs` antigo com o grafo (anda em cardinais, sem nós).
  Velocidade: `hider_speed` é a de CORRIDA (padrão 10.2 = o jogador); anda a 0.588× (6/10.2) e só corre fugindo,
  com fôlego (`GraphStamina`, `hider_stamina` em segundos).
- **Ações e locomoção** (v5, `GraphLocomotion`): 4 contínuas no referencial do mundo, o mesmo das observações de
  direção — `[0..1]` andar X/Z e `[2..3]` **olhar** X/Z. **Velocidade do JOGADOR** (PlayerDummy da main: moveSpeed 6,
  sprint ×1.7). SEM corrida por ação e sem fôlego: a velocidade vem do ESTADO DE ALERTA (`GraphLocomotion.Awareness`),
  que a política vê ([26], [32], [33]) — Patrulha 7 m/s (sem pista; era 6), Alerta 8.5 m/s (ouviu ping ou perdeu o alvo de
  vista há < `_alertSeconds` 10 s), Perseguição 10 m/s (VENDO o alvo, e segura 3 s depois de perder de vista,
  `_chaseHoldSeconds`, 06/10; o jogador corre a 10.2). |andar| < 0.1 = parado; acima anda na
  velocidade do estado. Inércia leve (acelera 20, freia 40 m/s²), giro do corpo 540°/s e 360°/s na perseguição, o
  corpo só anda para a frente (`SeekerMovementSystem.MoveFacing`), e o que a colisão tira da velocidade não volta de
  graça. O olhar vira só a CABEÇA (±60°, `_neckAngle`) e o cone de visão segue a cabeça. Animação:
  `GraphAnimationSystem` (molde do `SeekerAnimationSystem`; Animator `moveSpeed` 0 parado / 1 andar em patrulha e
  alerta / 2 correr na perseguição; desligado no treino). Se o jogador mudar de velocidade, mude as velocidades
  aqui e `GraphHider._speed`/`_walkFraction`. Métricas `Movement/ChaseFraction`, `AlertFraction`, `MeanSpeed`.
- **Modo de jogo** (`GraphArenaController._gameMode`): o seeker roda o `.onnx` em inferência determinística (o
  Manager ajusta o Behavior Parameters no Awake), a arena ignora o currículo (valem os `_default*`), o `GraphHider`
  sai de cena e o alvo vira o jogador, achado pela tag `_playerTag` ("Goal", a do PlayerDummy) e embrulhado num
  `GraphPlayerTarget` (nó atual + passos em nó de ping que viram ping: andar e correr 1.0, agachado 0). Visão, procura e ping
  falam com `IGraphTarget` (hider ou jogador). Sem timeout; só caça com o `GameManager` em Playing; pegar chama
  `GameManager.PlayerCaught()` (sem GameManager, renasce). Não ponha a tag do jogador no hider de treino: o alvo é
  achado no Initialize, antes de o hider ser desligado. `ChaseMusic`/`SeenPostFX` ainda ouvem só o Seeker antigo.
- **Anti-decoreba**: `GraphArenaController._spawnAtRandomNode` (nasce em qualquer nó ativo; v5.1: sala sorteada com peso 0.2 + (1 − taxa de conclusão), `_spawnFavorsRareRooms`; sala rara também vale até 2×, `GraphRoomMemory._rarityValueGain`; sala com ≥ 10 nós paga **migalhas** por nó novo antes de concluir, `_crumbMinNodes` 9/`_bigRoomCrumbReward` 0.5; taxa das salas começa em 1, senão todo resume inflava a reward) e `previsited_fraction`
  (salas que já nascem concluídas; saem do numerador e do denominador da cobertura e não pagam saída).
- **Gizmos**: NavGraph desenha estrutura (sempre) e o rótulo "S#" de cada sala depois do bake; GraphExplorationMemory
  desenha os nós pisados e a âncora; GraphRoomMemory desenha a novidade das portas (ciano → azul-escuro), "S# 3/4 ok"
  por sala — só em Play, legenda no cabeçalho de cada arquivo. Evite reutilizar
  verde/laranja/amarelo/magenta/branco/rosa/ciano em gizmos novos.

### Autorar um mapa novo (fluxo)

1. Duplique `NodeTraining.prefab` ou monte uma arena com `GraphArenaController` > `NavGraph` > nós.
2. Um nó `Porta` (ladrilho estreito) em cada vão e nós `Auxiliary` cobrindo o chão das salas/corredores.
3. Ligue os nós (na mão ou "Auto-ligar" + poda manual). Nenhuma ligação de chão pode passar reto por um vão: a
   porta tem que ser o único caminho entre as duas salas.
4. `NavGraph` > "Coletar nós filhos" (obrigatório após adicionar/remover nós), "Validar ligações" e
   "Relatório de salas e portas" (toda porta liga duas salas; máx. 8 portas por sala = os slots da observação).
5. Confira `_maxNodeDistance` (~ maior aresta do mapa). `_spawnPoints` só importa com `_spawnAtRandomNode`
   desligado.
6. Play e leia o Console: `ValidateSetup` e o bake avisam grafo desconexo, porta que não liga duas salas, vizinhos > slots,
   `VectorObservationSize` errado, referência vazada de outra arena.

## Treino e avaliação

Comandos completos na seção **Treinar: comandos no Anaconda PowerShell**, no topo. O que importa ao mexer:

- Behavior names: `GraphExplorer` e `Seeker` (têm que bater entre YAML e `Behavior Parameters` do prefab).
- Prefab de treino usa `DecisionRequester` com **Decision Period 5** e **Take Actions Between Decisions** ligado.
- Métricas que importam no TensorBoard: `Environment/Cumulative Reward`, `Environment/Episode Length`
  (7000 = timeout com os 35000 steps (700 s) da v5.1 e período 5; 4000 na v5.0; 1600 até a v4.x). Por sala: `Rooms/S00..S26` e `Environment/Lesson Number`.
- Os YAMLs explicam cada escolha de hiperparâmetro e a história dos runs anteriores — leia antes de mexer.

## Convenções de código

- Comentários em pt-BR e **explicam o porquê**, incluindo a conta (teto de penalidade, orçamento de recompensa,
  por que um valor antigo estava errado). Ao mudar um número, atualize o comentário que o justifica.
- Campos serializados: `[SerializeField] private float _camelCase`, agrupados por `[Header("-----Nome-----")]`.
  Propriedades públicas só de leitura expõem o que outros sistemas precisam.
- Referências Unity: `if (x == null)` explícito, **nunca** `??=`/`?.` (fake-null do Unity).
- Parede é identificada por **layer** (`LayerMask _wallLayer`, layer `Wall`), não por tag.
- Código só de editor (`[ContextMenu]`, `UnityEditor.Undo`) dentro de `#if UNITY_EDITOR`.
- Penalidades por step são raciocinadas pelo **teto** (valor × steps do episódio), comparado com a pressão
  existencial (-2/episódio). Recompensas positivas: some o total possível do mapa antes de treinar.
- Um sistema = uma responsabilidade; se um termo precisa de estado entre steps, ele mora no sistema dono desse estado,
  não no Manager.

## Armadilhas — antes de mudar X, leia Y

- `_maxEpisodeSteps`, `_stagnationSteps`, `StepsSinceProgress` são **steps de física** (FixedUpdate), não decisões.
  Ao mudar a duração do episódio, reescale `_wallContactPenalty` e `_stagnationPenalty` (tabela no cabeçalho de
  `GraphRewardSystem.cs`).
- Qualquer mudança no layout/tamanho das observações (`_neighborSlots`, `GlobalObservations`, ordem dos blocos)
  **invalida todos os `.onnx`** e exige ajustar `VectorObservationSize` no prefab. Use os blocos reservados antes de
  crescer o vetor.
- No currículo, todo parâmetro que muda de lição tem `completion_criteria` **idênticos de propósito** (o ML-Agents
  avalia cada parâmetro sozinho) — o `tools/gen_full_curriculum.py` garante isso. Parâmetros do mapa v4:
  `coverage_target` (fração de SALAS), `room_complete_threshold`, `previsited_fraction` (salas), `release_fraction`,
  `ping_interval`, `hider_mode`, `hider_speed`, `hider_stamina`, `hider_noise`, `ping_reward_scale`,
  `discovery_reward_scale`. Os thresholds em `reward` são estimativa (conta no cabeçalho do YAML).
- A fatia de sala é 1/⌈0.8 × nós da sala⌉: re-ladrilhar uma sala muda a fatia, não o total dela. Sala de 1 nó
  conclui ao entrar.
- Esqueceu "Coletar nós filhos" → nó aparece como ponto sem círculo e sem ligações no gizmo.
- `Heuristic` (dirigir com WASD) só compila com `ENABLE_LEGACY_INPUT_MANAGER`.
- `_episodeEndDelay` dos ArenaControllers é só debug visual — mantenha 0 para treinar.
- Erros de wiring do ML-Agents são silenciosos (treino que não converge). `ValidateSetup()` loga no Play — olhe o
  Console antes de iniciar um run longo.

## Verificação

Não há testes. O ciclo é: abrir a cena de treino (`Assets/Scenes/Arthur/V5 - Training.unity`), Play, conferir o Console
(sem erros de `ValidateSetup`/bake) e observar os gizmos com o agente selecionado. Para mudanças de recompensa ou
currículo, rode ~200k steps e compare as curvas do TensorBoard com o run anterior antes de commitar o ajuste.
