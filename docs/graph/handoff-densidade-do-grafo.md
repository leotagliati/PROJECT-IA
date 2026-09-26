# Handoff: o grafo gerado ficou denso demais

Registro de 23/09/2026, para quem continuar o trabalho do `NavGraphPlacer` num chat novo.
Leia antes, nesta ordem: `CLAUDE.md` (raiz), `docs/graph/posicionamento-de-nos.md` (o guia
atual do placer) e `docs/graph/handoff-cobertura-de-nos.md` (o problema anterior, já tratado).

> **Estado: correção implementada no mesmo dia, ainda não rodada no Unity.** As hipóteses da
> seção 6 foram medidas (seção 10), o código mudou conforme a seção 11 e os critérios de aceite
> combinados estão na seção 12. Falta rodar "7. Gerar do zero" e "8. Normalizar pesos" no
> `NodeTraining2`, salvar e medir com `tools/graph_report.py` e `tools/graph_coverage.py`.
>
> **Pendente, para outro dia:** o Arthur pediu área de nó retangular, nó de porta pequeno
> (0,5–1 m, ou dois de 0,25 m), primário que estica no corredor e encaixe "como Lego". Só está
> anotado, na seção 13; nada disso foi implementado.

## 1. O pedido

O Arthur rodou o placer no `NodeTraining2.prefab` e o resultado ficou ruim:

- nós demais;
- nós muito perto uns dos outros, alguns quase em cima de outros;
- nós ligados a muitos vizinhos muito próximos, como um nó com 5 ligações curtas em volta dele.

O objetivo é um grafo **enxuto**: poucos nós, bem espaçados, com ligações que descrevam a
topologia do prédio (sala, porta, corredor). Sem perder o que já foi conquistado (seção 5).

## 2. Estado do código

Branch `feature/node-exploration-vision`, **nada commitado**. `git status` mostra tudo.

Nesta sessão, antes deste problema, entraram:

| Onde | O quê |
|---|---|
| `NavGraphPlacer.Coverage.cs` (novo) | Cobertura do chão: estado OK / nó errado / sem nó por célula, raio escolhido pelo que cobre, cobertura gulosa com auxiliares, relatório, normalização de pesos (menu 8). |
| `NavGraphPlacer.Generation.cs` | Primários por folga, ligação por região com **caminho pelo chão** quando a reta não passa (`ConnectByWalking`, `InsertChain`), ponte entre pedaços pelo chão, **portas** (`FindDoors`, `AddDoorNodes`, `ReportDoors`), limpeza que não piora cobertura (`RemoveRedundant`). |
| `NavGraphPlacer.cs` | Diagnóstico com cobertura, pedaços soltos (`ReportPieces`) e portas; "Tudo" = 2 → 3 → 6 → 4. |
| `NavGraph.cs` | Caminho em metros (Dijkstra), `PathDiameter`, `CanSpawnAt`, **área de chegada cortada pela parede** (`_areasStopAtWalls`, `CanSeeFromNode`, `FindNodeAt` com raycast), gizmo da área cortada. |
| Runtime do agente | Captura do hider, distâncias em metros no ping e na fronteira, movimento com inércia, métricas `Exploration/*` e `Hunt/*`. Detalhes no `CLAUDE.md`. |
| `config/graph_explorer_v2.yaml` | Currículo novo (9 lições). |
| `tools/graph_report.py` (novo) | Mede um prefab sem abrir o Unity (ver seção 6). |

O `Assets/Prefabs/NodeTraining2.prefab` é do Arthur. **Pergunte antes de alterá-lo**, e só
leia para medir. O `NodeTraining.prefab` é o grafo feito à mão, que serve de referência.

## 3. O problema, medido

Saída do `tools/graph_report.py` nos dois prefabs salvos:

| Medida | `NodeTraining` (à mão) | `NodeTraining2` (gerado) |
|---|---|---|
| Nós | 102 | 211 |
| Primários | 23 | 59 |
| Ligações | 117 | 294 |
| Grau médio / máximo | 2,29 / 6 | 2,79 / 6 |
| Nós com grau ≥ 5 | 1 | 15 |
| Aresta mais curta | 3,21 m | **0,30 m** |
| Arestas com menos de 3 m | 0 | **53** |
| Vizinho mais perto, mediana | 6,40 m | 3,06 m |
| Nós a menos de 2 m de outro | 0 | **12** |
| Pedaços do grafo | 1 | 1 |
| Portas sem ligação atravessando | 0 de 30 | 2 de 30 |

Mapa de ~80 × 94 m. Com 59 primários, o orçamento de 23 dá peso ~0,39 para cada um.

**Atenção:** não se sabe com qual versão do código o `NodeTraining2` foi salvo (o arquivo é das
19:08). O `_maxAuxiliaryRadius` salvo nele é 5 e o passo 0,5, e a mediana dos overrides é 5,00,
então provavelmente foi antes do teto subir para 8. Rode de novo com o código atual antes de
tirar conclusões, e meça.

## 4. Como o gerador funciona hoje

Menu **7. Gerar do zero**:

1. `PlacePrimaries`: células por folga decrescente, aceita se não houver primário a menos de
   `_primarySpacing` (10 m) **andando**. Peso = orçamento / quantidade.
2. `CoverFloor` (Coverage.cs), que também é o menu 4 e o fim do "Tudo":
   1. `AddDoorNodes`: um auxiliar no meio de cada vão de porta sem nó a menos de 1 m.
   2. `FitRadiiDraft`: o primário fica com a metade da distância ao primário mais perto e
      encolhe se vazar. O auxiliar fica com o raio de maior pontuação.
   3. Até 3 voltas de: `FillCoverage` (gulosa, de célula ruim em célula ruim, da mais apertada
      para a mais aberta), depois `LinkRegions` (liga **todo par de regiões que se encostam**,
      com portal ou caminho pelo chão) e o raio dos nós que a ligação criou.
   4. `RemoveRedundant`: tira um auxiliar só se a cobertura **não piorar em nenhuma célula**.
3. `ApplyDraft` grava na cena. O resumo avisa se sobrou pedaço.

Regras de área, que valem para o jogo e para o placer: área = no raio (Chebyshev, forma
Square) **e** visível do centro na altura dos nós. Entre as áreas que contêm o ponto, vence o
centro mais perto. Célula **OK** = o vencedor a alcança por dentro da área. **Nó errado** = ele
a vê, mas o corpo não chega por dentro. **Sem nó** = nenhuma área contém a célula.

## 5. O que NÃO pode regredir

- **Grafo conexo**: 1 pedaço (`ReportPieces`, resumo do `ApplyDraft`).
- **Toda porta atravessada** por uma ligação (`ReportDoors`). O vão tem 2,54 m e o corpo 1,70, e
  ligação diagonal bate no batente.
- **Ligações livres para o corpo** (`IsSegmentClear` nos dois sentidos).
- **Cobertura do chão** perto da meta (`_coverageTarget` 98%, buraco ≤ 1 m). Pode ser renegociada
  (ver 6), mas não pode voltar a deixar corredor sem nó.
- **Primário apertado e sem sobreposição** entre primários; posse ≥ 75%.
- **Grau ≤ 8** (`_neighborSlots`).
- **Layout de observação intacto**, porque mudar invalida os `.onnx`. Pergunte antes.
- **Soma dos pesos dos primários = 23**, a base dos thresholds do currículo.

## 6. Causas prováveis (hipóteses — confirme medindo)

Em ordem do que parece pesar mais. Nenhuma foi verificada no Unity.

1. **A gulosa persegue 100%, não a meta.** `FillCoverage` visita toda célula ruim e só para
   quando acabam os candidatos. A meta de 98% é só relatório. Cada canto entre móveis vira um
   auxiliar pequeno. Parar ao bater a meta, ou exigir ganho mínimo em m² por nó novo, deve cortar
   muitos nós.
2. **"Nó errado" atrás de móvel baixo.** A área é cortada na altura dos nós (~1,84 m), então
   mesa e baia baixa não cortam a visão, mas bloqueiam o corpo. O chão atrás delas fica "visível
   mas inalcançável" = nó errado, e a gulosa põe um nó em cada bolsão. Salas de baias
   (`Cubicles`) devem estar cheias disso. Talvez esse chão devesse contar como OK quando o corpo
   chega nele por um caminho curto, mesmo saindo da área.
3. **Primários demais.** 59 contra 23 à mão: `_primarySpacing` de 10 m andando é pouco para este
   mapa. Cada primário traz raio apertado, sombra e posse, que proíbem auxiliar grande perto, e
   então a cobertura vira muitos auxiliares pequenos.
4. **`LinkRegions` liga todo par de regiões que se encostam.** Com nós pequenos e densos, isso é
   uma triangulação: arestas curtas e grau alto. Falta podar arestas redundantes, por exemplo com
   um grafo de vizinhança relativa ou de Gabriel: tirar A–B se existe C com A–C e C–B mais curtas.
   A poda precisa preservar a conexão e a travessia das portas.
5. **Limpeza estrita demais.** `RemoveRedundant` só remove se **nenhuma** célula piorar. Quase
   todo nó cobre alguma célula sozinho, então quase nada sai. Com tolerância, removendo enquanto a
   cobertura continua acima da meta, a limpeza trabalharia.
6. **Nós extras dos atalhos.** Os auxiliares de portal (`FindOrCreatePortalNode`, que reaproveita
   num raio de 3 m), as curvas do `InsertChain` e os nós de porta somam nós perto uns dos outros.
   O `_minNodeSpacing` (2 m) só vale para a gulosa, não para eles. A aresta de 0,30 m é quase
   certamente um desses.
7. **Portas duplas.** Vários batentes vêm em pares encostados, com 0,4 m entre as faces, e cada
   um pode gerar um nó. O `AddDoorNodes` pula se já houver nó a menos de 1 m, mas um portal ou
   uma curva criados depois não sabem disso.

## 7. Caminho sugerido

**Primeiro, meça.** Rode o placer atual no `NodeTraining2` e depois o `tools/graph_report.py`.
Anote nós, grau, arestas curtas e cobertura, e compare depois de cada mudança.

**Correções diretas**, cada uma ligada a uma hipótese da seção 6:
- **(1)** A gulosa para ao bater `_coverageTarget` e `_maxHoleSide`, ou exige ganho mínimo por nó.
- **(5)** A limpeza aceita remover enquanto a cobertura continua na meta, começando pelos nós que
  menos cobrem sozinhos.
- **(6)** Espaçamento mínimo para **todo** nó criado, de qualquer origem. Portal e curva
  reaproveitam o nó existente mais perto quando a reta passa.
- **(4)** Poda de arestas depois de ligar, sem partir o grafo, sem deixar porta sem travessia e
  preferindo grau ≤ 4.
- **(3)** `_primarySpacing` maior, ou primário só em "sala" de verdade, detectada pelas regiões
  separadas por portas.

**Se as correções não bastarem**, vale gerar pela topologia, com a cobertura só como verificação:
1. Descobrir as salas, isto é, as regiões do chão separadas pelos vãos de porta e pelos
   estreitamentos.
2. Pôr 1 primário por sala, ou 2–3 em salão, no ponto de maior folga.
3. Pôr 1 nó por porta.
4. Pôr nós no eixo dos corredores, pelo esqueleto (eixo medial) do chão, a cada ~6–8 m.
5. Ligar sala ↔ porta ↔ corredor.
6. Só então fechar buracos que passem da tolerância.

Esse caminho reproduz o que a autoria à mão fez no `NodeTraining`: 102 nós, grau médio 2,3,
nenhuma aresta abaixo de 3 m.

**Critérios de aceite sugeridos**, para combinar com o Arthur:
- 1 pedaço e 30 de 30 portas atravessadas;
- nenhuma aresta abaixo de ~2,5 m, exceto entre os dois nós de uma porta dupla, se existirem;
- grau médio ≤ 3 e máximo ≤ 6;
- nós na casa de 100–140 para este mapa;
- cobertura ≥ 95% com buraco ≤ 1,5 m, ou o que for combinado.

## 8. Como verificar

- **Sem Unity:** `python tools/graph_report.py Assets/Prefabs/NodeTraining2.prefab` lê o prefab
  **salvo** e mostra pedaços, portas, grau, arestas curtas e nós colados. Salve o prefab no Unity
  antes de medir.
- **Compilação:** o `Assembly-CSharp.csproj` fica na raiz, gerado pelo Unity e ignorado pelo git.
  Copie-o para uma pasta temporária, deixe os caminhos `Assets\`, `Library\` e `Packages\`
  absolutos e **inclua à mão os `.cs` novos** que o Unity ainda não pôs no csproj. Numa checagem
  anterior eles não tinham entrado e o build "passou" sem compilá-los. Rode `dotnet build` com os
  defines do editor e sem os `UNITY_EDITOR*` (player).
- **No Unity:** no Prefab Mode do `NodeTraining2`, rode "Tudo" ou "7. Gerar do zero", depois
  "8. Normalizar pesos" e "1. Diagnosticar", e leia o Console. O gizmo marrom é chão ruim, e o X
  vermelho marca nó solto ou sem solução.
- **No treino:** `Exploration/OffNodeFraction` perto de zero confirma que o agente quase nunca
  fica fora de nó.

## 9. Convenções

Estão no `CLAUDE.md`. As que mais importam aqui:
- comentários em pt-BR explicando o **porquê**, com a conta;
- código de editor em `#if UNITY_EDITOR`, com Undo em tudo;
- `if (x == null)`, nunca `?.`;
- campo serializado que muda de unidade ganha nome novo;
- atualizar `docs/graph/posicionamento-de-nos.md` e o `CLAUDE.md` junto com o código.

## 10. O que as medições mostraram

Sem o Unity, com uma réplica em Python do mapa do chão e da regra de cobertura do placer (lê as
1.700 caixas de parede e móvel do prefab; hoje em `tools/graph_coverage.py`). A réplica bate com o
Unity: dá 99,84% de chão OK no `NodeTraining2` salvo e, com 10 m, gera **os mesmos 59 primários**.

| Hipótese | Resultado |
|---|---|
| 1 e 5. Gulosa persegue 100%, limpeza rígida | **Pesa, mas pouco.** Com posições e raios fixos, cortar até 98% ainda exige 167 nós, e até 95% exige 153. Com a limpeza estrita saem 5 auxiliares. |
| 2. Nó errado atrás de móvel baixo | **Não é causa direta.** O grafo salvo tem 0% de nó errado; tirar qualquer auxiliar deixa o chão dele "sem nó", nunca "nó errado". |
| 3. Primários demais | **Confirmada.** 10 m → 59 primários; 16 → 31; 18 → 27; 20 → 23. |
| 4. Liga todo par de regiões encostadas | **Confirmada.** 73 de 294 ligações têm atalho por um vizinho comum; 76 triângulos (à mão: 4 e 6). |
| 6. Portal e curva sem espaçamento | **Confirmada.** As 8 ligações de 0,30–1,34 m são nós da ligação criados em cima de um nó de porta. |
| 7. Portas duplas | **Não é causa.** Os dois batentes ficam a 0,34 m e já viram um nó só (16 nós de porta para 30 batentes). |

Causas que a seção 6 não tinha:

- **Auxiliar colado no primário.** 38 das 53 arestas curtas eram auxiliar ↔ primário a 2,0–2,9 m
  (39 auxiliares a menos de 3 m de um primário; à mão, nenhum). O primário nasce no ponto de maior
  folga da sala com raio 1,8, e o auxiliar que cobre a sala queria o mesmo ponto. Só os 2 m de
  `_minNodeSpacing` separavam os dois.
- **A ordem da gulosa.** Ela ia de célula ruim em célula ruim e punha um nó que resolvesse aquela
  célula. Uma gulosa de ganho máximo (limite otimista: sem disputa de vencedor nem posse) chega a
  98% com ~57 auxiliares, contra 152. Com a regra do jogo, primários a 18 m e os 16 nós de porta,
  87 nós já cobrem 88,8%, e mais ~8 levam a 98% no limite otimista. Estimativa realista: 110–140 nós.
- **Porta de um lado só.** Nas 2 portas sem travessia, o nó do vão tinha uma ligação só.
- **Buraco medido pelo lado do retângulo.** Uma faixa fina ao longo da parede contava como buraco
  de 14 m.

Dois fatos que mudaram o plano:

- **O grafo à mão cobre 78% do chão com os móveis ligados** (92,6% sem eles). A referência de
  "enxuto" não bate 98%, então a meta foi renegociada para 97% com profundidade ≤ 1 m.
- **As portas não dividem o mapa em salas.** Cortando o chão nos 30 vãos, sobra um pedaço de
  4.834 m². O caminho "gerar pela topologia, sala a sala" da seção 7 não funciona aqui sem
  detectar estreitamentos; por isso a correção ficou no gerador atual.

## 11. O que mudou no código

| Onde | O quê |
|---|---|
| `Coverage.cs`, `FillCoverage` | Fase 1: gulosa de ganho máximo com fila de prioridade (reavalia só o topo), para na meta ou abaixo de `_minNodeGain` (4 m²). Fase 2: fecha, célula a célula, o chão a mais de `_maxHoleDepth` do OK. |
| `Coverage.cs`, meta | `_coverageTarget` 0,97 e `_maxHoleDepth` 1 m (substitui `_maxHoleSide`), numa função só (`CoverageMeets`) para gulosa, limpeza e relatório. |
| `Coverage.cs` | `_minNodeSpacing` 2 → 3 m. |
| `Generation.cs` | `_primarySpacing` 10 → 18 m; primário não nasce a menos de 3 m de um vão de porta. |
| `Generation.cs` | Portal e curvas do caminho pelo chão reaproveitam qualquer nó a menos de 3 m que enxergue os dois lados (`NearestUsable`); portal novo só onde não há nó a menos de 3 m. |
| `Generation.cs` | Limpeza tolerante: tira auxiliar enquanto a cobertura continua na meta, do que menos cobre para o que mais cobre. |
| `Generation.cs` | `LinkDoors`: nó de porta ligado a um nó de cada lado. `DoorCrossed`: travessia = aresta cruzando a linha ou nó da porta com vizinhos dos dois lados. |
| `Generation.cs` | `PruneRedundantEdges`: tira A–B quando existe C ligado aos dois com as duas pernas mais curtas; nunca ligação da cena nem a que deixaria porta sem travessia. |
| `NavGraphPlacer.cs` | Menu 3: o desvio reaproveita um nó que já existe antes de criar outro, e o criado respeita os 3 m. |
| `NodeTraining2.prefab` | Só o componente do placer: `_primarySpacing` 18, `_coverageTarget` 0,97, `_maxHoleDepth` 1, `_minNodeSpacing` 3, `_minNodeGain` 4, `_maxAuxiliaryRadius` 8 e `_auxiliaryRadiusStep` 1 (estavam 5 e 0,5, da versão antiga). |
| `tools/` | `graph_report.py` mede auxiliar colado em primário, triângulos, redundantes e soma dos pesos, e termina com o checklist de aceite; `graph_coverage.py` é a réplica de cobertura. |

Nada mudou no runtime, na observação ou nos `.onnx`.

## 12. Critérios de aceite combinados

No `NodeTraining2`, depois de "7. Gerar do zero" e "8. Normalizar pesos", salvo:

- `tools/graph_report.py`: 1 pedaço; 30 de 30 portas atravessadas; nenhuma aresta < 3 m; nenhum
  nó a < 3 m de outro; grau médio ≤ 2,6, máximo ≤ 6, no máximo 3 nós com grau ≥ 5; 100–140 nós;
  20–30 primários; soma dos pesos = 23.
- `tools/graph_coverage.py`: chão OK ≥ 97%; chão ruim a até 1 m do chão OK.
- "1. Diagnosticar": nenhuma ligação bloqueada, primários sem sobreposição, posse ≥ 75%.

O `NodeTraining` feito à mão passa em todos os itens do `graph_report.py` (e fica em 92,6% de
cobertura sem os móveis). O `NodeTraining2` salvo antes da correção falha em 7 de 10.

## 13. Próximo passo pedido pelo Arthur (para outro dia — NADA foi implementado)

Registro de 23/09/2026, depois da seção 11. O Arthur avaliou que ajustar o gerador atual não
basta e pediu a mudança abaixo. Neste dia **só foi anotado**: nenhum código, prefab ou
currículo mudou por causa desta seção.

### 13.1 O que foi pedido

1. **Área de nó RETANGULAR.** Cada nó passa a ter meia-largura em X e em Z, em vez de um raio só
   (hoje é um quadrado, `NodeShape.Square`, cortado pela parede). A ideia é a área encaixar no
   formato do lugar: faixa comprida no corredor, retângulo da sala.
2. **Nó de porta pequeno.** O nó do batente (`Wall_01_Door_Hole`) deve ter tamanho padrão de
   **0,5 ou 1 m**, porque ele marca a passagem e não o cômodo. Opção a avaliar: **dois nós de
   0,25 m** por porta, um de cada lado do batente, que encaixam com mais facilidade no vão.
3. **Primário ajusta o tamanho no corredor.** O primário deixa de ser sempre o quadrado apertado:
   num corredor ele estica no eixo do corredor, como os auxiliares.
4. **"Encaixar como Lego".** O gerador monta as áreas como peças que se encaixam, cobrindo o
   chão com o mínimo de sobreposição, e não como quadrados empilhados em volta de cada buraco.

### 13.2 Estado atual (ponto de partida)

- O raio mora em `NavNode._radiusOverride`, um float, com padrão por papel em
  `NavGraph._defaultPrimaryRadius` e `_defaultAuxiliaryRadius`. A métrica é `NavGraph.AreaDistance`
  (Chebyshev no quadrado). Quem usa: `FindNodeAt` (desempate pelo centro mais perto), o gizmo
  (`DrawArea` / `OutlineOf`), o placer inteiro (Coverage, Generation, `FitRadiiDraft`, posse,
  sombra, vazamento) e a réplica `tools/graph_coverage.py`.
- **Não existe tamanho próprio para nó de porta.** `AddDoorNodes` cria o nó com o raio padrão do
  auxiliar, e depois `FitRadiiDraft` escolhe o raio entre `_minAuxiliaryRadius` (1,5) e
  `_maxAuxiliaryRadius`. Um nó de 0,25–1 m hoje fica **abaixo do piso** do auxiliar.
- Invariante atual (seção 5): **primário apertado**, sem sobreposição entre primários (metade
  da distância ao primário mais perto) e posse ≥ 75%. O item 3 do pedido muda essa regra, e isso é
  decisão do Arthur, não efeito colateral.
- Critério de aceite atual (seção 12): nenhuma aresta com menos de 3 m e nenhum nó a menos de
  3 m de outro. Dois nós de 0,25 m na mesma porta **violam** isso, então a porta precisa de uma
  exceção explícita.

### 13.3 Pontos para decidir antes de codar

- **Desempate entre retângulos.** "Vence o centro mais perto" fica ambíguo quando as áreas têm
  tamanhos diferentes. Opções:
  - **(a)** distância normalizada, `max(|dx|/hx, |dz|/hz)`, em que vence quem contém o ponto mais
    "por dentro";
  - **(b)** um encaixe sem sobreposição, em que cada ponto pertence a uma peça só e o desempate
    quase some.

  "Encaixar como Lego" aponta para (b).
- **Alinhamento.** Retângulo alinhado aos eixos do mundo resolve este mapa: as paredes do kit são
  todas a 0 ou 90 graus. Rotação livre complica gizmo, métrica e placer sem ganho aqui.
- **Serialização.** A troca de `float _radiusOverride` por meia-largura X/Z precisa de campo novo
  e de migração: o valor antigo vira X = Z, e `FormerlySerializedAs` não converte float em
  Vector2. O `NodeShape` ganha `Rectangle`, ou o quadrado vira o caso X = Z.
- **Nó de porta.**
  - **Um nó de 0,5–1 m no meio do vão:** simples, e a travessia continua sendo "ligação cruzando a
    linha da porta" (`DoorCrossed`).
  - **Dois de 0,25 m, um de cada lado:** a ligação entre eles é a travessia. Fica curta, mas entra
    como exceção de espaçamento e de "aresta < 3 m".

  Nó de 0,25 m cruza em ~5 steps de física a 5 m/s. Ainda registra (a memória testa todo
  FixedUpdate), mas o piso `_minAuxiliaryRadius` tem que abrir exceção para porta.
- **Primário no corredor.** Ele estica no eixo do corredor até encostar em outra área, sem
  sobrepor outro primário. Isso muda o significado de "visitado" no corredor, que passa a ser
  "passei por este trecho". Os thresholds do currículo não mudam, porque os pesos não mudam.
- **Treino.** Mudar a forma da área muda a regra de chegada (`FindNodeAt`). O vetor de observação
  continua do mesmo tamanho, mas os `.onnx` treinados antes não servem: é run novo.

### 13.4 Caminho sugerido para o "Lego"

Uma abordagem que casa com o pedido e com o mapa do chão que o placer já tem (`WalkGrid`):

1. **Decompor o chão em retângulos.** Cobrir as células andáveis com retângulos alinhados aos
   eixos, os maiores primeiro, cada um recortado na parede (`SightBlocked`) e sem cruzar um vão de
   porta. Cada peça vira um nó com centro no meio e meia-largura X/Z = metade da peça. Peças
   menores que um mínimo (~1,5 m) se fundem com a vizinha.
2. **Portas.** Cada vão vira uma peça pequena própria, de 0,5–1 m ou duas de 0,25 m (13.3), entre
   as peças dos dois lados.
3. **Primários.** O primário continua sendo o ponto de vantagem da sala e ocupa a peça que o
   contém; no corredor, a peça comprida. O peso continua repartido do orçamento de 23.
4. **Ligações.** Ligar peças que dividem uma borda (adjacência natural das peças), com
   `IsSegmentClear` entre os centros. Se a reta não passa, usar o caminho pelo chão que já existe.
   A poda de redundantes (`PruneRedundantEdges`) continua.
5. **Verificar.** Com a cobertura de sempre (réplica em Python e Diagnosticar) e os critérios da
   seção 12, revistos para a exceção de porta.

Antes de implementar, mostre ao Arthur o plano e os critérios de aceite revistos. Ele pediu para
ser consultado.
