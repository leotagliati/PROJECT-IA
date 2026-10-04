# Gera config/graph_node4_full.yaml, config/graph_node4_search.yaml e as etapas graph_node4_e1..e5. Os parâmetros de currículo
# PRECISAM de completion_criteria idênticos (o ML-Agents avalia cada um sozinho); à mão, um
# threshold esquecido dessincroniza as lições. Mude LESSONS/PARAMS (ou SEARCH_*) e o cabeçalho
# aqui e rode:  python tools/gen_full_curriculum.py
import io, os

HEADER = """# ================================================================================================
# GraphExplorer - mapa da Node_4: TUDO NUM TREINO SO, do zero (28/09/2026)
#   explorar -> patrulhar -> ping -> hider parado -> anda -> foge -> foge rapido
#
#   mlagents-learn config/graph_node4_full.yaml --run-id=node4_full_01
#
# GERADO por tools/gen_full_curriculum.py - edite la, nao aqui.
# Junta o graph_node4_patrol.yaml e o graph_node4_hunt.yaml numa escada so, sem
# --initialize-from. Vetor 107, 4 acoes, rede 256 x 2.
#
# O QUE ESTE RUN TEM QUE O node4_patrol_02 NAO TINHA:
#   - parede mais cara (_wallContactPenalty 0.00025 -> 0.00075) + custo por BATIDA que cresce
#     com batidas seguidas em 5 s: o patrol_02 passava 43% do episodio encostado e isso custava
#     so ~-0.9;
#   - custo de SUAVIDADE (|mudanca de acao|^2): contra o giro de 'beyblade';
#   - patrulha mais lenta: no visitado fica 30 s em ZERO e recupera em 90 s; sala entediada
#     fica 20 s parada e esfria em 2 min;
#   - bonus por pegar o hider CEDO (+15 x fracao do episodio que sobra), senao capturar (que
#     encerra o episodio) cortava a renda de patrulha e nao compensava;
#   - as licoes de caca;
#   - MOVIMENTO LIVRE (docs/graph/movimento-livre.md): 6 m/s com aceleracao 40 (era 20/20),
#     4 acoes (andar X/Z + OLHAR X/Z), steer assist pelo curriculo (steer_assist 1.0 -> 0.3).
#     Vetor 107.
#
# LICOES (os NOVE parametros de curriculo tem completion_criteria IDENTICOS de proposito: o
# ML-Agents avalia cada um sozinho. Mexeu num threshold, mexa nos nove - este arquivo foi
# gerado por script justamente para isso nao escapar):
#
#   Licao        alvo  pre   jit   recup  tedio ping  hider   vel   assist threshold  min. episodios
#   Perto        0.3   0     0     0      0     0     -       -     1.0    4.5        80
#   Metade       0.6   0.25  0.25  0      0     0     -       -     1.0    6.0        80
#   Quase        0.9   0.5   0.5   0      0     0     -       -     0.7    5.0        80
#   Patrulha     1.1   0     0.5   90     1     0     -       -     0.5    8.0        1000
#   PingSolto    1.1   0     0.5   90     1     2000  -       -     0.3    8.0        1000
#   HiderParado  1.1   0     0.5   90     1     2000  parado  -     0.3    8.0        1000
#   HiderAnda    1.1   0     0.5   90     1     2000  anda    1.0   0.3    8.0        1000
#   HiderFoge    1.1   0     0.5   90     1     2000  foge    2.2   0.3    8.0        1000
#   FogeRapido   1.1   0     0.5   90     1     2000  foge    3.2   0.3    (final)
#
# STEER ASSIST: a "rodinha de bicicleta" desce junto com a exploracao ficando dificil e para
# em 0.3 na Patrulha em diante - e o valor que fica no jogo (default do GraphArenaController).
# Nao vai a zero: e parte do controlador do personagem, como assistencia de mira.
#
# EXPLORACAO: thresholds do graph_node4_noarrow.yaml MENOS 1.0 cada (5.5/7.0/6.0 -> 4.5/6.0/
# 5.0): parede 3x mais cara (0.00075/step, ~-2.6 com o contato do patrol_02 em vez de -0.9),
# batida escalonada (~-1) e suavidade (~-0.2) tiram ~2 de um episodio tipico no comeco; o
# threshold era ~65% do teto e fica ~55%, para a licao nao travar enquanto ele aprende a
# desviar.
#
# PATRULHA E CACA por reward, com threshold FIXO 8 e 1000 episodios minimos por licao:
#   - Patrulha, conta: cobertura ~0.87 x 17.25 = 15.0 - existencial 2 - parede ~2.6 - batidas
#     ~1 - loops ~0.5 - suavidade ~0.2 + revisitas ~1.5 (bem menos que no patrol_02, que
#     recuperava em 60 s sem carencia) = ~10. Threshold 8 = ~80% disso;
#   - nas licoes de caca o ping (+2 por chegada, -0.5 se expira) e a captura (+10 a +25) so
#     SOMAM a isso, entao 8 e facil de passar - quem segura cada licao e o minimo de 1000
#     episodios (~1.6M steps: 1000 x 1600 decisoes). E o "relogio" de cada licao de caca;
#   - se a fuga derrubar o reward abaixo de 8, ele fica na HiderFoge - aceitavel, e o
#     TensorBoard mostra (Lesson Number parado + Hunt/Caught caindo).
# Por que nao measure: progress (como no graph_node4_hunt.yaml)? Porque aqui a exploracao vem
# antes e demora quanto demorar: um progresso fixo poderia vencer enquanto ele ainda explora e
# as licoes de caca passariam direto.
#
# 24M steps: exploracao ~5M (o patrol_02 chegou na Patrulha nisso) + patrulha e 5 licoes de
# caca de >= 1.6M + folga. ~24 h a ~270 steps/s. Continue com --resume se ainda subir no fim.
#
# ANTES DE RODAR: salas numeradas ("11. Numerar salas"), pesos por area, Play sem erro no
# Console (VectorObservationSize 107, Continuous Actions 4).
#
# TensorBoard: Environment/Lesson Number/hider_mode diz em que fase esta. Exploracao:
# Exploration/Coverage. Paredes: Exploration/WallContactFraction (patrol_02: 0.43) e
# Movement/ActionJitter + Movement/LookJitter (tremor do andar e do olhar). Caca:
# Hunt/Seen, Hunt/Caught e Episode Length caindo (captura encerra o episodio).
# ATENCAO (night_04): se Cumulative Reward subir e Hunt/Caught nao, ele colhe o rastro de ping
# em vez de pegar - baixe _pingReachedReward.
# ================================================================================================

"""

# Hiperparametros comuns aos dois configs; so max_steps muda.
BEHAVIORS = """behaviors:
  GraphExplorer:
    trainer_type: ppo
    hyperparameters:
      batch_size: 1024
      buffer_size: 10240
      learning_rate: 0.0003
BETA_BLOCK      epsilon: 0.2
      lambd: 0.95
      num_epoch: 3
      learning_rate_schedule: linear
      beta_schedule: linear
      epsilon_schedule: linear
    network_settings:
      normalize: false
      hidden_units: 256
      num_layers: 2
      vis_encode_type: simple
    reward_signals:
      extrinsic:
        gamma: 0.995
        strength: 1.0
    keep_checkpoints: KEEP_CHECKPOINTS
    checkpoint_interval: 500000
    max_steps: MAX_STEPS
    time_horizon: 128
    summary_freq: 10000

environment_parameters:
  # ---- Constantes ----
"""

FULL_CONSTANTS = """  # Seta desligada o treino inteiro (TEM que estar aqui, senao o default do Inspector e 1).
  frontier_hint: 0.0
  frontier_hint_steps: 0
"""

# (nome, threshold, min_lesson_length)
LESSONS = [
    ("Perto", 4.5, 80),
    ("Metade", 6.0, 80),
    ("Quase", 5.0, 80),
    ("Patrulha", 8.0, 1000),
    ("PingSolto", 8.0, 1000),
    ("HiderParado", 8.0, 1000),
    ("HiderAnda", 8.0, 1000),
    ("HiderFoge", 8.0, 1000),
    ("FogeRapido", None, None),
]

PARAMS = [
    ("coverage_target",
     "Fracao do que resta descobrir que encerra o episodio (+5). Acima de 1 = sem fim por cobertura.",
     [0.3, 0.6, 0.9, 1.1, 1.1, 1.1, 1.1, 1.1, 1.1]),
    ("previsited_fraction",
     "Anti-decoreba: fracao dos primarios que ja nasce visitada. A partir da Patrulha, 0.",
     [0.0, 0.25, 0.5, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0]),
    ("weight_jitter",
     "Peso de cada no x U[1-j, 1+j] por episodio (a soma esperada nao muda).",
     [0.0, 0.25, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5]),
    ("value_recovery_seconds",
     "Patrulha: segundos para um no visitado recuperar o valor (depois de 30 s de carencia). 0 = desligado.",
     [0.0, 0.0, 0.0, 90.0, 90.0, 90.0, 90.0, 90.0, 90.0]),
    ("area_boredom",
     "Tedio de sala (NavNode._areaId): 1 liga, 0 desliga.",
     [0, 0, 0, 1, 1, 1, 1, 1, 1]),
    ("ping_interval",
     "Steps de fisica entre pings aleatorios (x U[0.5, 1.5]); 2000 = ~40 s. Com hider, os pings vem do rastro dele.",
     [0, 0, 0, 0, 2000, 2000, 2000, 2000, 2000]),
    ("hider_mode",
     "0 nenhum / 1 parado / 2 anda / 3 foge (GraphHider.Mode).",
     [0, 0, 0, 0, 0, 1, 2, 3, 3]),
    ("hider_speed",
     "m/s do hider (0 = padrao do GraphHider). O seeker anda a 6 m/s; 3.5 o Arthur achou rapido demais.",
     [0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 1.0, 2.2, 3.2]),
    ("steer_assist",
     "Steering assistido (0..1): fracao da velocidade contra a parede removida perto dela. 1 = desliza.",
     [1.0, 1.0, 0.7, 0.5, 0.3, 0.3, 0.3, 0.3, 0.3]),
]

# ------------------------------------------------------------------------------------------------
# PROCURA (docs/graph/procura-e-ping.md): ping + mapa de suspeita, hider desde a 1a licao.
# ------------------------------------------------------------------------------------------------
SEARCH_HEADER = """# ================================================================================================
# GraphExplorer - mapa da Node_4: PROCURA + PING, do zero (28/09/2026)
#   hider parado -> anda devagar -> anda -> foge -> foge rapido
#
#   mlagents-learn config/graph_node4_search.yaml --run-id=node4_search_01
#
# GERADO por tools/gen_full_curriculum.py - edite la, nao aqui. Plano: docs/graph/procura-e-ping.md.
# Vetor 118, 4 acoes, rede 256 x 2.
#
# O seeker carrega um MAPA DE SUSPEITA (GraphSuspicionMap): onde o hider pode estar. Espalha com
# o tempo, zera onde ele olha, concentra no ping (barulho) e na visao. Paga por suspeita limpa;
# o objetivo e a captura (10 + 15 x fracao do episodio que sobrava).
#
# LICOES (os QUATRO parametros de curriculo tem completion_criteria IDENTICOS de proposito):
#
#   Licao        hider   vel   barulho assist descob seta threshold  min. episodios
#   Parado       parado  -     1.0     1.0    1.0    1.0  18.0       300
#   AndaDevagar  anda    1.0   0.6     1.0    0.6    0.5  13.0       300
#   Anda         anda    2.2   0.4     0.7    0.3    0    12.0       300
#   Foge         foge    2.2   0.3     0.5    0.3    0    11.0       300
#   FogeRapido   foge    3.2   0.25    0.3    0.3    0    (final)
#
# SETA NO COMECO (frontier_hint 1.0 -> 0.5 -> 0): search_03 nao saia da sala de spawn nem com a
# descoberta cheia - andando aleatorio ele nunca achava a porta, nunca via recompensa positiva e
# aprendia a ficar parado (cobertura 2% -> 0.2%). A seta da o gradiente por METRO ate o proximo
# nao-visitado (_frontierApproachReward), que e o que tira ele da sala. Some na Anda. Threshold
# da Parado 18: a seta soma ~+3 por episodio a um explorador sem captura (~14 -> ~17).
#
# DESCOBERTA 1.0 NA PARADO: search_01/02 comecaram com 0.3 e ficaram em cobertura ~2% - do zero,
# sem nada positivo ao alcance, so as punicoes (estagnacao, parede) falavam. Com a descoberta
# cheia ele aprende a andar e explorar como nos runs de exploracao; threshold 16 porque explorar
# muito SEM pegar chega a ~14 (0.9 x 17.25 + suspeita ~2.7 - ~4), entao 16 ainda exige captura.
#
# CONSTANTES: sem seta, sem fim por cobertura (1.1), sem pre-visitados, jitter 0.5, patrulha e
# tedio DESLIGADOS (a suspeita substitui os dois), ping aleatorio desligado (quem pinga e o
# hider) e ping_reward_scale 0 (ping so informa; pago, o rastro virava renda - night_04).
# discovery_reward_scale desce a 0.3 depois da Parado: procurar ja e explorar, e a descoberta
# cheia competiria com a captura.
#
# THRESHOLDS (estimativa):
#   - episodio SEM captura: descoberta ~2.6 + suspeita ~3 - existencial 2 - parede ~1 + avistar
#     ~0.5 = ~+3;
#   - COM captura no meio do episodio: ~+20;
#   - reward ~ 3 + 17 x taxa de captura: 13 ~ 60% de captura, 11 ~ 45% nas licoes de fuga.
#   Sem capturar nenhuma licao passa. min_lesson_length 300: capturar encerra, episodios curtos.
#
# 15M steps. TensorBoard: Hunt/Caught (o que importa), Search/Cleared (suspeita limpa por
# episodio - se subir e Hunt/Caught nao, ele procura sem fechar a caca), Episode Length caindo.
#
# ANTES DE RODAR: Add Component > Graph Suspicion Map no agente, VectorObservationSize 118,
# Play sem erro no Console.
# ================================================================================================

"""

SEARCH_CONSTANTS = """  # Seta com duracao inteira quando ligada (a forca vem do curriculo, frontier_hint).
  frontier_hint_steps: 0
  coverage_target: 1.1
  previsited_fraction: 0.0
  weight_jitter: 0.5
  value_recovery_seconds: 0.0
  area_boredom: 0
  ping_interval: 0
  ping_reward_scale: 0.0
"""

SEARCH_LESSONS = [
    ("Parado", 18.0, 300),
    ("AndaDevagar", 13.0, 300),
    ("Anda", 12.0, 300),
    ("Foge", 11.0, 300),
    ("FogeRapido", None, None),
]

SEARCH_PARAMS = [
    ("hider_mode",
     "0 nenhum / 1 parado / 2 anda / 3 foge (GraphHider.Mode).",
     [1, 2, 2, 3, 3]),
    ("hider_speed",
     "m/s do hider (0 = padrao do GraphHider). O seeker anda a 6 m/s.",
     [0.0, 1.0, 2.2, 2.2, 3.2]),
    ("hider_noise",
     "Chance de cada chegada do hider num no virar ping (barulho). 1 = toda chegada.",
     [1.0, 0.6, 0.4, 0.3, 0.25]),
    ("steer_assist",
     "Steering assistido (0..1): fracao da velocidade contra a parede removida perto dela. 1 = desliza.",
     [1.0, 1.0, 0.7, 0.5, 0.3]),
    ("discovery_reward_scale",
     "Escala da recompensa de descoberta. 1.0 na Parado: do zero ele precisa aprender a andar e explorar (search_01/02: cobertura 2% com 0.3).",
     [1.0, 0.6, 0.3, 0.3, 0.3]),
    ("frontier_hint",
     "Seta para o nao-visitado mais proximo (observacao + shaping por metro). So no comeco: tira ele da sala de spawn.",
     [1.0, 0.5, 0.0, 0.0, 0.0]),
]


# Bonus de entropia. 0.015 nos configs antigos (full/search): "sem seta a unica forma de achar as
# saidas boas no comeco e tentar". Nas ETAPAS, 0.003: com 0.015 a Policy/Entropy NUNCA caiu (E1
# 1.42 -> 1.43, e2_02 1.45, e2_03 1.48 subindo) - para 4 acoes continuas 1.42 e desvio 1, o valor
# com que a rede nasce. Cada acao era a intencao + um ruido do tamanho da acao inteira: sem a seta
# pagando por metro, o ruido dominava (entra e sai do mesmo no, loops). Continuo, o normal e
# 0.001-0.005; o bonus estava ganhando da recompensa.
BETA_OLD = """      # 0.015: sem seta a unica forma de achar as saidas boas no comeco e tentar.
      beta: 0.015
"""
# Noite (02/10, pedido do Arthur: "deixe ela mais randomica"): o DOBRO das etapas. Muito novo
# entra no meio do run (patrulha, ping, hider, hider solto) e a politica que veio do v4_s1_01 ja
# esta ficando decidida; mais entropia mantem ela testando saidas. Nao 0.015: com ele a entropia
# nunca caia (E1..e2_03, desvio ~1 = politica aleatoria). Decai linear ate max_steps.
BETA_NIGHT = """      # 0.006 (o dobro das etapas): mais exploracao, porque o run da noite muda de tarefa varias
      # vezes. 0.015 ja se mostrou demais (a entropia nunca caia). Decai linear ate max_steps.
      beta: 0.006
"""
BETA_STAGES = """      # 0.003 (era 0.015): com 0.015 a entropia nunca caiu (E1..e2_03 em 1.42-1.48 = desvio ~1, a
      # politica continuava aleatoria). Para acoes continuas o normal e 0.001-0.005.
      beta: 0.003
"""


def write(filename, header, max_steps, constants, lessons, params, beta=BETA_OLD, keep=10):
    behaviors = (BEHAVIORS.replace("MAX_STEPS", str(max_steps)).replace("BETA_BLOCK", beta)
                 .replace("KEEP_CHECKPOINTS", str(keep)))
    out = [header, behaviors, constants, "\n  # ---- Curriculo ----\n"]
    for name, comment, values in params:
        assert len(values) == len(lessons), name
        out.append(f"  # {comment}\n")
        out.append(f"  {name}:\n    curriculum:\n")
        for lesson_spec, value in zip(lessons, values):
            # (nome, threshold, min_len) promove por reward; um 4o elemento troca a medida
            # ("progress" = fracao do max_steps, para licao que nao pode prender o run).
            lesson, threshold, min_len = lesson_spec[:3]
            measure = lesson_spec[3] if len(lesson_spec) > 3 else "reward"
            out.append(f"      - name: {lesson}\n")
            if threshold is not None:
                out.append(
                    "        completion_criteria:\n"
                    f"          measure: {measure}\n"
                    "          behavior: GraphExplorer\n"
                    "          signal_smoothing: true\n"
                    f"          min_lesson_length: {min_len}\n"
                    f"          threshold: {threshold}\n"
                    "          require_reset: true\n")
            out.append(f"        value: {value}\n")
        out.append("\n")

    path = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'config', filename)
    io.open(path, 'w', encoding='utf-8', newline='\n').write(''.join(out).rstrip('\n') + '\n')
    print(f'Gerado: {path}')


# ------------------------------------------------------------------------------------------------
# ETAPAS (28/09): em vez de tudo num run do zero, uma etapa por vez, cada uma herdando o cerebro da
# anterior (--initialize-from; o vetor 118 e fixo, entao a rede e compativel). search_01..03
# mudaram movimento, fisica, procura e punicoes de uma vez e nao deu para saber o que quebrou.
# Cada etapa tem um criterio de passagem lido no TensorBoard ANTES de rodar a proxima.
# ------------------------------------------------------------------------------------------------
def stage_header(title, name, run, init, body):
    init_arg = f" --initialize-from={init}" if init else ""
    return f"""# ================================================================================================
# GraphExplorer - Node_4 - {title}
#
#   mlagents-learn config/{name}.yaml --run-id={run}{init_arg}
#
# GERADO por tools/gen_full_curriculum.py - edite la, nao aqui. Etapas: E1 andar e explorar (com
# seta) -> E2 sem seta -> E3 achar hider parado -> E4 seguir -> E5 cacar.
# Vetor 118, 4 acoes. Nao rode a proxima etapa sem conferir o criterio desta no TensorBoard.
#
{body}# ================================================================================================

"""


# Nada de hider/procura/patrulha nas etapas de exploracao.
EXPLORE_OFF = """  hider_mode: 0
  hider_speed: 0.0
  hider_noise: 1.0
  ping_interval: 0
  value_recovery_seconds: 0.0
  area_boredom: 0
  ping_reward_scale: 1.0
  discovery_reward_scale: 1.0
  frontier_hint_steps: 0
"""

# ---- E1: andar e explorar, COM seta (alinhada com as saidas) ----
E1_BODY = """# So o movimento novo (6 m/s, acel. 15, olhar separado, steer assist) + exploracao, com a seta
# ligada (frontier_hint 1.0, apontando para a melhor saida - _frontierFollowsExits). E o que ja
# funcionou no patrol_02 (Perto/Metade/Quase), sem hider. Se ESTA nao aprender, o problema e
# movimento ou punicao, isolado.
#
#   Licao   alvo  pre   jit   assist threshold  min. episodios
#   Perto   0.3   0     0     1.0    7.5        80
#   Metade  0.6   0.25  0.25  1.0    9.5        80
#   Quase   0.9   0.5   0.5   0.7    (final)
#
# THRESHOLDS (~75% de um episodio bom): Perto = descoberta ~5 + conclusao 5 + seta ~1 - ~1 = ~10;
# Metade = ~7.8 + 5 + 1.5 - 2 = ~12. A punicao de parede/batida ficou ~2 mais cara na fase 4.
#
# PASSA QUANDO (na licao Quase): Exploration/Coverage > 0.6, WallContactFraction < 0.2, Episode
# Length caindo (termina antes do timeout). 5M steps; se passar antes, Ctrl+C e siga.
"""
E1_LESSONS = [("Perto", 7.5, 80), ("Metade", 9.5, 80), ("Quase", None, None)]
E1_PARAMS = [
    ("coverage_target", "Fracao do que resta descobrir que encerra o episodio (+5).", [0.3, 0.6, 0.9]),
    ("previsited_fraction", "Anti-decoreba: fracao dos primarios que ja nasce visitada.", [0.0, 0.25, 0.5]),
    ("weight_jitter", "Peso de cada no x U[1-j, 1+j] por episodio.", [0.0, 0.25, 0.5]),
    ("steer_assist", "Steering assistido (0..1). 1 = desliza na parede.", [1.0, 1.0, 0.7]),
]
E1_CONSTANTS = "  frontier_hint: 1.0\n" + EXPLORE_OFF

# ---- E2: tirar a seta ----
E2_BODY = """# Herda o E1 e CORTA a seta de uma vez (pedido do Arthur): o node4_e1_01 chegou na licao Quase
# em 190k steps com reward ~14.5 e cobertura 0.93-0.96. A exploracao fica na dificuldade final do
# E1 (alvo 0.9, pre 0.5, jitter 0.5). Quem guia sem seta e o "quanto resta" por vizinho - a mesma
# informacao que a seta realcava (_frontierFollowsExits), entao cortar e so parar de receber o
# realce. Uma coisa por licao: primeiro so a seta sai (assist igual ao da Quase), depois o assist
# desce ate o 0.3 que a caca usa.
#
#   Licao        seta  assist threshold  min. episodios
#   SemSeta      0     0.7    8.0        100
#   MenosAssist  0     0.3    (final)
#
# THRESHOLD 8: o reward de 14.5 do E1 inclui o shaping da seta (0.02/m ate o proximo passo;
# ~250-300 m andados na direcao certa = ~5-6). Sem ela, a mesma cobertura rende ~9-10.
#
# PASSA QUANDO (na MenosAssist): Exploration/Coverage > 0.6 e WallContactFraction < 0.25.
# 3M steps; se passar antes, Ctrl+C e siga.
"""
E2_LESSONS = [("SemSeta", 8.0, 100), ("MenosAssist", None, None)]
E2_PARAMS = [
    ("steer_assist", "Steering assistido (0..1).", [0.7, 0.3]),
]
E2_CONSTANTS = """  frontier_hint: 0.0
  coverage_target: 0.9
  previsited_fraction: 0.5
  weight_jitter: 0.5
""" + EXPLORE_OFF

# ---- E3..E5: procura ----
HUNT_CONSTANTS = """  frontier_hint: 0.0
  frontier_hint_steps: 0
  coverage_target: 1.1
  previsited_fraction: 0.0
  weight_jitter: 0.5
  value_recovery_seconds: 0.0
  area_boredom: 0
  ping_interval: 0
  ping_reward_scale: 0.0
  discovery_reward_scale: 0.3
"""

E3_BODY = """# Herda o E2 (node4_e2_06) e liga a PROCURA (GraphSuspicionMap) com o hider PARADO, barulho em
# toda chegada (o spawn dele ja pinga). Uma licao so. Ping so informa (ping_reward_scale 0);
# descoberta e navegacao a 30% (explorar agora e meio); captura = 10 + 15 x fracao que sobrava.
#
# A E2 foi FECHADA em cobertura ~0.42 (criterio era 0.6) por decisao: com previsited 0.5 isso e
# ~70% do mapa, e o que trava e o gargalo (Node (119)) quando o que falta fica do outro lado. Aqui
# a SUSPEITA faz o "reset aos poucos" que o Arthur queria: onde ele olhou zera e volta a crescer
# na velocidade do hider - sem patrulha junto (seriam dois relogios de "volte ali").
#
# Assist 0.7 (onde a E2 parou; ela nunca chegou na licao MenosAssist). Desce na E4 (0.5) e E5 (0.3).
#
# PASSA QUANDO: Hunt/Caught > 0.5 e Episode Length caindo (capturar encerra). 3M steps.
"""
E3_CONSTANTS = HUNT_CONSTANTS + """  steer_assist: 0.7
  hider_mode: 1
  hider_speed: 0.0
  hider_noise: 1.0
"""

E4_BODY = """# Herda o E3: o hider ANDA e faz barulho cada vez menos (o seeker passa a deduzir pela suspeita).
# Assist 0.7 -> 0.5.
#
#   Licao        vel   barulho threshold  min. episodios
#   AndaDevagar  1.0   0.6     13.0       300
#   Anda         2.2   0.4     (final)
#
# Reward ~ 3 + 17 x taxa de captura: 13 ~ 60%. PASSA QUANDO (na Anda): Hunt/Caught > 0.5. 4M steps.
"""
E4_LESSONS = [("AndaDevagar", 13.0, 300), ("Anda", None, None)]
E4_PARAMS = [
    ("hider_speed", "m/s do hider. O seeker anda a 6.", [1.0, 2.2]),
    ("hider_noise", "Chance de cada chegada do hider virar ping.", [0.6, 0.4]),
]
E4_CONSTANTS = HUNT_CONSTANTS + "  steer_assist: 0.5\n  hider_mode: 2\n"

E5_BODY = """# Herda o E4: o hider FOGE quando o seeker chega a 12 m. Assist 0.5 -> 0.3.
#
#   Licao       vel   barulho threshold  min. episodios
#   Foge        2.2   0.3     11.0       300
#   FogeRapido  3.2   0.25    (final)
#
# 11 ~ 45% de captura. PASSA QUANDO (na FogeRapido): Hunt/Caught > 0.4. 5M steps.
"""
E5_LESSONS = [("Foge", 11.0, 300), ("FogeRapido", None, None)]
E5_PARAMS = [
    ("hider_speed", "m/s do hider. O seeker anda a 6.", [2.2, 3.2]),
    ("hider_noise", "Chance de cada chegada do hider virar ping.", [0.3, 0.25]),
]
E5_CONSTANTS = HUNT_CONSTANTS + "  steer_assist: 0.3\n  hider_mode: 3\n"

# (arquivo, run-id, --initialize-from, titulo, texto, max_steps, constantes, licoes, parametros)
STAGES = [
    ("graph_node4_e1_explorar", "node4_e1_01", None, "E1 ANDAR E EXPLORAR (do zero)",
     E1_BODY, 5000000, E1_CONSTANTS, E1_LESSONS, E1_PARAMS),
    ("graph_node4_e2_semseta", "node4_e2_01", "node4_e1_01", "E2 EXPLORAR SEM SETA",
     E2_BODY, 3000000, E2_CONSTANTS, E2_LESSONS, E2_PARAMS),
    ("graph_node4_e3_achar", "node4_e3_01", "node4_e2_06", "E3 ACHAR O HIDER PARADO",
     E3_BODY, 3000000, E3_CONSTANTS, [], []),
    ("graph_node4_e4_seguir", "node4_e4_01", "node4_e3_01", "E4 SEGUIR O HIDER QUE ANDA",
     E4_BODY, 4000000, E4_CONSTANTS, E4_LESSONS, E4_PARAMS),
    ("graph_node4_e5_cacar", "node4_e5_01", "node4_e4_01", "E5 CACAR O HIDER QUE FOGE",
     E5_BODY, 5000000, E5_CONSTANTS, E5_LESSONS, E5_PARAMS),
]


# ------------------------------------------------------------------------------------------------
# MAPA V4 - SALAS E PORTAS (01/10, docs/graph/salas-e-portas.md). Exploracao paga por sala coberta
# (80% dos nos) e por porta atravessada (novidade que cai a cada repeticao); SEM seta, sem peso por
# no. Vetor 182 (os .onnx E1-E3 nao servem). Os parametros antigos (frontier_hint, weight_jitter,
# value_recovery_seconds, area_boredom) nao existem mais no C#; os YAMLs node4_* acima ficam como
# historico.
# ------------------------------------------------------------------------------------------------
def v4_header(title, name, run, init, body):
    init_arg = f" --initialize-from={init}" if init else ""
    return f"""# ================================================================================================
# GraphExplorer - mapa v4 (NodeTraining5, cena Node_5) - {title}
#
#   mlagents-learn config/{name}.yaml --run-id={run}{init_arg}
#
# GERADO por tools/gen_full_curriculum.py - edite la, nao aqui. Plano: docs/graph/salas-e-portas.md.
# Etapas: S1 salas (sem seta) -> S2 menos assist -> S3 liberacao -> S4 ping -> S5 hider -> S6 hider solto.
# Vetor 182, 4 acoes. Nao rode a proxima etapa sem conferir o criterio desta no TensorBoard.
#
{body}# ================================================================================================

"""


V4_EXPLORE_ONLY = """  hider_mode: 0
  hider_speed: 0.0
  hider_noise: 1.0
  ping_interval: 0
  ping_reward_scale: 1.0
  discovery_reward_scale: 1.0
  room_complete_threshold: 0.8
  release_fraction: 0.0
  previsited_fraction: 0.0
"""

S1_BODY = """# Do zero: recompensa e observacao novas. A sala vale o mesmo qualquer que seja o tamanho (0.25 ao
# descobrir os 80% dos nos + 0.25 ao concluir); porta nova 0.1, saida de sala concluida 0.15 (x a
# novidade da porta: sair por onde entrou vale metade). Sem seta e SEM nenhum pagamento por se
# aproximar de alvo escolhido por algoritmo: so eventos que ele causa (chao novo, sala concluida,
# porta atravessada). A decisao e "qual porta", e a novidade de cada uma esta na observacao. Mesma
# escada da E1 (que passou em 190k), alvo agora em SALAS concluidas.
#
#   Licao   salas  assist threshold  min. episodios
#   Perto   0.2    1.0    6.0        80
#   Metade  0.5    1.0    9.0        80
#   Quase   0.8    0.7    (final)
#
# THRESHOLDS (~75% de um episodio bom; 26 salas, 35 portas no NodeTraining5):
#   Perto  = 6 salas x 0.5 + ~7 portas x 0.1 + ~5 saidas x 0.15 + conclusao 5
#            - existencial/parede ~1 = ~8;
#   Metade = 13 x 0.5 + ~1.5 + ~1.9 + 5 - ~2.5 = ~12.
# Salas de 1 no (11 de 26) concluem ao entrar: as primeiras licoes ficam faceis de proposito.
#
# PASSA QUANDO (na Quase): Exploration/Coverage > 0.6 (salas), WallContactFraction < 0.2,
# Doors/RepeatFraction caindo, Episode Length caindo. 5M steps; se passar antes, Ctrl+C e siga.
# Se travar na Perto com Movement/IdleFraction alto: sem seta ele nao achou a primeira porta.
# Primeiro ajuste: subir _doorCrossReward (porta nova paga mais) - continua sendo evento, nao seta.
"""
S1_LESSONS = [("Perto", 6.0, 80), ("Metade", 9.0, 80), ("Quase", None, None)]
S1_PARAMS = [
    ("coverage_target", "Fracao das SALAS concluidas que encerra o episodio (+5).", [0.2, 0.5, 0.8]),
    ("steer_assist", "Steering assistido (0..1). 1 = desliza na parede.", [1.0, 1.0, 0.7]),
]

# ---- NOITE: S1 -> S6 num run so (pedido do Arthur, 01/10), herdando o v4_s1_01 ----
NIGHT_BODY = """# Tudo num run de ~8 h, herdando o cerebro do v4_s1_01 (o vetor 182 nao mudou). As licoes de
# exploracao promovem por REWARD (contas abaixo); as de patrulha/ping/caca por PROGRESSO (fracao do
# max_steps), porque o reward delas e dificil de prever e uma licao nao pode prender a noite.
#
#   #  Licao        salas assist pre  libera ping  hider  vel  barulho desc ping$ visao solto  criterio
#   1  Perto        0.2   1.0    0    0      0     -      -    -       1.0  1.0   0     0      reward 6.0  (80 ep.)
#   2  Metade       0.5   1.0    0    0      0     -      -    -       1.0  1.0   0     0      reward 9.0  (80)
#   3  Quase        0.8   0.7    0    0      0     -      -    -       1.0  1.0   0     0      reward 11.0 (150)
#   4  MenosAssist  0.8   0.3    0.3  0      0     -      -    -       1.0  1.0   0     0      reward 8.5  (150)
#   5  Patrulha     1.1   0.3    0    0.85   0     -      -    -       1.0  1.0   0     0      progresso 0.30 (300)
#   6  Ping         1.1   0.3    0    0.85   2000  -      -    -       1.0  1.0   0     0      progresso 0.40 (300)
#   7  HiderParado  1.1   0.3    0    0      0     parado -    1.0     0.5  0     1     0      progresso 0.52 (300)
#   8  HiderAnda    1.1   0.3    0    0      0     anda   1.0  0.6     0.5  0     1     0      progresso 0.64 (300)
#   9  HiderFoge    1.1   0.3    0    0      0     foge   2.2  0.4     0.5  0     1     0      progresso 0.78 (300)
#  10  HiderSolto   1.1   0.3    0    0      0     foge   2.2  0.3     0.5  0     1     1      (final)
#
# THRESHOLDS de reward (~70% de um episodio bom; 26 salas):
#   Quase       = 21 salas x 0.5 + ~2.5 portas + ~3 saidas + 5 - ~3.5 = ~17.5 -> 11.0;
#   MenosAssist = 30% das salas ja nascem concluidas: ~14.5 x 0.5 + ~2 + ~2 + 5 - ~3.5 = ~13 -> 8.5.
# PROGRESSO: com 10M steps, Patrulha sai aos 3M, Ping aos 4M, ... HiderSolto fica com os ultimos
# ~2.2M. Se a exploracao demorar, a licao de progresso ja vencido sai depois do minimo de 300
# episodios (~480k steps) - nenhuma licao fica sem treino nem prende o run.
#
# CONTRA OVERTRAINING (decorar o mapa / esquecer o que ja sabia):
#   - tudo varia por episodio: spawn em no aleatorio, salas pre-concluidas, nos de ping sorteados por
#     sala, hider nasce longe e (no fim) em pontos aleatorios e escondido;
#   - nas licoes de caca a exploracao continua pagando (discovery_reward_scale 0.5, nao 0): sem isso a
#     rede "esquece" de explorar enquanto aprende a cacar;
#   - checkpoint a cada 500k e os 20 guardados (a noite inteira): se o fim piorar, volte a um anterior
#     (tools/best_onnx.py) em vez de usar o ultimo;
#   - learning rate e entropia descem linearmente ate max_steps: o run termina refinando, nao pulando.
#
# PING: so paga chegar (+2) e cobra expirar (-0.5) na licao Ping; nas de caca o ping so INFORMA
# (ping_reward_scale 0, o rastro do hider nao vira renda - night_04). A sala do ping fica QUENTE
# (explorar vale 2x) e as portas dela mostram o calor. Nada paga por metro de aproximacao.
#
# O QUE OLHAR DE MANHA: Environment/Lesson Number (ate onde chegou), Exploration/Coverage (salas),
# Doors/RepeatFraction, Hunt/Seen e Hunt/Caught (nas licoes 7-10), WallContactFraction.
"""
NIGHT_LESSONS = [
    ("Perto", 6.0, 80),
    ("Metade", 9.0, 80),
    ("Quase", 11.0, 150),
    ("MenosAssist", 8.5, 150),
    ("Patrulha", 0.30, 300, "progress"),
    ("Ping", 0.40, 300, "progress"),
    ("HiderParado", 0.52, 300, "progress"),
    ("HiderAnda", 0.64, 300, "progress"),
    ("HiderFoge", 0.78, 300, "progress"),
    ("HiderSolto", None, None),
]
NIGHT_PARAMS = [
    ("coverage_target", "Fracao das SALAS concluidas que encerra o episodio (+5). Acima de 1 = sem fim por cobertura.",
     [0.2, 0.5, 0.8, 0.8, 1.1, 1.1, 1.1, 1.1, 1.1, 1.1]),
    ("steer_assist", "Steering assistido (0..1). 1 = desliza na parede.",
     [1.0, 1.0, 0.7, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3]),
    ("previsited_fraction", "Fracao das SALAS que ja nasce concluida (anti-decoreba).",
     [0.0, 0.0, 0.0, 0.3, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0]),
    ("release_fraction", "Patrulha: com esta fracao usada, a porta e a sala mais antigas voltam (valendo 0.5). 0 = off.",
     [0.0, 0.0, 0.0, 0.0, 0.85, 0.85, 0.0, 0.0, 0.0, 0.0]),
    ("ping_interval", "Steps de fisica entre pings aleatorios (x U[0.5,1.5]). Com hider, os pings vem dos passos dele.",
     [0, 0, 0, 0, 0, 2000, 0, 0, 0, 0]),
    ("hider_mode", "0 nenhum / 1 parado / 2 anda / 3 foge.",
     [0, 0, 0, 0, 0, 0, 1, 2, 3, 3]),
    ("hider_speed", "m/s do hider (o seeker anda a 6).",
     [0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 1.0, 2.2, 2.2]),
    ("hider_noise", "Chance de cada chegada do hider num no de ping virar ping.",
     [1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 0.6, 0.4, 0.3]),
    ("discovery_reward_scale", "Escala da exploracao. 0.5 na caca: continua valendo explorar (contra esquecer).",
     [1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 0.5, 0.5, 0.5, 0.5]),
    ("ping_reward_scale", "Escala do ping (chegar +2, expirar -0.5). 0 na caca: o rastro do hider so informa.",
     [1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 0.0, 0.0, 0.0, 0.0]),
    ("vision_explores", "1 = o que ele VE na sala atual conta como pisado.",
     [0, 0, 0, 0, 0, 0, 1, 1, 1, 1]),
    ("hider_loose", "1 = hider anda fora do centro dos nos e se esconde.",
     [0, 0, 0, 0, 0, 0, 0, 0, 0, 1]),
]
NIGHT_CONSTANTS = """  room_complete_threshold: 0.8
  ping_single_room_chance: 0.5
"""

# ---- V4.1 (02/10, era "V4B"): planta de salas + explorar = VER + suspeita multiplica, do zero ----
# O v4_noite_01 pegou o hider em 62-70%, mas com hider a cobertura caia a cada licao (54% -> 51%)
# enquanto a suspeita limpa subia: "limpar suspeita" e "explorar" pagavam separado e a suspeita
# ganhava; e as salas S24 (corredor de 20 nos em anel) e S25 (sala de 23x24 m dentro dele) quase
# nunca eram vistas - de longe nada dizia que elas existiam.
V4_1_BODY = NIGHT_BODY.replace(
    "# Tudo num run de ~8 h, herdando o cerebro do v4_s1_01 (o vetor 182 nao mudou).",
    """# DO ZERO (sensor novo = rede nova). O que mudou do v4_noite_01:
#   - PLANTA DE SALAS: BufferSensor "Rooms" com as 26 salas (direcao, distancia, portas ate la,
#     quanto ja VIU, concluida, quente, suspeita, atual). Ele sabe que a S25 existe e falta ver.
#   - EXPLORAR = VER, desde a licao 1 (vision_explores 1): no de QUALQUER sala que entra no cone
#     conta como visto. "Querer ver tudo".
#   - SUSPEITA MULTIPLICA: nao paga mais sozinha (_suspicionClearedReward saiu); multiplica o valor
#     de ver a sala (ate 3x) e REABRE sala concluida onde ela voltou a crescer. Por isso, na caca,
#     discovery_reward_scale 1.0 (era 0.5) e sem liberacao por tempo (a suspeita faz o papel).
#
# (Texto abaixo herdado do v4_noite_01; os valores da tabela de licoes sao os deste arquivo.)
#""")
V4_1_PARAMS = []
for _name, _comment, _values in NIGHT_PARAMS:
    if _name == "vision_explores":
        _values = [1] * len(_values)
        _comment = "1 = o que ele VE (qualquer sala) conta como visto. Ligado o run inteiro."
    elif _name == "discovery_reward_scale":
        _values = [1.0] * len(_values)
        _comment = "Escala da exploracao. 1.0 sempre: a suspeita so multiplica, entao explorar nao compete com nada."
    V4_1_PARAMS.append((_name, _comment, _values))

# ---- V4.2 (03/10, era "V5"): DO ZERO, caca com hider rapido, calor do ping, raios duplos, menos steering ----
# ABANDONADA aos 3.72M (v4.2_noite_01): parou de bater na parede porque parou de chegar perto dela,
# ou seja, de passar em porta (22% das salas na Metade; a v4.1 tinha 41%). Fica aqui como registro: o
# prefab ja NAO tem mais esses valores (voltou a fisica, a parede e o sensor da v4.1).
# Sensor novo (2 RayPerceptionSensors x 13 raios no lugar de 1 x 9) => obs_0/obs_3 mudam de tamanho =>
# o v4 e o modelo de 41k NAO carregam e nao servem de --initialize-from. Do zero, na escada da v4.1.
V4_2_BODY = """# V4.2 DO ZERO (03/10, ABANDONADA aos 3.72M - ver historico). O que mudou da v4.1 (v4.1_noite_01):
#   - PERCEPCAO: 2 sensores de raios de parede (RaysHigh/RaysLow, 13 raios cada, alcance 15 m, alturas
#     ajustaveis em WorldAlignedSensor._heightOffset) no lugar de 1 de 9 raios a 20 m. Sensor novo =
#     rede nova (por isso do zero).
#   - MOVIMENTO: seeker 20 m/s (era 40) com aceleracao 7 (era 15), freio 30 e atrito 1 (corpo nao e mais
#     sem atrito): ele deslizava e se jogava na parede, e o steering assist cobria o erro.
#   - STEERING MENOR: 0.6 -> 0.1 (era 1.0 -> 0.3); parede custa o dobro: contato 0.0015/step e batida 0.1.
#   - CALOR DO PING NO MAPA INTEIRO (GraphRoomMemory): 1 na sala do barulho, x0.65 por porta, meia-vida
#     25 s; observacao continua (sala atual, portas, planta). Explorar no frio vale 0.25x enquanto ha
#     calor; a sala quente vale ~8x. Ping chegado paga 5 (era 2). Suspeita zerada volta a pagar (1.0).
#   - CACA MUITO MAIS VALIOSA: captura 20 + ate 25 por pegar cedo, avistar 2, aproximar 0.4 por metro
#     (so com visao livre = linha reta), manter em visao 0.004/step; exploracao 0.5x na caca.
#   - HIDER: 10 m/s, sem pausa, foge do seeker a menos de 18 m, vagueia fora disso.
#
#   #  Licao        salas assist pre  libera ping  hider  vel  barulho desc ping$ visao solto  criterio
#   1  Perto        0.2   0.6    0    0      0     -      -    -       1.0  1.0   1     0      reward 5.0  (80 ep.)
#   2  Metade       0.5   0.5    0    0      0     -      -    -       1.0  1.0   1     0      reward 8.0  (80)
#   3  Quase        0.8   0.3    0    0      0     -      -    -       1.0  1.0   1     0      reward 10.0 (150)
#   4  MenosAssist  0.8   0.15   0.3  0      0     -      -    -       1.0  1.0   1     0      reward 7.5  (150)
#   5  Patrulha     1.1   0.1    0    0.85   0     -      -    -       1.0  1.0   1     0      progresso 0.30 (300)
#   6  Ping         1.1   0.1    0    0.85   2000  -      -    -       1.0  1.0   1     0      progresso 0.40 (300)
#   7  HiderParado  1.1   0.1    0    0      0     parado -    1.0     0.5  1.0   1     0      progresso 0.52 (300)
#   8  HiderAnda    1.1   0.1    0    0      0     anda   5.0  0.8     0.5  1.0   1     0      progresso 0.64 (300)
#   9  HiderFoge    1.1   0.1    0    0      0     foge   8.0  0.7     0.5  1.0   1     0      progresso 0.78 (300)
#  10  HiderSolto   1.1   0.1    0    0      0     foge  10.0  0.7     0.5  1.0   1     1      (final)
#
# THRESHOLDS: os da v4.1 menos ~1 (a parede custa o dobro; v4.1: Quase 10.9, MenosAssist 10.0 de media).
# ATENCAO: ping$ 1.0 na caca e o que virou renda no night_04. Se Exploration/Coverage cair enquanto o
# reward sobe, baixe ping_reward_scale para ~0.3. Compare tambem a captura x reward.
# O que olhar: Hunt/Caught e Hunt/Seen, Exploration/Coverage, WallContactFraction, Movement/IdleFraction.
"""
V4_2_LESSONS = [
    ("Perto", 5.0, 80),
    ("Metade", 8.0, 80),
    ("Quase", 10.0, 150),
    ("MenosAssist", 7.5, 150),
    ("Patrulha", 0.30, 300, "progress"),
    ("Ping", 0.40, 300, "progress"),
    ("HiderParado", 0.52, 300, "progress"),
    ("HiderAnda", 0.64, 300, "progress"),
    ("HiderFoge", 0.78, 300, "progress"),
    ("HiderSolto", None, None),
]
V4_2_OVERRIDES = {
    "steer_assist": ("Steering assistido (0..1). Menor que na v4.1: ele aprendia a se jogar na parede e deslizar.",
                     [0.6, 0.5, 0.3, 0.15, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1]),
    "hider_speed": ("m/s do hider. 10 no fim (sem pausa, foge a menos de 18 m).",
                    [0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 5.0, 8.0, 10.0]),
    "hider_noise": ("Chance de cada chegada do hider num no de ping virar ping.",
                    [1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 0.8, 0.7, 0.7]),
    "ping_reward_scale": ("Ping valendo tambem na caca (chegar +5). Cuidado: night_04.",
                          [1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0]),
    "discovery_reward_scale": ("Escala da exploracao. 0.5 na caca: o objetivo e pegar.",
                               [1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 0.5, 0.5, 0.5, 0.5]),
}
V4_2_PARAMS = []
for _name, _comment, _values in V4_1_PARAMS:
    if _name in V4_2_OVERRIDES:
        _comment, _values = V4_2_OVERRIDES[_name]
    V4_2_PARAMS.append((_name, _comment, _values))

# ---- V4.3 (03/10): CACA, herdando o cerebro da v4.1 ----
# Depois da v4.2: em vez de refazer tudo do zero, parte do explorador bom (v4.1, pegava ~85% do hider a
# 2.2 m/s) e muda UMA coisa - a caca. Fisica, parede e sensor de raios sao os da v4.1 (o 2o sensor
# RaysWorldLow esta DESATIVADO no prefab: com ele a observacao muda e o --initialize-from nao carrega).
V4_3_BODY = """# CACA, herdando a v4.1 (v4.1_noite_01, HiderFoge: pegava ~85% com o hider a 2.2 m/s e pausa nos nos).
#
#   #  Licao       vel  barulho solto  criterio
#   1  FogeLenta   4.0  0.4     0      progresso 0.20 (300 ep.)
#   2  FogeMedia   6.0  0.3     0      progresso 0.45 (300)
#   3  FogeRapida  8.0  0.3     0      progresso 0.70 (300)
#   4  Solto       8.0  0.3     1      (final)
#   Todas: sem fim por cobertura (1.1), assist 0.3, hider foge (modo 3), exploracao x0.5, ping x0.4.
#
# O QUE MUDA DA v4.1 (so a caca; fisica 40 m/s / acel. 15 / sem atrito, parede 0.00075 + 0.03 e raios
# 1 x 9 a 20 m continuam iguais):
#   - HIDER MAIS DIFICIL: sem pausa nos nos (_maxPauseSteps 0), foge a menos de 18 m (era 12) e mais
#     rapido em escada (4 -> 6 -> 8 m/s; a v4.1 parou em 2.2). O seeker anda a 40, mas numa sala de ~25 m
#     chega a ~19 m/s: 8 ja e metade disso. Subir de uma vez seria tirar a muleta toda de uma vez;
#   - CACA MAIS VALIOSA (prefab): captura 20 + ate 25 por pegar cedo, avistar 2, aproximar 0.4/m (so com
#     visao livre), manter em visao 0.004/step (teto 32 < 45 de pegar cedo), suspeita zerada 1.0;
#   - CALOR DO PING NO MAPA INTEIRO (GraphRoomMemory): x0.65 por porta, meia-vida 25 s, observacao
#     continua nos mesmos slots (a rede herdada so ve valores entre 0 e 1 onde antes via 0 ou 1);
#   - PING: chegar paga 5 no prefab; aqui x0.4 = 2, o valor que o plano da fuga previa. Ping pago na caca
#     foi o que virou renda no night_04: se Exploration/Coverage cair com o reward subindo, baixe para 0.2;
#   - exploracao x0.5 na caca (a v4.1 usava 1.0): o objetivo e pegar.
#
# PASSA QUANDO: Hunt/Caught nao cair abaixo de ~60% na FogeMedia/FogeRapida. Se cair a < 40%, a
# velocidade e demais: segure (rode de novo com a escada mais baixa) antes de mexer em recompensa.
# O que olhar: Hunt/Caught, Hunt/Seen, Exploration/Coverage, WallContactFraction (a v4.1 tinha ~18%
# na caca), Movement/IdleFraction. 5M steps, checkpoint a cada 500k (tools/best_onnx.py).
"""
V4_3_LESSONS = [
    ("FogeLenta", 0.20, 300, "progress"),
    ("FogeMedia", 0.45, 300, "progress"),
    ("FogeRapida", 0.70, 300, "progress"),
    ("Solto", None, None),
]
V4_3_PARAMS = [
    ("coverage_target", "Sem fim por cobertura: o episodio acaba pegando o hider ou no tempo.", [1.1, 1.1, 1.1, 1.1]),
    ("steer_assist", "Steering assistido (0..1). O mesmo do fim da v4.1.", [0.3, 0.3, 0.3, 0.3]),
    ("previsited_fraction", "Fracao das SALAS que ja nasce concluida.", [0.0, 0.0, 0.0, 0.0]),
    ("release_fraction", "Sem liberacao por tempo: a suspeita faz o papel.", [0.0, 0.0, 0.0, 0.0]),
    ("ping_interval", "0: os pings vem dos passos do hider.", [0, 0, 0, 0]),
    ("hider_mode", "3 = foge.", [3, 3, 3, 3]),
    ("hider_speed", "m/s do hider (sem pausa, foge a menos de 18 m). Escada: a v4.1 parou em 2.2.", [4.0, 6.0, 8.0, 8.0]),
    ("hider_noise", "Chance de cada chegada do hider num no de ping virar ping.", [0.4, 0.3, 0.3, 0.3]),
    ("discovery_reward_scale", "Exploracao vale metade da v4.1: a caca e o objetivo.", [0.5, 0.5, 0.5, 0.5]),
    ("ping_reward_scale", "Ping chegado 5 x 0.4 = 2 (expirar -0.2). Cuidado: night_04.", [0.4, 0.4, 0.4, 0.4]),
    ("vision_explores", "1 = o que ele VE (qualquer sala) conta como visto.", [1, 1, 1, 1]),
    ("hider_loose", "1 = hider anda fora do centro dos nos e se esconde.", [0, 0, 0, 1]),
]

# ---- V4.4 (03/10): CORRIDA, herdando a v4.3 ----
# Substitui o plano anterior da v4.4 (aceleracao 35 + freio 50), que nunca rodou: o Arthur pediu
# movimento SEM aceleracao, o corpo seguindo o movimento (so ha animacao de andar/correr para a frente),
# pescoco para o olhar, corrida com estamina e velocidade por estado. A tarefa continua a caca da v4.3.
# Vetor 182 e 4 acoes iguais (o slot [26], que era o steer assist, virou a estamina), entao o
# --initialize-from carrega.
V4_4_BODY = """# CORRIDA, herdando a v4.3 (v4.3_caca_01, 532k: via ~89% e pegava ~72% do hider a 4 m/s).
#
#   #  Licao        hider (corre/anda)  estamina hider  barulho solto  criterio
#   1  Lenta        4  / 2.7            3 s             0.4     0      progresso 0.15 (300 ep.)
#   2  Media        6  / 4              3 s             0.3     0      progresso 0.35 (300)
#   3  Rapida       8  / 5.3            3 s             0.3     0      progresso 0.55 (300)
#   4  MuitoRapida  10 / 6.7            3 s             0.3     0      progresso 0.75 (300)
#   5  Solta        10 / 6.7            3 s             0.3     1      (final)
#   Todas: sem fim por cobertura (1.1), hider foge (modo 3), exploracao x0.3, ping x0.4.
#
# SEEKER (GraphLocomotion, no prefab; correndo / andando = x2/3):
#   patrulha 15 / 10, alerta (calor do ping >= 0.5, ~25 s apos cada ping) 18 / 12,
#   perseguicao (vendo o hider + 3 s depois) 20 / 13.3. Corre com |andar| >= 0.9; estamina 5 s,
#   recupera 0.5 s/s (vazio -> cheio em 10 s). O hider tem 3 s: na perseguicao o seeker alcanca.
#
# O QUE MUDA DA v4.3:
#   - MOVIMENTO: sem aceleracao (velocidade na hora), sem steer assist, o corpo gira para onde anda e so
#     anda para a frente (de costas para o pedido, gira parado); o olhar [2..3] vira so a CABECA, ate
#     60 graus de cada lado, e o cone de visao vai junto;
#   - CORRIDA com estamina (observacao [26], que era o assist) e velocidade por estado (tabela acima);
#   - HIDER anda a 2/3 e so corre fugindo, com estamina; escada 4 -> 6 -> 8 -> 10 m/s correndo;
#   - PAREDE x2 na layer Wall (0.0015/step + 0.06/batida); layer Door (paredes Door_Hole) e Obstacle
#     (moveis) sem punicao - aplicar no prefab, ver historico;
#   - SUSPEITA zerada paga 2 (era 1); exploracao x0.3 (era 0.5): o foco e a captura.
#
# PASSA QUANDO: Hunt/Caught >= ~65% na MuitoRapida, WallContactFraction < 0.15, WallHits caindo,
# Movement/RunFraction entre ~0.1 e ~0.5 (0 = nunca corre; ~1 = corre ate esvaziar o tempo todo).
# Se Hunt/Caught cair < 40% numa licao, segure a escada do hider antes de mexer em recompensa.
# 5M steps, checkpoint a cada 500k (tools/best_onnx.py --metric Hunt/Caught).
"""
V4_4_LESSONS = [
    ("Lenta", 0.15, 300, "progress"),
    ("Media", 0.35, 300, "progress"),
    ("Rapida", 0.55, 300, "progress"),
    ("MuitoRapida", 0.75, 300, "progress"),
    ("Solta", None, None),
]
V4_4_PARAMS = [
    ("coverage_target", "Sem fim por cobertura: o episodio acaba pegando o hider ou no tempo.", [1.1] * 5),
    ("previsited_fraction", "Fracao das SALAS que ja nasce concluida.", [0.0] * 5),
    ("release_fraction", "Sem liberacao por tempo: a suspeita faz o papel.", [0.0] * 5),
    ("ping_interval", "0: os pings vem dos passos do hider.", [0] * 5),
    ("hider_mode", "3 = foge.", [3] * 5),
    ("hider_speed", "m/s do hider CORRENDO (anda a 2/3). Seeker corre a 15/18/20 (patrulha/alerta/perseguicao).",
     [4.0, 6.0, 8.0, 10.0, 10.0]),
    ("hider_stamina", "Segundos de corrida do hider (o seeker tem 5).", [3.0] * 5),
    ("hider_noise", "Chance de cada chegada do hider num no de ping virar ping.", [0.4, 0.3, 0.3, 0.3, 0.3]),
    ("discovery_reward_scale", "Exploracao x0.3 (era 0.5): o foco e pegar.", [0.3] * 5),
    ("ping_reward_scale", "Ping chegado 5 x 0.4 = 2 (expirar -0.2). Cuidado: night_04.", [0.4] * 5),
    ("vision_explores", "1 = o que ele VE (qualquer sala) conta como visto.", [1] * 5),
    ("hider_loose", "1 = hider anda fora do centro dos nos e se esconde.", [0, 0, 0, 0, 1]),
]

# ---- V4.5 (03/10): FUGA COM MAIS ESTAMINA, herdando a v4.4 ----
# Mesmo codigo e prefab da v4.4; muda so o hider: corre mais rapido que o seeker anda na perseguicao
# (13.3) e aguenta correr mais tempo que ele (5 s). Ganhar no folego nao da: tem que cortar caminho,
# usar o ping/calor e correr so na hora certa.
V4_5_BODY = """# FUGA, herdando a v4.4 (v4.4_corrida_01). Codigo e prefab iguais aos da v4.4; muda so o hider.
#
#   #  Licao      hider (corre/anda)  estamina hider  barulho solto  criterio
#   1  FogeIgual  10 / 6.7            6 s             0.3     0      progresso 0.25 (300 ep.)
#   2  FogeForte  12 / 8              8 s             0.3     0      progresso 0.50 (300)
#   3  FogeLonge  14 / 9.3            10 s            0.3     0      progresso 0.75 (300)
#   4  Solta      14 / 9.3            10 s            0.3     1      (final)
#   Seeker igual a v4.4: corre 15/18/20, anda 10/12/13.3, estamina 5 s.
#
# A 14 m/s o hider correndo e mais rapido que o seeker ANDANDO na perseguicao (13.3) e o folego dele
# dura 2x o do seeker: perseguir em linha reta ate o hider cansar nao funciona. O seeker tem que correr
# so perto (20 > 14), cortar pelo grafo e chegar pelo ping/calor antes de ser visto (o hider so corre
# com o seeker a menos de 18 m).
#
# PASSA QUANDO: Hunt/Caught >= ~50% na FogeLonge sem Hunt/Seen cair (< 80% = parou de procurar).
# Se cair < 30% na FogeForte, o hider ficou forte demais: volte a 12 / 8 s e rode a FogeLonge
# de novo com mais steps antes de mexer em recompensa. 5M steps, checkpoint a cada 500k.
"""
V4_5_LESSONS = [
    ("FogeIgual", 0.25, 300, "progress"),
    ("FogeForte", 0.50, 300, "progress"),
    ("FogeLonge", 0.75, 300, "progress"),
    ("Solta", None, None),
]
V4_5_OVERRIDES = {
    "hider_speed": ("m/s do hider CORRENDO (anda a 2/3). 14 > 13.3 do seeker andando na perseguicao.",
                    [10.0, 12.0, 14.0, 14.0]),
    "hider_stamina": ("Segundos de corrida do hider: MAIS que os 5 do seeker.", [6.0, 8.0, 10.0, 10.0]),
    "hider_noise": ("Chance de cada chegada do hider num no de ping virar ping.", [0.3] * 4),
    "hider_loose": ("1 = hider anda fora do centro dos nos e se esconde.", [0, 0, 0, 1]),
}
V4_5_PARAMS = []
for _name, _comment, _values in V4_4_PARAMS:
    if _name in V4_5_OVERRIDES:
        _comment, _values = V4_5_OVERRIDES[_name]
    else:
        _values = _values[:4]
    V4_5_PARAMS.append((_name, _comment, _values))

# Versoes do mapa v4 (salas e portas): v4.0 = v4_s1_01 + v4_noite_01; v4.1 = planta de salas (era
# "v4b"); v4.2 = do zero contra a parede (era "v5", abandonada); v4.3 = caca herdando a v4.1;
# v4.4 = corrida (sem aceleracao, estamina, corpo segue o movimento) herdando a v4.3;
# v4.5 = fuga com hider de mais estamina herdando a v4.4.
# Versao nova (v5) so quando mudar o mapa ou a forma de treinar; ajuste na mesma tarefa = v4.x.
# v5.0 = do zero com o corpo do jogador e vetor 188 (V5_STAGES, abaixo).
V4_STAGES = [
    ("graph_v4_s1_salas", "v4_s1_01", None, "V4.0 S1 SALAS E PORTAS (do zero, sem seta)",
     S1_BODY, 5000000, V4_EXPLORE_ONLY, S1_LESSONS, S1_PARAMS),
    ("graph_v4_noite", "v4_noite_01", "v4_s1_01", "V4.0 NOITE: S1 -> S6 NUM RUN SO (~8 h)",
     NIGHT_BODY, 10000000, NIGHT_CONSTANTS, NIGHT_LESSONS, NIGHT_PARAMS),
    ("graph_v4.1_noite", "v4.1_noite_01", None, "V4.1: PLANTA DE SALAS + VER TUDO (do zero, ~8 h)",
     V4_1_BODY, 10000000, NIGHT_CONSTANTS, NIGHT_LESSONS, V4_1_PARAMS),
    ("graph_v4.2_noite", "v4.2_noite_01", None, "V4.2 (ABANDONADA): DO ZERO - CACA RAPIDA, CALOR DO PING, RAIOS DUPLOS",
     V4_2_BODY, 10000000, NIGHT_CONSTANTS, V4_2_LESSONS, V4_2_PARAMS),
    ("graph_v4.3_caca", "v4.3_caca_01", "v4.1_noite_01", "V4.3: CACA, HERDANDO A V4.1",
     V4_3_BODY, 5000000, NIGHT_CONSTANTS, V4_3_LESSONS, V4_3_PARAMS),
    ("graph_v4.4_corrida", "v4.4_corrida_01", "v4.3_caca_01", "V4.4: CORRIDA (sem aceleracao, estamina, corpo segue o movimento)",
     V4_4_BODY, 5000000, NIGHT_CONSTANTS, V4_4_LESSONS, V4_4_PARAMS),
    ("graph_v4.5_fuga", "v4.5_fuga_01", "v4.4_corrida_01", "V4.5: FUGA (hider com mais estamina que o seeker)",
     V4_5_BODY, 5000000, NIGHT_CONSTANTS, V4_5_LESSONS, V4_5_PARAMS),
]

# ---- V5 (04/10): DO ZERO, corpo na escala do JOGADOR, episodio longo, vetor 188 ----
# Forma de treinar nova (vetor 188 = rede nova, prefab novo, corpo novo): nao herda nada.
def v5_header(title, name, run, body):
    return f"""# ================================================================================================
# GraphExplorer - mapa v4 (prefab NodeTraining6, cena Node_6 com as 6 arenas ligadas) - {title}
#
#   mlagents-learn config/{name}.yaml --run-id={run}
#
# GERADO por tools/gen_full_curriculum.py - edite la, nao aqui. Arquitetura: docs/graph/arquitetura.md.
# Vetor 188 (Behavior Parameters > Vector Observation Space Size), 4 acoes continuas, Decision Period 5.
#
{body}# ================================================================================================

"""


V5_BODY = """# DO ZERO. O que mudou da v4.x:
#   - VELOCIDADE PELO ESTADO DE ALERTA (escala do jogador, PlayerDummy da main: anda 6, corre 10.2):
#     Patrulha 6 m/s (sem pista), Alerta 8 m/s (ouviu um ping ou perdeu o alvo de vista ha < 10 s),
#     Perseguicao 10.2 m/s (VENDO o alvo). Sem corrida por acao e sem folego: ver o jogador e o que o deixa
#     rapido, e ele ve o proprio estado. Inercia leve (acelera 20, freia 40 m/s2), giro 540/s e 360/s na
#     perseguicao, parede
#     custa velocidade. Sem estados de alerta/perseguicao: a velocidade e sempre a do jogador.
#   - HIDER na mesma escala: hider_speed e a corrida dele (anda a 0.588 x, a razao do jogador).
#   - EPISODIO de 20000 steps = 400 s (era 160 s): a 6-10 m/s da ~2800 m de caminho, contra ~1100 m de
#     ligacoes no mapa (diametro 248 m). Custos por step divididos por 2.5 (mesmo teto da v4).
#   - OBSERVACAO 188 (+6): a propria velocidade X/Z, estado (perseguicao; alerta e quanto resta dele; [26] = a
#     velocidade do estado), fracao do episodio e tempo sem
#     progresso. Ele ve o proprio movimento e o relogio.
#   - TODA SALA VALE O MESMO na exploracao (1 por sala, qualquer tamanho); so calor do ping, suspeita
#     e sala ja explorada (cauda x0.2) mudam o valor.
#   - EXPLORAR = VER, e a sala so CONCLUI com 100% dos nos vistos (room_complete_threshold 1.0): nenhum
#     canto com no fica sem olhar. Degrau 0.8 -> 0.9 -> 1.0 nas tres primeiras licoes para o comeco do
#     zero nao travar no ultimo no de cada sala; de Quase em diante e 1.0 ate o fim. Canto SEM no nao conta.
#
#   #  Licao         salas sala% pre  libera ping  hider  corre  folego barulho desc ping$ solto  criterio
#   1  Perto         0.2   0.8   0    0      0     -      -      -      -       1.0  1.0   0      reward 6.0  (80 ep.)
#   2  Metade        0.5   0.9   0    0      0     -      -      -      -       1.0  1.0   0      reward 9.0  (80)
#   3  Quase         0.8   1.0   0    0      0     -      -      -      -       1.0  1.0   0      reward 11.0 (100)
#   4  Variado       0.8   1.0   0.3  0      0     -      -      -      -       1.0  1.0   0      reward 8.5  (100)
#   (sala% = room_complete_threshold; 1.0 da licao 3 ate o fim)
#   5  Patrulha      1.1   0    0.85   0     -      -      -      -       1.0  1.0   0      progresso 0.25 (150)
#   6  Ping          1.1   0    0.85   4000  -      -      -      -       1.0  1.0   0      progresso 0.33 (150)
#   7  HiderParado   1.1   0    0      0     parado -      10     1.0     0.5  0     0      progresso 0.42 (150)
#   8  HiderAnda     1.1   0    0      0     anda   5.1    10     0.6     0.5  0     0      progresso 0.52 (150)
#   9  HiderFoge     1.1   0    0      0     foge   7.0    10     0.4     0.5  0     0      progresso 0.64 (150)
#  10  HiderRapido   1.1   0    0      0     foge   8.5    10     0.3     0.5  0     0      progresso 0.78 (150)
#  11  HiderJogador  1.1   0    0      0     foge   10.2   10     0.3     0.5  0     1      (final)
#   "corre" = m/s do hider correndo (anda a 0.588 x: 3 / 4.1 / 5 / 6 m/s). Ver = explorar desde a licao 1.
#   Na licao 11 o hider e o jogador: corre 10.2 por 10 s; o seeker so chega a 10.2 vendo ele. So pega quem corta caminho.
#
# THRESHOLDS de reward: os da v4.1 (mesmas recompensas de sala/porta; ~70% de um episodio bom).
# PROGRESSO: 15M steps; a Patrulha sai aos 3.75M, o Ping aos ~5M ... HiderJogador fica com os ultimos
# ~3.3M. Um episodio longo tem ate 4000 decisoes: 150 episodios = ate 600k steps por licao.
#
# PASSA QUANDO: Exploration/Coverage > 0.7 na Quase; Hunt/Caught >= ~60% na HiderFoge e >= ~40% na
# HiderJogador; WallContactFraction < 0.15; Movement/ChaseFraction e AlertFraction subindo nas licoes de caca;
# Movement/MeanSpeed acima de ~6 (abaixo = parado demais). Checkpoint a cada 500k, todos guardados
# (tools/best_onnx.py). 15M steps ~ 12 h na maquina do Arthur (10M levavam ~8 h).
"""
V5_LESSONS = [
    ("Perto", 6.0, 80),
    ("Metade", 9.0, 80),
    ("Quase", 11.0, 100),
    ("Variado", 8.5, 100),
    ("Patrulha", 0.25, 150, "progress"),
    ("Ping", 0.33, 150, "progress"),
    ("HiderParado", 0.42, 150, "progress"),
    ("HiderAnda", 0.52, 150, "progress"),
    ("HiderFoge", 0.64, 150, "progress"),
    ("HiderRapido", 0.78, 150, "progress"),
    ("HiderJogador", None, None),
]
V5_PARAMS = [
    ("room_complete_threshold", "Fracao dos nos de uma sala que precisam ser VISTOS para concluir. 1.0 = a sala inteira.",
     [0.8, 0.9] + [1.0] * 9),
    ("coverage_target", "Fracao das SALAS concluidas que encerra o episodio (+5). Acima de 1 = sem fim por cobertura.",
     [0.2, 0.5, 0.8, 0.8, 1.1, 1.1, 1.1, 1.1, 1.1, 1.1, 1.1]),
    ("previsited_fraction", "Fracao das SALAS que ja nasce concluida (anti-decoreba).",
     [0.0, 0.0, 0.0, 0.3, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0]),
    ("release_fraction", "Patrulha: com esta fracao usada, a porta e a sala mais antigas voltam. 0 = off.",
     [0.0, 0.0, 0.0, 0.0, 0.85, 0.85, 0.0, 0.0, 0.0, 0.0, 0.0]),
    ("ping_interval", "Steps de fisica entre pings aleatorios (x U[0.5,1.5]; 4000 = 80 s). Com hider, os pings vem dos passos dele.",
     [0, 0, 0, 0, 0, 4000, 0, 0, 0, 0, 0]),
    ("hider_mode", "0 nenhum / 1 parado / 2 anda / 3 foge.",
     [0, 0, 0, 0, 0, 0, 1, 2, 3, 3, 3]),
    ("hider_speed", "m/s do hider CORRENDO (anda a 0.588 x). 10.2 = o jogador (e o seeker em perseguicao).",
     [0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 5.1, 7.0, 8.5, 10.2]),
    ("hider_stamina", "Segundos de corrida do hider (10 = o jogador).",
     [10.0] * 11),
    ("hider_noise", "Chance de cada chegada do hider num no de ping virar ping.",
     [1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 0.6, 0.4, 0.3, 0.3]),
    ("discovery_reward_scale", "Escala da exploracao. 0.5 na caca: continua valendo explorar (contra esquecer).",
     [1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 0.5, 0.5, 0.5, 0.5, 0.5]),
    ("ping_reward_scale", "Escala do ping (chegar +5, expirar -0.5). 0 na caca: o rastro do hider so informa.",
     [1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 0.0, 0.0, 0.0, 0.0, 0.0]),
    ("vision_explores", "1 = o que ele VE (qualquer sala) conta como visto. Ligado o run inteiro.",
     [1] * 11),
    ("hider_loose", "1 = hider anda fora do centro dos nos e se esconde.",
     [0] * 10 + [1]),
]
# room_complete_threshold sai das constantes: na v5 ele e curriculo (0.8 -> 0.9 -> 1.0).
V5_CONSTANTS = """  ping_single_room_chance: 0.5
"""
V5_STAGES = [
    ("graph_v5.0_zero", "v5.0_zero_01", "V5.0: DO ZERO - CORPO DO JOGADOR, EPISODIO LONGO, VETOR 188",
     V5_BODY, 15000000, V5_CONSTANTS, V5_LESSONS, V5_PARAMS),
]

write('graph_node4_full.yaml', HEADER, 24000000, FULL_CONSTANTS, LESSONS, PARAMS)
write('graph_node4_search.yaml', SEARCH_HEADER, 15000000, SEARCH_CONSTANTS, SEARCH_LESSONS, SEARCH_PARAMS)

for name, run, init, title, body, steps, constants, lessons, params in STAGES:
    write(name + '.yaml', stage_header(title, name, run, init, body), steps, constants, lessons, params,
          beta=BETA_STAGES)

for name, run, init, title, body, steps, constants, lessons, params in V4_STAGES:
    # Um checkpoint a cada 500k guardado o run inteiro: num run sem supervisao, e o que permite
    # voltar ao melhor ponto se o fim piorar (overtraining / esquecimento).
    write(name + '.yaml', v4_header(title, name, run, init, body), steps, constants, lessons, params,
          beta=BETA_NIGHT if name in ('graph_v4_noite', 'graph_v4.1_noite', 'graph_v4.2_noite') else BETA_STAGES, keep=max(10, steps // 500000))

for name, run, title, body, steps, constants, lessons, params in V5_STAGES:
    write(name + '.yaml', v5_header(title, name, run, body), steps, constants, lessons, params,
          beta=BETA_NIGHT, keep=max(10, steps // 500000))
