# Exploração local: sem seta, decidindo pelas saídas

Registro das mudanças de 19/09/2026 no GraphExplorer para que a política aprenda a
**explorar** (a regra) e não uma **rota** (a resposta). Escrito para quem vai retomar o
treino ou mexer em recompensa/observação depois.

## 1. Diagnóstico

O run `graph_11` parou aos 140k steps com Mean Reward 11.25, na lição 1 do currículo antigo
(`frontier_hint = 1.0`). O agente "aprendeu a seguir a linha roxa" e, quando spawnava nos
mesmos pontos, fazia os mesmos caminhos. Duas causas:

- **A seta era resposta.** A observação "direção do próximo passo até o não-visitado mais
  próximo" + shaping por aproximação diziam literalmente para onde ir. A política não
  precisava decidir nada, só executar.
- **Rota determinística.** 10 spawns fixos + BFS determinística = de cada spawn, sempre a
  mesma sequência ótima. A política decorava 10 rotas.

## 2. O que é legítimo o seeker saber

| Informação | Onipotente? | Por quê |
|---|---|---|
| Grafo de nós (estrutura do mapa) | Não | "Conheço o prédio." |
| Onde já estive (memória de visitados) | Não | Memória própria. |
| Raciocínio sobre grafo + memória (valor das saídas) | Não | Deduzido do que já sabe. |
| Ver o Hider através de parede | **Sim** | Fase de visão: cone + raycast, local. |

O critério não é "quanta informação", é **dado vs resposta**: o agente pode saber o que há
atrás de cada porta; ninguém pode dizer "vá por ali".

## 3. O que foi feito

### 3.1 A seta roxa foi removida por inteiro

Saíram: observação [9..12] (agora reservada, emite zero), `_frontierProgressReward`,
`_frontierApproachReward`, os parâmetros `frontier_hint` e `frontier_hint_steps` do
currículo, a BFS de fronteira (`TryFindNearestUnvisited`, `TryFindPathTo`), o estado de
fronteira na memória e o gizmo magenta.

### 3.2 No lugar: valor de cada saída (dado, não resposta)

Cada slot de vizinho tem 1 float: **peso não-visitado alcançável entrando por aquela
saída, descontado por `decay^distância`, relativo à melhor saída do nó atual**.

- `NavGraph.DiscountedUnvisitedBeyond(from, via, decay, visited)` — BFS a partir do vizinho
  com o nó atual carimbado como parede; soma `peso × decay^d` de cada nó não-visitado.
- `GraphExplorationMemory.ScoreExits()` — uma vez por decisão, calcula para todos os
  vizinhos do nó atual. `ExitScore(neighbor)` devolve o valor **relativo ao máximo**:
  1 = a melhor saída (ou empatada), 0 = nada por ali.
- `_lookaheadDecay = 0.85` (na memória). Sem limite de profundidade: o longe pesa pouco mas
  **nunca zera**, então num beco com tudo visitado por perto a saída que leva ao inexplorado
  ainda pontua mais. É o que evita o agente cego em beco, que era o que a seta resolvia.
- Relativo, e não absoluto, porque a decisão é "qual porta": um número que vale 1.0 para a
  melhor porta em qualquer mapa e fase é mais fácil de ler que uma fração que encolhe
  conforme o mapa é coberto. "Quanto falta no total" já está na cobertura (observação global).
- Gizmo: esfera **rosa** em cada vizinho do nó atual, tamanho = valor. Responde "por que ele
  foi por ali?".
- Fica ligado sempre, inclusive no jogo final.

### 3.3 Sinal denso sem direção

`_newEdgeReward` passou a pagar **qualquer aresta inédita** (antes só primário↔primário,
que não existe no mapa atual), com valor **0.02** (era 0.05). "Andei por onde nunca andei"
paga o mesmo em qualquer rumo — ensina a variar caminho, não a seguir um. É o único sinal
denso do sistema agora. Teto: 120 arestas × 0.02 = 2.4, contra 17.25 de cobertura.

### 3.4 Anti-decoreba (variação por episódio)

| Mecanismo | Onde | Efeito |
|---|---|---|
| Spawn em nó aleatório | `GraphArenaController._spawnAtRandomNode` (default ligado); `_nodeSpawnHeightOffset = 0.15` | 102 origens em vez de 10; `_spawnPoints` vira fallback. |
| Pré-visitados aleatórios | `previsited_fraction` no currículo → `GraphExplorationMemory.ResetEpisode(fraction)` | Fração dos primários já nasce visitada: sai do denominador da cobertura, aparece como visitado para o agente e no valor das saídas. Disco **azul-escuro** no gizmo. Sempre sobra ≥1 por descobrir. |

A ideia de "virar primários em auxiliares aleatoriamente" virou os pré-visitados: mesmo
efeito, mas o agente **enxerga** (flag de visitado); virar auxiliar seria invisível para ele.

### 3.5 Métricas de exploração (separadas da recompensa)

`GraphExplorerManager.RecordExplorationStats` → `Academy.Instance.StatsRecorder`, no
TensorBoard:

| Métrica | O que mede |
|---|---|
| `Exploration/Coverage` | cobertura ao fim do episódio |
| `Exploration/RevisitRatio` | (chegadas − inéditas) / chegadas a primários — vai-e-vem |
| `Exploration/NewNodesPerMin` | primários inéditos por minuto simulado — ritmo |
| `Exploration/StepsToTarget` | steps até bater o alvo (só nos episódios que bateram) |

Compare runs por elas; `Cumulative Reward` muda a cada ajuste de peso.

### 3.6 Currículo (2 eixos, 3 lições)

| Lição | coverage_target | previsited_fraction | teto estimado | threshold p/ sair |
|---|---|---|---|---|
| Quarter | 0.3 | 0.0 | ~9.3 | 7.0 |
| Half | 0.6 | 0.25 | ~12.0 | 9.5 |
| Most | 0.9 | 0.5 | ~11.8 | (final) |

Conta: 23 primários × 0.75 = 17.25 de cobertura, +5 de conclusão, +2.4 de arestas, ~−2 de
penalidades. O threshold é avaliado dentro da lição que encerra e **não cresce** de lição em
lição (pré-visitados tiram renda enquanto o alvo sobe). Thresholds são estimativa: se
`Lesson Number` travar em 0 depois de ~300k, abaixe os dois juntos.

### 3.7 Reconfiguração do mapa (`NodeTraining.prefab`, 102 nós, "totalmente preenchido")

Medido do prefab: 23 primários, 79 auxiliares, 120 arestas (mediana 7.4 m, máx 20.6 m),
grau máximo 6 (cabe nos 8 slots), diâmetro **42 arestas**, primário mais próximo de outro a
8.15 m / 3 arestas, grafo conexo, nenhum auxiliar eclipsando primário.

| Parâmetro | Antes | Agora | Motivo |
|---|---|---|---|
| `_defaultPrimaryRadius` | 1.8 | **2.5** | menor distância primário↔primário é 8.15 → até 4.0 não sobrepõe; 2.5 dá folga para não passar reto a 0.1 m/step |
| `_defaultAuxiliaryRadius` | 3 | **4** | com 3, 100 das 120 arestas deixavam buraco entre áreas; 4 (quadrado de 8 m) fecha toda aresta ≤ 8 m |
| `_maxNodeDistance` | 35 | **25** | maior aresta é 20.6 m; 35 desperdiçava resolução |
| `_lookaheadDecay` | — | **0.85** | diâmetro 42: a 3 arestas 61%, a 10 20%, a 42 0.1% — perto domina, longe não some |
| `VectorObservationSize` | 61 | **69** | 8 slots × 6 + 21 |

Todos os 102 nós têm peso 1 (como o Arthur deixou). Se alguma sala valer mais, é só mudar
o `_explorationWeight` dos primários dela.

## 4. O que NÃO foi feito, e por quê

- **Ação discreta "qual saída"** com controlador scriptado — descartado pelo Arthur (movimento
  menos orgânico; exigiria segundo modo na perseguição).
- **LSTM** — a memória explícita por nó já entrega "onde estive".
- **Regiões** — removidas antes (peso por nó).
- **Shaping direcional de qualquer tipo** — é o que a seta era. Se o treino não descolar em
  ~300k steps, o plano B é um potencial **não-direcional** sobre o inexplorado descontado
  (Φ = soma do valor de todas as saídas), nunca a seta de volta.

## 5. Próximos passos

1. `mlagents-learn config/graph_explorer_curriculum.yaml --run-id=graph_12`. No Play, olhar o
   Console (`ValidateSetup` confere o 69) e, com o agente selecionado, as esferas rosas:
   a maior deve estar na saída com mais inexplorado atrás.
2. **Treinar em 2–3 mapas ao mesmo tempo** (3 layouts × 3 cópias na cena) e segurar um 4º
   para avaliar — é o teste real de "entendeu o mapa via nós".
3. Acompanhar `Exploration/*`, não só reward.

## 6. Armadilhas conhecidas

- Lição Most: `previsited 0.5` + `coverage 0.9` sobre o que resta = ~11 primários, precisa de
  10. Se `Episode Length` cravar em 1600, baixe o alvo para 0.8 ou os pré-visitados para 0.4.
- Nascer em cima de um primário registra o nó como visitado sem pagar (1 nó "de graça" na
  cobertura). Aceitável; se incomodar, trate o nó do spawn como pré-visitado.
- `_newEdgeReward` × arestas tem que ficar bem abaixo da cobertura. Mapa com 500 arestas →
  0.005.
