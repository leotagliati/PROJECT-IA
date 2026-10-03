# Fases de treino do GraphExplorer — resumo

O que foi tentado, o que deu certo e o que deu errado, fase por fase. Os números vêm do TensorBoard.
Versão curta: a detalhada, com cada run, fica local (`historico-de-treinos.md`).

**Ao mudar algo:** atualize a fase atual e, se uma tentativa não funcionou (ou foi abandonada),
registre-a em [O que não deu certo](#o-que-não-deu-certo) — o erro documentado é o que evita
repetir o mesmo run.

**Glossário rápido**

- **Agente / seeker**: o personagem que está aprendendo (procura e depois caça).
- **Hider**: a presa. É scriptado, não aprende nada.
- **Nós**: pontos de referência colocados à mão no mapa, ligados como um mapa de metrô.
- **Cobertura**: fração do mapa que o agente visitou no episódio (0 a 1). Desde a Fase 7, fração das
  salas exploradas.
- **Sala / porta**: desde a Fase 7, o mapa é lido como salas (e corredores) ligadas por portas (os vãos).
- **Seta**: dica que aponta para o lugar não visitado mais próximo. É uma "muleta" que queremos tirar.
- **Lição / etapa**: degrau de dificuldade do currículo. O agente só sobe quando atinge a meta.
- **Loops**: voltar a um lugar recém-visitado em vez de ir para um novo.

---

## Visão geral

| # | Quando | Mapa | O que se tentou | Resultado |
|---|---|---|---|---|
| S | jul–set | Map_1..8 | **Seeker** (linha anterior): perseguir o hider com memória em grade | SeekerV1–V3; currículo travou mais de uma vez |
| G | 07–18/09 | Nodes | Primeiro GraphExplorer: regiões + seta que some | Modelo bom em 08/09; o run 05 colapsou ao perder a seta |
| A | 18–20/09 | Nodes_2 | Explorar com seta, ping, hider e visão | Ótimo com a seta; **sem ela, colapsa** |
| B | 20/09 | Nodes_2 | Branch `node-unexplored`: sem seta, com valor por saída e calor | Colapsou (`night_04`); as ideias foram portadas |
| C | 23–26/09 | NodeTraining2 → Node_4 | Grafo gerado automaticamente; currículo v2 | Gerador abandonado; mapa novo feito à mão |
| 0 | 27/09 | Node_4 | Primeiro run no mapa novo | Inválido: grafo quebrado |
| 1 | 27/09 | Node_4 | Explorar **sem seta**, do zero | Não aprendeu (cobertura 13%) |
| 2 | 27/09 | Node_4 | Corrigir física e observações | Melhorou (18% em 500k) |
| 3 | 28/09 | Node_4 | Exploração + patrulha | **Funcionou** (88%), mas 43% do tempo na parede |
| 4 | 28/09 | Node_4 | Tudo num treino só | Adiado, nunca rodou |
| 5 | 28/09 | Node_4 | Procura + ping, do zero | Não aprendeu: mudanças demais de uma vez |
| 6 | 28/09–01/10 | Node_4 | **Em etapas**, uma coisa por vez | E1 ✅, E2 parcial, E3 ✅ no limite |
| 7 | 01/10 → | Node_5 (v4) | **Salas e portas**: toda sala vale igual, porta perde valor ao repetir, sem seta | S1→S6 numa noite: explora ~70% das salas e pega o hider em ~2/3 |
| 7b | 02/10 | Node_5 (v4) | **Planta de salas** (sensor com as 26 salas), ver = explorar, suspeita multiplica | `v4b_noite_01` (parou aos 7.1M): explora 76–83% das salas e pega o hider em 83% (HiderFoge) |
| 7c | 03/10 | Node_5 (v4) | **Fuga a 6 m/s**: hider na velocidade do seeker, caça/suspeita/ping valendo mais, manter em visão | `v4c_fuga_01` preparado (parte do V4B); antes, a fuga scriptada do hider precisa melhorar |

---

## Fases S, G, B e C

Só aparecem em [O que não deu certo](#o-que-não-deu-certo): foram linhas anteriores ou
abandonadas.

## Fase A — Mapa Nodes_2 (runs `graph_*` e `night_*`), 18–20/09

- **O que era:** um mapa de 102 nós. A seta roxa apontava o próximo lugar não visitado. Depois
  entraram:
  - **ping**, um "barulho" num ponto do mapa;
  - o **hider** andando pelo grafo;
  - a **visão em cone**;
  - variações por episódio contra decorar rotas: nascer em lugar aleatório, parte do mapa já
    visitada, peso dos pontos sorteado.
- **O que deu:** com a seta, explorava bem (reward ~11–17). Quando a seta sumia, a exploração
  **despencava**: cobertura de 2–7% sem seta, e colapso no fim do fade.
- **Lição:** a rede aprende a seguir a seta, não a ler o mapa. Sem uma informação local de
  "para onde ainda falta ir", tirar a seta não funciona.

## Fase 0 — Primeiro run no Node_4, 27/09 · inválido

- Uma opção do grafo estava desligada, e só 6 dos 133 nós eram alcançáveis. O agente ficou cego.
- **Lição:** conferir o Console do Unity antes de qualquer run longo.

## Fase 1 — Sem seta, do zero, 27/09

- **Resultado:** 10M steps, cobertura parada em ~13%. No jogo ele travava em quinas, girava e
  atravessava parede.
- **Causas encontradas:**
  - o jeito de mover o corpo teleportava para dentro da parede;
  - os sensores de parede giravam com o corpo, mas as ações não;
  - ele não tinha memória de "de onde vim".

## Fase 2 — Correções de base, 27/09

- **Mudanças:**
  - movimento por velocidade, sem atravessar parede;
  - sensores presos ao mundo;
  - por saída, **quanto ainda falta explorar ali**, **de onde vim** e **quantas vezes já passei**.
- **Resultado:** runs curtos de validação. Em 500k: cobertura 18% contra 5% antes.

## Fase 3 — Exploração + patrulha, 28/09

- **Mudanças:**
  - **patrulha:** um lugar visitado volta a valer depois de um tempo;
  - **tédio de sala:** ficar muito tempo numa sala rende menos;
  - multa por ficar em loop;
  - folga do grafo medida pelo tamanho real do corpo.
- **Resultado (7M steps):** passou por todas as lições e patrulhou **88%** do mapa. Os
  problemas: 43% do tempo encostado em parede, girava sem parar ("beyblade") e os circuitos
  de patrulha eram curtos demais.

## Fase 4 — Tudo num treino só, 28/09 · adiada

- **Preparada:**
  - multa por **batida** na parede e por mudar de direção bruscamente;
  - **movimento livre**: andar e olhar separados, 6 m/s, andar de costas mais devagar e uma
    "rodinha" que desliza na parede;
  - física do corpo corrigida (giro, atrito e voo travados).
- Não rodou: a prioridade passou a ser a procura (fase 5).

## Fase 5 — Procura + ping, do zero, 28/09

- **Ideia nova, o mapa de suspeita:** o agente mantém uma crença de onde o hider pode estar.
  - Ela se espalha com o tempo.
  - Zera onde ele olha.
  - Concentra onde ouviu barulho.
  - É o "esquecer aos poucos" que substitui patrulha e tédio.
- **Resultado:** quatro tentativas (`search_01`–`_04`), todas com o agente parado na sala onde
  nasceu (cobertura 2–3%).
- **Lição:** mudamos movimento, física, procura e punições **ao mesmo tempo** e do zero.
  Quando não aprendeu, não deu para saber o que quebrou.

## Fase 7 — Salas e portas (mapa v4), 01/10 em diante · **atual**

Mapa novo (`NodeTraining5`, cena `Node_5`, 6 arenas) e um jeito novo de pontuar a exploração.
Os nós de exploração viraram **portas** (os vãos entre salas), e o mapa passa a ser lido como
**salas ligadas por portas**: 26 salas e 35 portas, calculadas sozinhas a partir das ligações.

- **Toda sala vale o mesmo**, do armário ao corredor. Saiu a pontuação pelo tamanho do nó.
- Paga **cobrir 80% de uma sala**. Depois disso, o resto da sala vale pouco.
- Paga **atravessar uma porta**, e cada repetição vale metade da anterior. Por isso sair por uma
  porta diferente da que entrou compensa mais que voltar por onde veio.
- Paga **sair de uma sala já explorada**.
- O agente vê **a sala em que está e as portas dela**: quanto cada porta ainda vale, por qual entrou
  e se a sala do outro lado já foi explorada. O que tem atrás de uma porta não entra na conta. **Não
  há seta.**

Por etapas, como na Fase 6. A **S1** treina salas e portas do zero e sem seta. As seguintes são
menos ajuda contra a parede, a patrulha (a porta e a sala mais antigas voltam a valer), o ping por
sala, o hider pingando e, por último, o hider se escondendo.

A S1 rodou ~200k steps sozinha (`v4_s1_01`) e serviu de base para um **run de uma noite com
S1 a S6 seguidas** (`v4_noite_01`, 10M steps, 01→02/10), que chegou à última lição.

| Etapa | Treina | Critério para passar | Resultado (`v4_noite_01`) |
|---|---|---|---|
| **S1** salas e portas | explorar por sala, sem seta | > 60% das salas | ✅ 67% das salas, 13% do tempo na parede |
| **S2** menos assist | menos ajuda contra a parede | manter a cobertura | ✅ 70% das salas, mas parede subiu para ~17% |
| **S3** patrulha | porta e sala mais antigas voltam a valer | — | ⚠️ quase não disparou: só libera com 85% usado, e ele chega a ~75% |
| **S4** ping | ir até o barulho | — | ⚠️ reward +2, cobertura igual; falta métrica de ping para confirmar |
| **S5** hider | achar e pegar o hider (parado → anda → foge) | — | ✅ pega em ~2/3 dos episódios |
| **S6** hider solto | hider se escondendo dentro das salas | — | ✅ pega em ~2/3 (66%), cobertura ~52% |

- **O que funcionou:** sem seta, ele aprendeu a explorar por salas (Fase 6 travava em ~40%) e a caçar.
  O tremor das ações caiu de 0,42 para 0,15.
- **V4B (`v4b_noite_01`, 02/10, do zero, parou aos 7.1M na lição HiderFoge):** sensor com a planta das 26
  salas, "ver" a sala conta como explorar e a suspeita passa a multiplicar o valor de ver (em vez de pagar
  sozinha). Resultado: cobertura de exploração **76–83%** (era ~70%), lições de exploração em 3M steps (eram
  4.7M) e **pega o hider em 83%** (era ~65%). Rewards não são comparáveis com o v4 (a recompensa mudou).
  Pontos fracos: portas repetidas 65% e ~11 loops na Patrulha/Ping, parede ~17% na caça, e não chegou ao
  HiderSolto (entropia ainda 0.82).
- **V4C (`v4c_fuga_01`, 03/10, preparado, ainda não rodou):** parte do cérebro do V4B e treina a fuga com o
  hider a 6 m/s (escada 4.0 → 6.0 → solto). Pedidos: exploração menos valiosa (×0.5), caça bem mais valiosa
  (captura 20 + até 25 por pegar cedo, avistar 1.0, aproximar 0.1/m), **manter o hider em visão** (0.003 por
  decisão), sala suspeita até 4× o valor base e ping valendo na caça. **Risco:** o ping pago foi o que virou
  renda no `night_04`; se a cobertura despencar com o reward subindo, baixar o ping para ~0.3.
  Antes do run, a **fuga do hider scriptado precisa melhorar**: hoje ele só reage a 12 m, olha um passo à
  frente (entra em beco) e pausa ~2 s em cada nó mesmo fugindo. Um hider treinado por IA fica para depois,
  quando a caça estabilizar contra um scriptado bom.
- **O que ainda falta (no v4_noite_01):**
  - **teto de ~70% das salas:** o episódio de exploração nunca terminou por cobertura (sempre por
    tempo), então o bônus de 80% quase não foi pago;
  - metade das travessias de porta é repetida, com ~12 loops por episódio;
  - ~38 batidas na parede por episódio na exploração.

## Fase 6 — Em etapas, 28/09 a 01/10

Cada etapa herda o cérebro da anterior e só avança quando bate um critério medido no TensorBoard.

| Etapa | Treina | Critério para passar | Resultado |
|---|---|---|---|
| **E1** explorar com seta | andar + explorar | cobertura > 0.6 | ✅ **93–96%** em 190k steps |
| **E2** explorar sem seta | tirar a seta | cobertura > 0.6 | ⚠️ parou em ~0.42, fechada por decisão |
| **E3** achar hider parado | procura com o mapa de suspeita | pegar o hider em > 50% dos episódios | ✅ **56%** em 749k, no limite |
| E4 seguir hider que anda | rastro que se move | pegar > 50% | a fazer |
| E5 caçar hider que foge | cortar caminho | pegar > 40% | a fazer |

**O que se aprendeu na E2:**
- Cortar a seta de uma vez derrubou a cobertura de 93% para ~10%. A E1 tinha aprendido a
  seguir a seta, não o mapa.
- **Batente de porta sem multa:** o corpo raspava em quase toda porta e sair da sala custava
  pontos.
- **Aleatoriedade demais:** o parâmetro que mantém a política aleatória estava 5× acima do
  normal para esse tipo de ação.
- **"Quão perto está o inexplorado" por saída**, com recompensa por metro de aproximação:
  levou a cobertura a ~0.43.
- **O gargalo continua sendo o mapa:** o Node_4 é quase uma corrente, e uma porta só separa
  ~37% dos pontos. Com previsited 0.5, a cobertura de 0.42 equivale a ~70% do mapa.

**E3 em números (início → fim):**
- pegou o hider em 25% → **56%** dos episódios;
- episódios mais curtos, 1347 → 1017 decisões (pegar encerra o episódio);
- "suspeita limpa" 0.53 → 0.72.

---

## O que não deu certo

Em ordem cronológica. Cada item: o que foi tentado → o que aconteceu → por quê → o que fizemos.

Fontes: commits do repo (todas as branches), docs antigos (`exploracao-local.md`,
`node-unexplored.md`, `treino-longo.md`, handoffs do placer), comentários dos YAMLs em
`config/` e os eventos de TensorBoard que ainda estão em `results/`. Runs sem número foram
apagados de `results/`, e o que está aqui vem do registro escrito na época.

### Seeker (linha anterior, Map_1..8), julho–setembro

- **Currículo por reward com a recompensa ainda mudando.** Cada ajuste nos pesos da
  recompensa invalidava os thresholds das lições, e isso "custou três runs" (comentário do
  `seeker_curriculum.yaml`). **Feito:** promoção por `progress`, que é por relógio, até a
  recompensa congelar.
- **Lição mínima longa demais.** Com o Map_8 os episódios ficaram mais longos, e os 800
  episódios mínimos por lição passaram a valer ~640k steps em vez de ~140k. O currículo ficou
  travado na primeira lição o run inteiro. **Feito:** 150 episódios.
- Dos runs de teste dessa época só sobrou o `teste2` (11/08, 360k): reward −0.4 → 1.1, na
  lição 1. Os modelos que ficaram são SeekerV1–V3 (PR #7, 05/09). O GraphExplorer começou em
  07/09 como uma linha separada.

### Primeiro GraphExplorer (Nodes), 07–18/09

- **Promoção por relógio (run 05).** As lições viraram aos 500k, 980k e 1.46M
  independentemente do desempenho. A última cortou a seta de 0.5 direto para 0. O reward vinha
  de −4.5 a **+15.1** e **desabou para −3.0**, e o episódio ficou sempre em timeout. **Por
  quê:** foi promovido antes de estar pronto e perdeu a muleta de uma vez. **Feito:** no
  GraphExplorer, promoção só por **desempenho** (`reward`) e seta descendo em escada suave.
  É o inverso da decisão do Seeker, e as duas fazem sentido: relógio serve enquanto a
  recompensa muda; desempenho serve quando ela está fixa.
- **Regiões com bônus de sala (até 18/09).** O primeiro desenho pagava por região, com bônus
  para entrar e para terminar uma sala. **Abandonado:** o peso passou a ser por nó e os bônus
  de sala saíram (`2992dd5`).
- **Decorar rotas (`graph_10`–`_12`, 15–18/09).** O `graph_11` chegou a reward 11.25 em 140k,
  mas seguindo a seta e repetindo as rotas dos 10 pontos de nascimento fixos. **Por quê:**
  nascimento fixo e busca determinística dão sempre a mesma resposta, e decorar compensava.
  **Feito:** pacote anti-decoreba:
  - nascer em qualquer nó;
  - parte do mapa já visitada;
  - seta sorteada entre os 3 alvos mais próximos;
  - depois, peso de cada ponto sorteado por episódio.

### Mapa Nodes_2, 19–20/09

- **Tirar a seta de vez (`db9f983`, runs `graph_14`–`_17`, 19/09).** A seta foi trocada pelo
  "valor por saída" e por um pagamento por aresta nova. Resultado:
  - cobertura **2–7%**;
  - reward −6 a −3;
  - todo episódio em timeout;
  - 51% do tempo na parede (`graph_17`).

  **Revertido no mesmo dia.** **Por quê** (achado depois, na branch `node-unexplored`): os
  raios de parede giravam com o corpo e as ações eram no referencial do mundo. Sem saber para
  onde estava virada, a rede não conseguia usar os raios e batia. Faltava também "de onde vim".
- **Seta sumindo aos poucos (fade; `graph_18`, `night_01`).** O agente ficava ótimo seguindo a
  seta e colapsava quando ela chegava a zero. No `night_01` (2.3M) o reward chegou a 17.6 e
  terminou em 6.9. **Por quê:** diminuir a força só reescala o número; a rede nunca precisou
  olhar os vizinhos e, sem a seta, não sabia sair de um beco. **Feito:** seta fixa em 1.0 na
  config da noite (`night_02`), e a versão sem seta foi para outra branch.

### Branch `node-unexplored`, 20/09 (analisada em 27/09)

- **Sem seta, com valor por saída, mapa de calor e "dropout" de episódios sem seta
  (`night_04`, 5.5M).** Chegou à última etapa, mas:
  - cobertura com pico de 67% e fim em **29%**;
  - revisitas 28% → **86%**;
  - ~48% do tempo na parede;
  - todo episódio em timeout;
  - reward subindo até 40 mesmo assim.

  **Por quê:** o ping pagava a cada chegada do hider, e seguir o rastro dele virou renda. O
  agente trocou explorar por colecionar ping. O `night_03` parou aos 20k.
  **Feito:** a branch não foi mergeada. Três ideias foram portadas para a
  `node-exploration-vision`:
  - o valor por saída, virou `ScoreBeyond` em metros;
  - o calor, virou o mapa de suspeita;
  - o ping, que passou a só informar (paga 0).

### Currículo v2 e mapa gerado, 23–26/09

- **Currículo v2 do zero (`graph_v2_01`, 23/09).** Parado aos 20k porque as lições eram
  lentas demais (~240k steps de piso cada). Ajustado para etapas menores, mas **abandonado**
  junto com o mapa quando o Node_4 foi montado à mão.

### Gerador automático de grafo (`NavGraphPlacer`), 23–26/09

- **Nó dentro de móvel e aresta atravessando mesa.** Os nós tinham sido postos com a mobília
  desligada, e a checagem de ligação passava por cima de móveis baixos (mesa, sofá, baia).
  **Feito:** checagem na altura do corpo inteiro e ferramentas de reposicionar.
- **Agente fora de qualquer nó.** Corredor estreito ficava sem nó, e a limpeza apagava justo
  os nós que cobriam o chão. Resultado: observação velha ou errada. **Feito:** a regra de
  cobertura passou a ser "todo chão dentro de algum nó", e a métrica `OffNodeFraction` mede isso.
- **Grafo denso demais.** O gerado tinha 211 nós contra 102 feitos à mão, arestas de 0.30 m e
  53 arestas com menos de 3 m. **Feito:** regras de espaçamento e limpeza de arestas
  redundantes.
- **Ladrilhamento automático do chão (`cd15116`, 26/09).** Foi trocado no mesmo dia por
  **nós retangulares colocados à mão** (`3702096`). Na sequência, os nós se embolavam nas
  portas, onde vários ladrilhos se encostavam, e entraram **nós de porta** estreitos
  (`51afb2b`). Esse é o Node_4 de hoje.

### Node_4, de 27/09 em diante

- **Grafo de mão única (`node4_01`/`_02`).** Só 6 dos 133 nós eram alcançáveis e o agente
  ficou cego por 6.5M steps. **Por quê:** a opção de espelhar as ligações estava desligada.
  **Feito:** ligar a opção e sempre olhar o Console antes do run.
- **Sem seta, do zero (`noarrow_01`, 10M).** A cobertura travou em ~13%; o agente girava e
  atravessava parede. **Por quê:**
  - o movimento teleportava para dentro da parede;
  - os sensores giravam com o corpo, mas as ações não;
  - ele via só uma aresta;
  - não sabia de onde tinha vindo.

  **Feito:** as correções da Fase 2.
- **Patrulha (`patrol_02`, 7M) — funcionou em parte.** Cobriu 88% do mapa, mas:
  - ficou 43% do tempo na parede;
  - girava como beyblade, porque a rotação física estava livre;
  - fazia circuitos curtos.

  **Feito:** multa por batida, suavidade, rotação travada e patrulha mais lenta.
- **Procura do zero (`search_01`–`_03`).** O agente ficava na sala de nascimento, com
  cobertura ~2%. As causas apareceram uma de cada vez:
  - **`search_01`:** o componente de suspeita estava no objeto errado, e a procura ficou
    desligada.
  - **`search_02`:** a descoberta pagava só 30% desde o início. Do zero, sem nada positivo
    ao alcance, ele não aprende a andar.
  - **`search_03`:**
    - a aceleração de 40 fazia o passeio aleatório tremer no lugar;
    - **o collider do corpo tinha 4.36 m, metade enterrada no chão**, e travava no batente;
    - o olho estava tão baixo que uma mesa tapava toda a visão.
  - **`search_04` (220k):** com aceleração 15 e a seta no começo, ainda com cobertura de
    3%, nenhuma captura e 33% do tempo na parede. O collider e o olho só foram corrigidos
    depois, já na E1.

  **Lição:** mudanças demais num run do zero. Foi isso que levou ao treino em etapas.
- **E2 — seta cortada de uma vez (`e2_01`).** A cobertura caiu de 93% para 7–15%, e o
  reward subia só porque ele batia menos. **Por quê:** a E1 aprendeu a seguir a seta, não a
  ler o "quanto resta" de cada saída.
- **E2 — batente punido (`e2_02`/`_03`).** O corpo raspava em quase toda porta, e sair da
  sala custava pontos. Tirar a multa dobrou a cobertura (~0.25), mas ficaram loops de 5–10
  por episódio. O `e2_03` foi repetição, porque a correção ainda não estava aplicada.
- **E2 — política que não parava de chutar.** A entropia não caiu em nenhum run até o
  `e2_03`. **Por quê:** o `beta` estava em 0.015, cerca de 5× o normal para ações contínuas.
  **Feito:** 0.003.
- **E2 — pisca-pisca de âncora (`e2_05`/`_06`).** Na borda entre dois ladrilhos o "nó atual"
  trocava a cada passo, e a lista de saídas mudava junto. A folga de 0.5 m (`e2_06`) **não
  resolveu**: pisca-pisca 3.2 → 5.8, loops 6.8 → 10.2, cobertura 0.40 → 0.38. A E2 foi
  fechada abaixo do critério (0.42 contra 0.6). O gargalo de uma porta só no Node_4 continua
  em aberto.

---

## Regras que saíram disso

1. **Uma mudança por vez**, com um critério medido antes de seguir.
2. **Os sinais nunca podem se contradizer.** Seta e observação apontando para portas diferentes
   viram cabo de guerra. Patrulha e suspeita juntas são dois relógios de "volte ali".
3. **Muleta tirada de uma vez quebra.** Primeiro dê a informação local, depois tire a muleta.
4. **Reward subindo não quer dizer que melhorou.** Várias vezes o reward subiu só porque o agente
   aprendeu a bater menos (ficar parado). Olhe sempre a cobertura e as capturas junto.
5. **Confira a física e o grafo antes de culpar a recompensa.** Os piores travamentos foram
   corpo com 4 m de altura, grafo desconexo e movimento que teleportava.
6. **Tudo que paga vira renda.** Se um sinal paga toda vez que acontece (como o ping a cada passo
   do hider no `night_04`), o agente aprende a colecionar esse sinal em vez de fazer a tarefa.
7. **Promover por relógio só enquanto a recompensa muda.** Com a recompensa fixa, promova por
   desempenho; por relógio, a lição vira mesmo com o agente sem estar pronto (run 05).

## Próximos passos

- **S1** (salas e portas, do zero, sem seta): `v4_s1_01`. Se passar, S2 (menos ajuda contra a
  parede), depois S3 (patrulha por liberação), S4 (ping por sala), S5 (hider pingando) e S6 (hider
  se escondendo).
- A linha E4/E5 do Node_4 fica parada: os cérebros dela não servem no vetor novo.
- Ideia não planejada: esconderijos embaixo de móveis.
