# Como eu faria o TCC — opinião sem filtro

Escrito a pedido do Arthur em 19/09/2026: *"o markdown 100% honesto de como deveria ser o
TCC"*. Não sei o prazo nem o curso, então falo do que vale para qualquer banca de
computação/jogos. Onde eu discordo do rumo atual, digo.

---

## 0. A frase que a banca vai fazer, e que hoje não tem resposta

> **"Por que aprendizado por reforço? Um BFS no grafo resolve isso em dez linhas."**

Ela está certa. Explorar um grafo de nós conhecido é um problema resolvido. Se o TCC for
"um agente que explora o mapa", a resposta honesta é "não precisava de RL", e o trabalho
vira uma demonstração de ferramenta, não um trabalho de conclusão.

O projeto **só vira TCC** quando a pergunta deixa de ser "consegue explorar?" e passa a ser
uma pergunta que o BFS não responde. Tudo abaixo é sobre isso.

---

## 1. A pergunta de pesquisa (escolha UMA e amarre tudo nela)

Três opções que o repositório de hoje sustenta. Da mais defensável para a menos:

### Opção A — Muleta que some *(a que eu escolheria)*

> "Uma política treinada com uma dica de navegação que é retirada progressivamente pelo
> currículo aprende a explorar **sem** a dica, e esse comportamento **transfere** para um mapa
> que ela nunca viu?"

- Tem hipótese falsificável (sim/não), variável independente (a escada do `frontier_hint`),
  variáveis dependentes (cobertura, tempo, revisitas, com e sem dica, mapa visto vs. não visto).
- O repositório **já tem** tudo: currículo com 5 lições, `frontier_hint` até zero, observações
  egocêntricas feitas para transferir.
- A contribuição é sobre **como treinar** (currículo como retirada de andaime), não sobre
  "fazer um explorador". Isso a banca reconhece como pesquisa.

### Opção B — Variação por episódio contra decoreba

> "Randomizar estado inicial (spawn, pré-visitados, peso dos nós) impede a política de decorar
> rotas, e quanto isso custa em tempo de treino?"

- Também já implementado. Ablação limpa: com e sem cada randomização.
- Mais fraca que A porque o resultado é meio previsível ("randomizar ajuda a generalizar" é
  literatura de 2017), mas é honesta e mensurável.

### Opção C — RL vs. heurístico num seeker de terror

> "Uma política aprendida bate um heurístico greedy em tempo-até-encontrar contra um hider
> que se esconde?"

- É a mais interessante para o *jogo*, mas exige o hider, a percepção e o mapa de crença —
  nada disso existe. Se o prazo for de meses, é opção; se for de semanas, não.

**Minha escolha: A como pergunta principal, B como ablação dentro de A.** C vira "trabalhos
futuros" com um parágrafo forte.

---

## 2. O que precisa existir para a pergunta A ter resposta

Hoje o repositório tem o **método**. Não tem o **experimento**. A diferença é o TCC.

| Precisa | Existe? | Custo | Sem isso |
|---|---|---|---|
| **Baseline scriptado** (greedy: vai ao vizinho não-visitado mais próximo; BFS até o não-visitado quando todos estão visitados) | Não | ~50 linhas num `Heuristic` | Não há régua. "A rede cobre 90% em 5000 steps" não significa nada sem "o greedy cobre em 4200". |
| **Métricas de exploração** separadas do reward (`Coverage`, `StepsToTarget`, `RevisitRatio`, `WallContactRatio`) | Não (foram feitas e revertidas) | ~30 linhas com `StatsRecorder` | Reward muda a cada ajuste de peso; não dá para comparar runs. |
| **Um segundo mapa**, nunca usado no treino | Não | Autoria: duplicar o prefab, mudar paredes/nós | Sem mapa de teste, "generaliza" é uma frase, não um resultado. |
| **Avaliação com dica = 0** no mapa de treino e no de teste | Parcial (`frontier_hint = 0` no `GraphArenaController`) | Um script de avaliação: 100 episódios, seeds fixas, `Behavior Type = Inference Only` | Sem isso não dá para dizer que a muleta sumiu de verdade. |
| **3 seeds por configuração** | Não | Tempo de máquina: 3× cada run | Um run só é anedota. Curva com média ± desvio é resultado. |
| **Ablações**: (i) sem fade (hint fixo em 1.0), (ii) sem randomização (spawn fixo, `previsited` 0, `weight_jitter` 0), (iii) sem dica desde o início | Não | Só YAML diferente | Sem ablação, não dá para atribuir o resultado ao currículo. |

Isso é o que separa "eu treinei um agente" de "eu testei uma hipótese".

---

## 3. Onde eu discordo do que foi feito até aqui

Dito com respeito. Você pode estar certo.

1. **Mexer em muitas coisas por run.** Hoje (19/09) entraram, de uma vez: seta sorteada,
   spawn aleatório, pré-visitados, peso por nó, variação de peso, peso na observação, 5ª lição,
   30M steps. Se o `graph_17` der certo, você não sabe *por quê*; se der errado, não sabe *o
   quê*. Para um TCC isso é fatal: o capítulo de resultados precisa dizer "X causou Y". Eu
   congelaria a configuração AGORA e a chamaria de "completa"; os runs seguintes tiram uma
   coisa de cada vez (ablações), nunca acrescentam.

2. **Reverter em vez de ramificar.** A remoção da seta (`db9f983`) era um experimento válido —
   e o resultado dele ("sem seta o agente aprende ou não?") é exatamente um dado do TCC. Foi
   revertido antes de rodar. Eu teria mantido num branch, rodado 1M steps, e usado como a
   ablação (iii). Custou zero para manter e teria virado uma figura.

3. **Todos os nós primários** foi tentado e desfeito, mas a ideia certa estava lá: para um
   *seeker*, corredor conta. Para o TCC não importa — mas importa **fixar** uma decisão e
   documentar o motivo, porque a banca pergunta "por que 23 e não 102?".

4. **Thresholds em reward absoluto.** Recalculados quatro vezes num dia. Cada recálculo é um
   parágrafo de justificativa que você não vai querer escrever. Alternativa que eu usaria:
   `measure: progress` com orçamento fixo por lição **para os runs do TCC** — é reprodutível
   ("cada lição durou 2M steps") e a comparação entre ablações fica justa (mesmo relógio).
   O YAML tem um argumento bom contra `progress`, e ele vale para *encontrar* uma boa
   política; para *comparar* políticas, o relógio fixo é melhor.

5. **Sem testes.** Grafo, memória e recompensa são C# puro. Vinte testes de unidade
   (`BuildAdjacency` espelha, `FindNodeAt` desempata pelo centro, pré-visitado sai do
   denominador, aresta paga uma vez) são meio dia de trabalho e um parágrafo de metodologia
   que a banca gosta de ver.

---

## 4. Estrutura do texto que eu escreveria

1. **Introdução** (3–4 páginas). O problema do jogo (seeker que precisa procurar), por que
   navegação por grafo de nós (é o padrão da indústria: waypoints/navmesh), e a pergunta A.
   Contribuições em três bullets. Sem prometer o hider.
2. **Fundamentação** (8–12). RL e PPO (o suficiente para explicar `gamma`, `beta`,
   `time_horizon`); ML-Agents (ciclo observação–ação–recompensa, `DecisionRequester`);
   currículo (Bengio 2009) e *reward shaping* (Ng 1999 — cite para justificar por que a seta é
   perigosa e por que o `_frontierApproachReward` é "diferença de potencial"); randomização de
   domínio (Tobin 2017); navegação em jogos (waypoint graphs, Alien: Isolation como referência
   de seeker com dois cérebros).
3. **Metodologia** (10–15). Este é o capítulo que o repositório já escreveu — os cabeçalhos
   dos arquivos são literalmente isso. Arquitetura (Manager/Memory/Reward/Arena), o grafo
   (primário/auxiliar, peso, raio), a observação (tabela dos 69 floats), a ação, a recompensa
   (tabela com cada termo, valor, teto e justificativa), o currículo (tabela das 5 lições), a
   randomização. Figuras: o mapa com o grafo; os gizmos; um diagrama do ciclo do step.
4. **Experimentos** (3–5). Configurações (tabela: nome do run, o que muda), métricas
   (definição exata de cada uma), protocolo de avaliação (100 episódios, seeds, dica = 0,
   mapa A e mapa B), baseline greedy.
5. **Resultados** (6–10). Uma figura por pergunta: (a) curvas de treino com 3 seeds, lições
   marcadas; (b) barras: cobertura e steps-até-alvo, rede vs. greedy, mapa A e B, dica 0;
   (c) ablações lado a lado; (d) `RevisitRatio` por lição. Tabela-resumo com média ± desvio.
6. **Discussão** (3–4). O que funcionou, o que não, **por quê**. Aqui entram as histórias
   honestas: run 05 (cortar a dica de 0.5 para 0 derrubou de +15 para −3), run 11 (seguir a
   seta e decorar rotas). Isso não é fraqueza — é o conteúdo. Limitações explícitas: um mapa
   de treino, grafo autorado à mão, sem hider, sem obstáculos móveis.
7. **Conclusão e trabalhos futuros** (2). Responda a pergunta A com sim/não/parcialmente. O
   futuro é o seeker de terror: mapa de crença, audição, diretor — dois parágrafos, sem
   prometer.

Tamanho honesto: 45–60 páginas com figuras. Mais que isso é enrolação; menos, falta o
capítulo 5.

---

## 5. O que NÃO afirmar (a banca vai testar)

- "O agente **entende** o mapa." Não entende; ele mapeia 69 números em 2. Diga "a política
  usa a estrutura local do grafo".
- "Explora **como um humano**." Sem estudo com humanos, não.
- "Generaliza." Só se houver o mapa B com número.
- "É melhor que o heurístico." Só se houver o greedy com número — e esteja preparado para
  **não** ser, o que é um resultado válido (e mais interessante de discutir).
- "Sem a seta ele explora sozinho." Só depois de avaliar com `frontier_hint = 0` em 100
  episódios, nos dois mapas.

---

## 6. Plano de tempo (assumindo ~8 semanas de trabalho útil)

| Semana | Fazer | Entrega |
|---|---|---|
| 1 | Congelar a configuração. Baseline greedy. Métricas `Exploration/*`. Script de avaliação (100 episódios, seeds). | Uma tabela: greedy no mapa A. |
| 2 | Mapa B (autoria). 20 testes de unidade. Rodar a configuração completa, 3 seeds (pode ser 5M steps cada, não 30M — o TCC precisa de comparação, não de perfeição). | Curvas com 3 seeds. |
| 3 | Ablações (i), (ii), (iii), 3 seeds cada. Máquina rodando; você escreve os capítulos 2 e 3. | Rascunho de fundamentação e metodologia. |
| 4 | Avaliação de tudo com dica 0 nos mapas A e B. Figuras. | Capítulo 5 em tabelas. |
| 5–6 | Escrever 1, 4, 5, 6, 7. Revisão dos comentários do código → texto (eles já estão em pt-BR e explicam o porquê; é copiar com cuidado). | Texto completo v1. |
| 7 | Revisão do orientador. Refazer figuras. | v2. |
| 8 | Folga para o que der errado (vai dar). Slides. | Defesa. |

Repare: **o run de 30M não está no plano.** Ele é ótimo para o jogo; para o TCC, 3 seeds de 5M
valem mais que 1 seed de 30M, porque a banca pergunta "e se você rodar de novo?".

---

## 7. O que já está bom e é para vender

- **Os comentários do código.** Explicam a conta, o teto, a história do valor antigo. É
  metodologia pronta e é raro em TCC. Cite trechos.
- **A arquitetura de responsabilidade única** (Manager não calcula recompensa; Reward é
  função pura do contexto). Dá um diagrama bonito e uma seção de engenharia de software.
- **A história dos runs** (05, 11): erros documentados com causa e correção. É o que
  diferencia pesquisa de tentativa e erro.
- **Observação egocêntrica por vizinho, ordenada por ângulo.** É uma decisão de projeto com
  justificativa (transferência entre mapas) — e é testável no mapa B.

---

## 8. Resumo em três frases

O TCC não é "um agente que explora"; é "**o currículo que retira a muleta** produz uma
política que explora sem ela e transfere?". Para responder, faltam quatro coisas baratas:
baseline greedy, métricas, um mapa de teste e 3 seeds. Congele a configuração hoje, pare de
acrescentar, e passe as próximas semanas **tirando** uma coisa por vez — é isso que vira
capítulo de resultados.
