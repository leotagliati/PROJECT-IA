# Branch `feature/node-unexplored`: explorar e caçar sem a seta

Criada em 20/09/2026 a partir de `7a4e4b0` (seeker com hider, ping, visão e seta fixa). Esta
branch responde a pergunta: **o agente consegue explorar e seguir o hider SEM a seta roxa,
recebendo só dados locais sobre cada saída?** Escrito para quem vai rodar/avaliar e para o TCC.

## 1. Por que a seta não pode simplesmente sumir (o que o run 18 mostrou)

Com o fade (`frontier_hint` 1.0 → 0.2 → 0) o agente ficou ótimo seguindo a seta e colapsou
quando ela zerou. Dois motivos:

1. **Fade não tira informação.** Multiplicar a observação por 0.2 deixa a direção inteira;
   a rede só reescala os pesos. Ela nunca é obrigada a olhar os vizinhos.
2. **Sem a seta faltava dado.** Com só vizinhos imediatos (direção, visitado, peso), todo beco
   é igual — nada diz *qual* saída leva de volta ao inexplorado a cinco nós dali. Não é falta
   de treino; é falta de informação.

A branch ataca os dois: (1) **dropout** de episódios inteiros em vez de fade; (2) **valor por
saída** na observação.

## 2. O que o agente vê agora

**Vetor: 89 floats** = 25 globais + 8 vizinhos × 8. `VectorObservationSize = 89` no prefab.

| Slot | Conteúdo | Mudou? |
|---|---|---|
| [0..7] | está em nó, âncora, nó mais próximo, cobertura | — |
| **[8]** | **"travado"**: steps sem nó inédito / `_stagnationSteps`, saturado em 1 | novo (era zero) |
| [9..12] | seta (direção, distância, arestas) — **zero em episódios sem seta** | dropout |
| [13..15] | ping: ativo, distância em arestas, quente/frio | — |
| [16..20] | visão: vendo, já viu, direção + distância à última posição vista | — |
| **[21..22]** | **heading**: para onde o corpo está virado (forward X/Z no mundo) | novo |
| **[23..24]** | **última ação** (X/Z) | novo |
| por vizinho | direção X/Z, distância, visitado, peso, **explorar**, **calor**, válido | +2 |

### Por que heading, última ação e um sensor de perto
Os nós são bem espaçados (aresta mediana 7.4 m, máx 20 m), então entre dois nós o agente
anda vários segundos "às cegas" com só a direção do alvo. O sensor de raios de parede é
**preso ao corpo** (que gira para onde anda) e as ações são **no mundo**: sem observar o
heading, "parede à esquerda nos raios" não tem tradução para "não ande para +X", e a rede
aprende a ignorar os raios — daí bater na parede. Sem a última ação, cada decisão parte do
zero — daí o ziguezague. E 7 raios em ±70° a 20 m dizem "tem parede longe", não "vou bater
em 1 m". Entraram: heading (2), última ação (2) e um segundo `RayPerceptionSensorNear`
(11 raios, ±100°, 6 m, a 0.2 m do chão) no prefab do agente.

### Valor por saída (`NavGraph.ScoreBeyond`, `GraphExplorationMemory.ScoreExits`)
A partir de cada vizinho, uma BFS que **não passa pelo nó atual** soma, para cada nó
alcançado, `massa × decay^distância` (`_lookaheadDecay = 0.85`):
- **explorar** = peso dos nós não-visitados ("quanto ainda há para ver por ali");
- **calor** = calor dos nós ("onde o hider pode estar por ali").

Cada um é normalizado **pela melhor saída do nó atual** (1 = a melhor, 0 = nada). Relativo
porque a decisão é "qual porta"; "quanto falta no total" já está na cobertura. Com decay 0.85 e
diâmetro 42, a 3 arestas um nó vale 61%, a 42 vale 0.1% — o perto domina, o longe **nunca
zera**, e é isso que dá saída de beco.

**É dado, não resposta**: o argmax coincide com a seta na maioria dos casos (bom — prova que
há informação suficiente), mas a política **pode** escolher a segunda melhor (mais perto,
mais valiosa) e aprende a pesar explorar × calor × visitado × peso × distância.

### Mapa de calor (`GraphExplorationMemory`, `_heat[]`)
"Quanto acho que o hider pode estar em cada nó" — memória do seeker, nunca a posição real.
- **Fontes**: ping (passo do hider num primário) → `+1.0` no nó (`_pingHeat`); avistar ou
  perder de vista → `+2.0` no nó mais próximo de onde viu (`GraphHiderPerception._sightHeat`).
- **Decai**: meia-vida 20 s (`_heatHalfLifeSeconds`).
- **Difunde**: 15%/s escorre para os vizinhos (`_heatDiffusionPerSecond`) — "ele se mexe".
- **Zera** no nó em que o seeker pisa: "conferi, não está aqui". É o que faz a busca avançar.
- Gizmo: coluna vermelha em cada nó com calor (altura = calor); esferas **rosa** (explorar) e
  **vermelhas** (calor) nos vizinhos do nó atual = os números que a rede recebe.

Ping, visão e hider funcionam como antes; o calor só **soma** um canal por onde a política
descobre para que lado ir quando o ping/visão não estão dizendo.

## 3. Dropout em vez de fade
`frontier_dropout` (currículo) = probabilidade de o **episódio inteiro** nascer sem seta
(`GraphExplorerManager._hintEnabledThisEpisode`; observação, shaping e gizmo zeram juntos).
Sobe 0 → 0.3 → 0.6 → 0.9 → 1.0. Nos episódios sem seta, o único caminho para a recompensa
passa pelo valor das saídas; a política aprende os dois mapeamentos ao mesmo tempo e não há
queda de gradiente quando chega a 100%. `Exploration/HintEnabled` no TensorBoard registra
qual episódio era qual.

## 4. Régua e métricas
- **Baseline greedy** (`GraphExplorerManager.Heuristic`, `_scriptedBaseline`): com `Behavior
  Type = Heuristic Only`, vai para a saída de maior valor (calor se houver, senão explorar;
  empate: a mais perto). Mesmas observações da rede, zero aprendizado. Se a rede não bater
  isto, ela não aprendeu nada além do óbvio — e isso também é resultado.
- **Métricas** (`RecordExplorationStats`): `Exploration/Coverage`, `StepsToTarget`,
  `RevisitRatio`, `NewNodesPerMin`, `WallContactRatio`, `HintEnabled`. Compare runs por elas,
  não por reward (que muda com cada peso e lição).
- **`config/graph_eval.yaml`**: sem seta, sem hider, pré-visitados 0 — o protocolo de medir.
  Rede: `mlagents-learn config/graph_eval.yaml --run-id=night_03 --resume --inference`.
  Greedy: `Heuristic Only` no prefab + `--run-id=eval_greedy --force`. Depois, o mesmo par
  num **mapa B** nunca visto = teste de transferência.

## 5. Currículo da noite (`config/graph_overnight.yaml`, run `night_03`, ~6 h)

| Etapa | termina | cobertura | pré | peso ± | sem seta | hider | vel. |
|---|---|---|---|---|---|---|---|
| E0 AndarAteNo | 0.4M | 0.3 | 0 | 0 | 0% | — | — |
| E1 ExplorarMais | 1.2M | 0.6 | 0.25 | 25% | 30% | — | — |
| E2 MetadeSemSeta | 2.2M | 0.9 | 0.5 | 50% | 60% | parado | — |
| E3 RastroQueAnda | 3.3M | 0.9 | 0.5 | 50% | 90% | anda | 1.0 |
| E4 SemSeta | 4.4M | 0.9 | 0.5 | 50% | **100%** | anda | 2.2 |
| E5 Fuga | 5.5M | 0.9 | 0.5 | 50% | 100% | foge | 3.2 |

```powershell
mlagents-learn config/graph_overnight.yaml --run-id=night_03
```

## 6. O que olhar de manhã
- `Exploration/HintEnabled` média: 1 → 0.7 → 0.4 → 0.1 → 0 (confirma o dropout).
- **`Exploration/Coverage` e `StepsToTarget` na E4–E5** (sem seta nenhuma) perto do que eram
  na E1 (com seta) = a pergunta tem resposta "sim". Se `Coverage` desabar na E4, o valor por
  saída não está sendo lido: olhar as esferas rosas no gizmo (a maior tem que estar na saída
  certa) e o `_lookaheadDecay`.
- `Cumulative Reward` **cai** um degrau em cada troca (a renda do shaping da seta some nos
  episódios sem ela) — é a conta mudando, não o agente piorando. Compare por `Coverage`.
- `RevisitRatio` caindo; `WallContactRatio` estável.

## 7. O que ficou igual e o que ficou fora
- Igual: hider (modos, velocidade), ping (+0.1/aresta, +2, −0.5), visão (+0.5, 0.05/m),
  peso por nó com variação, spawn/pré-visitados aleatórios, penalidades.
- Fora, de propósito: shaping por potencial (confundiria o teste), stacking/LSTM, penalidade
  de revisita (só se `RevisitRatio` pedir, e depois do dropout passar de 0.5), captura.
