# Arquitetura do GraphExplorer (refatoração de 04/10/2026)

A refatoração (04/10) não mudou comportamento: mudou onde cada coisa mora e como os sistemas se acham. Em seguida
veio a v5.0 (do zero): corpo na escala do jogador, episódio de 400 s e observação 188 (seção do prefab novo, no fim).

## Pastas (`Assets/Scripts/Graph/`)

| Pasta | O que tem | Componente? |
|---|---|---|
| `Agent/` | `GraphExplorerManager` (orquestra o step), `GraphObservations` (layout dos 182 floats + planta de salas), `GraphBodyTracker` (parede, batida, jitter, parado), `GraphRewardSystem`, `GraphStepContext`, `GraphLocomotion` (estado de alerta → velocidade), `GraphAnimationSystem` (estado → Animator), `GraphStamina` (fôlego do hider), `WorldAlignedSensor` | Manager, Reward, Locomotion e WorldAlignedSensor são; Observations e BodyTracker são classes simples criadas pelo Manager |
| `Memory/` | `GraphExplorationMemory` (camada de nós) + `GraphRoomMemory` (camada de salas, serializada DENTRO da de nós) | só a ExplorationMemory |
| `Hunt/` | `IGraphTarget`, `GraphHider`, `GraphPlayerTarget`, `GraphHiderPerception`, `GraphPingSystem`, `GraphSuspicionMap` | todos menos a interface |
| `Arena/` | `GraphArenaController` + `GraphEpisodeSettings` (struct no mesmo arquivo) | só a arena |
| `Map/` | `NavGraph` em partials (`.cs` campos/bake/chegada/física/spawn, `.Rooms`, `.Paths`, `.Validation`, `.Gizmos`, `.Editor`), `NavNode`, `NavBlockedArea`, `GraphGizmos`; `Placer/` = `NavGraphPlacer` | NavGraph, NavNode, NavBlockedArea, Placer |

Os `.meta` foram movidos junto: os GUIDs não mudaram, os prefabs continuam apontando para os mesmos scripts.

## Quem fala com quem

```
GraphArenaController (1 por arena, pai do agente)
  ResetEpisode() -> Settings (GraphEpisodeSettings: tudo que o currículo decide no episódio)
  Target         -> o alvo único: GraphHider no treino, GraphPlayerTarget no modo de jogo
  ResetHider(seeker), TryGetSpawn, ShowOutcome

GraphExplorerManager : Agent (no objeto do Rigidbody)
  Awake       acha os sistemas: campo do Inspector > este objeto ou FILHOS > cria com padrão (visão, ping,
              procura, locomoção) e avisa
  Initialize  configura: Perception/Ping/Suspicion recebem (grafo, arena.Target); Memory recebe (grafo,
              perception, suspicion); cria GraphObservations e valida
  OnEpisodeBegin   Settings da arena -> memory / ping / suspicion / hider (cada um lê o que precisa)
  FixedUpdate      sentir: memory.Tick (nós -> salas) -> ping -> calor -> perception -> suspicion
  CollectObservations   observations.Write
  OnActionReceived      body.BeginStep -> fim? (captura/cobertura) -> AddReward(Evaluate(contexto))
                        -> body.EndStep + ClearStepFlags -> locomotion.Drive -> terminar
```

Regras que saem disso:

- **Recompensa**: tudo dentro do `GraphRewardSystem.EvaluateStep`, inclusive os bônus de fim (`HiderCaught`,
  `CoverageReached` no contexto). O Manager não soma nada por fora.
- **Estado entre steps** mora no sistema dono: a aproximação do hider (distância na decisão anterior) agora é da
  `GraphHiderPerception` (`HasApproach`/`ApproachDelta`); batidas e jitter são do `GraphBodyTracker`.
- **Currículo novo**: campo no `GraphEpisodeSettings` + `_defaultX` + uma linha no `ApplyCurriculum` (o nome do
  YAML é constante ali; os 14 campos `_xParameterName` saíram, ninguém trocava pelo Inspector).
- **Contexto**: `GraphStepContext` é preenchido por nome no `BuildStepContext` (antes: construtor de 24
  argumentos posicionais, fácil trocar dois bools sem erro).
- **Ordem nós -> salas** é garantida dentro da `GraphExplorationMemory` (Reset, Tick, ClearStepFlags); o Manager
  não tem mais como inverter.

## O que foi removido (não era usado)

| Removido | Por quê |
|---|---|
| `WallHitTracker` | absorvido pelo `GraphBodyTracker` (junto com os contadores de parede/jitter/parado que estavam no Manager) |
| `SeekerMovementSystem`: `ConfigureSteering`, `Steer`, `_steerMargin`, `_brakeAcceleration`, `_syncVelocityWithBody` | steer assist saiu na v4.4; ninguém chamava, e brake/sync estavam em 0/false em todos os prefabs |
| `GraphRewardSystem.ResetEpisode` | vazio |
| `GraphExplorerManager.PlayerCaught` (evento) | sem nenhum assinante; o fim de jogo vai pelo `GameManager.PlayerCaught()` |
| `SetTarget` + `_hider` em Perception/Ping/Suspicion | o alvo vem da arena no `Configure` |
| `GraphHider._seeker` (e a busca dele no Awake) | a arena passa o seeker no `ResetEpisode` |
| `GraphArenaController.RandomSpawnNodeByRoom` (static) | virou `NavGraph.RandomSpawnNode(avoidRoom)`: é consulta de grafo |
| `_xParameterName` (14) e `_defaultSteerAssist` na arena | constantes / parâmetro morto |

Os campos removidos ainda aparecem como dado velho no YAML do prefab até ele ser salvo de novo; o Unity ignora.

**Mantido sem uso hoje** (ganchos para a animação, documentados como tal): `GraphLocomotion.HeadYaw`,
`Speed`, `State`; `GraphHider.IsRunning`; `GraphStamina.Exhausted`.

**Candidatos a apagar (não mexi, decisão sua):** `Assets/Scripts/SeekerAgent.cs` (protótipo antigo),
prefabs `NodeTraining.prefab` .. `NodeTraining4.prefab` e as cenas `Node_0..4` (histórico; com o código atual eles
compilam e rodam, mas a v4 só usa o NodeTraining5).

## Prefabs com os modelos reais (treino v5 na cena `V5 - Training`)

- **`SeekerAgentV2 - V5 training.prefab`**: o SeekerAgentV2 (modelo, Animator, luzes, estática e áudio) com os sistemas do
  GraphExplorer no lugar dos do Seeker antigo (mesmos objetos filhos: MemorySystem, RewardSystem, PerceptionSystem com
  visão/ping/procura, MovementeSystem com GraphLocomotion, Visual com GraphAnimationSystem + SeekerChaseState/AudioSystem/
  StaticVisual alimentados pelo Manager). Caixa trocada por cápsula (r 0.5, h 2.32) e modelo subido 0.42 m: o pé fica no
  pivô. Raios de parede em `RaysWorld` (0.6 m). Behavior Parameters 188 / 4 ações / `GraphExplorer`, sem Model.
- **`PlayerDummy - V5 training.prefab`**: o corpo do jogador como hider: sem câmera, input e scripts do jogador; tag
  Untagged (não `Goal`), layer Ignore Raycast; CharacterController trocado por cápsula; Rigidbody cinemático + GraphHider.
  Modelo subido 1 m (pé no pivô). O Animator fica parado (nada o alimenta no treino).
- **`NodeTraining - V5 training.prefab`**: o NodeTraining6 com esses dois no lugar do agente cápsula e do hider.

## Prefab e cena da v5.0 (prontos)

- **`Assets/Prefabs/NodeTraining6.prefab`**: cópia do NodeTraining5 (mesmo mapa, nós, arena e spawns) com o agente
  nos padrões da v5. Os campos dos componentes do agente e do hider foram apagados do YAML, então o Unity usa o
  padrão do código (= Reset). Ficaram só as referências e o que o padrão não cobre:
  - Behavior Parameters: 188, 4 ações contínuas, sem Model, Behavior Type Default;
  - GraphExplorerManager: Door Layer = Door (porta custa ¼ da parede); `GraphLocomotion` adicionado ao agente;
  - NavGraph: Wall Layer = Wall + Obstacle + Door;
  - NavGraphPlacer removido (só ferramenta de editor; o script continua no projeto);
  - GraphArenaController sem os `Default*` velhos, então valem os da v5 (a última lição).
- **`Assets/Scenes/Arthur/Node_6.unity`**: cópia da Node_5 apontando para o NodeTraining6, com as **6 arenas
  ligadas** (na Node_5, 5 estavam desligadas e a sexta rodava o v4.3 em inferência) e sem os overrides de Model,
  Behavior Type, modo de jogo e velocidade do hider.

**Layers:** tudo com collider é **Wall** (paredes, cantos, móveis; custo cheio), menos as 68 peças `Door_Hole`, que são
**Door** (contato e batida custam ¼ da parede, `GraphRewardSystem` → Door Penalty Scale). Chão e teto ficam fora. Para
aplicar: Prefab Mode do NodeTraining6 → `GraphArenaController` ⋮ → **"Ajustar layers das paredes (Wall / Door)"**, salvar,
e NavGraph ⋮ → "Validar ligações" (móvel que virou parede pode cortar ligação). Tudo é por LAYER; tag só acha o jogador.

Se precisar remontar o prefab do zero: duplique o NodeTraining5 e dê Reset (⋮ → Reset) nos componentes do agente e
do hider. Os padrões do código são os do treino; o Reset do Manager também preenche o Behavior Parameters (188, 4
ações) e o Decision Requester (5). Na arena, use ⋮ → "Usar padrões do treino (v5)" (o Reset dela apagaria os spawns).

### Paredes e cantos sem nó

- O **Ray Perception Sensor** do `RaysWorld` enxerga todas as layers menos Ignore Raycast: nada a fazer.
- **Cantos de corredor sem nó: não precisa fazer nada.** Fora de qualquer nó a âncora fica no último nó, a
  observação [0] vira 0 e a [4..6] aponta o nó livre mais perto. A sala conclui quando TODOS os nós dela foram
  vistos (100%, v5); canto sem nó não entra na conta, então não trava a exploração. Esperado:
  `Exploration/OffNodeFraction` acima de 0 (acima de ~0.15 = tempo demais fora da malha).
- Mexeu em nó: NavGraph → "Coletar nós filhos" e "Relatório de salas e portas".

### Primeiro Play

1. Abra `Assets/Scenes/Arthur/Node_6.unity`.
2. Play sem o trainer e leia o Console:
   - nenhum erro de `ValidateSetup` (tamanho do vetor 188, 4 ações);
   - nenhum aviso "criado em runtime";
   - "corpo do agente medido" e "26 sala(s), 35 porta(s)".
3. Treino:

```
conda activate mlagents
cd "C:\Users\Arthur Lee\Unity\PROJECT-IA"
mlagents-learn config/graph_v5.0_zero.yaml --run-id=v5.0_zero_01
```

**Modelo v4.3 e a cena Node_5:** com o código da v5 o agente emite 188 números; o `GraphExplorer_v4.3.onnx`
espera 182. A instância da Node_5 que roda em inferência com ele (Behavior Type = Inference Only) deixa de
funcionar até existir um `.onnx` da v5. A `Cena FInal` da main não usa o GraphExplorer, então o jogo não é afetado.
