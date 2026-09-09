# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Visão geral

Jogo de esconde-esconde em primeira pessoa feito em **Unity 6000.0.75f1 (URP)**, com o
perseguidor (Seeker) controlado por uma política treinada com **ML-Agents 4.0.3 (PPO)**.

O repositório tem duas metades que se encontram só na cena:

- **Lado RL** (`Assets/Scripts/Seeker/`, `SeekerManager.cs`, `HiderAgent.cs`) — arena de
  treino, observações, recompensas e currículo.
- **Lado jogo** (`Assets/Scripts/MovementSystem/`, `InputSystem/`, `AudioSystem/`,
  `Highlight/`, `Environment/`) — controle do jogador humano, lanterna, peek, áudio, outline.

Os comentários do código são em **português** e explicam o *porquê* das decisões (muitos
documentam bugs específicos que já custaram runs de treino). Ao editar, mantenha esse
padrão: comentário que só repete o que a linha faz é ruído aqui.

## Comandos

Não há build de linha de comando nem suíte de testes neste projeto — abra pelo Unity Hub
(versão exata em `ProjectSettings/ProjectVersion.txt`) e use Play Mode / Build Settings.

### Treinar o Seeker

O ambiente Python fica no conda env `mlagents`:

```bash
conda activate mlagents
```

Iniciar um run novo (depois aperte **Play** no Unity quando aparecer "Listening on port 5004"):

```bash
mlagents-learn config/seeker_curriculum.yaml --run-id=<nome-do-run>
```

Retomar um run interrompido, ou sobrescrever um `--run-id` já usado:

```bash
mlagents-learn config/seeker_curriculum.yaml --run-id=<nome-do-run> --resume
```

```bash
mlagents-learn config/seeker_curriculum.yaml --run-id=<nome-do-run> --force
```

Acompanhar as curvas:

```bash
tensorboard --logdir results
```

Saída em `results/<run-id>/Seeker.onnx` (mais checkpoints e `configuration.yaml` com os
hiperparâmetros efetivamente usados). `results/` é gitignored; os modelos promovidos ficam
versionados em `Assets/SeekerV*.onnx` e são arrastados no campo *Model* do
Behavior Parameters.

## Arquitetura do agente

`SeekerManager` (em `Assets/Scripts/Seeker/SeekerManager.cs`, apesar do nome do namespace) é
o **único** ponto que conhece os callbacks do ML-Agents. Ele não lê o mundo nem calcula
recompensa: orquestra a ordem do step (sentir → observar → agir → avaliar → terminar) e
delega para componentes filhos, todos serializados no Inspector:

| Componente | Responsabilidade |
| --- | --- |
| `SeekerPerceptionSystem` | Único ponto de amostragem do mundo: 8 raycasts de parede + cone de visão do hider. Expõe um snapshot. |
| `SeekerExplorationMemory` | Grade de células visitadas **relativa à arena**, e a janela 5x5 observada. |
| `SeekerMovementSystem` | Move/gira o Rigidbody a partir de 2 ações contínuas (X/Z). |
| `SeekerRewardSystem` | Função pura: recebe `SeekerStepContext`, devolve o delta de recompensa. Todo o tuning mora aqui. |
| `SeekerArenaController` | Dono do *ambiente* (não do agente): spawns, feedback visual, leitura do currículo. Um por arena. |

`SeekerStepContext` é um `readonly struct` montado pelo manager e é o único input do reward
system — é o que mantém o cálculo de recompensa testável e desacoplado.

### Invariantes que quebram silenciosamente

Erros de wiring em ML-Agents não dão exceção; aparecem só como treino que não converge.
`SeekerManager.ValidateSetup()` existe por isso. Pontos que exigem atenção:

- **`SeekerManager.ObservationCount` (= 38) tem que bater com o `VectorObservationSize` do
  Behavior Parameters** no prefab/cena. Ao mudar as observações, atualize a constante *e* o
  Inspector — senão o vetor roda truncado. Composição: 8 proximidades de parede + 2 flags de
  frescor + 3 (direção/distância até a última posição conhecida) + janela 5x5 (25).
- **Parede é identificada por LAYER** (`SeekerPerceptionSystem.WallLayer`), nunca por tag —
  os objetos do Map_8 estão na layer `Wall` mas não levam a tag, e a penalidade de contato
  ficou morta por isso. O **hider é identificado por tag `Goal`**.
- **`Rigidbody.MovePosition`/`.position` só chegam ao Transform depois da simulação de
  física.** Medir deslocamento dentro do mesmo step dá zero; ler `transform.position` logo
  após um respawn traz a posição do episódio *anterior*. É por isso que `HiderAgent.Spawn()`
  devolve a posição em vez de deixar quem chama consultar depois, e por que o
  `SeekerStepContext` guarda a posição do step **anterior**.
- **Avaliar antes de agir**: em `OnActionReceived`, a posição atual já é o resultado do move
  pedido no step anterior. Inverter a ordem desalinha a recompensa da ação que a causou.
- **Decision Period > 1**: `CollectObservations` e `OnActionReceived` rodam em cadências
  diferentes. A percepção é amostrada uma vez por step de física com dedup por
  `_physicsStep`; quem chegar primeiro dispara o `Tick`, o outro reaproveita o snapshot.
- **Contato com parede é por step, não por evento.** `OnCollisionStay` só marca uma flag;
  quem cobra é o step, uma vez só. Penalidade por evento explode em labirinto e passa
  despercebida em sala aberta.
- **Estado estático sobrevive ao Stop** quando o domain reload está desligado nas Play Mode
  Options — ver `PlayerInputProvider.ResetStatics()` com
  `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]`.

### Currículo

`config/seeker_curriculum.yaml` define três lições (`OpenRoom` → `Mixed` → `MazeHeavy`) via o
parâmetro `maze_enabled`. Duas coisas não óbvias:

- `maze_enabled` é lido por `SeekerArenaController.ApplyCurriculum()` como **probabilidade
  por episódio e por arena**, não como flag global. Com 9 arenas e `0.5`, o mesmo batch
  carrega os dois regimes — é isso que evita a perseguição em sala aberta ser desaprendida
  enquanto o labirinto é treinado.
- A arena **deriva o regime de recompensa** do mesmo sorteio: `ApproachRewardScale` e
  `WallProximityScale` caem no layout com labirinto (distância euclidiana deixa de ser
  progresso, e estar perto de parede é a condição normal de um corredor). Uma fonte de
  verdade só; não crie parâmetros de currículo separados para esses pesos.
- As `completion_criteria` usam `measure: progress` (fração de `max_steps`) de propósito,
  porque thresholds baseados em reward são invalidados a cada ajuste na função de
  recompensa. O `min_lesson_length` é calibrado em *episódios*, e escala com
  `_maxEpisodeSteps` e o Decision Period — recalibre ao trocar de mapa.

### Tuning de recompensa é específico do mapa

Em `SeekerRewardSystem`, `_newCellReward` precisa ser refeito sempre que a grade de
exploração mudar (`_arenaSize` / `_cellSize` em `SeekerExplorationMemory`): o produto
`células alcançáveis × _newCellReward` tem que ficar bem abaixo do `_hiderFoundReward` (+5),
senão cobrir o mapa vira objetivo em si. O arquivo documenta as contas das grades já usadas.

O `HiderAgent` **não é um `Agent` de ML-Agents** — é um MonoBehaviour com navegação
cardinal reativa (raycasts de folga + detector de encaixe em quina). Serve de alvo móvel
determinístico durante o treino.

## Sistemas do jogador

- **Input**: `PlayerInputProvider` é um wrapper estático com contagem de referências sobre
  `PlayerInputActions` (gerado de `Assets/Scripts/InputSystem/PlayerInputActions.inputactions`,
  que é o asset em uso — `Assets/InputSystem_Actions.inputactions` é o sample do package).
  Todo componente que lê input chama `Acquire()`/`Release()` em `OnEnable`/`OnDisable`; o
  action map só liga quando o primeiro usuário aparece. Ao regerar o `.cs`, não edite o
  arquivo gerado.
- **Ordem de execução da câmera** — vários componentes escrevem na mesma transform, e a
  ordem é declarada com `[DefaultExecutionOrder]`: `ShoulderPeek` (50) → `Flashlight` (100)
  → `AtmosphericParticles` (120), todos depois de `CameraJuice`, que ainda ajusta a câmera
  no `LateUpdate`. Se um efeito de câmera "some", quase sempre é ordem de execução.
- **Estado do jogador**: `PlayerMovement.CurrentState` (`PlayerState` + evento
  `StateChanged`) é a fonte de verdade para "está andando/correndo/pulando". Quem precisa
  reagir (áudio, IA, UI) lê isso em vez de recalcular por conta própria.
- **Áudio**: `AudioSystem` é um pool fixo de AudioSources 3D com API estática
  (`AudioSystem.Play("footstep", x, z)`, `PlayAt`, `PlayFollowing`, `PlayLoop`/`Stop`).
  Clipes são cadastrados no Inspector por id string, com jitter de pitch. Um único objeto
  na cena.
- **Outline**: `ObjectHighlighter` faz raycast do centro da tela e troca o objeto para a
  layer `Outline` (a render feature em `Assets/Render Features/` desenha o contorno);
  `HighlightTarget` guarda a layer original para restaurar.

## Tags e layers

Definidos em `ProjectSettings/TagManager.asset` e assumidos pelo código:

- Tags: `Goal` (o hider) e `Wall`.
- Layers: `Wall` (usada nos raycasts e na detecção de contato do seeker) e `Outline`.

## Cenas

`Treinando_Map_8.unity` é a cena de treino atual (múltiplas arenas duplicadas).
`SampleScene`, `Leo_Teste_Sem_Labirinto`, `Leo_teste_audio` e `OutlineShaderTestScene` são
cenas de teste isoladas.
