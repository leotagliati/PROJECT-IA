# Posicionamento de nós com mobília e cobertura do chão (`NavGraphPlacer`)

Registro de 23/09/2026. Por que os nós do `NodeTraining.prefab` quebram quando os
`Map_Objects` são ligados, como o `NavGraphPlacer` resolve isso e como usar.

## 1. O problema

Os 102 nós do `NodeTraining` foram posicionados com `Constraints/Map_Objects` **desligado**
(12 grupos: Boss_Office, Cubicles, Cabinets, Kitchen, Desks, Couches...). A mobília já está
na layer `Wall` e tem BoxCollider, então **para a física ela é parede**. Ao ligar:

- **Nó dentro de móvel.** O agente nasce entalado (`_spawnAtRandomNode` usa todo nó como
  spawn), a chegada ao nó fica impossível e, se o nó for primário, a cobertura-alvo pode
  nunca ser atingida.
- **Aresta atravessando móvel.** O `GraphHider` anda em linha reta pelas arestas e passa
  através da mesa. A política aprende a trombar seguindo a direção do vizinho.
- **Área de chegada vazando.** O quadrado de chegada passa por trás de uma baia ou
  armário. O agente "chega" sem ter passado pelo móvel.

E havia um quarto problema, que fazia o gizmo **não acusar** os dois primeiros:

- **A checagem de ligação não via móvel baixo.** Os nós ficam a ~1,84 m do chão (medido no
  prefab: chão em y 0,03, nós em y 1,87–2,0). O `IsSegmentClear` usava uma esfera de raio
  0,85 a +0,5 m do nó, então varria de 1,5 a 3,2 m de altura. Mesa, sofá, bancada e baia
  (0,8–1,5 m) passavam **por baixo** da sonda, e a aresta sobre a mesa aparecia verde.

## 2. O que mudou no `NavGraph`

- **Coluna do corpo.** `_bodyBottom = -1.6` e `_bodyTop = 1.8`, relativos à altura do nó.
  Com o corpo do `NodeSeekerAgent` (caixa 1,7 × 3,63 × 1,7) apoiado no chão, ele vai de
  nó − 1,84 a nó + 1,79. A base em −1,6 ignora os 24 cm de baixo (rodapé, soleira).
- `IsSegmentClear` agora é um **CapsuleCast** nessa coluna, não mais uma esfera numa altura só.
  Ele continua sendo a única definição de "o corpo passa". `_linkProbeHeight` ficou só
  como a altura em que o gizmo desenha a aresta.
- `IsBodyClear(posição, raio)` responde se o corpo cabe parado ali. É um `OverlapCapsule`
  na mesma coluna, e faz o que o cast não faz: o cast ignora o collider em que começa, então
  não diz se o nó está dentro de um móvel.
- As consultas usam a física **da cena do grafo** (`gameObject.scene.GetPhysicsScene()`). No
  Prefab Mode o prefab vive numa cena de preview com física própria, e a consulta global
  olhava para a cena errada.

Efeito em runtime: `FindNearestReachableNode` (observação [4..6]) usa o mesmo teste. Com
os Map_Objects desligados, o resultado é praticamente o mesmo de antes. Com eles ligados,
passa a ser o correto. O tamanho do vetor não muda, e os `.onnx` continuam válidos.


O bake também passou a checar a sobreposição de **todos** os pares de primários. Antes ele
só olhava os pares ligados por aresta, e dois primários em salas vizinhas (parede no meio,
sem aresta) podiam ter áreas sobrepostas sem aviso nenhum.

## 3. O `NavGraphPlacer`

É um componente no mesmo GameObject do `NavGraph` (`Graph` no prefab). Só tem menus de
contexto e não roda no treino. Tudo nele é **amostragem em grade no plano X/Z** contra a
física, com as duas funções acima: se o placer aprova, o gizmo e o runtime aprovam também.
O código está dividido em três arquivos:

- `NavGraphPlacer.cs` corrige nós que já existem (reposicionar, desviar ligação, diagnosticar);
- `NavGraphPlacer.Generation.cs` decide onde e quantos primários existem e quem liga com quem;
- `NavGraphPlacer.Coverage.cs` garante a **cobertura do chão**: raios e auxiliares novos.

| Menu | O que faz |
|---|---|
| **1. Diagnosticar** | Não altera nada. Lista nós dentro de obstáculo, nós sem folga de spawn, ligações bloqueadas, primários sobrepostos, grafo partido, áreas vazando, primários com área roubada, a **cobertura do chão**, pontos sem nó à vista e spawn points fora de chão coberto. |
| **2. Reposicionar nós** | Move cada nó obstruído para o melhor ponto livre a até `_maxDisplacement` (3 m). |
| **3. Consertar ligações bloqueadas** | Em cada aresta que o corpo não atravessa, tenta primeiro criar um **auxiliar de desvio**. Se não houver desvio, remove a aresta, mas só se o grafo continuar conexo. Senão, acusa erro. |
| **4. Ajustar raios e cobrir o chão** | Escolhe o raio de cada nó pelo que ele cobre, põe auxiliares até a cobertura bater a meta, liga os novos por região, liga cada porta dos dois lados, poda as ligações redundantes e mostra o relatório. Pode criar nós, então roda no Prefab Mode. |
| **5. Ligar vizinhos (por região)** | Liga todo par de nós cujas **regiões** se encostam. Só adiciona ligações, nunca remove as feitas à mão. |
| **6. Remover nós inúteis** | Apaga os auxiliares dentro de obstáculo, os isolados e os redundantes, enquanto a cobertura do chão continua na meta (ou não piora, se já estava abaixo). Primário nunca é apagado, só reportado. |
| **7. Gerar grafo do zero** | Apaga todos os nós e gera primários, raios, auxiliares e ligações a partir do espaço livre. Pede confirmação. |
| **8. Normalizar pesos dos primários** | Reescala os pesos para a soma dar `_primaryWeightBudget` (23), mantendo a proporção entre eles. |
| **Tudo (2 → 3 → 6 → 4)** | Arruma um grafo existente num só Undo e termina com o relatório de cobertura. |
| **Zerar raios ajustados** | Volta todos os nós para o raio padrão do papel. |

### O mapa do chão (base dos menus 1 e 4 a 8)

A arena inteira vira uma grade de `_generationStep` (0,3 m) no plano dos nós. Cada célula
recebe três informações:

- **Andável?** O corpo passa ali (`IsBodyClear` com a folga de passagem) **e** a célula se
  conecta, por flood fill, a um ponto de dentro do prédio. O flood fill começa nos nós
  existentes, no agente ou em `_generationSeeds`. O lado de fora do prédio fica de fora.
- **Folga.** Distância até o obstáculo mais próximo, calculada por transformada de distância
  sobre a própria grade, sem física extra. É alta no centro de salas e corredores.
- **Distância andando** até cada nó, por BFS sobre as células andáveis. Duas salas separadas
  por uma parede ficam a 30 cm em linha reta e a 15 m andando.

A **região** de um nó é o chão que fica mais perto dele andando do que de qualquer outro nó.

### A área de chegada para na parede

`NavGraph._areasStopAtWalls` vem ligado. Um ponto só pertence à área de um nó se estiver no
raio **e** houver linha livre do centro do nó até ele, na altura dos nós (~1,84 m), contra a
layer de parede. Antes, a área era um quadrado cego: atravessava parede, e o agente "chegava"
num nó da sala vizinha sem ter entrado nela.

- **No jogo:** o `FindNodeAt` testa os nós cujo raio contém o ponto, do centro mais perto para o
  mais longe, e fica com o primeiro que enxerga o ponto. Isso custa de 1 a 3 raycasts por step.
- **No gizmo:** a área é desenhada já cortada pelas paredes. São 48 raios por nó, guardados em
  cache e refeitos a cada 2 s fora do Play.
- **No placer:** a cobertura usa a mesma regra, com a linha de visão calculada no mapa do chão
  (uma caixa fina por célula, na altura dos nós, marca o que bloqueia a visão).
- **Na altura dos nós:** mesa e sofá não cortam a área, mas parede, divisória alta e armário
  cortam.

Isso muda a regra de chegada, então os `.onnx` treinados sem ela não servem com ela ligada.

Com a área cortada, sobra pouco "vazamento". Ele passa a ser só o chão que o nó enxerga mas o
corpo não alcança por dentro da área, como atrás de uma baia baixa. Os raios podem ser maiores, e
o mapa precisa de menos nós.

### Grafo enxuto

O `NodeTraining2` gerado com a versão anterior tinha 211 nós, 53 arestas com menos de 3 m e um nó
a 0,30 m de outro, contra 102 nós e nenhuma aresta abaixo de 3,2 m no grafo feito à mão. As causas
medidas e o que mudou estão em `handoff-densidade-do-grafo.md`. As regras agora:

- **Espaçamento para todo nó novo:** nenhum nó novo, de origem nenhuma (gulosa, portal da ligação,
  curva do caminho pelo chão, desvio do menu 3), nasce a menos de `_minNodeSpacing` (3 m) de outro.
  Portal, curva e desvio **reaproveitam** o nó que já está ali quando a reta passa. Só a curva de um
  caminho pelo chão pode furar a regra, porque o caminho tem que fechar.
- **A gulosa para na meta** e não cria nó que cubra menos de `_minNodeGain` (4 m²). Ver "Onde os
  auxiliares novos nascem".
- **Sobreposição custa:** tomar chão que já estava OK em outro nó custa `_overlapPenalty` (0,15)
  por célula. O raio para onde a área do vizinho começa, em vez de engolir o vizinho.
- **Limpeza final:** depois de cobrir o chão, o menu 4, o 7 e o "Tudo" tiram os auxiliares que
  cobrem menos primeiro, enquanto a cobertura continua na meta e o grafo não parte. Auxiliar de
  porta fica.
- **Poda de ligações redundantes:** a ligação A–B sai quando existe um C ligado aos dois com as
  duas pernas mais curtas que A–B. O caminho A–C–B continua, então o grafo não parte. Não sai
  ligação feita à mão nem uma cuja falta deixaria uma porta sem travessia.

### Cobertura do chão (o que o placer garante)

A garantia antiga era "todo ponto a menos de 6 m **andando** de algum nó". Isso não é o que o
agente precisa: com áreas de ±3 m, o chão entre dois nós a 6 m já ficava fora de todas as
áreas, e corredor com menos de ~2,5 m livres nunca recebia nó. Fora de nó, a observação de
vizinhos fica presa no último nó tocado (histórico em `handoff-cobertura-de-nos.md`).

A garantia nova usa a **mesma regra do jogo**. Entre as áreas que contêm um ponto, o
`FindNodeAt` escolhe o nó de centro mais perto. Então cada célula andável cai num de três
estados:

| Estado | O que significa | Gizmo |
|---|---|---|
| **OK** | O nó que vence ali alcança a célula sem sair da própria área. | nada |
| **Nó errado** | O nó que vence só contém a célula **através** de parede ou móvel. O agente "chega" num nó do outro lado da parede. | contorno marrom |
| **Sem nó** | Nenhuma área contém a célula. | quadrado marrom cheio |

A meta é `_coverageTarget` (97%) de chão OK e nenhum chão ruim (sem nó ou no nó errado) a mais
de `_maxHoleDepth` (1 m) andando do chão OK. É a mesma regra na parada da gulosa, no limite da
limpeza e no "OK" do relatório.

Por que profundidade, e não o lado do buraco: uma faixa de 0,3 m ao longo de uma parede de 14 m
media "14 m de lado" e pedia nó, mas o agente nela está a um passo do chão coberto. A
profundidade mede o que o agente sente. O relatório ainda mostra a maior mancha, como informação.

Por que 97%: medido no `NodeTraining2`, o último 1% custa uns 10 nós de 2–3 m² cada, e o grafo
feito à mão cobre só 78% com os móveis ligados.

O Diagnosticar também confere dois pontos extras:

- **Nó à vista:** de cada ponto do chão (amostra de 1 m), algum nó com reta livre a até o
  `_maxNodeDistance` do agente. Sem isso, a observação "nó mais próximo alcançável" aponta
  através da parede. Esfera marrom no gizmo.
- **Spawn points:** cada `_spawnPoints` do `GraphArenaController` cai em chão OK.

Marrom é a única cor ainda livre no vocabulário dos gizmos.

### Como o raio é ajustado (menu 4)

Tudo é medido no mapa do chão, então vazamento, posse e cobertura não podem discordar entre si.
O cálculo é feito em duas passadas, porque o limite do auxiliar depende do raio final dos
primários.

- **Primário:** fica apertado de propósito, porque "visitado" tem que significar "estive lá".
  Nunca passa do padrão. Encolhe até a **metade da distância** para o primário mais próximo,
  ligado ou não. Encolhe também enquanto a área vaza mais que `_maxLeakPrimary` (10%), porque
  vazamento no primário é visita de graça.
- **Auxiliar:** testa os raios de `_maxAuxiliaryRadius` (8) a `_minAuxiliaryRadius` (1,5) e fica
  com o que **mais cobre** o chão que só ele cobre. Um raio só vale se respeitar três regras:
  - vazar até `_maxLeakAuxiliary` (25%);
  - não deixar primário nenhum dono de menos de `_minPrimaryOwnership` (75%) da própria área;
  - não ficar a menos de meio raio de um primário, o que eclipsaria a visita dele.

  No empate, fica o raio mais perto do padrão, para não encher o mapa de override.

**Vazamento** é a fração do chão andável dentro da área que o corpo não alcança a partir do
centro sem sair da área (flood fill).

Encolher um raio pode abrir buraco, e isso é de propósito. Vazamento e roubo deixam o agente
"no nó errado", o que mente para a observação. O buraco é fechado no mesmo passo, com mais nós.

### Onde os auxiliares novos nascem (menus 4 e 7)

É uma cobertura de conjuntos **gulosa**, em duas fases.

**1. Ganho máximo.** Todo candidato numa malha de `_candidateStep` (0,6 m) sobre o chão andável,
fora do espaçamento dos nós que já existem, é avaliado com o raio que mais cobre ali (escada de
`_auxiliaryRadiusStep`, 1 m, de 8 a 1,5). A pontuação é +1 por célula que vira OK, −1 por célula
que vira nó errado e −0,15 por célula OK tomada de outro nó, e as três regras do raio (vazamento,
posse, sombra) valem aqui. Os candidatos entram numa fila de prioridade. A cada passo sai o de
maior ganho, que é reavaliado contra o mapa atual: se continua na frente, vira auxiliar; senão,
volta para a fila com o valor novo. Para quando a cobertura bate a meta ou quando o melhor ganho
fica abaixo de `_minNodeGain`.

**Candidato é qualquer célula onde o corpo passa**, inclusive corredor estreito. Nó apertado só
não vira spawn.

**2. Chão fundo.** O que ainda ficar a mais de `_maxHoleDepth` do chão OK (tipicamente corredor
estreito, que tem poucos candidatos e ganho pequeno) é fechado célula a célula, da mais apertada
para a mais aberta, sem o piso de ganho. Para cada célula: o candidato em volta dela que mais
pontua **resolvendo aquela célula**, e no empate o mais central.

Antes a gulosa era só a fase 2, para todo chão ruim e até acabar: cada canto entre móveis ganhava
o seu nó. Medido no `NodeTraining2`, eram 152 auxiliares onde uma gulosa de ganho máximo com as
mesmas regras de área chega a 98% com ~60.

Célula funda que nenhum candidato resolve fica no relatório para ser resolvida à mão. Isso
acontece com um nó errado colado num primário ou num canto entre móveis.

Nó apertado continua sendo âncora e caminho, mas **não vira spawn**. O `NavGraph.CanSpawnAt`
exige a folga de spawn (`NavGraph._spawnClearance`, 1,25), e isso vale para o agente e para o
hider.

### Onde os primários gerados nascem (menu 7)

As células são percorridas da **maior folga para a menor**. Uma célula é aceita se não houver
primário a menos de `_primarySpacing` (18 m) andando nem vão de porta a menos de
`_minNodeSpacing`. Cada região ganha o primário no ponto mais aberto dela.

Com 10 m, o `NodeTraining2` ganhava 59 primários, um a cada trecho de corredor, e cada um puxava um
auxiliar colado nele (o primário fica no ponto de maior folga, e o auxiliar que cobre a sala queria
o mesmo ponto). Medido na réplica do gerador: 10 m → 59, 14 → 41, 16 → 31, **18 → 27**, 20 → 23
(o número do grafo feito à mão).

O escritório é quase todo aberto: cortando o chão nos 30 vãos de porta, sobra um pedaço de
4.834 m² e outro de 26 m². Por isso o primário é espalhado por distância andando, e não "um por
sala".

O **peso** de cada primário é `_primaryWeightBudget / quantidade`. A soma fica sempre 23, que é
a base da conta dos thresholds do currículo. Gerar mais ou menos primários não muda o teto de
recompensa.

### Portas (`_doorNameContains`)

Todo objeto cujo nome contém `_doorNameContains` ("Door_Hole", o `Wall_01_Door_Hole` do kit)
é um **batente de porta**: passagem, não parede. O vão tem 2,54 m e o corpo 1,70, então só uma
linha quase perpendicular passa. Antes, uma ligação que vinha de um nó no fundo da sala batia
no batente, e a sala do outro lado ficava sem conexão. Agora:

- **Nó no vão.** O menu 4, o "Tudo" e o 7 põem um auxiliar no meio de cada vão que ainda não
  tem nó a menos de 1 m. O meio do vão é o ponto médio entre os dois colliders altos do batente,
  os pilares. A laje baixa do piso fica de fora. As portas duplas do kit (dois batentes com 0,34 m
  entre os meios) ficam com um nó só.
- **Ligado dos dois lados.** No fim do menu 4, do 7 e do "Tudo", todo nó de porta ganha uma ligação
  para o nó mais perto de cada lado da parede que ainda não tenha vizinho (reta livre, ou o caminho
  pelo chão). Antes duas portas do `NodeTraining2` tinham o nó do vão ligado a um lado só.
- **A limpeza não apaga** auxiliar de porta.
- **Relatório.** O Diagnosticar e os menus 4, 7 e "Tudo" dizem quantas portas têm uma ligação
  atravessando o vão e avisam, clicável, cada porta sem nenhuma. Conta como travessia uma
  aresta cruzando a linha da porta **ou** o nó da porta com vizinhos dos dois lados (a aresta
  que termina no nó só encosta na linha).
- **Porta fechada.** Se o vão não é chão andável, o Console avisa. Isso é geometria: a porta é
  estreita demais para o corpo ou tem um móvel no caminho.

Medido no `NodeTraining.prefab`: nenhum collider de parede invade os 30 vãos, e todos têm os
2,54 m livres.

### Um grafo só

Todo nó tem que alcançar todos os outros. Um pedaço solto é uma ilha de onde o agente, o hider e
a fronteira não saem. Três camadas garantem isso:

1. **Ligação por região** (menu 5). Quando nem a reta nem um auxiliar de portal passam, a
   ligação **segue o chão** de uma região até a outra. Ela usa o caminho mais curto, que corre
   pelo meio de salas e corredores e só encosta na parede onde precisa, e põe um auxiliar em cada
   curva ("puxando a corda": de cada ponto, vai até o mais adiante ainda com reta livre). A curva
   reaproveita um nó que já esteja a menos de `_minNodeSpacing` dela, se ele enxerga os dois
   trechos. As duas regiões são conexas e se encostam, então esse caminho sempre existe.
2. **Ponte entre pedaços.** Primeiro tenta a reta mais curta. Sem reta, usa o caminho no chão
   até o nó mais perto de outro pedaço, também com auxiliares nas curvas.
3. **Conferência.** O resumo de todo menu que altera o grafo dá erro se sobrar mais de um
   pedaço. O Diagnosticar lista os nós de cada pedaço solto e os marca com X vermelho.

A ponte só falha se o **chão** não liga os pedaços. Aí o problema é de geometria, e o Console
diz onde.

### Por que a sala grande "quebrava no meio" (menu 5)

Nenhuma regra ligava dois nós distantes da mesma sala: a auto-ligação antiga tinha limite
de 8 m, e na mão é fácil esquecer. O menu 5 troca "perto o bastante" por **"regiões que se
encostam"**:

- Dois nós de uma sala grande são vizinhos mesmo a 20 m, se não houver outro nó entre eles.
- Nós separados por parede **nunca** se ligam: a faixa em volta da parede não é andável, então
  as regiões não se tocam através dela.
- Para cada par, o menu guarda o **portal**, o ponto da fronteira entre as duas regiões com
  mais folga (numa porta, o meio do vão). Se a reta entre os dois nós passa, liga direto. Se
  não passa, usa um nó que já esteja a até 3 m do portal e enxergue os dois, ou põe um auxiliar
  no portal (se não houver nó a menos de `_minNodeSpacing`) e liga pelos dois lados. O portal exige só a folga de
  **passagem**, então um vão estreito também ganha nó. Se nem assim passa, a ligação segue o
  chão (ver "Um grafo só").
- Depois junta os **pedaços soltos** pelo par mais próximo com reta livre e corta as ligações
  novas mais longas dos nós que passaram de `_maxDegree` (8 = `_neighborSlots`), sem nunca
  partir o grafo.

### Quando um nó é inútil (menu 6)

Só auxiliares, em três passadas:

1. **Dentro de obstáculo ou fora do chão andável.** O corpo nunca chega nele. Ele é apagado
   e os vizinhos são religados entre si por reta livre, se precisar.
2. **Isolado**: sem nenhuma ligação.
3. **Redundante.** Sem ele a cobertura continua na meta (ou não piora, se já estava abaixo), e
   os vizinhos continuam conectados, direto ou por uma ligação nova entre eles, respeitando o
   teto de vizinhos. O teste vai do nó que menos chão vence no `FindNodeAt` para o que mais
   vence. Antes a regra era "nenhuma célula piora", e quase nada saía (5 de 152 auxiliares no
   `NodeTraining2`).

Primário dentro de obstáculo **não** é apagado: ele carrega peso, e apagar muda o teto de
recompensa. O Console avisa e a decisão fica com você.

### Como o reposicionamento escolhe o ponto (menu 2)

1. **Grade** de 0,2 m em volta do nó, marcando onde o corpo cabe com a folga de spawn
   (`NavGraph._spawnClearance` = 1,25). O valor vem da meia-diagonal da caixa de 1,7 girada a
   45° (1,2), porque o spawn sorteia a rotação.
2. **Componente conexo** a partir das células livres mais próximas da posição original. É
   isso que impede o nó de **pular uma parede fina** para uma sala vizinha mais vazia.
3. **Placar** = folga até o obstáculo mais próximo (limitada a `_desiredClearance` = 2 m)
   − 0,3 × deslocamento. Cada ligação que ficaria bloqueada custa −100, primário invadindo
   primário custa −10 e auxiliar a menos de meio raio de um primário custa −10.
   Só os 32 melhores candidatos pagam o teste das ligações, e o vencedor é **refinado** numa
   grade de 5 cm.
4. Se nada couber com 1,25, o nó tenta de novo com a folga de passagem (0,85) e avisa. Ele
   continua valendo como âncora, mas não vira spawn. Se ainda assim nada couber, ele fica onde
   está, marcado com um **X vermelho** no gizmo.

Com `_moveOnlyObstructed` desligado, o passo também centraliza os nós que já estão livres.

## 4. Como usar (NodeTraining)

1. Abra o `NodeTraining.prefab` em **Prefab Mode**, não a instância na cena. Assim a
   correção vale para as 9 arenas do `Nodes_2`, e o Unity não deixa apagar nó de uma instância.
2. Ligue os grupos de `Constraints/Map_Objects` que o mapa final vai ter. O placer avisa
   quantos colliders de parede estão desligados, porque eles não contam.
3. No GameObject `Graph`: **Add Component → NavGraphPlacer**, se ainda não estiver lá.
4. **1. Diagnosticar** e leia o Console. Cada linha é clicável e seleciona o nó.
5. Escolha um caminho:
   - **Manter a autoria e arrumar:** **Tudo (2 → 3 → 6 → 4)**.
   - **Descartar e gerar:** **7. Gerar grafo do zero**. Ajuste antes `_primarySpacing` se quiser
     mais ou menos primários.
6. **8. Normalizar pesos dos primários**, para a soma voltar a 23.
7. **1. Diagnosticar** de novo. A meta é o relatório dizer "OK". O que sobrar em marrom ou com
   X vermelho se resolve à mão.
8. Salve o prefab e confira fora do Unity: `python tools/graph_report.py <prefab>` (pedaços,
   portas, grau, arestas curtas) e `python tools/graph_coverage.py <prefab>` (cobertura do chão,
   réplica do mapa do placer). Os dois terminam com o checklist de aceite.
9. `NavGraph` → **Validar ligações**, depois Play e Console (`ValidateSetup`, avisos do bake).
10. Leia o resumo no Console: primários, peso total, maior aresta e diâmetro.
   - Ajuste `_maxNodeDistance` do `GraphExplorerManager` para a maior aresta.
   - O diâmetro é só informativo: o agente normaliza as distâncias de caminho por ele sozinho.
11. Se a cena tiver overrides antigos no `Graph` (lista `_nodes`, vizinhos), reverta-os na
    instância: eles apontam para nós que podem ter sido apagados.
12. No treino, acompanhe `Exploration/OffNodeFraction` no TensorBoard. Perto de zero confirma
    que o agente quase nunca fica fora de nó.

Tudo tem Undo: Ctrl+Z desfaz o passo inteiro.

### Alternativa: ladrilhar o chão (menu 9)

**9. Ladrilhar o chão com nós retangulares** troca os discos por **retângulos que não se
sobrepõem**. Cada ponto do chão fica em exatamente um nó, então não há desempate nem buraco.
O `NavGraph` passa para a forma **Retângulo** e os menus 1 a 7 passam a recusar este grafo,
porque eles trabalham com raio. Para voltar, troque o Node Shape para Circle/Square e gere de novo.

A grade de `_tileStep` (0,25 m) separa o chão em três camadas:

- **Chão:** a coluna do corpo está livre naquela célula.
- **Andável:** o centro do corpo pode estar ali (é conectado aos pontos de dentro do prédio).
- **Acessível:** o chão que o corpo encosta, ou seja, o andável dilatado por meia largura do
  corpo. É ele que vira ladrilho. Por isso os ladrilhos vão até a parede.

O que sai de cada tipo de espaço:

- **Porta:** um ladrilho do tamanho exato do vão, de pilar a pilar ao longo da parede e com a
  espessura da parede. Porta por onde o corpo não passa vira vermelha. Só portas alinhadas aos
  eixos do mundo são tratadas assim.
- **Corredor:** trecho mais estreito que `_corridorMaxWidth` (4 m) e mais comprido que largo.
  Cada seção transversal, de parede a parede, é inteira de um ladrilho, e seções seguidas com
  a mesma largura (± `_corridorTolerance`) formam o mesmo ladrilho. O resultado é uma **fila**,
  nunca dois ladrilhos lado a lado na largura. Um pilar que estreita o corredor fica dentro do
  retângulo, porque é obstáculo e o agente não pisa nele.
- **Sala e cruzamento:** a gulosa escolhe o maior retângulo livre, repetidamente. Vizinhos cuja
  união é um retângulo são juntados. Depois tudo é cortado em pedaços **iguais** de até
  `_maxTileSize` (5 m). Corredor só é cortado no comprimento.
- **Vermelho** (`NavBlockedArea`, não é nó): chão livre que o corpo não encosta, como um vão entre
  móveis ou uma sala de porta estreita. Mancha menor que `_minBlockedArea` é arredondamento e
  volta a ser chão. Mancha que encosta na borda do mapa é o lado de fora do prédio e é ignorada.

O nó de cada ladrilho fica no ponto **andável** mais perto do centro. É desse ponto que saem as
ligações e a direção na observação, enquanto o retângulo guarda o chão (`_areaSize`/`_areaOffset`
no `NavNode`). Dois ladrilhos são vizinhos quando o centro do corpo passa de um para o outro.

Se a reta entre dois nós raspa numa quina, o nó desliza dentro do próprio ladrilho até liberar a
ligação, o que na prática o põe na frente da porta. A ligação que continua bloqueada sai se o
grafo não partir sem ela. Se ela for a única passagem, fica e aparece com X vermelho.

Todos os nós nascem **auxiliares**. Os de exploração e de ping que já existiam passam tipo e
peso para o ladrilho que os contém (`_tilingKeepKinds`). Antes de treinar:

1. Marque os ladrilhos de **Exploração** e de **Ping**.
2. Rode **8. Normalizar pesos**.
3. Ajuste a pontuação de cada tipo no `NavGraph`.

Sem nenhum nó de exploração a cobertura nasce em 100%, e o bake acusa o erro.

## 5. Parâmetros (defaults e por quê)

| Campo | Default | Motivo |
|---|---|---|
| `NavGraph._spawnClearance` | 1,25 | meia-diagonal do corpo 1,7 (1,2) + 5 cm, para nascer em qualquer rotação; mora no NavGraph porque é o jogo que decide o spawn |
| `_desiredClearance` | 2 | acima disso a folga não vale nada, e o nó não é puxado para o centro do salão |
| `_maxDisplacement` | 3 | mais que isso vira outro nó, e a decisão de autoria deixa de valer |
| `_sampleStep` / `_refineStep` | 0,2 / 0,05 | ~2 steps de física do agente / precisão final |
| `_displacementCost` | 0,3 | anda ~3 m para ganhar 1 m de folga, o bastante para sair de trás de uma mesa |
| `_detourMargin` | 4 | contorna uma mesa de reunião ou uma fileira de baias |
| `_maxLeakPrimary` / `_maxLeakAuxiliary` | 0,10 / 0,25 | ver "Como o raio é ajustado" |
| `_minPrimaryRadius` / `_minAuxiliaryRadius` | 1,2 / 1,5 | abaixo disso o agente atravessa o nó sem registrar |
| `_maxAuxiliaryRadius` | 8 | com a área cortada pela parede, um nó grande cobre a sala toda; acima de 8 o cone por um vão de porta entra demais na sala vizinha |
| `_radiusStep` / `_auxiliaryRadiusStep` | 0,1 / 1 | passo do primário encolhendo / escada de raios do auxiliar (cada raio custa um flood fill) |
| `_minPrimaryOwnership` | 0,75 | o auxiliar pode encostar e entrar um pouco, sem comer a borda do primário |
| `_coverageTarget` / `_maxHoleDepth` | 0,97 / 1 m | meta da gulosa, da limpeza e do relatório; o último 1% custava ~10 nós de canto; a 1 m do chão OK o agente sai em ~10 steps |
| `_minNodeGain` | 4 m² | nó que cobre menos que um quadrado de 2 m é canto entre móveis; o chão fundo é fechado sem esse piso |
| `_candidateStep` | 0,6 | acha o meio de um corredor de 2 m; o custo cai com o quadrado deste valor |
| `_maxNewNodes` | 600 | trava contra mapa errado (Wall Layer faltando), não um limite de projeto |
| `_primaryWeightBudget` | 23 | soma dos pesos em que os thresholds do currículo foram calculados |
| `_generationStep` | 0,3 | ~90 mil células numa arena de 100 × 80 m, alguns segundos de física |
| `_primarySpacing` | 18 | ~27 primários no `NodeTraining2` (10 dava 59, um por trecho de corredor) |
| `_maxDegree` | 8 | = `_neighborSlots`; vizinho a mais some da observação |
| `_doorNameContains` | "Door_Hole" | nome dos batentes de porta do kit; vazio desliga o tratamento de portas |
| `_minNodeSpacing` | 3 m | vale para todo nó novo; = menor aresta do grafo feito à mão (3,2 m); 2 m deixava auxiliar colado em primário |
| `_overlapPenalty` | 0,15 | crescer o raio só compensa se ~7 células de sobreposição trouxerem 1 de chão novo |
| `NavGraph._areasStopAtWalls` | ligado | área de chegada cortada pela parede, no jogo, no gizmo e no placer |

O antigo `_auxiliarySpacing` (6 m) saiu: espaçamento não é cobertura.

## 6. Limites conhecidos

- **Não foi rodado no Unity ainda.** O código compila nas configurações de editor e de player,
  mas o primeiro uso real vai mostrar tempos e números. A estimativa para o `NodeTraining2` vem da
  réplica offline (`tools/graph_coverage.py` e as medições de `handoff-densidade-do-grafo.md`):
  ~110–140 nós a 97%. O mais pesado é a fase 1 da gulosa, que avalia uma vez cada candidato da
  malha em cada raio (~14 mil posições no `NodeTraining2`): espere dezenas de segundos, com barra
  de progresso cancelável.
- A distância andando é medida em passos de 8 vizinhos (a diagonal conta como 1 passo),
  então subestima distâncias diagonais em até ~30%. Isso só afeta o espaçamento dos primários.
- O desvio (menu 3) e o portal (menu 5) inserem **um** auxiliar. Um obstáculo em L que
  precise de dois pontos cai em "remover" ou "sem solução".
- O primário gerado é o ponto de maior folga da sala. Não é necessariamente o melhor ponto
  de **vista**, porque o sistema não calcula visibilidade. Se uma sala precisar de um ponto
  específico, mova o primário à mão e rode o menu 4.
- Nó desligado (`IsEnabled = false`) conta como cobertura no placer. Nenhuma lição do currículo
  desliga nós hoje.
- Posição e raio levam em conta os móveis **como estão agora**. Se você arrastar móveis
  depois, rode de novo.
