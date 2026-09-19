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
   mão nas salas e corredores, como um mapa de metrô. Cada sala tem um "valor" de exploração. O agente ganha
   pontos ao chegar em pontos novos, entrar em salas novas e terminar de ver uma sala; perde pontos por encostar
   em paredes, ficar parado ou demorar. Um "currículo" torna a tarefa gradualmente mais difícil: no começo ele
   precisa cobrir 30% do mapa e recebe uma seta apontando um lugar não visitado próximo; no fim precisa
   cobrir 90% sem seta nenhuma, e o valor de cada ponto muda um pouco a cada tentativa.

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

# GraphExplorer (agente atual)
mlagents-learn config/graph_explorer_curriculum.yaml --run-id=graph_06

# Seeker
mlagents-learn config/seeker_curriculum.yaml --run-id=seeker_04

# continuar um run interrompido / sobrescrever um run com o mesmo nome
mlagents-learn config/graph_explorer_curriculum.yaml --run-id=graph_06 --resume
mlagents-learn config/graph_explorer_curriculum.yaml --run-id=graph_06 --force
```

Quando aparecer `Listening on port 5004. Start training by pressing the Play button in the Unity Editor`,
abra a cena de treino (`Assets/Scenes/Arthur/Nodes_2.unity` para o GraphExplorer) e aperte **Play**. Para parar,
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
| **GraphExplorer** — explora o mapa sobre um grafo de nós autorados à mão | `Assets/Scripts/Graph/` | `config/graph_explorer_curriculum.yaml` | **Atual** (branch `feature/node-exploration-vision`) |
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
    Graph/          GraphExplorer: NavGraph, NavNode, GraphExplorerManager, GraphPingSystem, GraphHider,
                    GraphHiderPerception,
                    GraphExplorationMemory, GraphRewardSystem, GraphStepContext, GraphArenaController, GraphGizmos
    Seeker/         Seeker: SeekerManager, SeekerPerceptionSystem, SeekerExplorationMemory, SeekerRewardSystem,
                    SeekerStepContext, SeekerArenaController, SeekerMovementSystem (reusado pelo GraphExplorer)
    HiderAgent.cs   NPC scriptado (anda em cardinais, vira ao bater em parede) — presa do Seeker
    AudioSystem/    Pool estático de AudioSources 3D: AudioSystem.Play("id", x, z); AudioEmitter dispara por trigger
    Highlight/      Outline por raycast (ObjectHighlighter + HighlightTarget), shader em Assets/Shaders
    InputSystem/    PlayerInputActions (gerado pelo Input System) + MouseController
    MovementSystem/ Controle FPS do jogador (PlayerMovement, PlayerCamera, CameraJuice)
  Scenes/Arthur/    Nodes_2.unity = cena de treino atual do GraphExplorer; Nodes*, Node_* = versões anteriores;
                    Map_1..8 = mapas do escritório / seeker. Leo_* na raiz de Scenes = testes de áudio do Leo
  Prefabs/          NodeTraining.prefab (arena completa do GraphExplorer), SeekerAgent, HiderAgent, Maps/, mobília
  *.onnx            Modelos exportados: GraphExplorer_05_1M/2M, GraphExplorerV1, SeekerV1..V3
config/             YAMLs do mlagents-learn (hiperparâmetros + currículo), com o raciocínio comentado inline
results/            Saída do treino (gitignored): checkpoints, TensorBoard, configuration.yaml de cada run
```

## Arquitetura dos agentes (vale para as duas linhas)

Cada agente é um conjunto de componentes com uma responsabilidade cada, ligados no Inspector (com fallback por
`GetComponentInChildren` no `Initialize`):

```
*Manager : Agent            único que conhece os callbacks do ML-Agents; ordem do step:
                            sentir -> observar -> agir -> avaliar -> terminar. NÃO calcula recompensa.
   -> monta *StepContext    struct readonly com tudo que a recompensa precisa saber daquele step
   -> *RewardSystem         calculadora PURA: EvaluateStep(context) -> delta. Todo o tuning mora aqui.
   *ExplorationMemory       estado de episódio, uma instância POR AGENTE (o que já visitei)
   *ArenaController         dono do ambiente, um POR ARENA: spawns, feedback (chão verde/vermelho),
                            lê o currículo via Academy.Instance.EnvironmentParameters.GetWithDefault
   SeekerMovementSystem     driver de Rigidbody sem regra de agente (Move / ResetMovement) — compartilhado
```

Regras derivadas disso:
- Novo sinal de recompensa = novo campo no `*StepContext` + termo no `*RewardSystem`. Nunca `AddReward` solto no Manager
  (a única exceção existente é o bônus de término, `FullCoverageReward`/`HiderFoundReward`).
- Novo parâmetro de currículo = campo `_xParameterName` + default no `*ArenaController.ApplyCurriculum()`, exposto como
  propriedade lida pelo Manager ao montar o contexto.
- A cena de treino tem **várias cópias da arena** (9). Tudo é relativo à arena: grade do seeker em coordenadas
  locais, um `NavGraph` por cópia. Nunca use estado estático nem referências entre cópias.

## GraphExplorer em detalhe

- **NavNode** — posicionado à mão na cena. `Primary` = ponto de vantagem (paga cobertura, pode ser alvo da
  fronteira; raio apertado). `Auxiliary` = guia (não vale nada; raio generoso, só para o agente ter âncora no meio
  do corredor). Ligações declaradas de um lado só; o bake espelha.
- **Peso por nó** — cada `Primary` tem `_explorationWeight` (1 = referência); é o único lugar que diz quanto um
  ponto vale. Não existe mais agrupamento por região: adensar uma sala com mais primários **aumenta** quanto ela
  paga, então ao adensar reparta o peso entre os nós dela. Auxiliar ignora o campo. O bônus de entrar/concluir
  sala saiu junto com as regiões.
- **NavGraph** — um por arena. Bake idempotente (`EnsureBaked`), adjacência, BFS de fronteira (não-visitado mais
  próximo), validação de ligações contra a layer `Wall` com SphereCast (`_linkClearance` = metade da largura do
  agente). Menus de contexto: **Coletar nós filhos**, **Auto-ligar por linha de visão**, **Validar ligações**.
- **Observações (69 floats)** = 21 globais + 8 slots de vizinho × 6 (direção, distância, visitado, **peso** do
  vizinho relativo ao nó mais valioso do episódio, válido). Layout documentado em
  `GraphExplorerManager.cs` (cabeçalho). Bloco [13..15] = **ping** (ativo, distância em arestas, quente/frio);
  [16..20] = **visão** (vendo, já viu, direção + distância à última posição vista). Vizinhos ordenados por ângulo
  no mundo (ordem estável = slot com significado geométrico).
- **Visão** (`GraphHiderPerception`, um por agente): cone `_viewAngle` 100° / `_viewDistance` 15 m + raycast contra
  `Wall` (só parede bloqueia). Paga `_hiderSpottedReward` 0.5 ao avistar (cooldown `_respotCooldownSteps` 250) e
  `_hiderApproachReward` 0.05/m enquanto vê (só comparável se via nas duas decisões). Sempre ligada. Sem captura.
- **Ping** (`GraphPingSystem`, um por agente): a cada `ping_interval` steps de física (×U[0.5,1.5]) um primário
  aleatório a ≥ 2 arestas "toca" por 3000 steps; o agente vê ativo/distância/quente-frio, **sem direção**, e é
  pago por aresta de aproximação (0.1), chegada (+2) e expiração (−0.5). Não escala com a lição (é objetivo, não
  muleta). Farol rosa no gizmo. Distância via `NavGraph.TryFindPathTo`.
- **Hider** (`GraphHider`, um por arena, **scriptado** — só o seeker treina): anda de nó em nó pelo grafo; ao
  chegar num primário deixa `PendingArrival`, que o `GraphPingSystem` consome e transforma em ping (o rastro).
  Com hider ligado o ping aleatório fica desligado. `hider_mode` no currículo: 0 nenhum / 1 parado / 2 anda /
  3 foge (maximiza distância em arestas ao seeker quando ele chega a `_fleeRadius`). Nasce ≥ 6 arestas do seeker.
  O seeker nunca recebe a posição dele. Não use o `HiderAgent.cs` antigo com o grafo (anda em cardinais, sem nós).
- **Ações**: 2 contínuas (X, Z) no referencial do mundo, mesmo referencial das observações de direção.
- **Fronteira**: direção + distância em arestas até um não-visitado próximo, escalada por `frontier_hint` do
  currículo. O alvo é **sorteado entre os `_frontierCandidates` (3) mais próximos** e fica fixo até ser visitado
  (`GraphExplorationMemory`) — com 1 ele volta a ser determinístico e a política decora rotas. Observação, shaping
  e gizmo roxo são escalados **juntos** (são a mesma muleta) via `GraphExplorerManager.CurrentFrontierHint`. Dois
  eixos no currículo: `frontier_hint` (força: 1.0 → 0.7 → 0.4 → 0.2 → **0.0** na 5ª lição `NoHint`, onde o run
  longo passa a maior parte do tempo) e `frontier_hint_steps` (duração por episódio em steps de física; 0 =
  episódio inteiro; depois do limite a dica vai a zero e o agente termina sozinho).
- **Anti-decoreba** (variação por episódio, para a política aprender a regra e não a rota):
  `GraphArenaController._spawnAtRandomNode` (nasce em qualquer nó ativo, não nos `_spawnPoints`) e
  `previsited_fraction` do currículo (fração dos primários que já nasce marcada como visitada; sai do denominador
  da cobertura, aparece como visitada para o agente e para a BFS; disco azul-escuro no gizmo) e `weight_jitter`
  (peso de cada nó × U[1−j, 1+j] por episódio, `GraphExplorationMemory.DrawEpisodeWeights`; a soma esperada não
  muda, então os thresholds não mudam — e o peso está na observação, senão seria só ruído).
- **Gizmos**: NavGraph desenha estrutura (sempre); GraphExplorationMemory desenha estado (só em Play; legenda no
  cabeçalho do arquivo). Evite reutilizar verde/laranja/amarelo/magenta/branco em gizmos novos.

### Autorar um mapa novo (fluxo)

1. Duplique `NodeTraining.prefab` ou monte uma arena com `GraphArenaController` > `NavGraph` > nós.
2. Coloque nós `Primary` (1–3 por sala/corredor) e `Auxiliary` ao longo dos corredores.
3. Dê o `_explorationWeight` de cada primário (some o total do mapa: é o teto da recompensa de cobertura).
   Ligue os nós (na mão ou "Auto-ligar" + poda manual).
4. `NavGraph` > "Coletar nós filhos" (obrigatório após adicionar/remover nós) e "Validar ligações".
5. Confira `_maxNodeDistance` (~ maior aresta do mapa). `_spawnPoints` só importa com `_spawnAtRandomNode`
   desligado.
6. Play e leia o Console: `ValidateSetup` e o bake avisam grafo desconexo, primário com peso 0, vizinhos > slots,
   `VectorObservationSize` errado, referência vazada de outra arena.

## Treino e avaliação

Comandos completos na seção **Treinar: comandos no Anaconda PowerShell**, no topo. O que importa ao mexer:

- Behavior names: `GraphExplorer` e `Seeker` (têm que bater entre YAML e `Behavior Parameters` do prefab).
- Prefab de treino usa `DecisionRequester` com **Decision Period 5** e **Take Actions Between Decisions** ligado.
- Métricas que importam no TensorBoard: `Environment/Cumulative Reward`, `Environment/Episode Length`
  (1600 = timeout com 8000 steps e período 5) e `Environment/Lesson Number`.
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

- `_maxEpisodeSteps`, `_stagnationSteps`, `StepsSinceNewNode` são **steps de física** (FixedUpdate), não decisões.
  Ao mudar a duração do episódio, reescale `_wallContactPenalty` e `_stagnationPenalty` (tabela no cabeçalho de
  `GraphRewardSystem.cs`).
- Qualquer mudança no layout/tamanho das observações (`_neighborSlots`, `GlobalObservations`, ordem dos blocos)
  **invalida todos os `.onnx`** e exige ajustar `VectorObservationSize` no prefab. Use os blocos reservados antes de
  crescer o vetor.
- No currículo do GraphExplorer, `coverage_target`, `frontier_hint`, `frontier_hint_steps`, `previsited_fraction`,
  `weight_jitter`, `ping_interval`, `hider_mode` e `hider_speed` têm `completion_criteria` **idênticos de
  propósito** (o ML-Agents avalia cada parâmetro sozinho) — 5 lições, thresholds 8.5 / 10.5 / 8.0 / 7.5. Mexeu num
  threshold, mexa nos outros sete. `config/graph_overnight.yaml` é a versão por **relógio** (`progress`, 8M) para
  rodar sem supervisão; `config/graph_hider_test.yaml` é só para ver o hider/ping/visão funcionando. Os thresholds
  em `reward` são estimativa (conta no YAML) e **não crescem de lição em lição**: os pré-visitados tiram renda
  enquanto a cobertura-alvo sobe. `max_steps` = 30M (~30 h) — as lições avançam por desempenho, o resto é refino.
- `_newEdgeReward` só paga aresta **entre dois primários**. No mapa atual do `NodeTraining.prefab` não existe
  nenhuma (todo primário se liga via auxiliares), então o termo está morto ali.
- `_frontierApproachReward` tem um teto por mapa (`chegada / aresta_mediana`) — refaça a conta em mapa novo.
- Esqueceu "Coletar nós filhos" → nó aparece como ponto sem círculo e sem ligações no gizmo.
- `Heuristic` (dirigir com WASD) só compila com `ENABLE_LEGACY_INPUT_MANAGER`.
- `_episodeEndDelay` dos ArenaControllers é só debug visual — mantenha 0 para treinar.
- Erros de wiring do ML-Agents são silenciosos (treino que não converge). `ValidateSetup()` loga no Play — olhe o
  Console antes de iniciar um run longo.

## Verificação

Não há testes. O ciclo é: abrir a cena de treino (`Assets/Scenes/Arthur/Nodes_2.unity`), Play, conferir o Console
(sem erros de `ValidateSetup`/bake) e observar os gizmos com o agente selecionado. Para mudanças de recompensa ou
currículo, rode ~200k steps e compare as curvas do TensorBoard com o run anterior antes de commitar o ajuste.
