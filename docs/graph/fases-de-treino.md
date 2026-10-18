# Fases de treino do monstro — resumo

O que foi tentado, o que deu certo e o que deu errado, versão por versão. Os números vêm do TensorBoard.
Versão curta: a detalhada, com cada run, fica local (`historico-de-treinos.md`).

**Ao mudar algo:** atualize a versão atual e, se uma tentativa não funcionou (ou foi abandonada),
registre-a em [O que não deu certo](#o-que-não-deu-certo) — o erro documentado é o que evita
repetir o mesmo run.

**Glossário rápido**

- **Agente / seeker / monstro**: o personagem que está aprendendo (procura e depois caça).
- **Hider / presa**: quem é caçado no treino. É scriptado, não aprende nada. No jogo, é o jogador.
- **Nós**: pontos de referência colocados à mão no mapa, ligados como um mapa de metrô (v4 a v6).
- **Cobertura**: fração do mapa que o agente explorou no episódio (0 a 1). Da v5 em diante, fração das
  salas exploradas.
- **Sala / porta**: da v5 em diante, o mapa é lido como salas (e corredores) ligadas por portas (os vãos).
- **Seta**: dica que aponta para o lugar não visitado mais próximo. Foi a "muleta" da v4; saiu na v5.
- **Suspeita**: a crença do monstro sobre onde a presa pode estar. Espalha com o tempo e zera onde ele olha.
- **NavMesh**: o chão andável que o Unity calcula sozinho a partir do mapa. Na v6 dá a rota na perseguição; da
  v7 em diante substitui os nós.
- **Pontos de visão** (v7 em diante): pontos gerados sozinhos pelo NavMesh que o monstro precisa VER para
  explorar. Ele não anda por eles.
- **Lição / etapa**: degrau de dificuldade do currículo. O agente só sobe quando atinge a meta.
- **Loops**: voltar a um lugar recém-visitado em vez de ir para um novo.

---

## Versões

**Como ler o número (decidido em 16/10/2026):** `vM.m.p`

- **M (major)** é o **paradigma**: como o monstro enxerga e anda pelo mapa.
- **m (minor)** é um **marco**: mapa novo ou cérebro novo que virou referência.
- **p (patch)** são as **tentativas e ajustes que saíram daquele marco**: ajuste fino herdando o cérebro, variante
  do zero, plano que nunca rodou. Sem patch = o marco em si.

| Versão | Paradigma | Quando | Código |
|---|---|---|---|
| **v1–v3** | **Seeker**: perseguir o hider com memória em grade | jul–set | `Assets/Scripts/Seeker/` |
| **v4** | **Grafo de nós com seta**: explorar nós autorados à mão, guiado pela seta; toda tentativa de tirá-la quebrou | 07/09 – 01/10 | `Assets/Scripts/Graph/` |
| **v5** | **Salas e portas + mapa de suspeita + caça**, sem seta, ainda sobre os nós | 01/10 – 07/10 | `Assets/Scripts/Graph/` |
| **v6** | **Grafo de nós + NavMesh**: o NavMesh dá a rota na perseguição e tira a presa dos nós (v6.0); depois gera o próprio grafo, sem nó autorado (v6.1) | 09/10 – 10/10 | `Assets/Scripts/Graph/` |
| **v7** | **Explorador livre**: só NavMesh; o monstro vê pontos e portas, sem nó, vizinho nem ligação | 10/10 → | `Assets/Scripts/Free/` |
| **v8** | **Habilidades**: o cérebro ganha ações além de andar e olhar (o clone) e espaço reservado para as próximas | 12/10 → | `Assets/Scripts/Free/` |

**Os nomes de arquivo NÃO mudaram.** Run, `.onnx`, config, cena, prefab e branch ficam com o nome da época, e a
coluna "Antes" da tabela abaixo dá o nome antigo de cada versão. Exemplos: o modelo da v5.1 continua
`GraphExplorer_v4.1.onnx`, o da v5.3 é `GraphExplorer_05_50M.onnx`, o da v7.2 é `FreeExplorer_v8.2_10M.onnx` (config
`free_v8.2.yaml`), e a v8.0 é a branch `feature/v9-seeker` com o `free_v9.yaml`.

**Renomeações:** em 03/10, "V4B" virou v4.1 e "V5" virou v4.2; "v4c" e `v5_fuga_01` foram só planos e nunca
rodaram. Em 16/10 tudo foi renumerado pela regra acima: o que se chamava v4.x e v5.x passou a v5 e v6, a antiga v7
virou v6.1, a v8 virou v7 e a v9 virou v8.

**"Mapa v4" é nome de mapa, não de versão:** os docs antigos chamam assim o `Node_5` / `NodeTraining5` (o 4º mapa
de nós), usado da v5 à v6. Aqui ele aparece como **mapa Node_5**.

---

## Visão geral

| Versão | Antes | Quando | Mapa | O que se tentou | Resultado |
|---|---|---|---|---|---|
| **v1–v3** | S | jul–set | Map_1..8 | **Seeker**: perseguir o hider com memória em grade | SeekerV1–V3; currículo travou mais de uma vez |
| **v4.0** | G | 07–18/09 | Nodes | Primeiro GraphExplorer: regiões + seta que some | Modelo bom em 08/09; o run 05 colapsou ao perder a seta |
| **v4.1** | A | 18–20/09 | Nodes_2 | Explorar com seta, ping, hider e visão | Ótimo com a seta; **sem ela, colapsa** |
| v4.1.1 | — | 19/09 | Nodes_2 | Tirar a seta de vez (valor por saída + aresta nova) | Cobertura 2–7%; revertido no mesmo dia |
| v4.1.2 | B | 20/09 | Nodes_2 | Branch `node-unexplored`: sem seta, com valor por saída e calor | Colapsou (`night_04`); as ideias foram portadas |
| **v4.2** | C | 23–26/09 | NodeTraining2 → Node_4 | Grafo gerado automaticamente; currículo v2 | Gerador abandonado; mapa novo feito à mão |
| **v4.3.0** | Fase 0 | 27/09 | Node_4 | Primeiro run no mapa novo | Inválido: grafo quebrado |
| v4.3.1 | Fase 1 | 27/09 | Node_4 | Explorar **sem seta**, do zero | Não aprendeu (cobertura 13%) |
| v4.3.2 | Fase 2 | 27/09 | Node_4 | Corrigir física e observações | Melhorou (18% em 500k) |
| v4.3.3 | Fase 3 | 28/09 | Node_4 | Exploração + patrulha | **Funcionou** (88%), mas 43% do tempo na parede |
| v4.3.4 | Fase 4 | 28/09 | Node_4 | Tudo num treino só + movimento livre | Adiado; só o run curto de validação do movimento |
| v4.3.5 | Fase 5 | 28/09 | Node_4 | Procura + ping, do zero (nasce o mapa de suspeita) | Não aprendeu: mudanças demais de uma vez |
| v4.3.6 | Fase 6 | 28/09–01/10 | Node_4 | **Em etapas**, uma coisa por vez | E1 ✅, E2 parcial, E3 ✅ no limite |
| **v5.0** | Fase 7 · v4.0 | 01–02/10 | Node_5 | **Salas e portas**: toda sala vale igual, porta perde valor ao repetir, sem seta | S1→S6 numa noite: explora ~70% das salas e pega o hider em ~2/3 |
| **v5.1** | 7b · v4.1 (V4B) | 02/10 | Node_5 | **Planta de salas** (sensor com as 26 salas), ver = explorar, suspeita multiplica | `v4.1_noite_01` (parou aos 7.1M): explora 76–83% das salas e pega o hider em 83% (HiderFoge) |
| v5.1.1 | 7c · v4.2 (V5) | 03/10 | Node_5 | **Do zero contra a parede**: caça com hider rápido, calor do ping no mapa inteiro, 2 sensores de raios, menos steering, parede mais cara | `v4.2_noite_01` abandonado aos 3.72M: parou de bater mas também de explorar (22% das salas na Metade) |
| v5.1.2 | 7d · v4.3 | 03/10 | Node_5 | **Caça herdando a v5.1**: hider sem pausa, foge a 18 m, escada 4→6→8 m/s; caça mais valiosa; calor do ping; física, parede e raios voltam aos da v5.1 | `v4.3_caca_01` parado aos 532k: vê ~89%, pega ~72%; modelo `GraphExplorer_v4.3.onnx` |
| v5.1.3 | 7e · v4.4 | 03/10 | Node_5 | **Corrida**: sem aceleração nem steer assist, corpo segue o movimento (pescoço até 60°), corre com estamina, 15/18/20 m/s (patrulha/alerta/perseguição); hider anda e corre fugindo, 4→10 m/s; parede ×2 (Door grátis); suspeita ×2 | Não rodou (substituída pela v5.2) |
| v5.1.4 | 7f · v4.5 | 03/10 | Node_5 | **Fuga**: hider corre 10→14 m/s com 6→10 s de estamina (o seeker tem 5) | Não rodou (substituída pela v5.2) |
| **v5.2** | Fase 8 · v5.0 | 04/10 | Node_5, prefab novo | **Do zero, escala do jogador**: velocidade pelo estado de alerta (patrulha 6, alerta 8, perseguição 10,2 m/s vendo o jogador), inércia, episódio de 400 s, vetor 188, hider na mesma escala | `v5.0_zero_02` (3.15M): passou a Perto em ~1.47M, Metade em ~3.1M, parado na Quase com cobertura ~44%. **Decorou um loop de salas baratas** (1 nó) perto do spawn e ignorou as grandes. Abandonado. |
| **v5.3** | Fase 9 · v5.1 | 04–07/10 | Node_5, prefab novo | **Economia de salas**: sala paga UMA VEZ ao completar (0.5), porta paga só a 1ª vez (0.1), valor cresce com o progresso. Sem salas pré-concluídas. Meta = 100% dos nós vistos. Planta com hops/16. | `v5.1_zero_01` (50M, um run só do zero até a HiderJogador): explora ~24 de 26 salas na lição Completo; na caça pega 97–99% do hider, ~84 s da 1ª vista à captura. Modelo `GraphExplorer_05_50M.onnx`. No jogo, o jogador ainda escapava. |
| **v6.0** | Fase 10 · v5.2 | 09–10/10 | Node_5 + NavMesh, prefab V6 | **Caça herdando o 50M**: perseguição 11.5 m/s, rastro de 3 s depois de ver, previsão ao perder de vista, rota NavMesh até o alvo na perseguição, pressa por step caçando, hider fora do centro e fora dos nós | `v5.2_caca_01` (8M, ~9 h): pega 99.8%, **~25 s** da 1ª vista à captura (era ~74 s), perde de vista 1.2× por episódio (era 4.7×), parede 0.019. Modelo `GraphExplorer_v5.2.onnx`. No jogo: anda de centro em centro de nó. |
| v6.0.1 | Fase 11 · v5.3 | 10/10 | Node_5 + NavMesh, prefab V6 | **Sair do centro dos nós**, herdando a v6.0: hider sempre a ≥ 3.5 m de todo centro de nó, procura só descarta um nó vendo cantos e entorno, vizinho aponta para a área, visão 140° / 35 m | Preparada (`config/graph_v5.3_caca.yaml`), sem run registrado: no mesmo dia a linha passou para a v7 |
| **v6.1** | v7 | 09–10/10 | mapa Node_5 sem nós autorados | **Sem nós autorados**: grafo gerado do NavMesh e dos batentes, pegadas, clone, agachar | Rodou fora deste repo, em paralelo à v7.0; código e números não estão aqui |
| **v7.0** | Fase 12 · v8.0 | 10/10 | V8 - Training (mapa Node_5 + NavMesh) | **Explorador livre, do zero** (linha `FreeExplorer`): o corpo e o estado de alerta da v6, mas os nós viram só **pontos gerados pelo NavMesh que ele precisa ver**, sem vizinho nem ligação; paga ver ponto novo e porta nova (andar não paga); foco na presa e pegadas que ele só segue se vir | `v8.0_zero_02`: chegou à caça e pegava ~99% em ~13 s na Foge. Modelo `FreeExplorer_v8.0_9.5M.onnx`. No jogo caçava "em linha de nó": a presa do treino ainda andava de nó em nó |
| **v7.1** | Fase 13 · v8.1 | 11–13/10 | Arena - V8 (só NavMesh), depois mobiliado (Arena - V9) | **Do zero, currículo completo com chaves**, do Explorar à presa mista (foge, escondida, rápida) com chaves que ela leva para escapar; de 25M em diante no mapa mobiliado, e de 27.9M em diante com visão e velocidade cortadas para os números do jogo | `v8.1_zero_01` (35M): pega ~95% (rápida ~90%), ~13 s da 1ª vista à captura, cobertura ~0.48; salas muito mobiliadas quase nunca concluem. Modelo `FreeExplorer_v8.1M_35M.onnx` ("M" = mobiliado) |
| **v7.2** | Fase 14 · v8.2 | 13–15/10 | Map - V8 (mapa editado à mão, NavMesh próprio) | **Cérebro da v7.1 no mapa novo com os números do jogo**: visão 80°/30 m, perseguição 9, presa sem a rápida e que se esconde depois de fugir, campar na sala custa, chave e passos fazem barulho, parede mais cara; no `_05`, visão do alvo em 3D, agachar de verdade, corpos de 3 m e presa que nasce e se esconde onde o monstro menos olha | `v8.2_mapa_01`..`_04` (até 3.2M: pega ~0.89, mas faz sempre a mesma ronda). `_05` (10M): Caught caiu a 0.26 antes de voltar a 0.43; parede ótima, cobertura 0.43 → 0.535, mas 9 de 23 salas seguem em < 6% dos episódios. Modelo `FreeExplorer_v8.2_10M.onnx` (o da `Cena Final V8`). `_06` preparado |
| v7.3 | V8.3 | 16–17/10 | Map - V8 | **Quebrar a ronda fixa, herdando o 10M da v7.2**: ponto novo paga mais quanto menos a arena o viu nos últimos episódios, e 1 em 4 episódios é só de explorar (o plano do `_06`, que não rodou). Branch `feature/v7.3-seeker`. Na v7.3.1, horizonte maior (gamma 0.997 → 0.999) | `v7.3_ronda_01` parado em 3.6M sem mexer na ronda. `v7.3_ronda_02` (v7.3.1) parado em 11.46M: pega ~0.48 (0.37 no `_05`), mas a exploração não mudou (portas usadas 0.47, cobertura só de explorar parada em 0.64) |
| **v7.4** | — | 17/10 → | Cena Final (mapa do jogo, 21 salas) | **Cérebro da v7.3.1 no mapa do jogo**, com os números do jogo: monstro a 5.5/6.5/8 m/s e olho a 3.18 m, presa com a corrida do jogador (8.5 m/s, 10 s de fôlego). Arena gerada do `Mapa Final` pelo menu `v7.4` (`FreeV74Final`), com as bordas abertas para a Entrada tampadas só no treino | `v7.4_final_01` (`config/free_v7.4_final.yaml`, herda o `v7.3_ronda_02`): parado em 1.02M. **v7.4.1**: sala concluída paga 0.4 e a raridade (ponto e sala) vira curva, x(1 + 4 x raridade²), para a ronda variar e cobrir todas as salas; mapa refeito da Cena Final com as mesas da MapCheck. `v7.4.1_ronda_01` (`config/free_v7.4.1_ronda.yaml`, herda o `_01`): parado em 1M (cobertura 0.34 → 0.39, captura 0.66 → 0.51). **v7.4.2**: entrar em sala nova paga 0.2 e sala concluída passa a valer com 60% dos pontos (era 90%; com móveis no meio quase nunca fechava). `v7.4.2_salas_01` (`config/free_v7.4.2_salas.yaml`, herda o `v7.4.1_ronda_01`): parado em ~6.5M; cobertura 0.43 → 0.50, portas usadas 0.48, mas 8 salas (ala norte, becos, escritório grande) em < 45% dos episódios e captura 0.71 → 0.47 (a presa se esconde nelas). **v7.4.3**: o objetivo vira ENTRAR em toda sala: entrar 0.5, concluir 0.15, todas no episódio +3, raridade por entrada; métricas `RoomsEntered`. `v7.4.3_visitar_01` (`config/free_v7.4.3_visitar.yaml`): a partir de 18/10 8h |
| **v8.0** | v9 | 12/10 → | Arena - V9 (TaskFurniture, mobiliado) | **Do zero, cérebro com habilidades**: clone (andarilho ou vigia), lista de eventos, vetor 200 com 32 posições reservadas para habilidades futuras, até 64 salas e 12 portas por sala | `v9_zero_01` abandonado em ~4M (planta de 64 salas = atualização 2.3× mais lenta); `v9_zero_02` (32 salas) rodou em outro PC, sem números aqui. Ver a v8.1 |
| v8.1 | — | 16/10 → | Map - V8 | **Habilidades sobre a base da v7.2**: o clone e a lista de eventos da v8.0 trazidos para a main (branch `feature/v9-updated-seeker`) | A fazer: falta ligar no Manager e nas observações |

---

## v1–v3 — Seeker (jul–set)

A linha anterior ao grafo: o seeker perseguia o `HiderAgent` com memória numa grade regular, nos mapas Map_1..8.
Os modelos que ficaram são SeekerV1–V3 (PR #7, 05/09), usados pela `Cena FInal`. Houve também uma variante com
armadilhas de som (branch `feature/traps-sounds`, 01–05/09, apagada em 16/10). O que deu errado está em
[O que não deu certo](#seeker-v1v3-julhosetembro).

## v4 — Grafo de nós com seta (07/09 a 01/10)

O GraphExplorer começou explorando um grafo de nós colocados à mão, guiado por uma seta que apontava o próximo
lugar não visitado. Toda tentativa de tirar a seta derrubou a exploração. A v4 termina quando a v5 troca a unidade
do mapa (nós por salas e portas) e tira a seta de vez. As v4.0, v4.1.1, v4.1.2 e v4.2 só aparecem em
[O que não deu certo](#o-que-não-deu-certo).

### v4.1 — Mapa Nodes_2 (runs `graph_*` e `night_*`), 18–20/09

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

### v4.3 — Mapa Node_4, 27/09 a 01/10

Mapa refeito à mão depois do gerador (v4.2). Cada patch é uma das fases desse mapa.

#### v4.3.0 — Primeiro run no Node_4, 27/09 · inválido

- Uma opção do grafo estava desligada, e só 6 dos 133 nós eram alcançáveis. O agente ficou cego.
- **Lição:** conferir o Console do Unity antes de qualquer run longo.

#### v4.3.1 — Sem seta, do zero, 27/09

- **Resultado:** 10M steps, cobertura parada em ~13%. No jogo ele travava em quinas, girava e
  atravessava parede.
- **Causas encontradas:**
  - o jeito de mover o corpo teleportava para dentro da parede;
  - os sensores de parede giravam com o corpo, mas as ações não;
  - ele não tinha memória de "de onde vim".

#### v4.3.2 — Correções de base, 27/09

- **Mudanças:**
  - movimento por velocidade, sem atravessar parede;
  - sensores presos ao mundo;
  - por saída, **quanto ainda falta explorar ali**, **de onde vim** e **quantas vezes já passei**.
- **Resultado:** runs curtos de validação. Em 500k: cobertura 18% contra 5% antes.

#### v4.3.3 — Exploração + patrulha, 28/09

- **Mudanças:**
  - **patrulha:** um lugar visitado volta a valer depois de um tempo;
  - **tédio de sala:** ficar muito tempo numa sala rende menos;
  - multa por ficar em loop;
  - folga do grafo medida pelo tamanho real do corpo.
- **Resultado (7M steps):** passou por todas as lições e patrulhou **88%** do mapa. Os
  problemas: 43% do tempo encostado em parede, girava sem parar ("beyblade") e os circuitos
  de patrulha eram curtos demais.

#### v4.3.4 — Tudo num treino só, 28/09 · adiada

- **Preparada:**
  - multa por **batida** na parede e por mudar de direção bruscamente;
  - **movimento livre**: andar e olhar separados, 6 m/s, andar de costas mais devagar e uma
    "rodinha" que desliza na parede;
  - física do corpo corrigida (giro, atrito e voo travados).
- Não rodou: a prioridade passou a ser a procura (v4.3.5). Só rodou o teste curto do movimento
  (`node4_move_01`).

#### v4.3.5 — Procura + ping, do zero, 28/09

- **Ideia nova, o mapa de suspeita:** o agente mantém uma crença de onde o hider pode estar.
  - Ela se espalha com o tempo.
  - Zera onde ele olha.
  - Concentra onde ouviu barulho.
  - É o "esquecer aos poucos" que substitui patrulha e tédio.
- **Resultado:** quatro tentativas (`search_01`–`_04`), todas com o agente parado na sala onde
  nasceu (cobertura 2–3%).
- **Lição:** mudamos movimento, física, procura e punições **ao mesmo tempo** e do zero.
  Quando não aprendeu, não deu para saber o que quebrou.

#### v4.3.6 — Em etapas, 28/09 a 01/10

Cada etapa herda o cérebro da anterior e só avança quando bate um critério medido no TensorBoard.

| Etapa | Treina | Critério para passar | Resultado |
|---|---|---|---|
| **E1** explorar com seta | andar + explorar | cobertura > 0.6 | ✅ **93–96%** em 190k steps |
| **E2** explorar sem seta | tirar a seta | cobertura > 0.6 | ⚠️ parou em ~0.42, fechada por decisão |
| **E3** achar hider parado | procura com o mapa de suspeita | pegar o hider em > 50% dos episódios | ✅ **56%** em 749k, no limite |
| E4 seguir hider que anda | rastro que se move | pegar > 50% | não rodou (a v5 mudou o vetor) |
| E5 caçar hider que foge | cortar caminho | pegar > 40% | não rodou (a v5 mudou o vetor) |

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

## v5 — Salas e portas, suspeita e caça (01/10 a 07/10)

Mapa novo (`NodeTraining5`, cena `Node_5`, 6 arenas; nos docs antigos, o "mapa v4") e um jeito novo de pontuar a exploração.
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

### v5.0 — Salas e portas (antes v4.0), 01–02/10

Por etapas, como na v4.3.6. A **S1** treina salas e portas do zero e sem seta. As seguintes são
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

- **O que funcionou:** sem seta, ele aprendeu a explorar por salas (a v4.3.6 travava em ~40%) e a caçar.
  O tremor das ações caiu de 0,42 para 0,15. Modelo `Assets/GraphExplorer_v4.0.onnx`.

### v5.1 — Planta de salas (antes v4.1 e "V4B"), 02/10

- **`v4.1_noite_01`, do zero, parou aos 7.1M na lição HiderFoge:** sensor com a planta das 26
  salas, "ver" a sala conta como explorar e a suspeita passa a multiplicar o valor de ver (em vez de pagar
  sozinha). Resultado: cobertura de exploração **76–83%** (era ~70%), lições de exploração em 3M steps (eram
  4.7M) e **pega o hider em 83%** (era ~65%). Rewards não são comparáveis com a v5.0 (a recompensa mudou).
  Pontos fracos: portas repetidas 65% e ~11 loops na Patrulha/Ping, parede ~17% na caça, e não chegou ao
  HiderSolto (entropia ainda 0.82).
- **Modelo (03/10):** o cérebro do `v4.1_noite_01` (checkpoint 7.17M, lição HiderFoge, pega o hider em ~85%)
  foi promovido: `Assets/GraphExplorer_v4.1.onnx`.

#### v5.1.1 — Do zero contra a parede (antes v4.2 e "V5"), 03/10 · abandonada aos 3.72M

`v4.2_noite_01`, ver "O que não deu certo". Treino **do zero**, porque os 2 sensores de raios (13 raios cada,
alcance 15 m, alturas ajustáveis) mudam o tamanho da observação e a v5.1 não carrega. O que mudou em relação à v5.1:
- **Movimento:** seeker 20 m/s e aceleração 7 (eram 40 e 15), freio 30 e atrito 1. Ele deslizava e se jogava
  na parede, e o steering assist cobria o erro. O assist cai de 1.0→0.3 para 0.6→0.1.
- **Parede mais cara:** contato 0.0015/step e batida 0.1 (eram 0.00075 e 0.03).
- **Caça muito mais valiosa:** captura 20 + até 25 por pegar cedo, avistar 2, aproximar 0.4 por metro (só com
  visão livre, ou seja, em linha reta), manter o hider em visão 0.004 por step; exploração ×0.5 na caça.
- **Calor do ping no mapa inteiro:** a sala do barulho vale 1, cada porta de distância ×0.65, meia-vida de
  25 s; vira observação contínua. Explorar no frio vale ×0.25 enquanto há calor, a sala quente vale ~8×,
  o ping chegado paga 5 (era 2) e a suspeita zerada volta a pagar (1.0).
- **Hider:** 10 m/s, sem pausa, foge a menos de 18 m (escada 5 → 8 → 10).

**Risco (anotado antes do run):** ping pago na caça foi o que virou renda no `night_04`; se a cobertura cair com
o reward subindo, baixar `ping_reward_scale` para ~0.3. O termo "manter em visão" é por STEP de física (8000 por
episódio), não por decisão; 0.004 já limita a renda a 32 contra 45 de pegar. Ficou de fora: fuga do hider com
olhada de dois nós e hider treinado por IA.

#### v5.1.2 — Caça herdando a v5.1 (antes v4.3), 03/10 · parada aos 532k

`v4.3_caca_01`, parte do cérebro da v5.1 (`--initialize-from=v4.1_noite_01`) e muda só a caça:
- **Hider:** sem pausa nos nós, raio de fuga 18 m (era 12), velocidade em escada 4 → 6 → 8 m/s e depois solto
  (a v5.1 parou em 2.2).
- **Recompensas:** captura 20 + até 25 por pegar cedo, avistar 2, aproximar 0.4/m, manter em visão 0.004/step,
  suspeita zerada 1.0; exploração ×0.5 na caça.
- **Calor do ping** no mapa inteiro; ping chegado 5 × `ping_reward_scale` 0.4 = 2.
- **Voltaram aos da v5.1:** física (40 m/s, aceleração 15, sem atrito), parede (0.00075 contínuo, 0.03 por
  batida), steer assist 0.3 e sensor de raios (1 × 9 raios a 20 m). O 2º sensor (`RaysWorldLow`) está
  desativado no prefab, porque com ele o `--initialize-from` não carrega.
- **Critério:** `Hunt/Caught` não cair abaixo de ~60% na FogeMedia/FogeRapida; abaixo de 40%, segurar a
  velocidade. 5M steps.
- **Resultado:** viu ~89%, pegou ~72%, estável; ~1 em 6 hiders escapava depois de visto (na v5.1, viu =
  pegou). A 40 m/s com aceleração 15 o seeker leva 53 m para parar e usa steer assist + corpo sem atrito
  como trilho. Modelo promovido: `GraphExplorer_v4.3.onnx`.

#### v5.1.3 — Corrida (antes v4.4), 03/10 · não rodou

`v4.4_corrida_01`, planejado herdando a v5.1.2. Substituiu um plano anterior com aceleração 35 + freio 50, que
também nunca rodou. Vetor 182 e 4 ações iguais, então o `--initialize-from` carregaria.
- **Movimento (`GraphLocomotion`, novo):** sem aceleração e sem steer assist; o corpo gira para onde anda e só
  anda para a frente (a animação só tem andar e correr para a frente); o olhar vira só a cabeça, até 60° de
  cada lado, e o cone de visão vai junto.
- **Velocidade por estado, correndo / andando:** patrulha 15 / 10, alerta (até ~25 s depois de um ping) 18 / 12,
  perseguição (vendo o hider e 3 s depois) 20 / 13.3 m/s. Corre com a ação de andar quase no máximo, enquanto
  tem estamina (5 s, recupera em 10 s); a estamina entra na observação [26], que era o steer assist.
- **Hider:** anda a 2/3 e só corre fugindo, com estamina (3 s); escada 4 → 6 → 8 → 10 m/s correndo.
- **Recompensa:** parede ×2 na layer Wall (0.0015/step, 0.06/batida), paredes Door_Hole na layer Door sem
  punição; suspeita zerada paga 2 (era 1); exploração ×0.3.
- **Critério:** `Hunt/Caught` ≥ ~65% na MuitoRapida, parede < 0.15, `Movement/RunFraction` entre 0.1 e 0.5.

#### v5.1.4 — Fuga (antes v4.5), 03/10 · não rodou

`v4.5_fuga_01`, planejado herdando a v5.1.3; mesmo código e prefab. O hider corre 10 → 12 → 14 m/s com
6 → 8 → 10 s de estamina, mais que os 5 s do seeker. A 14 m/s ele foge do seeker andando (13.3) e aguenta mais:
o seeker teria que correr só perto e cortar caminho. Critério: `Hunt/Caught` ≥ ~50% na FogeLonge.

**v5.1.3 e v5.1.4 não rodaram:** em 04/10 o plano virou um treino do zero, a v5.2.

### v5.2 — Do zero, na escala do jogador (antes v5.0), 04–05/10 · abandonada

`v5.0_zero_02`, `config/graph_v5.0_zero.yaml`, 3.15M de 15M steps, prefab novo, build standalone.
- **Velocidade pelo estado de alerta**, na escala do jogador (PlayerDummy da `main`: anda 6, corre 10,2): patrulha
  6 m/s sem pista, **alerta** 8 m/s quando ouviu um barulho ou perdeu o jogador de vista há menos de 10 s, e
  **perseguição** 10,2 m/s enquanto vê o jogador. Não há corrida por ação nem fôlego; a IA vê o próprio estado.
  Inércia leve, giro mais lento na perseguição, bater na parede custa velocidade.
- **Hider na mesma escala:** escada 5,1 → 7 → 8,5 → 10,2 m/s correndo; na última lição é o jogador.
- **Episódio de 400 s** (era 160 s): ~2.800 m de caminho para um mapa de ~1.100 m de ligações. Custos por step
  divididos por 2,5.
- **Observação 188** (+6): velocidade própria, estado (patrulha/alerta/perseguição), fração do episódio, tempo
  sem progresso.
- **Toda sala vale o mesmo** na exploração (decidido com o Arthur); só o calor, a suspeita e a sala já explorada
  mudam o valor.
- **Sala 100% explorada:** 80% → 90% → 100% nas três primeiras lições (o critério subiu de 80% de nós
  pisados/vistos de uma vez, impedindo o começo do zero).
- **Resultado:** passou a Perto em ~1.47M, Metade em ~3.1M (meta trocada no meio), parado na Quase com cobertura
  ~44%. **Decorou um padrão barato:** as 11 salas de 1 nó perto do spawn valiam ~0.5 pontos cada, enquanto as
  grandes (S7–S9, S14, S17–S18, S23–S25) ficaram ignoradas. Causa: valor fixo por sala completada, sem escala
  por tamanho.

### v5.3 — Economia de salas, 50M (antes v5.1), 04–07/10

`v5.1_zero_01`, `config/historico/graph_v5.1_zero.yaml`, do zero, build standalone.
- **Economia de salas:** sala paga 0.5 **uma única vez** ao completar (era fatias até 80% e cauda ×0.2). Porta
  paga 0.1 só a 1ª travessia (`_doorPaidCrossings` 1; beco: volta paga igual). Saída de sala concluída 0.15, só
  primeira vez.
- **Valor crescente:** as salas que sobram por último valem ~2× as primeiras, via
  `GraphRoomMemory._progressValueGain` 1. O agente vê essa escala em observação.
- **Sem muleta:** sem salas pré-concluídas (`previsited_fraction` 0), meta 100% desde a lição 1 (Inicio 0.8 / Meio
  0.9 / Completo 1.0 de nós vistos). Critério de passagem por reward (10 / 15 / 21).
- **Planta atualizada:** `BufferSensor` com uma entrada por sala, incluindo hops até o ping ÷ 16 (era ÷10,
  saturava). Sem "bússola por porta".
- **Critério:** cobertura > 80% na lição Completo. Se travasse em ~50% após ~3M steps, o próximo seria um run com
  **LSTM** para capturar sequências temporais.
- **Resultado (50M, 07/10):** não precisou de LSTM. Depois de um platô em ~0.75 de cobertura, saltou para ~24
  salas em 15.4–15.9M e seguiu pelas lições de caça até a HiderJogador (desde 42M). No fim: pega 97–99%,
  ~84 s da 1ª vista à captura, ~5 perdas de vista por episódio, parede ~0.02. Modelo `GraphExplorer_05_50M.onnx`.
  No jogo, o jogador correndo (10.2 m/s contra 10) escapava, e ao perdê-lo de vista o monstro ia até onde ele sumiu.

## v6 — Grafo de nós + NavMesh (09/10 a 10/10)

Mesmo mapa da v5 (Node_5), ainda pensado como grafo de nós, mas agora com NavMesh assado. Na v6.0 o grafo segue
autorado à mão (prefab `NodeTraining - V6 Training`, cena `V6 - Training`, build `Builds/V6`) e o NavMesh entra só
onde a posição do alvo é conhecida: a rota na perseguição, exceção combinada à regra "sem seta", sem recompensa. Na
v6.1 o próprio grafo passa a ser gerado do NavMesh.

### v6.0 — Caça herdando o 50M (antes v5.2), 09–10/10

`v5.2_caca_01`, `config/historico/graph_v5.2_caca.yaml`, 8M steps, duas lições (HiderFoge → HiderJogador, troca
em 2.4M), ajuste fino (LR 1e-4, beta 0.003).
- **Mais rápido:** perseguição 11.5 m/s (era 10), alerta 9.5 (era 8.5), giro na perseguição 540°/s.
- **Não solta na hora:** depois de ver, segue o alvo por 3 s sem linha livre (era 0.5 s); depois disso, aponta
  para a previsão (última posição + velocidade vista, até 2 s) em vez de onde ele sumiu.
- **Rota NavMesh** na direção até o alvo (só na perseguição, sem recompensa): o monstro não empaca mais em
  móvel com o jogador do outro lado.
- **Pressa:** custo por step enquanto vê ou procura; seguir de longe deixa de render.
- **Hider mais parecido com o jogador:** nunca para no centro do nó, e 40% dos esconderijos ficam fora dos nós.
- **Resultado (8M):** pega 99.8%; **~25 s** da 1ª vista à captura (era ~74 s); 1.2 perdas de vista por episódio
  (era 4.7); episódio pela metade; batidas em parede e porta ~4× menos. A cobertura caiu (0.58 → 0.48), o
  esperado com a exploração valendo 0 na caça. Modelo `GraphExplorer_v5.2.onnx`.
- **No jogo (`V6 - Test`):** anda de centro em centro de nó. A captura é a 2.5 m e o hider de treino estava sempre
  a < 2.5 m de algum centro: parar no centro bastava para pegar.

### v6.0.1 — Sair do centro dos nós (antes v5.3), 10/10 · preparada

`config/graph_v5.3_caca.yaml`, run `v5.3_caca_01` (4M, herdando a v6.0). O hider para sempre a ≥ 3.5 m de todo
centro de nó (a captura é a 2.5 m) e nasce fora dos nós; a procura só descarta um nó vendo centro, cantos e
entorno; a direção dos vizinhos aponta para a área, não para o centro; visão 140° / 35 m. Critério:
`OffNodeFraction` sobe (0.14 na v6.0) e a captura volta a > 95%. **Sem run registrado:** no mesmo dia o trabalho
passou para a v7, que tira os nós de vez.

### v6.1 — Sem nós autorados (antes v7), 09–10/10

Proposta de 09/10 (`docs/graph/handoff-v7-sem-nos.md`, local): nenhum nó colocado à mão. As portas saem dos
batentes (`Door_Hole`), as salas saem do NavMesh cortado nesses vãos, e a cobertura vira pontos de visão gerados
por sala. Foi implementada e rodou **fora deste repositório**, em paralelo à v7.0 (portas 5005+ da mesma máquina),
com pegadas (`GraphFootprintTrail`), clone (`GraphCloneAbility`) e agachar no alvo. O código e os resultados não
estão aqui. Ficaram na main: o `NavDoorMarker` (porta virtual onde não há batente), a lógica de juntar batentes do
`FreeMap`, e as ideias de pegadas (v7) e clone (v8).

## v7 — Explorador livre (10/10 em diante)

Linha nova (`FreeExplorer`, `Assets/Scripts/Free/`), branch `feature/v8-livre` e depois `feature/v8-seeker-map`.
O corpo, o estado de alerta e o olhar vêm da v6. O mapa vira só NavMesh: **pontos de visão** gerados numa grade de
3 m, que o monstro precisa VER, e **portas** achadas pelos batentes. Ele não vê nó, vizinho, ligação, âncora nem
"nó atual" (o defeito da v6: andar de centro em centro de nó). Nada aqui é seta: o que ele vê é o que falta ver
dentro da sala, onde dá para andar e as portas.

### v7.0 — Do zero, explorador livre (antes v8.0), 10/10

`config/free_v8.0_zero.yaml`, runs `v8.0_zero_01` (parado em ~200k para entrar foco e pegadas, vetor 120 → 126) e
`v8.0_zero_02`.
- **Recompensa:** andar não paga. Ver ponto novo paga (8 no total do mapa, dividido pelos pontos); porta nova paga
  0.15; ver 95% dos pontos paga +5 e encerra. Parede: contato, batida e **presa na parede** (encostada, pedindo
  para andar e parada).
- **Caça:** a mesma presa da v6; avistar +2 (só a 1ª vez), recorde de proximidade 1.0/m, captura 20 + 25 × fração
  restante. **Foco na presa:** caçando, a exploração paga zero e some da observação.
- **Pegadas:** a presa deixa pegadas que esmaecem; o monstro só sabe da que VÊ, e ela vira pista e alerta. Nada
  paga seguir pegada: só a captura.
- **Meta = o mapa quase inteiro** (95%) e spawn em sala rara desde o step 0 (as duas correções que levaram a v5.3
  ao mapa inteiro, só que desde o começo).
- **Resultado:** chegou às lições de caça e, na Foge, pegava ~99% em ~13 s. Modelo `FreeExplorer_v8.0_9.5M.onnx`.
  No jogo caçava "em linha de nó" e não achava quem ficava parado num canto: a presa do treino ainda andava de nó
  em nó.

### v7.1 — Do zero, só NavMesh, presa nova e chaves (antes v8.1), 11–13/10

`config/free_v8.1_chaves.yaml`, run `v8.1_zero_01`, 35M.
- **Arena "Arena - V8":** sem NavGraph nem nó; a presa (`FreeHider`) anda pelo NavMesh desde o step 0.
- **Lições novas depois da Foge:** Escondido (presa agachada em esconderijos, parada 15–45 s, só foge achada),
  Rápida (presa a 12.5 m/s contra 11.5 do monstro) e Chaves (3 chaves; a presa vai buscá-las; o monstro sabe onde
  estão e só fica sabendo que uma foi levada 3 s depois; levar todas = escapou). A Final sorteia o tipo de presa e,
  em metade dos episódios, as chaves: o modelo serve com ou sem chaves no jogo.
- **Ronda × campar:** conferir uma chave parada há mais de 60 s paga; ficar mais de 15 s perto da mesma chave custa.
- **Audição:** ouve os `NoiseEvents` do jogo (passos correndo, itens jogados).
- **Mapa mobiliado de 25M em diante** (Arena - V9, o escritório com os móveis da TaskFurniture) e, de 27.9M em
  diante, **números do jogo**: visão 140°/35 m → 105°/25 m, perseguição 11.5 → 10.55, alerta 9.5 → 9, aceleração
  instantânea.
- **Resultado (35M, 13/10):** pega ~95% (a rápida ~90%), ~13 s da 1ª vista à captura, cobertura ~0.48. Salas muito
  mobiliadas quase nunca concluem, e a ronda das chaves não voltou no mobiliado. Modelo
  `FreeExplorer_v8.1M_35M.onnx` (antes: 11.25M, 13.5M, 25M e 27.9M, todos em `Assets/Brains/FreeExplorer/`).

### v7.2 — O cérebro da v7.1 no Map - V8 (antes v8.2), 13–15/10

`config/free_v8.2.yaml`, sem currículo (todo episódio é a lição Final da v7.1), herdando o 35M
(`--initialize-from`). Mapa novo: o "Map - V8", editado à mão (móveis sem clipping, cubículos abertos como
esconderijo, NavMesh próprio, 23 salas e 29 portas).
- **Números do jogo no treino:** perseguição 9, alerta 7.5, patrulha 6.5, visão 80°/30 m, memória do alvo 0.5 s
  (atrás de um móvel o alvo some de verdade).
- **Runs `_01` a `_04` (até 3.2M):** campar na mesma sala custa; presa sem a rápida (o monstro a 9 nunca a
  alcançava) e que se esconde depois de despistar; chave pega e passos fazem barulho; parede mais cara; a última
  vista só aponta até ele conferir o lugar. Pega ~0.79–0.89, mas faz sempre a mesma ronda: S06 vista em 0% dos
  episódios, S04 2%, S02 3%.
- **Run `_05` (10M):** visão do alvo em 3D (grade 4×3 do corpo), agachar de verdade, jogador e presa com 3 m em pé
  e 1.5 m agachados, e presa que nasce e se esconde nas salas e nos pontos que o monstro menos vê. Resultado: a
  presa passou a se instalar nas salas puladas e o Caught caiu de 0.70 para 0.26 antes de voltar a ~0.43; parede
  ótima (presa na parede 0.02% do tempo); cobertura 0.43 → 0.535, mas 9 de 23 salas seguem em < 6% dos episódios.
  Modelo `FreeExplorer_v8.2_10M.onnx`, o que roda na `Cena Final V8`.
- **Run `_06` (preparado em 15/10, herdando o `_05`, 30M; não rodou, virou a v7.3):** contra a ronda fixa, ponto novo paga mais quanto
  menos a arena o viu nos últimos episódios (bônus no raro, em vez de custo no comum), e 1 em 4 episódios é só de
  explorar, sem presa. Critério: `Hunt/Caught` > 0.45 e a fração vista das 9 salas puladas acima de ~0.5.

### v7.3 — Quebrar a ronda fixa, herdando a v7.2, 16/10 →

`config/free_v7.3_ronda.yaml`, run `v7.3_ronda_01`, `--initialize-from=v8.2_mapa_05` (10M). Não é do zero: só muda a
recompensa e o ambiente, a observação continua a mesma (vetor 126).
- **O problema, no fim do `_05` (9M–10M):** episódio de ~450 s com 98 travessias de porta, 62% repetidas, mas só 47%
  das portas usadas e 54% do mapa visto. Ele não fica parado nem preso: roda em circuito na mesma metade do mapa. A
  presa se instala nas salas que ficam de fora e o Caught caiu de 0.77 para 0.37. (Ler direito: `Rooms/S##` é a
  fração de episódios em que a sala foi CONCLUÍDA, 90% dos pontos; S04, S06 e S08 ficam em ~1%, mas com ~65% dos
  pontos vistos: ele vai, só não fecha. As puladas de verdade, pela `RoomsFrac/S##`, são S02, S03, S05, S09, S10,
  S12 e S13, com 11% a 25% dos pontos vistos.)
- **Mudanças (o plano do `_06`):** ponto novo paga × (1 + 1.5 × raridade), raridade = quão pouco a arena viu o ponto
  nos últimos ~20 episódios de caça; e 1 em 4 episódios sem presa, só de explorar (cobertura 0.9 encerra).
- **Critério:** parcial em ~3M, `Doors/UsedFraction` > 0.55 e `RepeatFraction` < 0.55; final, `Hunt/Caught` > 0.45
  e `RoomsFrac/S##` das salas puladas acima de ~0.5. Se travar, a próxima etapa é o horizonte: com gamma 0.997 ele
  enxerga ~33 s à frente, menos que o tempo de atravessar o mapa até uma presa quieta.
- **Resultado do `v7.3_ronda_01` (parado em 3.6M, 16–17/10):** não passou no parcial. Portas usadas 0.44 (0.45 no
  começo), cobertura 0.49, e nos episódios só de explorar, onde explorar é a única coisa que paga, a cobertura ficou
  parada em 0.63 e nunca chegou aos 90%. O tamanho da recompensa não era o gargalo.

### v7.3.1 — O horizonte (gamma 0.999), herdando a v7.3, 17/10 →

`config/free_v7.3_ronda_02.yaml`, run `v7.3_ronda_02`, `--initialize-from=v7.3_ronda_01`, mesmo build. Só muda o
gamma: 0.997 → 0.999, horizonte de ~33 s para ~100 s. Critério: em ~3M, cobertura só de explorar > 0.70 e portas
usadas > 0.50; final, `Hunt/Caught` > 0.45 e as salas puladas acima de ~0.5.
- **Parcial em 11M (17/10):** pega mais. Caught 0.48 e Seen 0.54, contra 0.37 e 0.43 no fim do `_05`, com a presa
  instalada em 0.63 dos episódios (0.75 no `_05`; a raridade recomeça a cada run e a presa endurece junto, por isso
  o Caught sai de 0.81 e desce). A exploração não mudou: portas usadas 0.47, repetidas 0.55, cobertura só de explorar
  em 0.64, o mesmo teto dos três runs, e as salas puladas seguem entre 11% e 25%. A curva está achatada desde ~6M.
- **Suspeitas para a próxima etapa:** o teto de 0.64 pode ser do mapa, e não da política (pontos que só aparecem de
  dentro das baias, olho em 4.2 m, 2 portas descartadas por falta de chão). E as 5 arenas geram mapas diferentes
  desde o `_05` (874 a 924 pontos; as arenas 1 e 4 com 22 salas em vez de 23), então os números `S##` não são a
  mesma sala em todas.

## v8 — Habilidades (12/10 em diante)

### v8.0 — Do zero, clone e reservas (antes v9), 12/10

`config/free_v9.yaml` (branch `feature/v9-seeker`), do zero, no mapa mobiliado (Arena - V9). O cérebro muda de
forma para caberem habilidades: vetor 200, com o bloco do **clone** e **32 posições reservadas** (sempre 0) para as
próximas, ramos de ação discretos, lista de **eventos** (som ouvido, chave levada, o clone viu), até 32 salas e
12 portas por sala, até 8 chaves. Com as reservas, uma habilidade nova entra com `--resume` e uma lição nova, sem
treinar do zero de novo.
- **Clone:** o monstro pede pelo ramo de ação e para 1.5 s canalizando; o clone nasce onde ele está, como andarilho
  (segue o corredor) ou vigia (fica varrendo o olhar). Só vê, nunca pega: o que ele vê vira pista forte para o
  monstro. Vive 20 s, recarga de 120 s. Usar não paga nada: só vale se ajudar a pegar.
- **Runs:** `v9_zero_01` (12/10) abandonado em ~4M na lição Explorar: a planta de 64 salas deixava cada
  atualização da rede 2.3× mais cara. `v9_zero_02` (mesmo cérebro com 32 salas) rodou em outro PC; os números não
  estão aqui.

### v8.1 — Habilidades sobre a base da v7.2, 16/10 · a fazer

A branch `feature/v9-updated-seeker` sai da main com o clone (`FreeCloneAbility`) e os eventos (`FreeEventLog`)
da v8.0. Falta ligá-los no `FreeExplorerManager` e nas observações da v7.2; como isso muda o cérebro, é treino do
zero ou uma lição nova. A branch `feature/v9-seeker` fica como referência dessa ligação até a v8.1 funcionar.

---

## O que não deu certo

Em ordem cronológica. Cada item: o que foi tentado → o que aconteceu → por quê → o que fizemos.

Fontes: commits do repo (todas as branches), docs antigos (`exploracao-local.md`,
`node-unexplored.md`, `treino-longo.md`, handoffs do placer), comentários dos YAMLs em
`config/` e os eventos de TensorBoard que ainda estão em `results/`. Runs sem número foram
apagados de `results/`, e o que está aqui vem do registro escrito na época.

### Seeker (v1–v3), julho–setembro

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

### v4.0 — Primeiro GraphExplorer (Nodes), 07–18/09

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

### v4.1 — Mapa Nodes_2, 19–20/09

- **v4.1.1 — Tirar a seta de vez (`db9f983`, runs `graph_14`–`_17`, 19/09).** A seta foi trocada pelo
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
  config da noite (`night_02`), e a versão sem seta foi para outra branch (v4.1.2).

### v4.1.2 — Branch `node-unexplored`, 20/09 (analisada em 27/09)

- **Sem seta, com valor por saída, mapa de calor e "dropout" de episódios sem seta
  (`night_04`, 5.5M).** Chegou à última etapa, mas:
  - cobertura com pico de 67% e fim em **29%**;
  - revisitas 28% → **86%**;
  - ~48% do tempo na parede;
  - todo episódio em timeout;
  - reward subindo até 40 mesmo assim.

  **Por quê:** o ping pagava a cada chegada do hider, e seguir o rastro dele virou renda. O
  agente trocou explorar por colecionar ping. O `night_03` parou aos 20k.
  **Feito:** a branch não foi mergeada (apagada em 16/10; o doc dela ficou guardado). Três ideias foram portadas
  para a `node-exploration-vision`:
  - o valor por saída, virou `ScoreBeyond` em metros;
  - o calor, virou o mapa de suspeita;
  - o ping, que passou a só informar (paga 0).

### v4.2 — Currículo v2 e mapa gerado, 23–26/09

- **Currículo v2 do zero (`graph_v2_01`, 23/09).** Parado aos 20k porque as lições eram
  lentas demais (~240k steps de piso cada). Ajustado para etapas menores, mas **abandonado**
  junto com o mapa quando o Node_4 foi montado à mão.
- **Gerador automático de grafo (`NavGraphPlacer`).**
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
    (`51afb2b`). Esse é o Node_4 da v4.3.

### v4.3 — Node_4, de 27/09 em diante

- **v4.3.0 — Grafo de mão única (`node4_01`/`_02`).** Só 6 dos 133 nós eram alcançáveis e o agente
  ficou cego por 6.5M steps. **Por quê:** a opção de espelhar as ligações estava desligada.
  **Feito:** ligar a opção e sempre olhar o Console antes do run.
- **v4.3.1 — Sem seta, do zero (`noarrow_01`, 10M).** A cobertura travou em ~13%; o agente girava e
  atravessava parede. **Por quê:**
  - o movimento teleportava para dentro da parede;
  - os sensores giravam com o corpo, mas as ações não;
  - ele via só uma aresta;
  - não sabia de onde tinha vindo.

  **Feito:** as correções da v4.3.2.
- **v4.3.3 — Patrulha (`patrol_02`, 7M) — funcionou em parte.** Cobriu 88% do mapa, mas:
  - ficou 43% do tempo na parede;
  - girava como beyblade, porque a rotação física estava livre;
  - fazia circuitos curtos.

  **Feito:** multa por batida, suavidade, rotação travada e patrulha mais lenta.
- **v4.3.5 — Procura do zero (`search_01`–`_03`).** O agente ficava na sala de nascimento, com
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

  **Lição:** mudanças demais num run do zero. Foi isso que levou ao treino em etapas (v4.3.6).
- **v4.3.6 — E2, seta cortada de uma vez (`e2_01`).** A cobertura caiu de 93% para 7–15%, e o
  reward subia só porque ele batia menos. **Por quê:** a E1 aprendeu a seguir a seta, não a
  ler o "quanto resta" de cada saída.
- **v4.3.6 — E2, batente punido (`e2_02`/`_03`).** O corpo raspava em quase toda porta, e sair da
  sala custava pontos. Tirar a multa dobrou a cobertura (~0.25), mas ficaram loops de 5–10
  por episódio. O `e2_03` foi repetição, porque a correção ainda não estava aplicada.
- **v4.3.6 — E2, política que não parava de chutar.** A entropia não caiu em nenhum run até o
  `e2_03`. **Por quê:** o `beta` estava em 0.015, cerca de 5× o normal para ações contínuas.
  **Feito:** 0.003.
- **v4.3.6 — E2, pisca-pisca de âncora (`e2_05`/`_06`).** Na borda entre dois ladrilhos o "nó atual"
  trocava a cada passo, e a lista de saídas mudava junto. A folga de 0.5 m (`e2_06`) **não
  resolveu**: pisca-pisca 3.2 → 5.8, loops 6.8 → 10.2, cobertura 0.40 → 0.38. A E2 foi
  fechada abaixo do critério (0.42 contra 0.6). O gargalo de uma porta só no Node_4 ficou
  para a v5, que trocou o mapa.

### v5 — Salas e portas, 01–07/10

- **v5.0 — Teto de ~70% das salas e patrulha que não disparava (`v4_noite_01`).** O episódio nunca acabou por
  cobertura, e a liberação da patrulha só disparava com 85% usado, mais do que ele alcançava (~75%). Com o
  hider, "limpar suspeita" competia com explorar e ganhava. **Feito:** v5.1, com a planta de salas e a
  suspeita multiplicando o valor de ver.
- **v5.1.1 — Do zero contra a parede (`v4.2_noite_01`, 03/10).** Cinco mudanças contra a parede ao mesmo tempo +
  sensor novo (do zero). Ele parou de encostar em parede (0.2% do tempo) mas também parou de explorar: levou
  3.4M steps para passar a 1ª lição (a v5.1 levou 380k) e ficou em 22% das salas na Metade. Abandonado aos
  3.72M. **Feito:** voltar ao cérebro da v5.1 e mudar uma coisa por vez (v5.1.2).
- **v5.1.2 — Corpo rápido demais.** A 40 m/s com aceleração 15 ele levava 53 m para parar e usava a parede e o
  steer assist como trilho; ~1 em 6 hiders escapava depois de visto. **Feito:** o plano de corrida (v5.1.3), que
  virou o corpo do jogador da v5.2.
- **v5.2 — Decorou salas baratas (`v5.0_zero_02`, 04/10).** As 11 salas de 1 nó perto do spawn valiam o mesmo que
  as grandes, e ele fazia um loop nelas. **Feito:** v5.3, em que a sala paga uma vez ao completar e o valor cresce.
- **v5.3 — Farm de salas reabertas e de aproximação (`v5.1_zero_01`, 06/10).** Com o hider, sala concluída reabria
  pela suspeita e valia até 8×: perder o hider de vista e varrer a vizinhança rendia mais que pegá-lo. Depois,
  aproximar vendo a 1.0/m pagava todo metro encurtado. **Feito:** suspeita não reabre sala, teto de 4× no valor,
  ping some com o alvo à vista, e a aproximação só paga batendo o recorde de proximidade do episódio.
- **v5.3 — Bom no treino, ruim no jogo.** Pegava 97–99% do hider de treino, mas o jogador escapava: correndo a
  10.2 contra 10, e ao sumir de vista o monstro ia até onde ele sumiu. **Feito:** v6.0.

### v6 — NavMesh, 09–10/10

- **v6.0 — De centro em centro de nó (`v5.2_caca_01`).** Pegava 99.8% no treino, mas no jogo só andava de centro
  em centro de nó. **Por quê:** a captura é a 2.5 m e o hider de treino estava sempre a < 2.5 m de algum centro;
  parar no centro bastava. **Recusado no mesmo dia:** desviar pelo NavMesh na locomoção (a direção pedida virava
  a livre mais próxima ao bater). **Feito:** v6.0.1 (hider longe dos centros) e, logo depois, a v7, sem nós.
- **Rastro longo e "sentir" através da parede (09/10).** Propostos 5–15 s de rastro e sentir o alvo a < 6–15 m
  através de parede. **Recusados:** exagero; esconder perto tem que funcionar. Ficou o rastro de 3 s.

### v7 — Explorador livre, 10/10 em diante

- **v7.0 — Caçava em linha de nó.** A presa do treino ainda andava de nó em nó, e no jogo o monstro não achava
  quem ficava parado num canto. **Feito:** v7.1 do zero, com a presa andando pelo NavMesh.
- **v7.1 — Mapa mobiliado.** Salas com muitos móveis quase nunca concluem (a Archive tem 129 móveis), e a ronda das
  chaves sumiu (conferências por episódio 1.06 → 0.27). O run também morreu uma vez porque o terminal do treino
  estava dentro do VS Code e ele foi fechado. **Feito:** o treino roda num Anaconda Prompt aberto à parte.
- **v7.1 — Batch 2048 com `threaded`.** Aprendeu mais devagar e não ganhou velocidade: o gargalo é a atualização
  da rede na CPU. **Feito:** batch 1024 e `threaded` false de volta na v8.
- **v7.2 — Presa rápida impossível.** A 12.5 m/s contra os 9 do monstro, pegava ~20%. **Feito:** saiu do misto.
- **v7.2 — Esperar onde perdeu o alvo era de graça.** Procurando, o relógio de campar pausava. E a última vista
  ficava ligada o episódio todo, então ele voltava em loop para a sala em que perdeu o jogador. **Feito:**
  procurar conta como campar, e a última vista só aponta até ele conferir o lugar (3 m) ou por 20 s.
- **v7.2 — A mesma ronda sempre (`_01`–`_04`).** S06 vista em 0% dos episódios, S04 2%, S02 3%. A presa que nasce
  e se esconde onde ele menos olha (`_05`) derrubou o Caught a 0.26 antes de voltar a 0.43, e 9 salas seguiram em
  < 6%: a ronda cresceu, mas continuou fixa. **Feito:** `_06`, com bônus no ponto raro e episódios só de explorar.
- **v7.2 — Build sem NavMesh.** Apagar e recriar o `NavMesh.asset` no menu fez um build sair sem NavMesh.
  **Feito:** o menu copia por cima, sem apagar.

### v8 — Habilidades, 12/10 em diante

- **`v9_zero_01` — Planta de 64 salas.** Cada atualização da rede ficou 2.3× mais cara. Abandonado em ~4M.
  **Feito:** `v9_zero_02`, com 32 salas.

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
   do hider no `night_04`, ou as salas reabertas da v5.3), o agente aprende a colecionar esse sinal em vez
   de fazer a tarefa.
7. **Promover por relógio só enquanto a recompensa muda.** Com a recompensa fixa, promova por
   desempenho; por relógio, a lição vira mesmo com o agente sem estar pronto (run 05).
8. **Bom no treino não quer dizer bom no jogo.** A v5.3 pegava 99% e o jogador escapava; a v6.0 pegava 99.8% e
   andava de centro em centro. Teste no jogo antes de dar uma versão por pronta.
9. **A presa do treino ensina o que ela é.** Presa que anda de nó em nó ensina a caçar em linha de nó (v6.0,
   v7.0); presa que nunca se esconde onde ele não olha deixa a ronda fixa passar (v7.2).

## Próximos passos

- **v7.3.1, run `v7.3_ronda_02`** (branch `feature/v7.3-seeker`, rodando desde 17/10, 30M): terminar e decidir a
  próxima etapa. Antes de mexer de novo na recompensa, conferir se a cobertura de 0.9 é alcançável no Map - V8 e por
  que as arenas 1 e 4 geram 22 salas.
- **v8.1, habilidades** (branch `feature/v9-updated-seeker`): ligar o clone e os eventos no `FreeExplorerManager` e
  nas observações da v7.2. Muda o cérebro: treino do zero ou uma lição nova com as reservas.
- A linha E4/E5 do Node_4 (v4.3.6) e a v6.0.1 ficam paradas: os cérebros delas não servem nos vetores novos.
- Ideia não planejada: esconderijos embaixo de móveis.
