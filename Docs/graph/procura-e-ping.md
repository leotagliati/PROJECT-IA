# Procura e ping — mapa de suspeita (plano)

28/09/2026. Segundo passo do roteiro de caça, na frente da audição a pedido do Arthur: treinar
primeiro **ping + procura**. O ping (que já existe) é a pista; a procura é o **mapa de
suspeita**. Treina em cima do movimento livre (`movimento-livre.md`).

## A ideia

O seeker carrega uma crença: **"onde o hider pode estar agora?"**, uma probabilidade por nó do
grafo (soma 1). Tudo o que ele sabe entra por aí:

| Evento | O que acontece com a suspeita |
|---|---|
| Começo do episódio | uniforme: ele pode estar em qualquer lugar |
| O tempo passa | **espalha** pelas arestas na velocidade do hider (ele pode ter andado) |
| Ver um nó vazio (cone + linha livre) ou passar nele | aquele nó vai a **zero** e o resto é renormalizado |
| Ping (o hider fez barulho num nó) | a suspeita vira **quase toda** naquele nó |
| Ver o hider | vira **toda** no nó dele |
| Tudo zerado (procurou em todo lugar e errou) | volta a uniforme nos nós que ele não está vendo |

É o mesmo raciocínio de uma pessoa: "ouvi barulho na cozinha há 10 s, então ele está na cozinha
ou nas salas ao lado; já olhei a cozinha, então é nas salas ao lado".

**Não é trapaça:** a suspeita só usa o que o seeker viu e ouviu. A posição real do hider nunca
entra, só o ping e a visão.

## Observação (vetor 107 → 118)

| Índice | O quê |
|---|---|
| por vizinho, +1 (10 → 11 floats) | **suspeita por esta saída**: soma da suspeita alcançável por ela, descontada por metro (`NavGraph.ScoreBeyond`, o mesmo do "quanto resta"), relativa à melhor saída |
| [27] | suspeita ativa (tem hider neste episódio) |
| [28] | **certeza**: a maior suspeita de um nó (1 = sei exatamente onde está) |
| [29] | tempo desde a última pista (ping ou visão), / 60 s |

Sem direção para o "centro" da suspeita: a regra do projeto é **dado, não resposta**. A direção
já está nas saídas.

## Recompensa

- **Limpar suspeita:** +0.5 × suspeita zerada ao ver ou visitar nós vazios. Uma vez a cada 10 s
  por nó (`_reclearCooldownSteps`), senão ficar parado olhando para um lugar por onde a
  suspeita escorre viraria renda. Não paga enquanto vê o hider.
  - Conta: cada vez que ele limpa metade do que resta, ganha 0.5 × 0.5. Um episódio de procura
    boa zera a suspeita umas 6 vezes, o que dá ~+3.
- **No config de procura:**
  - `ping_reward_scale` **0**: o ping vira só informação. O rastro do hider pagava +2 por
    chegada, e o night_04 aprendeu a colher rastro em vez de pegar. A suspeita já paga ir aonde
    o barulho foi.
  - `discovery_reward_scale` **0.3**: descobrir nó novo paga 30%. Com suspeita uniforme no
    começo, procurar já é explorar; a descoberta cheia (~17 no mapa) competiria com a captura.
- Captura continua a mesma: 10 + 15 × fração do episódio que sobrava.

## Hider mais discreto (`hider_noise`)

Hoje **toda** chegada do hider num primário vira ping, o que é quase um GPS. Agora cada chegada
faz barulho com probabilidade `hider_noise`: 1.0 (padrão, o de antes) → 0.25 na última lição.
Entre um barulho e outro, quem guia é a suspeita espalhando.

## Currículo: `config/graph_node4_search.yaml` (do zero, 15M)

| Lição | Hider | Vel. | Barulho | Assist | Threshold |
|---|---|---|---|---|---|
| Parado | parado | – | 1.0 | 1.0 | 13 |
| AndaDevagar | anda | 1.0 | 0.6 | 1.0 | 13 |
| Anda | anda | 2.2 | 0.4 | 0.7 | 12 |
| Foge | foge | 2.2 | 0.3 | 0.5 | 11 |
| FogeRapido | foge | 3.2 | 0.25 | 0.3 | (final) |

Constantes: sem seta, sem fim por cobertura (alvo 1.1), sem pré-visitados, jitter 0.5, patrulha
e tédio **desligados** (a suspeita substitui os dois), ping aleatório desligado (quem pinga é o
hider).

**Conta dos thresholds** (estimativa, como sempre):
- Episódio **sem** captura: descoberta ~2.6 + suspeita ~3 − existencial 2 − parede ~1 + avistar
  ~0.5 ≈ **+3**.
- Episódio **com** captura no meio: ~+20 (captura 17.5 + metade do resto).
- Reward ≈ 3 + 17 × taxa de captura. **13 ≈ 60% de captura**; 11 ≈ 45% nas lições de fuga.
- Sem capturar, nenhuma lição passa: a renda sem captura (~3–5) fica bem abaixo de 11.

`min_lesson_length` 300 episódios (capturar encerra cedo, os episódios são curtos).

## Arquivos

| Arquivo | Mudança |
|---|---|
| `Graph/GraphSuspicionMap.cs` (**novo**) | a crença: espalhar, limpar por visão/visita, ping, visão do hider; gizmo |
| `Graph/GraphHiderPerception.cs` | `CanSeePoint` público (o mesmo cone) |
| `Graph/GraphHider.cs` | barulho com probabilidade por chegada |
| `Graph/GraphArenaController.cs` | `hider_noise`, `ping_reward_scale`, `discovery_reward_scale` |
| `Graph/GraphStepContext.cs`, `GraphRewardSystem.cs` | suspeita limpa + as duas escalas |
| `Graph/GraphExplorerManager.cs` | observações novas, liga a suspeita |
| `tools/gen_full_curriculum.py` | gera também o `graph_node4_search.yaml` |

## No Inspector (NodeTraining4.prefab, objeto do agente)

- **Add Component → Graph Suspicion Map**
- **Behavior Parameters → Space Size: 118**

## O que olhar no TensorBoard

- `Hunt/Caught` subindo é o que importa.
- `Search/Cleared` (suspeita limpa por episódio): se subir e `Hunt/Caught` não, ele está
  procurando sem fechar a caça.
- `Environment/Lesson Number/hider_mode` e `Episode Length` caindo (captura encerra).
