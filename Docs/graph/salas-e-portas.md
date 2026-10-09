# Plano — Exploração por SALAS e PORTAS (GraphExplorer, mapa v4 / NodeTraining5)

> Versão 3 (01/10/2026). **S0 + S1 IMPLEMENTADAS** (código compila; Play e treino ainda não
> conferidos). A seção 9 diz o que mudou em relação à versão aprovada. Atualize este arquivo se o
> desenho mudar.

## 1. Contexto

Hoje a exploração paga **por nó primário**, com peso `NavNode._explorationWeight` gravado pelo menu
"10. Pesos por área" (peso ∝ área^0.5). Essa é a "pontuação por tamanho do raio" que **sai**. No mapa v4
(`Assets/Prefabs/NodeTraining5.prefab`, cena de treino `Assets/Scenes/Arthur/Node_5.unity`, 6 arenas) os primários estão nos
vãos de porta. O jeito novo de pontuar:

- **Porta** (o antigo Primário/Exploração) = vão entre duas áreas.
- **Sala/corredor** = pedaço conexo do grafo entre portas, calculado no bake (sem `_areaId` à mão).
- Paga cobrir **80% de uma sala** (depois disso, pouco), **atravessar portas** (decai a cada repetição) e
  **sair de uma sala concluída** (porta diferente da entrada vale mais).
- O agente só enxerga **a sala atual e as portas dela**. **Sem seta desde o início.**
- Quando quase tudo foi usado, a **porta e a sala mais antigas são liberadas** (patrulha).
- Fases seguintes: ping sorteado por sala → calor; hider pinga; hider solto.

## 2. Decisões tomadas

| Tema | Decisão |
|---|---|
| Prefab | Corrigido pelo Arthur (ligado à mão); a segmentação sai limpa, sem regra especial. |
| Medida dos 80% | **Nós pisados**, sem re-ladrilhar: uma sala de 1 nó fica concluída ao entrar. |
| Porta diferente | **Novidade da porta**: 1 → 0.5 → 0.25 por travessia. Numa sala de 1 porta, a ida e a volta contam como uma. |
| O que o agente vê da porta | Direção/distância, novidade, "entrei por aqui", **sala do outro lado concluída** (memória). |
| Liberação | **Portas e salas**: a mais antiga de cada uma volta. Valendo **menos que nova** (`_releasedValue` 0.5), para "sala nova sempre compensa mais". |
| Seta | **Nenhuma**, desde a S1. |
| Ping | **Sorteia sala, depois nó**. Sala de 1 nó entra com chance `p`. Os 14 nós Ping viram legado. |
| Sistema antigo | **Substituído**: saem peso por nó, `weight_jitter`, patrulha por tempo, tédio de sala e `_newEdgeReward`. |
| Corredor | Por padrão, sala como as outras. Se a métrica mostrar o agente varrendo o corredor, entra a flag "corredor" (sem bônus de saída). |

## 3. Estado do NodeTraining5 (lido do prefab depois do apply)

147 nós: 35 Porta (Primary), 98 Auxiliar, 14 Ping. Segmentando: **26 salas, 35 portas** (conferido
depois das correções do Arthur em 01/10, 22:30).

- Toda porta liga exatamente duas salas, e nenhuma porta é ligada direto em outra.
- Portas por sala: 1 porta ×2 salas, 2 ×10, 3 ×11, 4 ×2, 7 ×1 (corredor, 20 nós). Máximo 7, então 8 slots
  de porta bastam.
- Nós por sala: 11 salas com 1 nó; a maior tem 20.
- A maioria das salas não tem nó Ping. Isso confirma o sorteio por sala.
- Sem ligação lixo (`null`, auto-ligação) nem nó isolado (conferido às 22:30).

## 4. Riscos que continuam (e mitigação)

1. **Do zero e sem seta.** A E2 sem seta travou em 0.42, mas lá a decisão era "qual ladrilho"; aqui é
   "qual porta", com novidade visível. Mitigação na S1:
   - lição inicial fácil (alvo de 20% das salas, steer assist 1.0);
   - **nenhum** pagamento por se aproximar de um alvo escolhido por algoritmo (decisão do Arthur, 01/10:
     "não quero uma IA que segue um raio feito por um algoritmo"). O gradiente dentro da sala vem da fatia
     por nó novo; entre salas, da novidade das portas, que está na observação;
   - spawn em nó aleatório (já existe).
2. **Salas de 1 nó são binárias**: 11 de 26 salas ficam concluídas ao entrar. Aceito. Se o agente "pisar
   e sair" das salas grandes, re-ladrilhamos depois.
3. **Farm**, que fica limitado assim:
   - vai-e-vem na porta: novidade geométrica, soma ≤ 2× o valor;
   - sala de 1 porta: ida e volta valem uma travessia;
   - a liberação só dispara com ≥ `release_fraction` usado e devolve valendo 0.5.
4. **Liberar sala muda o fim do episódio**: com liberação ligada não há fim por cobertura (vai até o
   timeout). Por isso ela entra só na S3, separada.

## 5. Desenho

### 5.1 Grafo — `NavGraph` (bake)
- `NodeKind.Primary` → **`Door`** (int 0 mantido, prefab intacto), Inspector "Porta". `IsPrimary` → `IsDoor`.
- Segmentação no `EnsureBaked`: tira as portas e faz BFS. Fica a mesma lógica do menu 11 (`NavGraphPlacer.Coverage.cs:293`), movida para o runtime.
- Consultas novas:
  - `RoomCount`;
  - `RoomOf(node)` (porta = -1);
  - `DoorsOf(room)`;
  - `RoomsOfDoor(door)` → (a, b);
  - `NodesOf(room)`;
  - `RoomNodeCount(room)`.
- Dijkstra limitado à sala: variante de `ScoreBeyond`/`DistanceToNearestBeyond` (L983/L1028) em que as
  portas fecham a busca.
- Avisos no `ValidateBakedGraph`:
  - porta com menos de 2 salas;
  - porta ligada direto em porta;
  - sala sem porta;
  - lista "sala → portas" no Console no primeiro bake.
- Saem `DiscoveryValue`, `_explorationNodeScore` e `_auxiliaryNodeScore`. `PingValue` fica sem peso.
  `_areaId` vira legado; o gizmo mostra o "S#" calculado.

### 5.2 Memória — `GraphRoomMemory.cs` (novo, um por agente)
- **Por sala**:
  - nós visitados / total;
  - `Completed` (≥ `room_complete_threshold`, default 0.8);
  - passo da conclusão (para a liberação);
  - `Released` (vale `_releasedValue`).
- **Por porta**: travessias, passo da última, novidade (`0.5^n`, ou a partir de `_releasedValue` se
  liberada).
- **Travessia**: a âncora estava na sala A, passa pela porta e chega à sala B ≠ A. Entrar no vão e voltar
  não conta. Usa `CurrentNodeIndex`/`PreviousNodeIndex` da `GraphExplorationMemory`.
- **Liberação** (`release_fraction`, 0 = desligada): quando as portas usadas são ≥ fração, a porta mais
  antiga volta. Quando as salas concluídas são ≥ fração, a sala concluída há mais tempo volta (nós a não
  visitados, valendo `_releasedValue`).
- **Eventos por decisão**: valor de nós novos, salas concluídas, valor de travessia, valor de saída de
  sala concluída.
- A `GraphExplorationMemory` continua com a âncora, as visitas por nó, o loop/pisca-pisca e o "quanto resta"
  (agora restrito à sala). Saem dela:
  - pesos, jitter e `previsited` por nó;
  - patrulha (`Recovery`/`RevisitValue`);
  - tédio de sala;
  - fronteira/seta.

### 5.3 Recompensa — `GraphRewardSystem`, seção "Salas e portas" (valores iniciais)
| Termo | Valor | Regra |
|---|---|---|
| Nó novo da sala | 0.25 / ⌈0.8·N⌉ até 80%; ×0.2 depois | Toda sala vale igual, independente do tamanho. |
| Sala concluída | +0.25 | Uma vez; a liberada vale ×0.5. |
| Travessia de porta | 0.1 × novidade | |
| Saída de sala concluída | 0.15 × novidade | Sair pela porta de entrada = metade. Sala pré-concluída não paga saída. |
| Fim por cobertura | +5 (já existe) | Fração de salas concluídas ≥ `coverage_target`. |

- Teto: 26 × (0.25+0.25) + 35 × 0.1 + ~26 × 0.15 ≈ +20, mais +5 (≈ +19 com o alvo de 80%). É a mesma ordem do orçamento antigo (~17+5+seta ~3); as penalidades não mudam.
- Ficam como estão: existencial, parede, batida, suavidade, revisita precoce, ping, visão, captura e
  suspeita.
- A estagnação passa a zerar com "progresso de sala" (nó novo de sala, sala concluída ou porta com
  novidade ≥ 0.25) em vez de "nó primário novo".
- A revisita precoce (loop) passa a ser medida em PORTA (era em primário).
- Saem: `_nodeCoverageReward`, `_newEdgeReward`, os termos de fronteira (`_frontierProgressPerMeter`,
  `_frontierApproachReward`), `_boredomPenalty` e a renda de revisita.

### 5.4 Observação — `GraphExplorerManager` (o vetor muda: ajustar `VectorObservationSize` no prefab)
- **Globais**:
  - mantêm: âncora, nó mais próximo, encostado, ping, visão, corpo e procura;
  - [7] cobertura passa a ser a fração de salas concluídas;
  - o bloco da seta [9..12] vira o bloco da sala: fração explorada, concluída, estou num vão, nº de portas
    / 8;
  - [21] tédio passa a ser o calor da sala (0 até a S4).
- **Vizinhos (8 × 10)**:
  - continuam: direção/distância, visitado, vim daqui, quantas vezes, suspeita, válido;
  - "quanto resta" e "quão perto" passam a ser **só dentro da sala**;
  - sai o peso.
- **Portas da sala atual (8 × 9)**, ordenadas por ângulo em volta do centro da sala:
  - direção X/Z e distância;
  - distância pelo grafo dentro da sala;
  - novidade;
  - entrei por aqui;
  - sala do outro lado concluída;
  - calor;
  - válido.
- Os slots mostram sempre as portas da SALA ATUAL (a última em que o agente pisou num nó de sala).
  Parado num vão, ele ainda vê as da sala de onde veio; ao pisar na outra sala, os slots trocam.
- Tamanho final: 30 + 8 × 10 + 8 × 9 = **182** (`VectorObservationSize` do NodeTraining5 já ajustado).

### 5.5 Currículo — `GraphArenaController` + `tools/gen_full_curriculum.py`
- Parâmetros novos: `room_complete_threshold` (0.8), `release_fraction` (0 = desligada).
- `coverage_target` passa a ser a fração de salas.
- Saem `weight_jitter`, `value_recovery_seconds`, `area_boredom`, `frontier_hint` e
  `frontier_hint_steps`.
- `previsited_fraction` vira "salas que já nascem concluídas" (anti-decoreba).
- Série nova de etapas no gerador (`V4_STAGES` em `tools/gen_full_curriculum.py`): por enquanto só
  `config/graph_v4_s1_salas.yaml`. Os YAMLs `graph_node4_*` ficam como histórico (os parâmetros
  antigos não existem mais no C#).
- Node_5 tem 6 arenas (antes 9). O step do ML-Agents soma todos os agentes, então os steps e episódios por lição não mudam; o treino fica ~1.5× mais lento no relógio. Os thresholds não mudam.

### 5.6 Fases futuras (desenho; implementadas na etapa delas)
- **Ping (S4)**: a cada ping, sorteia uma sala (salas de 1 nó com chance `ping_single_node_chance`) e um
  nó dela.
  - A sala fica **quente**: o calor aparece no [21] e nos slots de porta que levam a ela, e se propaga do
    nó pelas portas da sala.
  - A sala "desconclui" e explorá-la paga ×`ping_room_multiplier`.
  - Chegar ao nó do ping paga como hoje.
- **Hider + ping (S5)**:
  - os nós de ping do episódio são sorteados por sala no reset e compartilhados entre `GraphPingSystem`
    e `GraphHider`; o hider pinga ao passar neles;
  - o `GraphSuspicionMap` passa a trabalhar por sala, e 80% **vista** (`CanSeePoint`) limpa a sala;
  - aqui entra "ver conta como explorar".
- **Hider solto (S6)**: o hider anda até pontos aleatórios dentro do retângulo dos nós, e prefere cantos
  fora da linha de visão das portas.

## 6. Arquivos

| Arquivo | Mudança |
|---|---|
| `Assets/Scripts/Graph/NavNode.cs` | `Primary`→`Door` / `IsDoor`, InspectorName "Porta", comentário do peso |
| `Assets/Scripts/Graph/NavGraph.cs` | salas no bake, consultas, Dijkstra na sala, avisos, gizmo "S#" e novidade |
| `Assets/Scripts/Graph/GraphRoomMemory.cs` (novo) | estado de sala/porta, travessia, liberação |
| `Assets/Scripts/Graph/GraphExplorationMemory.cs` | remove peso, jitter, patrulha, tédio e fronteira; "quanto resta" restrito à sala |
| `Assets/Scripts/Graph/GraphStepContext.cs`, `GraphRewardSystem.cs` | campos e termos novos; cabeçalho com o orçamento novo |
| `Assets/Scripts/Graph/GraphExplorerManager.cs` | layout novo, slots de porta, cobertura por salas, métricas `Rooms/*` e `Doors/*`, `ValidateSetup` |
| `Assets/Scripts/Graph/GraphArenaController.cs` | parâmetros novos, remoção dos antigos |
| `Assets/Scripts/Graph/GraphPingSystem.cs`, `GraphHider.cs`, `GraphSuspicionMap.cs` | só na S4/S5 |
| `Assets/Scripts/Graph/NavGraphPlacer.*.cs` | remove menus 8 e 10; menu 11 vira "Relatório de salas e portas" |
| `tools/gen_full_curriculum.py` + `config/graph_v4_s*.yaml` | etapas novas |
| `docs/graph/salas-e-portas.md`, `historico-de-treinos.md`, `fases-de-treino.md`, `CLAUDE.local.md` | documentação da fase |
| `NodeTraining5.prefab` | só o `VectorObservationSize` (peço antes; ou o Arthur ajusta) |

## 7. Etapas

| Etapa | Muda | Herda | Critério para passar |
|---|---|---|---|
| **S0** | infra, sem treino | — | Console com 26 salas / 35 portas, sem erro; Heuristic confirma travessia, novidade e conclusão |
| **S1** salas | recompensa e observação novas, sem seta; lições: alvo de salas 0.2 → 0.5 → 0.8, assist 1.0 → 0.7 | do zero | salas concluídas > 0.6, parede < 0.2 |
| **S2** menos assist | assist 0.7 → 0.3, salas pré-concluídas 0 → 0.3 | S1 | salas > 0.6, parede < 0.25 |
| **S3** liberação | `release_fraction` 0.85, sem fim por cobertura | S2 | travessias novas por minuto estáveis, `Doors/RepeatFraction` baixo |
| **S4** ping | ping por sala + calor | S3 | `Ping/Reached` > 0.6 |
| **S5** hider + ping | hider pinga, suspeita por sala, "ver" conta | S4 | `Hunt/Caught` > 0.5 |
| **S6** hider solto | hider fora do centro, esconderijo | S5 | `Hunt/Caught` > 0.4 |

Nesta rodada a implementação vai até **S0 + S1** (código, config e docs). S2 em diante, só depois do
resultado da S1.

## 8. Verificação
- **S0**: Play no `Node_5.unity` (6 arenas NodeTraining5).
  - O Console lista 26 salas / 35 portas, sem aviso de porta.
  - Os gizmos mostram "S#" por sala e a cor da novidade por porta.
  - Dirigindo no `Heuristic`:
    - travessia só conta ao trocar de lado;
    - a 2ª travessia vale metade;
    - sala de 1 porta: a ida e a volta valem uma;
    - a sala conclui em 80%.
  - `ValidateSetup` sem erro com o `VectorObservationSize` novo.
- **S1**: `mlagents-learn config/graph_v4_s1_salas.yaml --run-id=v4_s1_01`, ~200k steps. Acompanhar:
  - `Rooms/CompletedFraction`;
  - `Doors/Crossings` e `Doors/RepeatFraction`;
  - `WallContactFraction`;
  - `Movement/IdleFraction`;
  - `Episode Length`.

  Comparar com `node4_e1_01`. Registrar em `historico-de-treinos.md`.

## 9. Implementação (01/10/2026) — o que ficou diferente do plano aprovado

- **Corte de aresta pelo vão: não implementado.** Depois das correções do Arthur no prefab nenhuma
  aresta contorna uma porta, então a regra não foi necessária. O bake avisa se aparecer de novo
  ("a porta X tem os DOIS lados na mesma sala").
- **Valores da recompensa** um pouco menores que o rascunho (0.25 / 0.25 / 0.1 / 0.15) para o teto
  ficar na mesma ordem do orçamento antigo.
- **Vizinhos com 10 floats** (o rascunho dizia 9 — o antigo tinha 11, saiu só o peso) e **portas com 9**
  → vetor 182.
- **Slots de porta = sempre a sala atual** (ver 5.4), em vez de "as duas salas do vão".
- **Liberação já implementada** na `GraphRoomMemory`, mas desligada (`release_fraction` 0) até a S3.
- **Pré-concluídas não pagam saída**: sair de uma sala que já nasceu concluída não é mérito do agente.
- **GraphRoomMemory no prefab**: se o agente não tiver o componente, o Manager cria um em runtime com os
  valores padrão (e avisa no Console). Para ajustar no Inspector: Add Component > Graph Room Memory no
  NodeSeekerAgent.
- **Verificação feita**: compila sem erros e sem avisos nos scripts do Graph, com uma cópia do
  `Assembly-CSharp.csproj`. Falta o Play (S0) e o run curto (S1).
- **Sem "alvo local" (01/10, depois da primeira versão):** existia um alvo escolhido por algoritmo (nó que
  falta na sala, ou a porta mais nova; linha violeta no gizmo) e a recompensa pagava 0.01 por metro de
  aproximação. Era a seta por outro caminho — o agente aprenderia a seguir o algoritmo. Saiu inteiro. A
  memória continua medindo a distância pelo grafo até cada porta, mas só como INFORMAÇÃO da porta
  (observação), sem pagar nada. Thresholds da S1: Perto 6.0, Metade 9.0 (era 9.5).

## 10. Run da noite (01/10) — S4, S5 e S6 implementados

Pedido do Arthur: tudo num run de ~8 h, herdando o `v4_s1_01`. Config `config/graph_v4_noite.yaml`, run
`v4_noite_01`, 10 lições (tabela no cabeçalho do YAML). Compila; **não foi testado no Play antes do run**.

- **Ping por sala (S4):** `NavGraph.DrawEpisodePingNodes` sorteia um nó por sala a cada episódio (sala de 1
  nó entra com `ping_single_room_chance` 0.5). O ping aleatório e o barulho do hider usam esses nós; os 14
  nós Ping autorados viram legado.
- **Sala quente:** o ping que começa esquenta a sala dele (`GraphRoomMemory.HeatRoom`). Se ela estava
  concluída, volta a ser explorável, e vale ×2 até ser concluída de novo. Uma sala quente por vez.
  Observação: [21] = a sala atual está quente; o float de calor da porta = a porta é da sala quente.
- **Ping sem pagamento por metro:** saiu o `_pingApproachPerMeter`, pelo mesmo motivo do alvo local. O ping
  paga chegar (+2) e cobra expirar (−0.5) só na lição Ping; na caça ele só informa.
- **Ver conta como explorar (S5, `vision_explores`):** nós da sala atual que entram no cone de visão contam
  como pisados.
- **Hider solto (S6, `hider_loose`):** anda para pontos aleatórios dentro do retângulo dos nós. Metade das
  vezes escolhe o ponto visto pelo menor número de portas da sala e fica parado 3× mais tempo. Se a reta
  até o ponto bate em parede, passa antes pelo centro do nó.
- **Ainda não feito:** a suspeita continua por NÓ (`GraphSuspicionMap`), não por sala.

- **Spawn por sala (02/10):** o agente nasce numa sala sorteada por igual e num nó aleatório dela (antes era
  um nó qualquer do mapa, e o corredor grande nascia muito mais). O hider usa a mesma regra, numa sala
  diferente da do agente e a pelo menos 40 m. Sala sem nó com folga para o corpo é pulada.

## 11. V4.1, antes chamada V4B (02/10) — planta de salas, explorar = ver, suspeita multiplica

Resultado do `v4_noite_01`: pega o hider em ~2/3 dos episódios, mas com hider a cobertura cai a cada
lição (a suspeita paga separado e ganhou da exploração), e as salas S24 (corredor em anel de 20 nós) e
S25 (sala de 23×24 m dentro dela) quase nunca eram vistas. Decisões do Arthur:

- **Planta de salas:** BufferSensor `Rooms` com todas as salas (10 floats cada: direção e distância até
  o centro, portas até lá, quanto já viu, concluída, quente, suspeita, atual, nº de portas). Criado em
  runtime pelo Manager. É a planta do prédio + memória, não o caminho.
- **Explorar = ver**, desde a lição 1: nó de qualquer sala que entra no cone conta como visto.
- **Suspeita multiplica**: não paga sozinha; multiplica o valor de ver a sala (até 3×) e reabre sala
  concluída onde ela passou de 2× a média. Na caça, exploração a 100% e sem liberação por tempo.
- **Sala continua valendo igual.**
- Config `config/graph_v4.1_noite.yaml`, run `v4.1_noite_01` (antes `v4b_noite_01`), do zero.
