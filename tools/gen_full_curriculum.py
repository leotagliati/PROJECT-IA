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
    keep_checkpoints: 10
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
BETA_STAGES = """      # 0.003 (era 0.015): com 0.015 a entropia nunca caiu (E1..e2_03 em 1.42-1.48 = desvio ~1, a
      # politica continuava aleatoria). Para acoes continuas o normal e 0.001-0.005.
      beta: 0.003
"""


def write(filename, header, max_steps, constants, lessons, params, beta=BETA_OLD):
    behaviors = BEHAVIORS.replace("MAX_STEPS", str(max_steps)).replace("BETA_BLOCK", beta)
    out = [header, behaviors, constants, "\n  # ---- Curriculo ----\n"]
    for name, comment, values in params:
        assert len(values) == len(lessons), name
        out.append(f"  # {comment}\n")
        out.append(f"  {name}:\n    curriculum:\n")
        for (lesson, threshold, min_len), value in zip(lessons, values):
            out.append(f"      - name: {lesson}\n")
            if threshold is not None:
                out.append(
                    "        completion_criteria:\n"
                    "          measure: reward\n"
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


write('graph_node4_full.yaml', HEADER, 24000000, FULL_CONSTANTS, LESSONS, PARAMS)
write('graph_node4_search.yaml', SEARCH_HEADER, 15000000, SEARCH_CONSTANTS, SEARCH_LESSONS, SEARCH_PARAMS)

for name, run, init, title, body, steps, constants, lessons, params in STAGES:
    write(name + '.yaml', stage_header(title, name, run, init, body), steps, constants, lessons, params,
          beta=BETA_STAGES)
