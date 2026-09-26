# Handoff: o grafo gerado deixa o agente sem nó

Registro de 23/09/2026, para quem continuar o trabalho do `NavGraphPlacer` num chat novo.
Leia junto com `CLAUDE.md` (raiz) e `docs/graph/posicionamento-de-nos.md`.

> **Estado: tratado no mesmo dia.** As invariantes A–D da seção 3 viraram código em
> `NavGraphPlacer.Coverage.cs` (menu "4. Ajustar raios e cobrir o chão", relatório no
> "1. Diagnosticar", gizmo marrom). A limpeza passou a usar a invariante A, o portal aceita vão
> estreito e o spawn ficou em `NavGraph.CanSpawnAt`. A métrica `Exploration/OffNodeFraction`
> mede o resultado no treino. A rede de segurança em runtime (seção 4, item 5) **não** foi feita:
> só vale a pena se essa métrica mostrar o agente fora de nó com frequência. Este documento fica
> como histórico do problema; o guia atual é `posicionamento-de-nos.md`.

## 1. Onde estamos

Branch `feature/node-exploration-vision`, **nada commitado**. Mudanças desta sessão:

| Arquivo | O que é |
|---|---|
| `Assets/Scripts/Graph/NavGraph.cs` | Checagem de ligação virou CapsuleCast na **coluna do corpo** (`_bodyBottom = -1.6`, `_bodyTop = 1.8`, relativos ao nó); novo `IsBodyClear`; física da cena do grafo (Prefab Mode); aviso de sobreposição do bake agora vale para **todos** os pares de primários; `AreaDistance`, `RadiusOf` e `CollectChildNodes` viraram `internal`. |
| `Assets/Scripts/Graph/NavNode.cs` | `SetRadiusOverride` e `SetKind` (internal, para ferramentas de autoria). |
| `Assets/Scripts/Graph/NavGraphPlacer.cs` | Menus 1–4 e "Tudo": diagnosticar, reposicionar nó obstruído, desviar aresta bloqueada, ajustar raios (vazamento, buraco, primário × primário, posse do primário). |
| `Assets/Scripts/Graph/NavGraphPlacer.Generation.cs` | Menus 5–8: mapa do chão andável, ligar por região, completar com auxiliares, remover inúteis, gerar do zero. |
| `docs/graph/posicionamento-de-nos.md` | Guia do placer (vai precisar de revisão depois da correção abaixo). |
| `CLAUDE.md`, `docs/graph/exploracao-local.md` | Atualizados. |

`Assets/Prefabs/NodeTraining2.prefab` apareceu no working tree e não foi criado nesta sessão;
provavelmente é o teste do Arthur com o gerador. Pergunte antes de mexer.

Contexto medido no `NodeTraining.prefab`:
- os nós ficam ~1,84 m acima do chão;
- o corpo do agente é uma caixa de 1,7 × 3,63 × 1,7;
- a forma de área é **Square**: o raio é meia-aresta, na métrica Chebyshev;
- os raios padrão são 1,8 (primário) e 3 (auxiliar);
- os `Map_Objects` estão na layer `Wall`, com BoxCollider, e hoje estão desligados.

O código compila nas configurações de editor e de player. Para conferir sem o Unity, copie o
`Assembly-CSharp.csproj` com caminhos absolutos para uma pasta temporária, inclua os dois
arquivos do placer e rode `dotnet build`. Verifique com `grep` que os arquivos entraram mesmo
no projeto: numa checagem desta sessão eles não tinham entrado e o build "passou" sem compilá-los.

## 2. O problema (não foi tratado)

O gerador e a limpeza garantem a coisa errada. A garantia atual é: **todo ponto em que um nó
caberia fica a menos de 6 m ANDANDO de algum nó**. O que o agente precisa é outra coisa:
**todo ponto em que o corpo consegue estar fica DENTRO da área de chegada de algum nó**, ou
pelo menos com um nó à vista.

### 2.1 Onde o código falha

1. **Corredor estreito fica sem nó.** `FillAuxiliaries` e `PlacePrimaries` só percorrem
   `grid.CellsByClearance(_nodeClearance)`, ou seja, células com folga ≥ 1,25. Uma célula de
   corredor com folga entre 0,85 (o corpo passa) e 1,25 (não cabe nó) nunca é testada nem
   coberta. Um corredor com menos de ~2,5 m livres pode ficar **sem nenhum nó**.
2. **O critério de cobertura não é área.** Com espaçamento de 6 m andando e área de ±3 m, o
   chão entre dois nós fica fora de qualquer área por construção.
3. **O ajuste de raio abre buracos.** `FitRadiiStep` encolhe raios por vazamento, por posse do
   primário e por primário × primário. Ele não confere se algum chão ficou fora de todas as
   áreas: só avisa buraco **ao longo de arestas**, nunca de área.
4. **A limpeza apaga cobertura.** `PruneUseless` considera redundante um auxiliar cuja região
   continua "a menos de 6 m andando" de outro nó. É o mesmo critério errado, então ela apaga
   justamente nós que davam área ao chão.
5. **O portal falha no estreito.** `FindOrCreatePortalNode` exige folga ≥ 1,25 no portal, e
   num vão estreito não cria o nó.

### 2.2 O que acontece em jogo quando o agente está fora de todo nó

Comportamento lido no código, sem mudança nesta sessão:

- `GraphExplorationMemory.Tick`: `FindNodeAt` devolve -1 → `IsAtNode = false`, e
  `CurrentNodeIndex` **continua sendo o último nó alcançado** (a âncora).
- `GraphExplorerManager.CollectObservations`: os 8 slots de vizinho descrevem os vizinhos
  **da âncora**. Se o agente andou muito, ela pode estar longe ou atrás de uma parede.
- Observação [4..6]: `FindNearestReachableNode` procura o nó mais perto com reta livre. Se
  nenhum estiver à vista, cai no mais perto **atravessando parede**.
- A fronteira (BFS) parte da âncora. A estagnação conta steps sem nó novo, então um trecho
  longo sem nó também é punido.
- Resultado: o agente fica **perdido**, com observação velha ou mentirosa.

### 2.3 E o spawn

- Com `_spawnAtRandomNode` ligado (o padrão), o agente nasce **em cima de um nó** e o
  `OnEpisodeBegin` já registra esse nó. Então o spawn em si não cai fora de nó.
- **Mas** qualquer nó pode ser spawn, e o hider também nasce em nó. Um nó gerado num canto,
  cercado de chão sem cobertura, gera episódios que começam numa ilha de onde o agente sai
  para o "sem nó".
- Com `_spawnAtRandomNode` desligado, os `_spawnPoints` nunca são validados contra o grafo.
- Se no futuro o spawn virar "qualquer ponto do chão", a cobertura de área passa a ser
  obrigatória, não só desejável.

## 3. O que precisa valer (invariantes)

Todos medidos no mesmo mapa do chão do gerador (`WalkGrid`, células andáveis = o corpo passa
com `LinkClearance` e há conexão com o interior do prédio):

- **A. Cobertura de área.** Toda célula andável fica dentro da área de chegada de algum nó
  ativo, na métrica do `NavGraph.AreaDistance`. Além disso, a célula alcança o centro desse
  nó **sem sair da área** (mesmo flood fill do vazamento). Meta: 100%, ou uma tolerância
  explícita, por exemplo ≥ 98% e nenhum buraco com mais de 1 m de lado.
- **B. Orientação.** De toda célula andável, `FindNearestReachableNode` acha um nó com reta
  livre (`IsSegmentClear`) a no máximo `_maxNodeDistance`, sem cair no fallback "através da parede".
- **C. Spawn.** Todo nó é spawn válido (`IsBodyClear` com `_nodeClearance`). Nos `_spawnPoints`,
  o ponto está dentro da área de algum nó.
- **D. O que já existia continua valendo.** Primário × primário sem sobreposição, posse do
  primário ≥ `_minPrimaryOwnership`, vazamento dentro da tolerância, grafo conexo, grau ≤ 8,
  arestas livres para o corpo.

A e D brigam: raio maior cobre mais, mas vaza e rouba área. Quando brigarem, a saída é
**mais nós**, e não relaxar a regra. D pode forçar raios pequenos; A então pede nós mais
densos. Ninguém deve ficar sem nó.

## 4. Caminho sugerido

1. **Medir antes de mudar.** O Diagnosticar ganha:
   - a % de chão coberto (invariante A);
   - o maior buraco;
   - as células sem nó à vista (B);
   - um gizmo de **chão descoberto**, de uma cor ainda livre no vocabulário (vermelho já é
     "problema de geometria"; evitar verde, laranja, amarelo, magenta, branco e rosa).
2. **Gerar por cobertura de conjuntos**, não por espaçamento.
   - Enquanto houver célula andável descoberta, escolha o candidato que cobre mais células
     descobertas, com o raio que ele pode ter sem violar D (vazamento, posse, sobreposição).
   - Candidato = qualquer célula onde caiba nó.
   - No corredor estreito, avaliar se o nó pode ter folga menor que 1,25. Nesse caso ele deixa
     de ser spawn válido: excluir do sorteio de spawn exige mudar o `GraphArenaController` —
     pesar isso.
   - Primários continuam sendo escolhidos por ponto de vantagem. Os auxiliares fecham a cobertura.
3. **Proteger a cobertura nos outros passos.** `FitRadiiStep` e `PruneUseless` só encolhem ou
   apagam se A continuar valendo; senão, mantêm ou pedem um nó novo. O critério "6 m andando"
   do `PruneUseless` deve ser substituído pela invariante A.
4. **Ligações** continuam por região, que já está correto. Revisar só o portal no vão estreito.
5. **Runtime, só se ainda precisar.** Uma rede de segurança no agente (por exemplo, a âncora
   passa a ser o nó alcançável mais próximo quando ele está longe dela) muda o significado de
   observações e **invalida os `.onnx`**. Se for preciso, use o slot reservado [8] e avise antes.

## 5. Como verificar

- Rodar os menus no `NodeTraining.prefab` em Prefab Mode, com os `Map_Objects` ligados, e ler o
  Console: cobertura %, maior buraco, contagens.
- Play e observar com o agente selecionado: `IsAtNode` deveria ficar quase sempre em 1 andando
  por qualquer lugar. Vale logar a fração de steps com `IsAtNode = 0` como métrica
  `Exploration/*` no TensorBoard.
- Refazer a conta de recompensa se o número ou o peso dos primários mudar (thresholds do
  currículo; ver `CLAUDE.md`, "Armadilhas").

## 6. Convenções que valem aqui

Estão no `CLAUDE.md`; as que mais importam para este trabalho:
- Comentários em pt-BR que explicam o **porquê**, com a conta.
- `[SerializeField] private` com `[Header]`, e `if (x == null)`, nunca `?.`.
- Código de editor dentro de `#if UNITY_EDITOR`, com Undo em tudo.
- Não mudar o layout de observação sem avisar.
