# Histórico de treinos — GraphExplorer no mapa Node_4

Um registro por fase: o que mudou antes do run, com que config ele rodou e o que deu.
Os números vêm do TensorBoard (média dos últimos 500k steps do run), exceto onde indicado.
Para gerar o `.onnx` de qualquer run: `python tools/best_onnx.py <run-id>`.

**Como ler as métricas:**

| Métrica | O que é | Bom é |
|---|---|---|
| Reward | `Environment/Cumulative Reward` por episódio | subir (mas só compara dentro da mesma lição) |
| Cobertura | `Exploration/Coverage`: fração do peso dos primários visitada | subir |
| Parede | `Exploration/WallContactFraction`: fração do episódio encostado | cair |
| Loops | `Exploration/EarlyRevisits`: chegadas em primário visitado há < 15 s | cair |
| Lição | `Environment/Lesson Number/*` | avançar |
| Duração | `Environment/Episode Length`: 1600 = timeout | cair na exploração (terminou antes); 1600 na patrulha é normal |

---

## Fase 0 — node4_01 / node4_02 (27/09, madrugada) · INVÁLIDO

- **Config:** `graph_node4.yaml` (seta roxa que some aos poucos, 6 lições).
- **Problema:** `Make Links Bidirectional` desligado no NavGraph — as ligações de um lado só
  viraram mão única e só 6 dos 133 nós eram alcançáveis. O agente ficou cego.
- **Resultado:** node4_01 rodou 6.5M steps na lição 0, cobertura 7%. node4_02 parado em 78k.
- **Lição aprendida:** conferir o Console (“grafo desconexo”) antes de qualquer run longo.

## Fase 1 — node4_noarrow_01 (27/09) · sem seta, primeiro run válido

- **Config:** `graph_node4_noarrow.yaml` — sem seta, sem hider/ping, 4 lições por cobertura
  (Perto 30% → Metade 60% → Quase 90% → Tudo). Vetor 69.
- **Resultado (10M steps):** nunca saiu da Perto. Cobertura estabilizou em **~13%** desde os
  3M; reward **−3.3**; quase todo episódio em timeout. No jogo: travava em quinas, girava,
  clipava para fora do mapa.
- **Diagnóstico:**
  - `MovePosition` num Rigidbody dinâmico teleportava o corpo para dentro da parede → o
    depenetrador jogava ele para fora do mapa;
  - raios de parede presos ao corpo, mas ações/observações no referencial do mundo — a rede não
    sabia para onde estava virada, então não conseguia desviar;
  - só enxergava 1 aresta: com todos os vizinhos visitados, nada dizia para onde ir;
  - sem memória de “de onde vim” → A-B-A-B.

## Fase 2 — node4_noarrow_02 / _03 (27/09, noite) · correções de base

- **Mudanças:**
  - movimento por **velocidade** + colisão contínua (fim do clipping);
  - raios num filho `RaysWorld` presos ao mundo, em 360°;
  - por vizinho: **quanto resta explorar por aquela saída** (`NavGraph.ScoreBeyond`, em metros),
    **vim daqui**, **quantas vezes já passei**; `[8]` = encostado em parede;
  - histerese de âncora no `FindNodeAt`;
  - menu **“10. Pesos por área”** (peso ∝ área^0.5 do retângulo, soma 23);
  - o Arthur reduziu o agente de escala 1.7 para 1 em X/Z (raio 1.17 → 0.69 m).
  - Vetor 69 → 93.
- **Resultado:** runs curtos (≤ 470k), só para validar. No _03, aos 500k: cobertura **18%**
  e reward **−1.5** — o _01 estava em 5% e −5.1 no mesmo ponto.

## Fase 3 — node4_patrol_01 / _02 (28/09) · exploração + patrulha

- **Config:** `graph_node4_patrol.yaml` — Perto → Metade → Quase → **Patrulha**.
- **Mudanças:**
  - **folga do grafo medida** do CapsuleCollider do agente (`_useAgentBodySize`);
  - **patrulha:** primário visitado cai a 0 e recupera em 60 s; revisita paga no máximo 30% da
    descoberta (× recuperação²); lição Patrulha sem fim por cobertura (alvo 1.1);
  - **tédio de sala:** `NavNode._areaId` (menu “11. Numerar salas”); ~30 s numa sala a deixa
    chata (paga menos, custa por step acima de 0.7);
  - **revisita precoce:** −0.05 por chegada em primário visitado há < 15 s, a partir da 4ª seguida;
  - métricas `WallContactFraction` e `EarlyRevisits`.
  - Vetor 93 → 102.
- **Resultado (patrol_02, 7M steps):** passou a Perto em **~1M**, a Metade em **1.45M** e a
  Quase em **4.04M**. Na Patrulha: cobertura **88%** do mapa, reward **+14.9**, loops ~11 por
  episódio, mas **43% do tempo encostado em parede**. (patrol_01 = teste de 18k.)
- **Observações do Arthur:** patrulha eficiente demais (circuitos curtos), ainda bate muito e
  gira como beyblade.

## Fase 4 — node4_full_01 (a rodar) · tudo num treino só

- **Config:** `graph_node4_full.yaml` (gerado por `tools/gen_full_curriculum.py`), 24M steps:
  Perto → Metade → Quase → Patrulha → PingSolto → HiderParado → HiderAnda → HiderFoge → FogeRapido.
- **Mudanças desde a fase 3:**
  - **patrulha mais lenta:** nó fica 30 s em zero e recupera em 90 s; sala entediada fica 20 s
    parada e esfria em 2 min;
  - **parede:** contínuo 0.00025 → **0.00075**/step + custo por **batida** (0.03 × batidas nos
    últimos 5 s, até ×5);
  - **suavidade:** −0.0005 × |mudança de ação|² por decisão (contra o giro);
  - corpo só gira acima de 0.5 m/s e a 360°/s (era 720°/s); **Deterministic Inference** ligado
    no prefab (só afeta o teste no editor, não o treino);
  - **captura cedo:** +10 + 15 × fração do episódio que sobrava (capturar encerra o episódio e
    cortava a renda de patrulha);
  - thresholds de exploração −1.0 (4.5 / 6.0 / 5.0) e patrulha/caça 8 com 1000 episódios mínimos;
  - métricas novas: `Exploration/WallHits`, `Movement/ActionJitter`.
  - **movimento livre (A+C)**, ver `docs/graph/movimento-livre.md`:
    - velocidade 20 → **6 m/s** e aceleração 20 → **40** (frenagem de ~10 m para ~0.45 m);
    - 4 ações: mover X/Z + **olhar X/Z** (o cone de visão deixa de ser preso ao movimento), e
      andar de costas a 60% da velocidade;
    - **steer assist** (desliza em vez de bater), com força pelo currículo `steer_assist`
      1.0 → 0.3;
    - observações: para onde está virado, velocidade do hider enquanto vê, nível do assist.
      Vetor 102 → **107**;
    - custo de suavidade do olhar (0.00025 × |Δolhar|²) e métrica `Movement/LookJitter`;
    - **rotação Y do Rigidbody travada** (estava livre: cada batida fazia o corpo girar por
      segundos, o beyblade físico), **corpo sem atrito** (agarrava na parede em vez de deslizar)
      e **altura travada** (às vezes saía voando para cima numa batida).
  - Antes do run longo: um curto de validação (`node4_move_01`, ~1–2M) só para ver parede e
    tremor caírem nas lições de exploração.
- **Resultado:** _preencher ao fim do run._ (adiado: o Arthur quis treinar primeiro a procura, fase 5)

## Fase 5 — node4_search_01 (a rodar) · procura + ping

- **Config:** `graph_node4_search.yaml` (gerado por `tools/gen_full_curriculum.py`), do zero, 15M:
  Parado → AndaDevagar → Anda → Foge → FogeRapido. Hider desde a 1ª lição, sem lições de
  exploração pura. Plano: `docs/graph/procura-e-ping.md`.
- **Mudanças desde a fase 4:**
  - **mapa de suspeita** (`GraphSuspicionMap`): crença de onde o hider está. Espalha na
    velocidade dele, zera onde o seeker olha, concentra no ping e na visão;
  - recompensa por **suspeita limpa** (+0.5 × massa zerada, carência de 10 s por nó);
  - observação: suspeita por saída (11º float por vizinho) + ativa/certeza/idade da pista.
    Vetor 107 → **118**;
  - **barulho do hider** (`hider_noise`): cada chegada vira ping só com essa chance
    (1.0 → 0.25);
  - `ping_reward_scale` 0 (ping só informa) e `discovery_reward_scale` 0.3;
  - patrulha e tédio desligados nesta config (a suspeita substitui os dois);
  - métrica nova: `Search/Cleared`.
- **Tentativas descartadas:**
  - **search_01** (430k): `GraphSuspicionMap` foi parar na raiz da arena, não no agente →
    procura desligada. Cobertura ~2%, nunca viu o hider, reward −9 (estagnação + parede).
  - **search_02** (90k): procura ligada, mas ainda com descoberta 0.3 desde o começo → mesma
    cobertura ~2%. Do zero, sem nada positivo ao alcance, ele não aprende a andar.
  - Correção: `discovery_reward_scale` virou currículo (1.0 na Parado → 0.6 → 0.3) e o
    threshold da Parado subiu para 16 (explorar sem pegar chega a ~14). Próximo: search_03.
  - **search_03** (150k): descoberta cheia, mas continuava sem sair da sala de spawn — cobertura
    2% → 0.2%; o reward subia só porque ele aprendia a bater menos (ficar parado). O teste de
    porta com steer assist 0 e 1 não mostrou diferença (não era o steering).
  - Correção: aceleração 40 → **15** m/s² (a 40 o passeio aleatório tremia no lugar; o Arthur
    também achou rápido demais) e **seta no começo** (`frontier_hint` no currículo: 1.0 na Parado,
    0.5 na AndaDevagar, 0 depois), que dá o gradiente por metro até o próximo não-visitado.
    Threshold da Parado 16 → 18. Próximo: search_04.
  - **search_04** (220k, registrado depois a partir do TensorBoard): cobertura ~3%, Caught 0,
    Seen máx 0.2, parede 0.30 → 0.33, reward −8.7 → −8.5. Mesmo sintoma; o collider de 4.36 m e o
    olho baixo só foram achados depois (Fase 6). Foi o que levou ao treino em etapas.
  - **Seta alinhada com as saídas** (`_frontierFollowsExits`, pedido do Arthur): a seta apontava
    para um não-visitado sorteado entre os 3 mais próximos, e o "quanto resta" por vizinho podia
    indicar outra porta — seguir a observação pagava a descoberta e cobrava o shaping da seta.
    Agora a seta aponta para o vizinho com o maior "quanto resta"; as duas concordam sempre.
- **Resultado:** _preencher ao fim do run._

## Fase 6 — node4_e1_01 … e5_01 (a rodar) · em etapas

- **Por quê:** os search_01..03 mudaram movimento, física, procura e punições de uma vez, num run
  do zero. Quando não aprendeu, não deu para saber o que quebrou. Agora é **uma etapa por vez**,
  cada uma herdando o cérebro da anterior (`--initialize-from`; o vetor 118 é fixo) e com um
  critério de passagem conferido no TensorBoard antes da próxima.
- **Configs** (gerados por `tools/gen_full_curriculum.py`; o comando de cada um está no cabeçalho):

  | Etapa | Config | Treina | Passa quando |
  |---|---|---|---|
  | E1 | `graph_node4_e1_explorar.yaml` (5M, do zero) | movimento + exploração, **com seta** alinhada | cobertura > 0.6, parede < 0.2 |
  | E2 | `graph_node4_e2_semseta.yaml` (4M) | seta 0.5 → 0.2 → 0 | cobertura > 0.6 sem seta |
  | E3 | `graph_node4_e3_achar.yaml` (3M) | procura, hider parado | Hunt/Caught > 0.5 |
  | E4 | `graph_node4_e4_seguir.yaml` (4M) | hider anda, barulho 0.6 → 0.4 | Hunt/Caught > 0.5 |
  | E5 | `graph_node4_e5_cacar.yaml` (5M) | hider foge, 2.2 → 3.2 m/s | Hunt/Caught > 0.4 |

- O `graph_node4_search.yaml` fica como referência (tudo de uma vez); não é mais o caminho.
- **Antes da E1, duas correções físicas** (achadas porque os search_* não saíam das salas):
  - o `CapsuleCollider` do agente tinha **4.36 m de altura** (Height 1.2 × escala Y 3.63), centrado
    no pivô: metade enterrada no chão (daí o "voo") e o topo a ~2.3 m, travando no batente das
    portas. Corrigido no prefab: Height 0.47, Center Y 0.21 → corpo de 1.71 m, do chão a ~1.76 m;
  - olho da visão 0.5 → **1.4** acima do pivô (~1.55 m do chão): a mobília está na layer Wall e
    uma mesa tapava o cone inteiro.
- **Resultado E1 (node4_e1_01):** passou em **190k steps**. Chegou na lição Quase; reward
  −5.4 → **~14.5**; cobertura 13% → **93–96%**; episódios terminando em ~1000 decisões (antes do
  timeout); parede 0.21. Parado aí para seguir.
- **E2 encurtada** (pedido do Arthur, "corta a seta agora"): seta direto a 0 na 1ª lição (assist
  0.7, igual à Quase), depois assist 0.3. Threshold 8 (o reward da E1 inclui ~5–6 do shaping
  da seta). 3M steps.
- **E2 node4_e2_01 (seta cortada de uma vez), 100k:** cobertura 0.93 → **0.07–0.15, parada**;
  o reward subia só porque ele batia menos (contato 0.13 → 0.04) — aprendendo a ficar quieto.
  Carregou o cérebro da E1 certo (`init_path` conferido): a E1 aprendeu a seguir a SETA, não a ler
  o "quanto resta".
- **Parede sem punição nos batentes** (pedido do Arthur): layer nova (`Batente`) nos Door_Hole, que
  é parede para visão/grafo/steering/observação mas não pune contato nem batida
  (`_penaltyFreeWallLayer`). Motivo: corpo de 1.38 m contra vão de ~1.48 m — quase toda passagem de
  porta raspava e contava batida, então sair da sala custava. Próximo: node4_e2_02 com SÓ esta
  mudança (seta continua cortada) — se não resolver, E2 em degraus.
- **Métrica nova `Movement/IdleFraction`** (só medição): fração do episódio abaixo de 0.5 m/s.
  Parar continua permitido (no jogo, parar para olhar é o que assusta); a métrica pega o ótimo
  local "fico quieto e não perco nada".
- **e2_02 (batente sem punição), 510k:** cobertura ~0.25 (dobrou), parado só 2% — mas loops
  5–10/episódio e todo episódio em timeout. e2_03 = repetição do e2_02 (correção não aplicada).
- **Entropia nunca caiu** (E1 1.42 → 1.43, e2_02 1.45, e2_03 1.48 subindo): a política continuava
  com desvio ~1, o de nascença. `beta` 0.015 → **0.003** nas etapas (YAML). **e2_04** (170k):
  entropia 1.48 → 1.45 (passou a cair), cobertura ~0.3, loops ~5. Melhor, mas lento.
- **Pacote de navegação** (para o e2_05, herdando o e2_04):
  - observação: por saída, **quão perto** está o inexplorado mais próximo (campo de distância;
    seguir o 1 só diminui, sem loop). Ocupa o float do tédio por vizinho (sempre 0 nas etapas):
    vetor segue 118;
  - recompensa: **0.01 por metro** que a distância pelo grafo até o inexplorado mais próximo cai
    (e o mesmo cobrado quando sobe), sem depender da seta — voltar de beco por visitados paga;
    ir e voltar soma zero;
  - diagnóstico: `Exploration/AnchorFlicker` (A-B-A em < 2 s andando < 1 m = borda de ladrilho)
    e o log opcional `_logLoopNodes` com os nós mais repetidos.
- **e2_05 (pacote de navegação), 640k:** o melhor da E2 até aqui — cobertura 0.27 → **~0.43**
  (platô desde ~300k), reward −3.2 → ~−0.5, entropia 1.45 → 1.39 (caindo). Mas loops ~8–10 e
  **pisca-pisca subindo (5.7 → 6.9–8.4 por episódio)**: a borda dos ladrilhos trocava a âncora e a
  lista de saídas mudava junto.
- **Folga da âncora** (`NavGraph._anchorHysteresis` = 0.5 m, só Retângulo): o nó atual segue
  âncora até o agente sair 0.5 m dele, e o vizinho só assume depois de ele entrar 0.5 m (ou até a
  metade do vizinho — por causa dos nós de porta, estreitos). Não muda observação nem vetor.
  Próximo: **e2_06** herdando o e2_05. Esperado: AnchorFlicker ~0, loops caindo, cobertura saindo
  do platô.
- **Resultado E2 (node4_e2_06):** 260k steps, lição 0. Reward −0.69 → −0.27 (sobe por bater menos
  em parede, não explorar mais). Cobertura 0.403 → 0.376 (passa do pico). EarlyRevisits 6.8 → 10.2;
  AnchorFlicker 3.2 → 5.8 (ambos pioram). WallHits 18.6 → 14.8 (melhora). **E2 fechada por decisão**
  — E3 herda e2_06.
- **E3 preparada** (`graph_node4_e3_achar.yaml`, herda node4_e2_06): procura + hider parado. O
  "reset aos poucos" que o Arthur queria vem da SUSPEITA (onde olhou zera e volta a crescer), sem
  patrulha junto. Assist fica em 0.7 (onde a E2 parou) e desce 0.5 na E4 e 0.3 na E5.
- **Resultado E3 (node4_e3_01), 749k steps, lição 0** (início → média dos últimos 5 pontos):
  **Hunt/Caught 0.25 → 0.56** (critério > 0.5 cruzado só nos últimos pontos), Hunt/Seen 0.41 → 0.48,
  **Episode Length 1347 → 1017** (capturar encerra), Search/Cleared 0.53 → 0.72, reward 3.2 → 10.7,
  cobertura ~0.37, loops 3.5 → 5.5, entropia 1.35 → 1.28. Critério atingido no limite. `.onnx` de
  validação: `Assets/GraphExplorer_node4_e3_01_748k.onnx`. Próximo: `--resume` do e3_01 (~300k) para
  confirmar Caught > 0.5 estável, ou direto a E4 com `--initialize-from=node4_e3_01`.

## Fase 7 — v4_s1_01 (01/10) · salas e portas (mapa v4, NodeTraining5 / cena Node_5)

- **Por quê:** a E2 sem seta travou em cobertura ~0.42 e o valor por nó/por área dava muitos sinais
  puxando para lados diferentes. A ideia agora é mudar a unidade do mapa: em vez de "pontos que valem
  peso", o mapa é feito de **salas** ligadas por **portas**, e a recompensa vem de conhecer salas.
  Plano completo: `docs/graph/salas-e-portas.md`. Ainda **sem treino**.
- **Config:** `config/graph_v4_s1_salas.yaml` (gerado por `tools/gen_full_curriculum.py`), **do zero**,
  5M steps, `beta` 0.003, sem seta, mínimo de 80 episódios por lição. Lições (cobertura = fração de
  SALAS concluídas):

  | Lição | Salas a concluir | Steer assist | Threshold de reward |
  |---|---|---|---|
  | Perto | 0.2 | 1.0 | 6.0 |
  | Metade | 0.5 | 1.0 | 9.0 |
  | Quase | 0.8 | 0.7 | (última) |

- **Comando:** `mlagents-learn config/graph_v4_s1_salas.yaml --run-id=v4_s1_01`
- **Mudanças (código, já implementadas):**
  - **Primário virou Porta:** `NodeKind.Primary` agora é `NodeKind.Door` ("Porta", mesmo valor 0). No
    NodeTraining5 os primários estão nos vãos de porta.
  - **Salas calculadas no bake** (`NavGraph`): tira as portas e cada pedaço conexo que sobra vira uma
    sala. No NodeTraining5: **26 salas, 35 portas, máximo de 7 portas por sala, 11 salas de 1 nó**.
    Menu de contexto novo **"Relatório de salas e portas"** (no NavGraph e no NavGraphPlacer, menu 11,
    que antes era "Numerar salas").
  - **Saiu:** peso por nó e por área (menus 8 e 10 do placer, `_explorationNodeScore`,
    `_auxiliaryNodeScore`, `DiscoveryValue`), `weight_jitter`, patrulha por tempo
    (`value_recovery_seconds`), tédio de sala (`area_boredom`), seta de fronteira (`frontier_hint`,
    `frontier_hint_steps`) e o shaping dela, `_newEdgeReward`, `_nodeCoverageReward`.
  - **Componente novo `GraphRoomMemory`** (um por agente): cobertura por sala (80% dos nós pisados =
    concluída, `room_complete_threshold`); **novidade de porta** (1 → 0.5 → 0.25 a cada travessia; em
    sala de 1 porta, ida+volta conta como uma travessia só); **liberação** da porta e da sala mais
    antigas (`release_fraction`, a volta passa a valer 0.5; desligada na S1); "quanto resta" por saída restrito
    à sala. **Nada paga aproximar-se de um alvo escolhido por algoritmo** (um "alvo local" com 0.01/m
    existiu na primeira versão e saiu no mesmo dia: era a seta por outro caminho).
  - **Recompensa nova** (`GraphRewardSystem`, seção "Salas e portas"): sala descoberta 0.25 (paga aos
    poucos por nó até 80%); cauda depois de concluída ×0.2; sala concluída 0.25; porta 0.1 × novidade;
    saída de sala concluída 0.15 × novidade; **+5** ao atingir
    `coverage_target`. Penalidades (existencial, parede, batida, suavidade, estagnação, revisita precoce
    em PORTA) **iguais** às anteriores. Teto de recompensa ~25 no mapa inteiro.
  - **Observação 118 → 182** floats: 30 globais ([7] = fração de salas; [9..12] = progresso da sala
    atual / concluída / estou num vão / nº de portas ÷ 8; [21] = calor da sala, 0 por enquanto) + 8
    vizinhos × 10 (saiu o peso) + 8 portas × 9 (direção, distância, distância pelo grafo, novidade,
    entrei por aqui, sala do outro lado concluída, calor, válido). `VectorObservationSize` do
    NodeTraining5 ajustado para 182. **Os `.onnx` anteriores não servem.**
- **Critério de passagem** (na lição Quase): `Exploration/Coverage` > 0.6, `WallContactFraction` < 0.2,
  `Doors/RepeatFraction` caindo, `Episode Length` caindo. Métricas novas: `Rooms/Completed`,
  `Doors/Crossings`, `Doors/RepeatFraction`, `Doors/UsedFraction`.
- **Risco conhecido:** do zero e sem seta (a E2 sem seta travou em cobertura ~0.42). Se travar na
  Perto com `Movement/IdleFraction` alto, subir `_doorCrossReward` primeiro (continua sendo evento).
- **Resultado:** run interrompido de propósito aos **~196k steps**, na lição Perto (a primeira tentativa,
  de 106k, foi sobrescrita com `--force`). Reward **−4.2 → 1.4**, cobertura **0.10 → 0.18**, parede
  **0.108 → 0.064**, batidas por episódio **37 → 15.5**. Serviu de base (`--initialize-from`) para o
  `v4_noite_01`.

### Run da noite v4_noite_01 (01/10 → 02/10)

- **Por quê:** o Arthur pediu S1→S6 num run só de ~8 h para deixar treinando à noite, herdando o
  cérebro do `v4_s1_01`. Ele aprendia rápido: estava na lição Perto com ~200k steps, a ~400 steps/s.
- **Comando:** `mlagents-learn config/graph_v4_noite.yaml --run-id=v4_noite_01 --initialize-from=v4_s1_01`
- **Config:** `config/graph_v4_noite.yaml` (gerada por `tools/gen_full_curriculum.py`), `max_steps` 10M,
  checkpoint a cada 500k com `keep_checkpoints` 20. O run começou com `beta` 0.003, parou em 765k (00:20),
  o config foi editado para `beta` **0.006** às 00:22 e o run foi retomado com `--resume` até os 10M
  (07:06). A primeira tentativa (01/10 23:44) caiu aos 37k com "Communicator has exited" e foi refeita
  com `--force`. 10 lições:

  | Lição | O que muda | Threshold / passagem |
  |---|---|---|
  | Perto | salas 0.2 | reward 6.0 |
  | Metade | salas 0.5 | reward 9.0 |
  | Quase | salas 0.8, assist 0.7 | reward 11.0 |
  | MenosAssist | assist 0.3, 30% das salas já nascem concluídas | reward 8.5 |
  | Patrulha | `release_fraction` 0.85, sem fim por cobertura | progress 0.30 |
  | Ping | `ping_interval` 2000 | progress 0.40 |
  | HiderParado | hider parado | progress 0.52 |
  | HiderAnda | hider a 1.0 m/s, barulho 0.6 | progress 0.64 |
  | HiderFoge | hider a 2.2 m/s, barulho 0.4 | progress 0.78 |
  | HiderSolto | barulho 0.3, `hider_loose` 1 | final |

  Nas lições de caça: `discovery_reward_scale` 0.5, `ping_reward_scale` 0, `vision_explores` 1,
  `release` 0. `min_lesson_length` 80/80/150/150 nas quatro primeiras e 300 nas de progresso.
- **Código novo desta noite (S4–S6), compilado mas NÃO testado no Play antes do run:**
  - **Ping por sala:** `NavGraph.DrawEpisodePingNodes` sorteia 1 nó por sala por episódio (sala de 1 nó
    entra com `ping_single_room_chance` 0.5); o ping aleatório e o barulho do hider usam esses nós.
  - **Sala quente:** o ping que começa esquenta a sala dele (`GraphRoomMemory.HeatRoom`). Se a sala estava
    concluída, volta a ser explorável e vale ×2 (`_hotRoomValue`) até ser concluída de novo; só uma sala
    quente por vez. Observação [21] = sala atual quente; o float de calor da porta = porta da sala quente.
  - **Saiu o `_pingApproachPerMeter`** (pagava por metro de aproximação do ping pelo grafo): o Arthur não
    quer IA que segue alvo calculado por algoritmo. O ping paga só chegar (+2) e cobra expirar (−0.5).
    Mais cedo no mesmo dia já tinha saído o "alvo local" (0.01/m até nó/porta escolhido por algoritmo),
    pelo mesmo motivo.
  - **Ver conta como explorar** (`vision_explores`): nós da sala atual dentro do cone de visão contam
    como pisados.
  - **Hider solto** (`hider_loose`): anda para pontos aleatórios dentro do retângulo dos nós; metade das
    vezes escolhe o ponto visto pelo menor número de portas da sala e pausa 3× mais (esconderijo); se a
    reta bate em parede, passa antes pelo centro do nó.
- **Contra overtraining:** variação por episódio (spawn, salas pré-concluídas, nós de ping, hider),
  exploração continua pagando na caça (0.5), 20 checkpoints para voltar ao melhor
  (`tools/best_onnx.py`), LR e entropia lineares até `max_steps`.
- **O que olhar de manhã:** `Environment/Lesson Number`, `Exploration/Coverage`, `Doors/RepeatFraction`,
  `Hunt/Seen`, `Hunt/Caught`, `WallContactFraction`.
- **Resultado:** chegou à última lição (HiderSolto) e rodou até os 10M. Início de cada lição: Perto 0,
  Metade 320k, Quase 1.39M, MenosAssist 2.80M, Patrulha 4.24M, Ping 4.72M, HiderParado 5.20M,
  HiderAnda 5.46M, HiderFoge 6.41M, HiderSolto 7.81M. Início → fim de cada lição:

  | Lição | Reward | Duração | Cobertura (salas) | Parede | Batidas/ep | Portas repetidas | Viu / Pegou |
  |---|---|---|---|---|---|---|---|
  | Perto | 2.2→7.0 | 1150→608 | 0.19→0.23 | 0.06→0.10 | 15→11 | 0.26→0.16 | – |
  | Metade | 3.6→8.9 | 1524→1360 | 0.33→0.46 | 0.08→0.12 | 25→28 | 0.51→0.44 | – |
  | Quase | 8.5→11.4 | 1583→1598 | 0.53→0.67 | 0.12→0.13 | 31→36 | 0.51→0.46 | – |
  | MenosAssist | 7.8→8.2 | 1572→1574 | 0.67→0.70 | 0.17→0.16 | 40→36 | 0.50→0.49 | – |
  | Patrulha | 12.6→12.4 | 1592→1599 | 0.72→0.70 | 0.16→0.17 | 38 | 0.50→0.53 | – |
  | Ping | 14.4→14.7 | 1599 | 0.71→0.72 | 0.18→0.17 | 38→39 | 0.51→0.50 | – |
  | HiderParado | 16.1→18.4 | 1017→853 | 0.58→0.56 | 0.21→0.19 | 22→20 | 0.30→0.27 | 0.81/0.64 → 0.75/0.73 |
  | HiderAnda | 16.7→18.8 | 882→817 | 0.50→0.51 | 0.19→0.18 | 19→18 | 0.28→0.26 | 0.73/0.65 → 0.78/0.75 |
  | HiderFoge | 15.7→15.0 | 969→1011 | 0.52 | 0.16→0.15 | 20 | 0.32→0.36 | 0.71/0.59 → 0.67/0.58 |
  | HiderSolto | 15.4→16.8 | 972→931 | 0.49→0.53 | 0.15→0.16 | 19→18 | 0.32→0.30 | 0.69/0.60 → 0.72/0.66 |

  No último 1M, estável: reward ~17, cobertura ~0.52, pegou ~0.67, parede ~0.155. Tremor de ação
  0.42 → 0.15 no run todo; tremor do olhar ficou ~0.40.
- **Observações:**
  - **Teto de ~70% das salas** na exploração (18–19 de 26 concluídas, ~78% das portas usadas): nas
    lições de exploração o episódio nunca acabou por cobertura (duração 1599 = timeout), então o +5 de
    80% quase nunca foi pago. Critério da S1 na Quase: cobertura > 0.6 ok (0.67), parede < 0.2 ok (0.13),
    portas repetidas caindo ok (0.51 → 0.46), duração caindo **não** (1598).
  - **Portas repetidas** ~50% das travessias e ~12 loops por episódio nas lições de exploração.
  - **Parede subiu com o assist cortado:** 0.13 (assist 0.7) → ~0.17 (assist 0.3), ~38 batidas/ep.
  - **Patrulha não mudou o comportamento:** a liberação só dispara com 85% das portas usadas ou 85% das
    salas concluídas (`GraphRoomMemory.ReleaseOldest`), e ele chega a ~78% / ~70%, então quase não
    disparou; a lição virou "Quase sem fim por cobertura".
  - **Ping:** o reward subiu ~2 (12.4 → 14.4) sem mudar cobertura nem portas; não existe métrica de ping
    (nenhuma tag `Ping/*`), então não dá para confirmar chegadas/expirações.
  - **Caça funcionou:** pega o hider em ~2/3 dos episódios, inclusive com o hider solto fugindo a 2.2 m/s.
  - **HiderParado só teve 250k** (o progresso 0.52 já tinha vencido quando começou).
  - **Melhores checkpoints:** Quase 2499990 (cobertura 0.645), MenosAssist 3999963 (cobertura 0.713),
    HiderSolto 8499873 (R100k 17.4, parede 0.144) e o final 10000010 (R100k 16.8, parede 0.169).

- **Resultado (médias por lição, 10M steps, chegou à lição final HiderSolto aos 7.81M):**

  | L | Lição | entrou | Reward | Ep.Len | Coverage | Salas | Parede | Entropy |
  |---|---|---|---|---|---|---|---|---|
  | 0 | Perto | 10k | 4.09 | 909 | 20.4% | 5.3 | 7.9% | 1.39 |
  | 1 | Metade | 320k | 6.27 | 1482 | 40.0% | 10.4 | 9.3% | 1.35 |
  | 2 | Quase | 1.39M | 9.95 | 1597 | 60.7% | 15.8 | 13.0% | 1.25 |
  | 3 | MenosAssist | 2.80M | 7.92 | 1572 | 68.7% | 12.4 | 16.5% | 1.14 |
  | 4 | Patrulha | 4.24M | 12.56 | 1597 | 70.5% | 18.2 | 16.5% | 1.08 |
  | 5 | Ping | 4.72M | 14.50 | 1599 | 71.1% | 19.3 | 17.3% | 1.05 |
  | 6 | HiderParado | 5.20M | 17.49 | 891 | 54.5% | 14.2 | 19.5% | 1.03 |
  | 7 | HiderAnda | 5.46M | 17.34 | 902 | 53.4% | 14.2 | 17.8% | 1.01 |
  | 8 | HiderFoge | 6.41M | 15.79 | 977 | 52.3% | 14.0 | 17.7% | 0.98 |
  | 9 | HiderSolto | 7.81M | 16.72 | 925 | 51.4% | 13.6 | 15.2% | 0.95 |

  Caça (L6-9): Hunt/Seen 76% → 75% → 71% → 71%; Hunt/Caught 70% → 68% → 62% → 65%;
  Search/Cleared 1.04 → 1.38 → 1.42 → 1.35; Doors/RepeatFraction ~27-31%.
- **Leitura:** funcionou (pega o hider em ~2/3 dos episódios), mas com hider a cobertura cai a cada
  lição enquanto a suspeita limpa sobe: "limpar suspeita" (0.5 cheio) e explorar (×0.5) competiam e a
  suspeita ganhou. O Arthur viu que as salas S24 (corredor de 20 nós em anel, 7 portas) e S25 (sala de
  23×24 m, 1 nó, dentro da S24) quase nunca eram vistas: sala igual paga pouco por nó no corredor e, de
  longe, nada dizia que a S25 existia. Ressalva: o run rodou sem as correções de prefab (Wall Layer do
  NavGraph sem a layer Obstacle dos Door_Hole, então a visão atravessava essas paredes; hider na layer
  Default, então os raios de 360° o detectavam).

### V4.1 v4.1_noite_01 (antes v4b_noite_01) (02/10) · planta de salas + ver tudo

- **Comando:** `mlagents-learn config/graph_v4.1_noite.yaml --run-id=v4.1_noite_01` (DO ZERO: o sensor
  novo muda a rede, sem `--initialize-from`).
- **Config:** mesmas 10 lições e critérios do `v4_noite_01`, 10M steps, 20 checkpoints.
- **Prefab (fazer antes, no Unity):** Graph > Wall Layer = Wall + Obstacle; Hider > Layer = Ignore Raycast.
- **Mudanças:**
  - **(a) Planta de salas:** `BufferSensor` "Rooms" (criado em runtime no Awake do
    `GraphExplorerManager`; 10 floats por sala, até 32): direção X/Z até o centro, distância/diâmetro,
    portas até lá/10, quanto já viu, concluída, quente, suspeita normalizada, é a atual, nº de portas/8.
    Assim o agente sabe que a S25 existe mesmo de longe.
  - **(b) Explorar = ver desde a lição 1:** nós de QUALQUER sala dentro do cone contam como vistos.
  - **(c) Suspeita multiplica:** saiu o `_suspicionClearedReward`; a suspeita (razão da crença média da
    sala contra a média geral) multiplica o valor de ver a sala em até 3× e REABRE sala concluída quando
    passa de 2× (exceto a atual). Na caça, `discovery_reward_scale` 1.0 e sem liberação por tempo. Por
    quê: limpar suspeita competia com explorar e ganhava.
  - **(d) Sala continua valendo igual** (decisão do Arthur).
  - **(e) Spawn por sala** (sala sorteada por igual, nó aleatório; hider em sala diferente) e `beta` 0.006.
- **Resultado:** run parado aos **~7.17M de 10M steps**, na lição 9 de 10 (HiderFoge); **não chegou à
  HiderSolto** e não gerou `.onnx` final, só checkpoints até 7169211. Médias por lição:

  | Lição | entrou | Reward | Cobertura | Parede | Batidas/ep | Portas repetidas | Loops | Viu | Pegou | Duração |
  |---|---|---|---|---|---|---|---|---|---|---|
  | Perto | 10k | 2.89 | 18.7% | 7.4% | 16.8 | 29.5% | 2.14 | – | – | 1005 |
  | Metade | 380k | 7.78 | 41.4% | 7.6% | 20.3 | 45.0% | 3.76 | – | – | 1209 |
  | Quase | 670k | 10.95 | 68.2% | 11.8% | 31.8 | 48.8% | 5.58 | – | – | 1535 |
  | MenosAssist | 1.07M | 9.98 | 75.9% | 16.4% | 32.7 | 47.6% | 4.15 | – | – | 1380 |
  | Patrulha | 1.28M | 15.37 | 83.2% | 12.8% | 32.3 | 65.0% | 10.75 | – | – | 1599 |
  | Ping | 3.01M | 16.94 | 78.6% | 15.0% | 31.3 | 67.0% | 11.33 | – | – | 1599 |
  | HiderParado | 4.01M | 32.30 | 61.3% | 18.4% | 19.9 | 32.1% | 4.56 | 70.3% | 58.7% | 923 |
  | HiderAnda | 5.21M | 31.82 | 59.4% | 18.3% | 21.2 | 31.9% | 3.64 | 75.9% | 76.3% | 825 |
  | HiderFoge | 6.41M | 34.07 | 59.0% | 17.5% | 20.9 | 32.3% | 4.04 | 83.7% | 83.3% | 786 |

- **Leitura:**
  1. **A planta de salas funcionou:** cobertura de exploração de 76–83% (na v4.0: ~70%) e as lições 1–6
     terminaram em 3M steps (na v4.0: 4.7M).
  2. **A caça melhorou:** pega o hider em 83% dos episódios na HiderFoge (na v4.0: ~61–65%).
  3. **Os rewards NÃO são comparáveis com os da v4.0:** a suspeita agora multiplica a descoberta e o bônus de
     captura cresce com o tempo que sobrava no episódio.
  4. **A queda de cobertura na caça é em parte esperada:** capturar encerra o episódio (duração ~800), então
     sobra menos tempo para explorar. Falta uma métrica de cobertura por step para separar isso de piora real.
  5. **Pontos fracos:** portas repetidas subiram de 48% para 65–67% e os loops de 4 para 11 na Patrulha e no
     Ping (a liberação de 0.85 agora dispara); na caça a parede fica em ~17% com ~20 batidas por episódio;
     a entropia estava em 0.82 ao parar (o `beta` decai linear até 10M).
- **Próximos passos:** escolher o checkpoint de HiderFoge entre 6.5M e 7.0M (`tools/best_onnx.py
  v4.1_noite_01 --list`) e decidir entre `--resume` até os 10M (para chegar à HiderSolto) ou um run novo.

- **Retomada (02/10 à noite → 03/10):** o run foi retomado com `--resume` aos 7.18M (lição HiderFoge) e
  encerrado em seguida pelo Arthur para partir para a v4.2 (antes chamada V5). Nas poucas dezenas de milhares de steps do
  resume: reward 33.2, cobertura 54%, parede 17.8%, Hunt/Seen 0.81, Hunt/Caught **0.87**. Sem `.onnx` final
  do run (o último export foi o checkpoint 7169211; a pasta do run tem um `GraphExplorer.onnx` desse ponto).
  A primeira tentativa de resume deu `UnityTimeOutException`: o Play do Unity não foi apertado em ~60 s
  depois do "Listening on port 5004" (o Claude não aperta o Play — o Arthur roda o treino e o Play).

### Modelo v4.1 promovido (03/10)

- O cérebro do `v4.1_noite_01` aos 7.17M (HiderFoge, captura ~85%) virou o **modelo v4.1**:
  `Assets/GraphExplorer_v4.1.onnx` (pesos juntados num arquivo único com `onnx.save(save_as_external_data=False)`,
  porque o export grava os pesos em `.onnx.data`, que o Unity não acha).
- O que era "V4C" (depois "V5": `config/graph_v5_fuga.yaml`, run-id `v5_fuga_01`) nunca rodou com esse nome; virou a
  v4.3 (`config/graph_v4.3_caca.yaml`, `v4.3_caca_01`, `--initialize-from=v4.1_noite_01`). Existe
  `results/v4c_fuga_01` (41k steps, teste interrompido, fica com o nome antigo): ignorar.
- Convenção (03/10): o mapa v4 é a família v4 e um ajuste na mesma tarefa é v4.x; v5 só quando mudar o mapa ou
  a forma de treinar. Renomeados no disco: v4_noite_01 (v4.0, modelo `Assets/GraphExplorer_v4.0.onnx`),
  v4b → v4.1 e v5 → v4.2.

### V4.2 v4.2_noite_01 (antes v5_noite_01) (03/10) · do zero: caça com hider rápido, calor do ping, raios duplos · **abandonado aos 3.72M**

- **Comando:** `mlagents-learn config/graph_v4.2_noite.yaml --run-id=v4.2_noite_01` (DO ZERO: o tamanho da
  observação mudou, a v4.1 e o modelo de 41k do `v4c_fuga_01` não carregam e não servem de `--initialize-from`).
- **Config:** `config/graph_v4.2_noite.yaml` (gerado por `tools/gen_full_curriculum.py`, `V4_2_*`), 10M steps,
  as 10 lições da v4.1 com thresholds de reward ~1 abaixo (parede custa o dobro).
- **Mudanças (pedidos do Arthur em 03/10):**
  - **Percepção:** 2 `RayPerceptionSensor3D` no `NodeTraining5` (RaysHigh e RaysLow, 6 raios por lado = 13,
    alcance 15 m, 160°), cada um com `WorldAlignedSensor._heightOffset` (0 e -0.12; ajustar no Inspector).
  - **Movimento (`SeekerMovementSystem`, prefab):** `_moveSpeed` 20, `_acceleration` 7 (eram 40 e 15), novo
    `_brakeAcceleration` 30 (freia quando a velocidade desejada é menor que a atual), `_frictionlessBody` 0 e novo
    `_bodyFriction` 1. Steering assist 0.6→0.1 (default 0.1).
  - **Parede:** `_wallContactPenalty` 0.0015, `_wallHitPenalty` 0.1.
  - **Caça:** captura 20 + 25 cedo, avistar 2, aproximar 0.4/m, manter em visão 0.004/step (`_hiderInViewReward`),
    ping chegado 5, suspeita zerada 1.0 (`_suspicionClearedReward`, voltou), exploração 0.5× na caça.
    **Correção:** `OnActionReceived` roda todo step de física; a 1ª versão (0.003) foi calculada por decisão.
  - **Calor do ping (`GraphRoomMemory`):** calor por sala = 0.65^portas até a sala do ping, meia-vida 25 s;
    `_pingQuietScale` 0.25 (explorar no frio vale menos), `_heatValueBoost` 4. As observações antes binárias
    (`CurrentRoomHot`, `DoorIsHot`, Rooms[6]) agora são contínuas (`CurrentRoomHeat`, `DoorHeat`, `RoomHeat`).
  - **Hider:** `_defaultHiderSpeed` 10 (arena e suspeita), `_maxPauseSteps` 0, `_fleeRadius` 18; defaults de arena
    para o Inference: hider modo 3, 10 m/s, noise 0.7, steer 0.1, visão 1, cobertura 1.1.
- **Risco conhecido:** ping pago na caça (`ping_reward_scale` 1.0) é o que virou renda no `night_04`; se a
  cobertura cair com o reward subindo, baixar para ~0.3.
- **Não feito (planejado em `plano-fuga-do-hider.md`):** olhada de dois nós na fuga do hider; hider por IA.
- **Dúvida aberta:** `_moveSpeed` 40 no prefab contra "6 m/s" na documentação: velocidade real não medida.
- **Resultado (3.72M steps, ~6 h, interrompido com Ctrl+C):** passou a Perto só aos **3.42M** (v4.0: 320k, v4.1: 380k) e travou na Metade. Mesma janela de currículo (início da Metade, ~290k steps): reward −0.14 (v4.1 7.78), cobertura 22.5% (v4.1 41.4%), duração 1558–1599 (todo episódio no timeout), parede 0.2% (v4.1 7.6%), batidas 0.7/ep (v4.1 20), idle 10% (v4.1 2.3%), loops 9.1 (v4.1 3.8).
- **Leitura:** a parede "resolveu" do jeito errado: ele aprendeu a não chegar perto de parede nenhuma (o que num mapa de salas e portas é não passar em porta), andando mais devagar e parando mais. Causa: cinco mudanças juntas contra a parede (velocidade 40→20, aceleração 15→7, atrito 1, assist 1.0→0.6, contato ×2 e batida ×3.3) + sensor novo que obrigou a treinar do zero. Com a velocidade efetiva menor, a cobertura de 50% nem cabe bem no episódio. Os schedules lineares (LR e beta até 10M) também já tinham gasto ~37% enquanto ele ainda estava na lição 1.
- **Decisão:** não retomar. Voltar ao cérebro da v4.1 (`--initialize-from=v4.1_noite_01`) e mudar uma coisa por vez.

### V4.3 v4.3_caca_01 (03/10) · caça herdando a v4.1 · **parado aos 532k**

- **Comando:** `mlagents-learn config/graph_v4.3_caca.yaml --run-id=v4.3_caca_01 --initialize-from=v4.1_noite_01`
- **Config:** `config/graph_v4.3_caca.yaml` (gerado por `tools/gen_full_curriculum.py`, bloco `V4_3_*`), 5M steps,
  beta 0.003, 4 lições por progresso: FogeLenta 4.0 m/s (0.20), FogeMedia 6.0 (0.45), FogeRapida 8.0 (0.70),
  Solto 8.0 + `hider_loose`.
- **Mudanças:**
  - **Cérebro:** parte do da v4.1 (`--initialize-from`); muda só a caça.
  - **Hider:** sem pausa nos nós, raio de fuga 18 m (era 12), velocidade em escada 4 → 6 → 8 m/s e depois solto
    (a v4.1 parou em 2.2).
  - **Recompensas:** captura 20 + até 25 por pegar cedo, avistar 2, aproximar 0.4/m, manter em visão 0.004/step,
    suspeita zerada 1.0; exploração ×0.5 na caça.
  - **Calor do ping** no mapa inteiro; ping chegado 5 × `ping_reward_scale` 0.4 = 2.
  - **Voltaram aos da v4.1:** física (40 m/s, aceleração 15, sem atrito), parede (0.00075 contínuo, 0.03 por
    batida), steer assist 0.3 e sensor de raios (1 × 9 raios a 20 m). O 2º sensor (`RaysWorldLow`) está
    desativado no prefab, porque com ele o `--initialize-from` não carrega.
  - **Critério:** `Hunt/Caught` não cair abaixo de ~60% na FogeMedia/FogeRapida; abaixo de 40%, segurar a
    velocidade.
- **Resultado (532k steps, ~1 h, lição FogeLenta, hider a 4 m/s):** o Unity parou de responder aos ~530k e o run foi interrompido. Médias por 100k: viu 85–89%, **pegou 65–78%** (0–100k 77%, 300–400k 78%, 400–500k 72%), duração ~870–1030, parede 15–19%, ~23–26 batidas/episódio. Pegar ficou estável, sem tendência: vê quase sempre, mas ~1 em 6 escapa depois de visto (na v4.1 viu = pegou). Modelo promovido: checkpoint 532.706 → `Assets/GraphExplorer_v4.3.onnx` (empate com o 499.941 em Hunt/Caught 0.72; `tools/best_onnx.py --metric Hunt/Caught`).
- **Leitura:** o limite não é a recompensa, é o movimento: a 40 m/s com aceleração 15 ele leva 53 m para parar, não faz curva sem a parede e usa o steer assist + corpo sem atrito como trilho. Daí a v4.4.

### V4.4 v4.4_movimento_01 (03/10) · **cancelado antes de rodar**

- Plano de movimento com aceleração (10 m/s, aceleração 35, freio 50, `_syncVelocityWithBody`, steer assist 0.3 → 0). Substituído pela V4.4 corrida abaixo, a pedido do Arthur: sem aceleração, corpo seguindo o movimento e corrida. `config/graph_v4.4_movimento.yaml` foi apagado.

### V4.4 v4.4_corrida_01 (03/10) · corrida: sem aceleração, estamina, corpo segue o movimento · **não rodou, substituída pela V5.0**

- **Comando:** `mlagents-learn config/graph_v4.4_corrida.yaml --run-id=v4.4_corrida_01 --initialize-from=v4.3_caca_01`
- **Config:** `config/graph_v4.4_corrida.yaml` (gerado, bloco `V4_4_*`), 5M steps, beta 0.003, 5 lições por progresso: Lenta (hider corre 4, 0.15), Media (6, 0.35), Rapida (8, 0.55), MuitoRapida (10, 0.75), Solta (10, `hider_loose`). Hider com 3 s de estamina, barulho 0.4 → 0.3, exploração ×0.3, ping ×0.4, sem fim por cobertura. `steer_assist` saiu do currículo (e do C#); `hider_stamina` entrou.
- **Mudanças (pedido do Arthur, 03/10):**
  - **`GraphLocomotion` (novo, no agente; criado em runtime se faltar):** velocidade por estado, correndo — patrulha 15, alerta 18 (calor do mapa ≥ 0.5, ~25 s depois de cada ping), perseguição 20 (vendo o hider + `_chaseBoostSeconds` 3 s); andando ×2/3 (10 / 12 / 13.3). Corre com |andar| ≥ 0.9 enquanto há estamina (`GraphStamina`: 5 s, recupera 0.5 s/s, volta a correr com 25%). Pescoço: o olhar vira a cabeça até 60° (360°/s) e o cone de visão segue a cabeça (`GraphHiderPerception.SetViewDirection`).
  - **`SeekerMovementSystem.MoveFacing`** (substitui `Move(direção, olhar)`): sem aceleração; gira o corpo a `_turnSpeed` e anda só para a frente × alinhamento com o pedido (de costas = gira parado). `_moveSpeed`/`_acceleration` não valem mais para o GraphExplorer. Steer assist desligado (a arena não lê mais `steer_assist`).
  - **Observação [26]:** estamina (0..1) no lugar do steer assist (0.3 fixo na v4.3). Vetor 182 e 4 ações iguais.
  - **`GraphHider`:** anda a `_walkFraction` (2/3) e corre só fugindo, com estamina (`hider_stamina`); `hider_speed` agora é a velocidade CORRENDO.
  - **Recompensa:** `_wallContactPenalty` 0.00075 → 0.0015, `_wallHitPenalty` 0.03 → 0.06 (layer Wall); `_penaltyFreeWallLayer` = Door + Obstacle (as paredes Door_Hole passam para a layer Door); `_suspicionClearedReward` 1 → 2.
  - **Métricas novas:** `Movement/RunFraction`, `Movement/ChaseFraction`.
- **Risco:** muita coisa muda junto (movimento, velocidade, parede, suspeita), contra a regra de uma mudança por etapa; o que segura é herdar a v4.3 e o hider começar lento. Se `Hunt/Seen` despencar logo no início, o problema é o movimento novo (o cone segue a cabeça, que vira só 60°), não o hider.
- **Critério:** Hunt/Caught ≥ ~65% na MuitoRapida, WallContactFraction < 0.15, WallHits caindo, RunFraction entre ~0.1 e ~0.5.
- **Resultado:** não rodou; substituída pela V5.0 (`v5.0_zero_01`) em 04/10.

### V4.5 v4.5_fuga_01 (03/10) · fuga: hider com mais estamina que o seeker · **não rodou, substituída pela V5.0**

- **Comando:** `mlagents-learn config/graph_v4.5_fuga.yaml --run-id=v4.5_fuga_01 --initialize-from=v4.4_corrida_01`
- **Config:** `config/graph_v4.5_fuga.yaml` (gerado, bloco `V4_5_*`), 5M steps, 4 lições por progresso: FogeIgual (hider corre 10, 6 s de estamina, 0.25), FogeForte (12, 8 s, 0.50), FogeLonge (14, 10 s, 0.75), Solta (14, 10 s, `hider_loose`).
- **Mudanças:** só o currículo do hider; código e prefab iguais aos da v4.4. A 14 m/s o hider correndo foge do seeker andando na perseguição (13.3) e o fôlego dele é 2× o do seeker.
- **Critério:** Hunt/Caught ≥ ~50% na FogeLonge sem Hunt/Seen cair abaixo de 80%. Abaixo de 30% na FogeForte: voltar a 12 m/s / 8 s antes de mexer em recompensa.
- **Resultado:** não rodou; substituída pela V5.0 (`v5.0_zero_01`) em 04/10.

---

## Fase 8 — v5.0_zero_01 (04/10/2026) · V5.0 DO ZERO: corpo na escala do jogador · **planejado**

- **Por quê:** a v4.x treinava um corpo e um hider em escalas que não são as do jogo (a v4.3 chegou a 40 m/s). Agora o seeker e o hider andam como o jogador de verdade, e como a rede muda (vetor de observação maior) não dá para herdar um cérebro antigo: é do zero. **Substitui os planos v4.4_corrida e v4.5_fuga, que nunca rodaram.**
- **Comando:** `mlagents-learn config/graph_v5.0_zero.yaml --run-id=v5.0_zero_01` (sem `--initialize-from`).
- **Config:** `config/graph_v5.0_zero.yaml` (gerado por `tools/gen_full_curriculum.py`, bloco `V5_STAGES`), 15M steps, beta 0.006, checkpoint a cada 500k (30 guardados). Roda num prefab NOVO do NodeTraining (o Arthur está criando a partir do NodeTraining5); mapa v4 igual: 26 salas, 35 portas.
- **Currículo (11 lições):**

  | Lição | Passagem |
  |---|---|
  | Perto, Metade, Quase, Variado | por reward: 6 / 9 / 11 / 8.5 |
  | Patrulha | por progresso 0.25 |
  | Ping (`ping_interval` 4000) | por progresso 0.33 |
  | HiderParado | por progresso 0.42 |
  | HiderAnda | por progresso 0.52 |
  | HiderFoge | por progresso 0.64 |
  | HiderRapido | por progresso 0.78 |
  | HiderJogador (`hider_loose` 1) | final |

  Lições de progresso com no mínimo 150 episódios. Escada do hider: `hider_speed` 5.1 → 7 → 8.5 → 10.2, estamina 10 s. "Ver = explorar" vale desde a lição 1.
- **Mudanças (em relação à v4.x):**
  - **Refatoração antes (sem mudar comportamento):** o código do Graph foi separado em pastas (Agent, Memory, Hunt, Arena, Map), com `GraphObservations`, `GraphBodyTracker` e `GraphEpisodeSettings` novos; a `GraphRoomMemory` passou para dentro da `GraphExplorationMemory` e a recompensa de término para dentro do `GraphRewardSystem`. Ver `docs/graph/arquitetura.md`.
  - **Corpo na escala do jogador** (PlayerDummy da main: andar 6 m/s, correr ×1.7 = 10.2 m/s; fôlego de 10 s de corrida, enche em 6 s depois de 1 s parado, volta a correr com 30%). `GraphLocomotion`: marchas fixas parado / anda 6 / corre 10.2 (|andar| ≥ 0.9 corre), com inércia (acelera 20, freia 40 m/s²), giro de 540°/s andando e 360°/s correndo; bater na parede custa velocidade; saíram os estados de alerta e de perseguição. `GraphStamina` copia o modelo do `PlayerStamina`.
  - **Hider na mesma escala:** 10.2 m/s correndo, anda a 0.588× disso.
  - **Episódio de 20000 steps de física = 400 s** (era 8000 = 160 s). Os custos por step foram divididos por 2.5: parede 0.0006, suavidade 0.0002 (corpo) / 0.0001 (olhar), estagnação 0.0002 depois de 2500 steps, hider em vista 0.0015/step. Revisita precoce passa a janela de 1500 steps (30 s).
  - **Observação 182 → 188** (+6 globais, [30..35]): velocidade própria X/Z ÷ 10.2, correndo, sem fôlego, fração do episódio, tempo sem progresso ÷ 2500. Métrica nova `Movement/MeanSpeed`; saiu `Movement/ChaseFraction`.
  - **Valor das salas:** toda sala vale o mesmo na exploração (já era assim); só calor do ping, suspeita e sala já explorada (cauda ×0.2) mudam o valor. Confirmado com o Arthur.
  - **Velocidade pelo estado de alerta (substitui a corrida/fôlego do seeker, pedido do Arthur):** Patrulha 6 m/s (sem pista), Alerta 8 m/s (ping começou ou perdeu o alvo de vista há < 10 s, `_alertSeconds`), Perseguição 10.2 m/s (vendo o alvo). Sem corrida por ação nem fôlego no seeker (o `GraphStamina` fica só no hider). Observação: [26] = velocidade do estado ÷ 10.2, [32] em perseguição, [33] fração restante do alerta (substituem fôlego/correndo/sem fôlego). Métricas `Movement/ChaseFraction`, `Movement/AlertFraction` (saiu `RunFraction`). `GraphAnimationSystem` novo (molde do `SeekerAnimationSystem`): `moveSpeed` 0/1/2, andar na patrulha e no alerta, correr na perseguição; opcional, desliga no treino.
  - **Sala 100% vista:** explorar é pela visão (vision_explores 1 o run inteiro) e a sala só conclui com TODOS os nós vistos ou pisados (`room_complete_threshold` 1.0; degrau 0.8 → 0.9 → 1.0 nas lições Perto/Metade/Quase, depois 1.0 até o fim). Pedido do Arthur: nenhum canto com nó fica sem olhar. Canto sem nó não conta.
  - **Mapa medido:** ~6100 m² andáveis, diâmetro de 248 m pelo grafo, ~1100 m de ligações. Alguns cantos de corredor ficam sem nó de propósito (o Arthur não quer adicionar), então `OffNodeFraction` vai ser maior que 0: esperado.
- **Critério de passagem:** Coverage > 0.7 na Quase; `Hunt/Caught` ≥ ~60% na HiderFoge e ≥ ~40% na HiderJogador; `WallContactFraction` < 0.15; `RunFraction` entre 0.1 e 0.5; `MeanSpeed` entre 6 e 10.
- **Resultado v5.0_zero_02 (3.15M steps, ~13 h em build standalone com --no-graphics, 3 envs, 41 s/10k steps):** passou a Perto em **~1.47M**, a Metade em **~3.1M** (meta trocada no meio para coverage 1.0 / thresholds ajustados). Parado na Quase com `Exploration/Coverage` **~0.44**. Problema: decorou um **loop de salas baratas** (1 nó) perto do spawn e ignorava as grandes e do fundo da planta (S7, S8, S9, S14, S17, S18, S23, S24, S25). Testadas (e revertidas): 3ª travessia de porta sem paga; salas pré-concluídas 0.2; salas marcadas 2×; valor crescente por cobertura. Causa raiz identificada: valor fixo por sala (independente do tamanho) + `room_complete_threshold` de 0.8 (à ¿¿¿¿¿) virou "sala de 1 nó = fácil, logo vale explorar"; o grafo tinha ~1100 m de ligações e ele só cobria ~50 m de raio do spawn. **Abandonado em favor da v5.1**, que muda a economia: sala só paga completando e valor cresce com o tamanho.

## Fase 9 — v5.1_zero_01 (04/10/2026 à noite) · economia de salas: paga só completar, valor crescente · **concluída em 50M (07/10/2026)**

- **Por quê:** a v5.0 decorou um padrão barato (salas de 1 nó) e ignorou as grandes, porque toda sala valia igual ao completar. Agora a recompensa vem só de COMPLETAR a sala (não de explorar incrementalmente) e o valor cresce com o tamanho.
- **Comando:** `mlagents-learn config/graph_v5.1_zero.yaml --run-id=v5.1_zero_01` (DO ZERO, sem `--initialize-from`).
- **Config:** `config/graph_v5.1_zero.yaml` (gerado por `tools/gen_full_curriculum.py`), 15M steps, beta 0.006, checkpoint a cada 500k (30 guardados). Build Builds/V5/PROJECT-IA.exe com `--num-envs=3 --no-graphics`.
- **Currículo (5 lições de exploração + 4 de caça):**

  | Lição | O que muda | Threshold |
  |---|---|---|
  | Inicio | coverage 0.8 de nós VISTOS ou PISADOS | reward 10 |
  | Meio | coverage 0.9 | reward 15 |
  | Completo | coverage 1.0 | reward 21 |
  | Patrulha | sem fim por cobertura, Ω por progresso | progress 0.50 |
  | HiderParado | hider parado no mapa | progress 0.65 |
  | HiderAnda | hider a 6 m/s | progress 0.75 |
  | HiderRapido | hider a 10.2 m/s | progress 0.85 |
  | HiderSolto | hider solto (aleatório no mapa) | final |

  `min_lesson_length` 150. Exploração continua pagando na caça (×0.5).
- **Mudanças de economia (código em `GraphRewardSystem` + `GraphRoomMemory`):**
  - **Fora:** fatias de progresso (0.25 por nó até 80%), cauda ×0.2, `_roomExploreReward`.
  - **Novo:** `_roomCompletedReward` 0.5 (paga UMA VEZ ao atingir `room_complete_threshold`); nada cresce de forma contínua na sala.
  - **Porta:** paga `_doorCrossReward` 0.1 só a 1ª vez (`_doorPaidCrossings` 1); beco (1 porta): a volta paga igual `_becoReturnPaid` 1 (ida + volta = 1 travessia).
  - **Saída de sala concluída:** paga `_releasedRewardOnExit` 0.15 × novidade só a 1ª vez.
  - **Valor por sala crescente:** `GraphRoomMemory._progressValueGain` 1 (primeira sala = 1, última ≈ 2); valores observados em [11] por porta/sala.
  - **Mapa medido:** 26 salas, 11 de 1 nó. Sala de 1 nó conclui ao pisá-la; sala de 10 nós precisa de 8–9 pisados/vistos (80–90%). Teto esperado: ~13 (0.5 × 26 salas completadas).
- **Observação 188 igual; Rooms buffer agora 26 floats** (sala de referência para alinhar com 26 salas, vetor 188 + 260 = 448; `BufferSensor.ObservationSize` = 260, computado em runtime).
- **Currículo do hider:** escada igual aos da v5.0. Critério: Hunt/Caught ≥ ~60% na HiderRapido e ≥ ~40% na HiderSolto.
- **Meta visual (diferente da v5.0):** todas as 26 salas alcançáveis desde a lição 1 (sem salas pré-concluídas, `previsited_fraction` 0). Pedido do Arthur.
- **Planta com hops até a sala do ping:** `GraphRoomMemory.ComputeRoomHops` mede hops de grafo; planta transmite `RoomDistanceByHops ÷ 16` (era ÷10, saturava). Sem "bússola por porta".
- **Bake:** `_maxNodeDistance` 28 m (checar no play da NodeTraining5; campo no NavGraph se vazio). Espera-se SEM pisca-pisca de âncora (`AnchorFlicker` ~0).
- **Métricas a olhar no TensorBoard:** `Exploration/Coverage` (0.8 → 0.9 → 1.0 pelas lições), `Rooms/Completed`, `Doors/Crossings`, `WallContactFraction`, `Movement/ActionJitter`, `Hunt/Seen`, `Hunt/Caught` na caça.
- **Risco:** sala de 1 nó pode enganar ("completou fácil"), mas são só 11 de 26 — o grosso é tamanho. Se travar em ~0.5 de cobertura após ~3M steps, próximo passo é **LSTM** num run novo (agora a rede vê só a planta atual, não percebe padrão temporal).

- **Resultado (ajustes no meio do run, 05/10/2026):** o run começou com a config de 15M e beta 0.006; abaixo seguem os ajustes aplicados.

### Ajuste 1 — ~9.8M steps

- **Mudança:** `--resume` com `beta` 0.012 (dobrando: entropia estava alta) e `max_steps` 25M (era 15M, para ter mais tempo nas lições de caça).
- **Status:** mudanças só no YAML / aprendizado.

### Ajuste 2 — ~11.9M steps: spawn em salas raras + episódio de 700 s

- **Mudanças:**
  - **Spawn:** `_spawnFavorsRareRooms` ligado, peso 0.2 + (1 − taxa de conclusão da sala) para cada sala (nasce com mais frequência em salas exploradas pouco), e `_rarityValueGain` 2.0 (salas raras valem até 2× ao concluir, caindo conforme exploram).
  - **Episódio:** 20000 → **35000 steps** (400 s → 700 s); custos por step **reescalados pelo mesmo fator de 1.75** para manter o teto de penalidades igual (parede 0.0006 → 0.00034, suavidade 0.0002 → 0.00011, estagnação 0.0002 → 0.00011, hider em vista 0.0015 → 0.00086).
  - **Métricas:** `Rooms/S00..S25` (uma por sala, value/progress, para rastrear cada uma no TensorBoard).
- **Build novo:** `Builds/V5/PROJECT-IA.exe` compilado com `--env="Builds/V5/PROJECT-IA.exe" --no-graphics --num-envs=3`.
- **Resultado:** **platô até ~14.4M steps:** cobertura ~0.75 (19–20 salas / 26), parede ~0.15, loops ~6, reward ~12; depois **salto em 15.4–15.9M:** coverage 0.94, **24–24.4 salas concluídas**, reward ~22.4, parede ~0.14. A ala S14/S17/S18/S23/S25 saiu de 0% a ~100% (inclusive S17, anel pequeno de 8 nós em volta de S18). **Passou à lição 3 (Patrulha) em ~15.93M steps.**
- **Exceção:** S24 (anel de 20 nós, ~32×36 m, ao redor de S25) ficou em 0%: as 2 portas de S25 ficam no meio dos lados do perímetro, a visão alcança 15 m e os cantos estão a ~18 m de distância. Ir de uma porta a um canto não pagava progressão de cobertura (nó longe demais para ver completo do meio da sala).

### Ajuste 3 — ~16M steps (a aplicar): recompensa por nó em salas grandes

- **Mudança (código):** salas com ≥ 10 nós (S12, S16, S24) recebem também `_bigRoomCrumbReward` (migalhas): 0.5 × valor da sala dividido pelos nós da sala, pago ao pisar/ver nó novo na sala (se a sala ainda não está concluída). Motivo: S24 com 20 nós é árdua de concluir em episódio de 700 s andando a 6 m/s (perímetro ~100 m), então não paga nem um canto isolado que toma 15 s para alcançar.
- **Status:** _ainda a aplicar com build novo + `--resume` do último checkpoint._

### Ajuste 4 — ~30.9M steps (HiderFoge, a aplicar): porta é parede, procura e ping só sem o alvo à vista

- **Visto no jogo:** o monstro ficava preso nas portas de vez em quando, e uma vez não foi atrás do hider que estava bem na frente dele.
- **Visto no TensorBoard:** a captura funciona (`Hunt/Caught` ~0.91 na HiderFoge, ~0.99 na HiderAnda), mas a reward por episódio passa muito do teto da caça (~495 na HiderParado em 24M, ~146 agora) e `Rooms/Completed` chega a 146 por episódio com ~15 salas diferentes: salas reabrindo e pagando de novo. Causa: com hider, sala concluída reabre quando a suspeita dela passa de 2x a média e vale até 8x (suspeita) x 2 (progresso) x 2 (rara) x ~8.5 (calor do ping). Perder o hider de vista e varrer a vizinhança rendia mais que pegá-lo (pegar encerra o episódio). Da HiderAnda para a HiderFoge, a reward subiu (100 → 146) enquanto a captura caiu e o episódio dobrou.
- **Mudanças:**
  - **Door_Hole na layer Wall** (68 overrides no `NodeTraining - V5 training.prefab`; o botão do `GraphArenaController` agora põe tudo em Wall). Porta custa como parede. Risco: na v4.2 o custo cheio ensinou a evitar portas; olhar `Doors/UsedFraction` e `WallContactFraction`.
  - **Suspeita zerada enquanto vê o hider**; ao perder de vista, ela nasce no anel de nós mais próximo fora da vista a partir de onde ele sumiu, com peso extra na direção em que corria (`GraphSuspicionMap.SeedFromLoss`, `_headingBias` 2).
  - **Ping some com o alvo à vista** (sem contar como perdido, métrica `Ping/Silenced`) e o calor da sala do ping é apagado (`GraphRoomMemory.ClearHeat`).
  - **Suspeita não reabre sala concluída** (`GraphRoomMemory`; reabria a 2x a média). Sala paga uma vez, como na exploração.
  - **Teto de 4x no valor de uma sala** (`_maxValueScale`): 4 = progresso 2 x rara 2, o máximo da exploração pura, que fica igual; corta só suspeita (até 8x) e calor do ping (até ~8.5x) empilhados.
  - **Exploração na caça x0.2** (era 0.5) e **x0 em HiderRapido/HiderJogador** (`discovery_reward_scale`, gerador).
  - **Hider fugindo sem hesitar** (`GraphHider`): não pausa nos nós enquanto foge; continua fugindo 3 s depois de o seeker sair dos 12 m (`_fleeMemorySeconds`; antes alternava fugir/vaguear na borda); dá meia-volta no meio da aresta se o destino o leva para o seeker (uma vez por aresta).
- **Critério:** `Rooms/Completed` ≤ ~27 por episódio, reward por episódio dentro do teto da caça (~45 + exploração), `Hunt/Caught` subindo e `Episode Length` caindo na HiderFoge, `Doors/UsedFraction` sem cair.
- **Resultado (31.14M → 34.97M, 06/10 11:10–15:27, build novo, parado com Ctrl+C na HiderFoge):** farm eliminado. Reward ~141 → ~58–63, estável; `Losses/Value Loss` 34 → ~3; `Rooms/Completed` ~17–20 (< 27). `Hunt/Caught` 0.93 → 0.97, `Hunt/Seen` ~0.99, `Episode Length` 1960 → 1510 decisões (pega mais rápido). Contato com parede (agora inclui porta) 0.10 → 0.045 (antes: 0.04 parede + 0.16 porta). `Doors/UsedFraction` ~0.54 e `Coverage` ~0.58 (caíram por a exploração valer 0.2 e o episódio acabar mais cedo). `Search/Cleared` estável ~4 (sem farm novo). `Ping/Silenced` ~1.9/episódio. Entropia 0.40 → 0.28. Modelo: `results/v5.1_zero_01/GraphExplorer.onnx` (34.97M). Faltam ~1M para a HiderRapido (36M).

### Ajuste 5 — ~36.8M steps (HiderRapido, a aplicar): perseguição que gruda e visão do alvo mais firme

- **Visto no jogo (modo de jogo, modelo de 36.79M):** contra o jogador, o monstro perde a visão muito fácil e sai do vermelho logo. Causas: o hider do treino é mais lento (7-8.5 m/s), só foge a 12 m, sem quebrar linha de visão, e anda pelo centro dos nós; ao perder de vista o monstro caía para 8.5 m/s com o jogador a 10.2; um raio só até o centro do alvo e o cone de 100° preso à cabeça.
- **Mudanças:**
  - **Perseguição segura 3 s** depois de perder de vista (`GraphLocomotion._chaseHoldSeconds`): vermelho, 10 m/s; o Alerta conta depois.
  - **Visão do alvo** (`GraphHiderPerception.CanSeeTarget`, só o alvo): já vendo, cone 160° / 20 m; a < 4 m sem cone; 3 raios (centro + ombros ±0.35 m); 0.5 s de tolerância sem linha livre.
  - **Métricas** `Hunt/Sightings`, `Hunt/LostSight` (≥ 1 s), `Hunt/SightToCatchSeconds`.
  - **Ver o alvo manda em tudo:** visão 22 m (era 15) e 30 m já vendo; aproximar vendo paga 1.0/m (era 0.4), ~+20 do limite da visão até pegar.
  - **Foco no alvo** (`GraphObservations.FocusLevel`): vendo o alvo, exploração e pistas zeradas (só geometria, corpo e visão); perdeu de vista há < 20 s (`_searchFocusSeconds`), exploração zerada e suspeita/calor/ping valendo; depois disso, tudo de volta. Mesmo vetor.
  - **Ping do alvo esmaece** em vez de expirar: força na observação [13] (era 0/1), meia-vida 10 s desde a última renovação; só some quando outro toca, o seeker chega ou o alvo é visto.
  - **Hider solto já na HiderRapido** (`hider_loose` 1, era só na HiderJogador): no jogo o seeker ia até o NÓ do jogador e parava no centro; o hider do treino sempre andou no centro dos nós.
  - **Audição de corrida** (`GraphPingSystem`): alvo correndo a ≤ 30 m pelo grafo é ouvido; o ping vai para o nó dele e o acompanha enquanto é ouvido, o seeker entra em Perseguição; ping de alvo some 10 s depois da última renovação (era 60 s). Métrica `Ping/Heard`. No treino o hider só corre fugindo, então isso pesa contra a fuga dele.
- **Critério:** `Hunt/LostSight` por episódio caindo, `Hunt/SightToCatchSeconds` caindo, `Hunt/Caught` alto na HiderRapido; no jogo, despistar exige quebrar a linha de visão de verdade, não um passo de lado.
  - **Hider foge como gente** (`GraphHider`): foge ao VER o seeker (linha livre, até 25 m, `_fleeSightDistance`), não só a 12 m; na escolha da fuga, vizinho fora da vista do seeker vale +10 m (`_hiddenExitBonus`): dobra esquinas e passa portas em vez de correr reto à vista.
  - **Visão mira o CORPO do alvo** (cabeça e peito do collider, mais ombros), saindo do olho a 2.78 m; antes o ponto subia até a altura do olho e a linha passava por cima de qualquer móvel mais baixo (via o jogador agachado atrás de armário). Nós continuam na linha horizontal.
  - **Raios de parede do sensor só em Wall/Obstacle/Door** (máscara 648; era tudo menos Ignore Raycast): o jogador (Default) aparecia como obstáculo, o hider do treino (Ignore Raycast) não.
- **Resultado parcial (36.79M → 38.36M, build das 20:20, 5 envs × 5 arenas, ~0.8M/h):** `Hunt/Caught` caiu de ~0.95 para ~0.86 (0.76–0.97), episódio ~2300–2900 decisões, `Hunt/LostSight` ~12 por episódio, ~130 s da 1ª vista à captura, entropia 0.18 → 0.15. Reward ~100 (acima do teto da caça, ~45): **farm de aproximação** — pagava todo metro encurtado vendo sem descontar o que o hider abria fora da vista; com 1.0/m perseguir rendia mais que pegar.
  - **Correção:** aproximação paga só batendo o RECORDE de proximidade do episódio (teto ~20–30); avistar (+2) só na 1ª vista do episódio (saiu `_respotCooldownSteps`).
- **Resultado final (38.4M → 50M, 06/10 22:58 → 07/10 ~11:30, build das 22:57 com a aproximação por recorde):** reward ~60–67 estável (era ~100 com o farm de aproximação). `Hunt/Caught` 0.92 → 0.99; `Episode Length` 1803 → 1273 decisões; `Hunt/LostSight` ~8.5 → ~5 por episódio; `Hunt/SightToCatchSeconds` ~90 → ~84. `WallContactFraction` 0.084 → 0.02; `DoorContactFraction` (batente, métrica nova) 0.058 → ~0.02; `IdleFraction` 0.11 → 0.03; `MeanSpeed` 7.4 → 8.0; entropia 0.13 → -0.04 (schedule linear chegando ao fim). Lição HiderJogador desde 42.0M. Modelo: `Assets/GraphExplorer_05_50M.onnx`.
  - **Atenção:** o `.onnx` exportado sai com os pesos num `.onnx.data` separado; a cópia em `Assets` foi regravada com os pesos embutidos em 08/10 (antes o monstro não se mexia no jogo).
  - **No jogo:** bem melhor que o 36M, mas ainda perde o jogador (o jogador corre a 10.2 contra 10 do monstro, e ao sumir de vista o monstro ia até onde ele sumiu).
- **Sem variável nova de estado:** a política já infere vendo/procurando/patrulha por [16], [32], [33], [29] e pelo próprio apagão da exploração; crescer o vetor obrigaria a treinar do zero.

---

## Fase 10 — v5.2_caca_01 (08/10/2026) · caça herdando o 50M: mais rápido, previsão ao perder de vista, pressa, hider fora do centro · **a rodar**

- **Por quê:** o 50M pega o hider do treino em 99% mas o jogador escapa (ver Fase 9).
- **Comando:** `mlagents-learn config/graph_v5.2_caca.yaml --run-id=v5.2_caca_01 --initialize-from=v5.1_zero_01 --env="Builds/V6/PROJECT-IA.exe" --no-graphics --num-envs=3`
- **Config:** `config/graph_v5.2_caca.yaml` (gerado por `tools/gen_full_curriculum.py`, `V5_2_STAGES`), 8M steps, LR 1e-4 e beta 0.003 lineares (ajuste fino), gamma 0.998, time_horizon 256. A v5.1 foi para `config/historico/`.
- **Lições:** HiderFoge (foge, 8.5 m/s, progresso 0.30) → HiderJogador (10.2, final). Ambas com `hider_loose` 1, fôlego 10 s, barulho 0.3, exploração x0, ping x0.
- **Mudanças no código:**
  - **Mais rápido** (`GraphLocomotion`): perseguição 11.5 m/s (era 10), alerta 9.5 (era 8.5), giro na perseguição 540°/s (era 360).
  - **Previsão ao perder de vista** (`GraphHiderPerception.PredictedPosition` + observação [18..20]): sem ver, aponta para a última posição vista projetada com a velocidade vista suavizada (0.3 s), até 2 s, parando 0.6 m antes de parede. Só dados que o monstro viu; nada paga ir até lá.
  - **Pressa** (`GraphRewardSystem._huntPressurePenalty` 0.0008/step enquanto vê o alvo ou procura, `IsSearching`, < 20 s; novo campo `GraphStepContext.HuntingTarget`): vendo, cancela o +0.00086 de "em vista".
  - **Hider fora do centro** (`GraphHider`): com `hider_loose` o destino fica a ≥ 0.6 m do centro do nó (`_minCenterOffset`), portas inclusive; 8 sorteios (`_looseCandidates`, era 2); desvio por parede refaz o ponto em vez de cair no centro do nó de destino.
  - Vetor 188 igual.
  - **(09/10) O código acima ficou só num stash do GitHub Desktop até 09/10 e não rodou;** voltou para o working tree junto com:
  - **Rastro** (`GraphHiderPerception._trackMemorySeconds` 3): depois de ver, segue "vendo" (posição real, Perseguição, cone travado) por 3 s sem linha livre. Era 0.5 s (`_trackGraceSteps` 25). A previsão e a audição de corrida entram quando o rastro solta. **Recusado pelo Arthur no mesmo dia:** 5–15 s de rastro e "sentir" o alvo a < 6–15 m através de parede (exagero; esconder perto tem que funcionar).
  - **Rota NavMesh em [18..20]** (`GraphHiderPerception.ChaseAim`): direção da próxima quina do caminho NavMesh até o alvo (reta livre = o próprio alvo), distância = metros pelo caminho. No jogo o monstro empacava em móvel ou num canto sem nó com o jogador do outro lado. Sem NavMesh assado, cai na reta (aviso no Console). Gizmo vermelho-vivo no `PerceptionSystem`. Exceção combinada com o Arthur à regra "sem seta": só na perseguição, com a posição conhecida, sem recompensa.
  - **Hider fora dos nós** (`GraphHider._offNodeChance` 0.4, `_offNodeReach` 4 m): 40% dos esconderijos ficam fora da área de qualquer nó (cantos sem nó, atrás de móvel), com reta livre do centro do nó.
  - **Prefab de treino novo:** `NodeTraining - V6 Training.prefab` (o Arthur montou em 09/10), com `NavMeshSurface` assado.
- **Critério:** `Hunt/Caught` > 0.95 na HiderJogador; `Hunt/LostSight` e `Hunt/SightToCatchSeconds` abaixo de ~5 e ~84; `Movement/ChaseFraction` sem subir; `WallContactFraction` < 0.05; no jogo (V5 - Test) o jogador correndo em linha reta é pego.
- **Resultado:** _a preencher_.

---

## Modelo para a próxima fase

```
## Fase N — <run-id> (<data>) · <resumo em meia linha>

- **Config:** <arquivo>, <steps>, <lições>.
- **Mudanças:** <o que mudou no código/prefab/config desde a fase anterior, e por quê>.
- **Resultado:** <lição alcançada e em que step; reward, cobertura, parede, loops, caça>.
- **Observações:** <o que se viu no jogo>.
```
