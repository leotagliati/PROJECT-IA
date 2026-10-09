# Handoff: v7 — mapa sem nós autorados: salas e portas, NavMesh e portas pelos batentes

Escrito em 09/10/2026 para quem continuar num chat novo. É uma **proposta em aberto**: nada da v7 foi
implementado, e as decisões marcadas como pendentes são do Arthur. Antes de mexer, leia:

- `CLAUDE.md` e `CLAUDE.local.md` (raiz): arquitetura, convenções e armadilhas;
- `docs/graph/arquitetura.md` (quem fala com quem) e `docs/graph/salas-e-portas.md` (o modelo de salas atual);
- `docs/graph/historico-de-treinos.md`, Fases 8 a 10 (o que já deu errado do zero e por quê).

## 1. Onde estamos (09/10)

- **Branches:** `feature/v6-seeker` = o treino v5.2 abaixo (código, prefab e cena V6); `feature/v7-seeker` (sai
  da v6) = este trabalho. O Arthur roda o git (ver seção 6).
- **Modelo em uso:** `Assets/GraphExplorer_05_50M.onnx` (run `v5.1_zero_01`, 50M steps, do zero). Explora o mapa
  inteiro e pega o hider do treino em ~99%; no jogo ainda perde o jogador.
- **Próximo run (v5.2, ajuste fino da caça herdando o 50M):**
  - config `config/graph_v5.2_caca.yaml`, gerada por `tools/gen_full_curriculum.py` (`V5_2_STAGES`);
  - cena `Assets/Scenes/Arthur/V6 - Training.unity`, com 5 instâncias de `Assets/Prefabs/NodeTraining - V6 Training.prefab`;
  - build pelo perfil `V6 - Training` em `Builds/V6/PROJECT-IA.exe`;
  - comando `mlagents-learn config/graph_v5.2_caca.yaml --run-id=v5.2_caca_01 --initialize-from=v5.1_zero_01 --env="Builds/V6/PROJECT-IA.exe" --no-graphics --num-envs=3`;
  - confira em `results/` se já rodou e preencha o "Resultado" da Fase 10 no histórico.
- **O que mudou em 09/10 e já está no código** (vetor 188 igual, por isso o 50M carrega):
  - **Rastro de 3 s** (`GraphHiderPerception._trackMemorySeconds`): depois de ver, segue "vendo" (posição real)
    sem linha livre. O Arthur **recusou** rastro mais longo (5–15 s) e "sentir" o alvo através de parede.
  - **Rota NavMesh** (`GraphHiderPerception.ChaseAim`): a observação [18..20] aponta para a próxima quina do
    caminho NavMesh até o alvo; a distância é em metros pelo caminho. Sem NavMesh assado, cai na reta e avisa uma
    vez no Console. Gizmo vermelho-vivo no filho `PerceptionSystem`.
  - **Previsão** (`PredictedPosition`): sem ver, projeta a última posição com a velocidade vista, até 2 s.
  - **Pressa:** `_huntPressurePenalty` + `GraphStepContext.HuntingTarget`.
  - **Velocidades:** perseguição 11,5 m/s, alerta 9,5, giro na perseguição 540°/s (`GraphLocomotion`).
  - **Hider de treino:** nunca no centro do nó; 40% dos esconderijos FORA da área de qualquer nó
    (`GraphHider._offNodeChance` / `_offNodeReach`).
- **NavMesh:** o `NodeTraining - V6 Training` tem um `NavMeshSurface` assado, mas o arquivo de dados está em
  `Assets/Scenes/Arthur/V5 - Test/NavMesh-NodeTraining - V5 training.asset`, compartilhado com a cena `V5 - Test`.
  Re-assar em qualquer um dos dois sobrescreve o do outro. O ideal é re-assar no Prefab Mode do V6 para o asset
  ficar junto do prefab. O pacote é `com.unity.ai.navigation` 2.0.12.

## 2. A ideia da v7

**Objetivo do Arthur: mapa SEM NÓS autorados.** Nenhum `NavNode` colocado à mão. O agente pensa só em **salas e
portas**:
- **portas** = os vãos, achados automaticamente pelos **batentes** (peças `Door_Hole` do mapa);
- **salas** = o chão (NavMesh) cortado nesses vãos;
- **dentro da sala** ele anda livre;
- **cobertura** = pontos de visão gerados sozinhos por sala, que ele não segue.

Por dentro continua havendo pontos (centro de cada vão, pontos de visão), mas todos gerados por código a partir do
NavMesh e dos batentes. Mapa novo = assar o NavMesh + rodar a geração, sem autoria.

Nível intermediário possível, como etapa (seção 5): tirar só os nós de chão e manter os 37 nós de porta
autorados, para não mudar duas coisas de uma vez.

Por que o Arthur quer isso:
- **O trilho atrapalha.** O jogador nunca anda pelos centros dos nós, e o seeker aprendeu num mundo onde tudo
  acontece neles. Ele ia até o nó do jogador e parava; empacava com o jogador num canto sem nó.
- **Autoria.** Cada mapa novo exige ~100 nós à mão. O gerador (`NavGraphPlacer`) foi abandonado em 26/09.
- **O que decide já é sala + porta:** a planta de salas (BufferSensor "Rooms", 32 × 10) e as 8 portas × 9 da sala
  atual já existem. Os vizinhos de chão são detalhe.

**Custo: é treino DO ZERO**, porque o vetor muda. A v5.1 levou 50M steps (~3 dias de máquina). Duas tentativas do
zero foram abandonadas (v4.2 e v5.0, ver histórico): a v7 precisa ser planejada em etapas, com critério por etapa.

## 3. Inventário: o que depende dos nós de chão hoje

Mapa do `NodeTraining - V6 Training`: **148 nós = 37 portas (Door, `_kind` 0) + 97 Auxiliary (1) + 14 Ping (2)**;
27 salas; **68 peças `Door_Hole`** (por que 68 para 37 portas não foi conferido: 2 peças por vão? vãos sem nó?).

| Sistema | Arquivo | Uso dos nós de chão | Na v7 |
|---|---|---|---|
| Conclusão de sala | `Memory/GraphRoomMemory.cs` | sala conclui com `room_complete_threshold` dos NÓS vistos ou pisados | pontos de visão amostrados por sala |
| Migalhas de sala grande | `GraphRoomMemory._crumbMinNodes`, `GraphRewardSystem._bigRoomCrumbReward` | por nó novo em sala com ≥ 9 nós | por ponto de visão |
| Âncora / nó atual | `Memory/GraphExplorationMemory.cs`, `NavGraph.FindNodeAt` | `CurrentNodeIndex`, histerese de âncora | sala atual (+ porta, se estiver no vão) |
| Observação de vizinhos | `Agent/GraphObservations.cs` (8 × 10 = 80 floats) | direção, visitado, "quanto resta", "quão perto" por vizinho | sai; candidato: grade local egocêntrica do que já foi visto (como a janela 5×5 do Seeker antigo) |
| Nó mais próximo [4..6] e [0] "dentro de nó" | `GraphObservations`, `NavGraph.FindNearestReachableNode` | referência quando sai da malha | sai |
| Suspeita | `Hunt/GraphSuspicionMap.cs` | crença por nó, espalha pelas arestas | por ponto de visão ou por sala; espalha pelas portas |
| Ping / audição | `Hunt/GraphPingSystem.cs` | nó de ping, distância pelo grafo (`TryFindPathTo`) | ponto no NavMesh; distância pelo caminho NavMesh |
| Hider de treino | `Hunt/GraphHider.cs` | anda de nó em nó (reta entre centros) | anda no NavMesh (`NavMesh.CalculatePath`) |
| Spawn | `NavGraph.RandomSpawnNode`, `GraphArenaController` | nó aleatório por sala | ponto do NavMesh na sala sorteada |
| Estagnação / revisita | `GraphExplorationMemory`, `GraphRewardSystem` | "progresso" = nó novo; revisita precoce = PORTA | progresso = ponto/sala/porta; revisita continua por porta |
| Métricas | `GraphExplorerManager` (`Exploration/OffNodeFraction`, `AnchorFlicker` etc.) | — | revisar |
| Jogador como alvo | `Hunt/GraphPlayerTarget.cs` | `CurrentNode` fica o último nó pisado fora da malha | sala/ponto mais próximo |

Contrato da observação: `GraphObservations.GlobalObservations` (36) + vizinhos + portas = 188, conferido por
`GraphExplorerManager.EnforceBrainShape` / `ValidateSetup`. A planta de salas é um BufferSensor criado em runtime
(`GraphObservations.EnsureRoomSensor`). Por isso o Inspector do Behavior Parameters acusa um falso
"[?x188] vs [?x32x10]": o sensor não existe fora do Play.

## 4. Portas pelos batentes (`Door_Hole`)

- O código já acha batente **pelo nome**: `GraphExplorerManager` (`t.name.Contains("Door_Hole")`, só métrica),
  `GraphBodyTracker` (contato com batente) e `NavGraphPlacer.Generation._doorNameContains`. Esta última é a lógica
  antiga do gerador, que cortava salas nos vãos; vale ler antes de reescrever.
- Desde 06/10 todas as `Door_Hole` estão na **layer Wall** (custo cheio): na layer Door, a 25%, o monstro raspava o
  batente e ficava preso. Não volte isso sem conversar.
- Ideia a validar: cada vão = o espaço entre as duas faces de um `Door_Hole`. Desse vão sai um "nó porta"
  automático (centro + largura). As salas saem de cortar o NavMesh nesses vãos, ou de cortar o grafo de pontos de
  visão neles. Verificar no mapa real antes de assumir a geometria.
- **Etapa intermediária (não é o destino):** manter os 37 nós de porta autorados e só remover os de chão. Separa as
  duas mudanças (regra do Arthur: uma coisa por vez). O destino é zero nós autorados.
- Hoje o `NavGraph` (Dijkstra, salas no bake, `RoomOf`/`DoorsOfRoom`, gizmos) é construído a partir de `NavNode`
  filhos. Sem nós autorados, ele precisa ser montado em runtime/no editor a partir das portas geradas e dos pontos
  de visão. Avaliar se vale reaproveitar o `NavGraph` alimentado por nós gerados, ou trocar por um grafo de salas
  e portas mais simples.

## 5. Plano sugerido (decisão do Arthur)

Etapas, cada uma com critério medido antes da seguinte:

1. **Medir sem treinar:** gerar os pontos de visão por sala (amostrados no NavMesh, ~3 m) e comparar a conclusão
   de sala "por pontos" com a "por nós" num episódio do 50M (log/gizmo). Critério: as duas concordam na maioria
   das salas; a S24/S26 (o anel grande) não fica impossível.
2. **Hider e spawn no NavMesh**, ainda com os nós de chão, num ajuste fino herdando o modelo atual. Critério:
   `Hunt/Caught` não cai.
3. **v7 do zero:** sem nós de chão, observação nova (vetor menor), portas autoradas ou de batente. Currículo gerado
   pelo `tools/gen_full_curriculum.py` (novo bloco `V7_STAGES`), começando pela exploração como na v5.1.
   Critério: `Exploration/Coverage` > 0.8 na lição de exploração completa antes de qualquer caça.
4. **Portas pelos batentes** (se não entrou no passo 3).

Perguntas a fazer ao Arthur antes de começar:
- Destino já decidido: zero nós autorados, com portas geradas dos `Door_Hole`. Falta decidir se passa pela etapa
  intermediária (só tirar os nós de chão) ou vai direto.
- Isso vira capítulo do TCC ("grafo autorado × só salas e portas")? Se sim, guardar o 50M como linha de base.
- Nome decidido: **v7** (branch `feature/v7-seeker`; config `graph_v7.0_<tarefa>.yaml`, run `v7.0_<tarefa>_NN`).
  "V6" é o prefab/cena/build do treino v5.2.

## 6. Regras do Arthur (obrigatórias)

- **Nunca commitar** nem dar `git add`/push: mande os comandos; mensagem curta em inglês, sem citar Claude.
- **Nada de seta nem recompensa por se aproximar de alvo escolhido por algoritmo** na exploração e na procura; só
  eventos que o agente causa (sala concluída, porta atravessada). Exceção acertada em 09/10: a rota NavMesh até o
  jogador na perseguição, com a posição conhecida e sem recompensa. Grade "do que já vi" é informação, não seta.
- **Uma mudança por vez, com critério;** os sinais nunca podem se contradizer; tudo que paga vira renda (procure o
  farm antes de treinar).
- **Explique simples:** ele é pt-BR informal e já pediu "me explique como se eu não soubesse nada".
- **Código:** comentários em pt-BR que explicam o PORQUÊ, com a conta; `[SerializeField] private float _camelCase`
  com `[Header]`; `if (x == null)`, nunca `?.`/`??=` em objeto Unity; código de editor em `#if UNITY_EDITOR`;
  penalidade por step raciocinada pelo teto (valor × steps do episódio). Detalhes em `CLAUDE.local.md`.
- **Docs:** `docs/`, `CLAUDE.md` e `.claude/` são locais (gitignored), menos `docs/graph/fases-de-treino.md`
  (versionado, resumo para o time; está atrasado: não tem o resultado da v5.1 nem a v5.2). Ao fechar uma fase,
  atualize `historico-de-treinos.md` e `fases-de-treino.md`; o que falhou vai em "O que não deu certo".

## 7. Armadilhas conhecidas

- **Do zero falha por excesso de mudança:** a v5.0 decorou um loop de salas baratas (1 nó); a v4.2 parou de passar
  por porta. Some o orçamento de recompensa do mapa antes de treinar (cabeçalho do `GraphRewardSystem`).
- **Sala grande e visão:** o anel S24 (20 nós) ficou em 0% até ser cortado em S24 + S26; com pontos de visão, o
  mesmo problema volta se o alcance (22 m) não cobrir os cantos.
- **Mudou código ou prefab = refazer o build** (`Builds/V6`). Parar o treino só com `Ctrl+C`.
- **Export do `.onnx`:** os pesos saem num `.onnx.data` separado; embuta antes de copiar para `Assets/` (sem isso o
  monstro não se mexe no jogo).
- **A cena `V5 - Test` está com mudanças grandes não commitadas** do Arthur: arenas desempacotadas e uma
  "NodeTraining - V6 training" dentro dela. Não mexa sem perguntar. O `stash@{1}` ("On main", 08/10) guarda uma
  versão dessa cena com o `GameManager` e mudanças nos scripts de `Assets/Scripts/Game/`. Também não mexa.
- **Sem como compilar fora do Unity:** o VS Code não carrega o projeto (o `.slnx` não acha os `.csproj`). O
  "sem erro" do diagnóstico do VS Code não prova nada; peça ao Arthur o Console do Unity.
