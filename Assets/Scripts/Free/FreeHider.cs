using System.Collections.Generic;
using System.Linq;
using Assets.Scripts.Graph;
using UnityEngine;
using UnityEngine.AI;

namespace Assets.Scripts.Free
{
    /// <summary>
    /// A presa SCRIPTADA da v8, só com NAVMESH (sem nó, sem grafo): faz o papel do jogador no treino e não aprende
    /// nada. Substitui o GraphHider, que andava em linha reta de centro em centro de nó, e o monstro treinado contra
    /// ele aprendia a caçar por cima do grafo.
    ///   ANDA     escolhe um destino na sala atual ou numa vizinha (pelas portas do <see cref="FreeMap"/>): um ponto
    ///            qualquer do chão ou, com _hideChance, um ESCONDERIJO; vai pelo caminho do NavMesh e pausa ao chegar
    ///            (escondido, pausa mais).
    ///   FOGE     com o monstro a menos de _fleeRadius ou à vista (até _fleeSightDistance): sorteia pontos em volta e
    ///            corre para o que fica mais longe DO MONSTRO pelo NavMesh, valendo mais o que ele não vê; refaz a cada
    ///            _fleeReplanSeconds. Corre só com estamina (GraphStamina, o modelo do jogador).
    ///   FOGE E SE ESCONDE (v8.2)  despistou o monstro (a fuga acabou): com _hideAfterFleeChance vai a um esconderijo
    ///            que ele não vê de onde está e fica agachada _hideAfterFleeMin..MaxSeconds, só fugindo com ele colado.
    ///   ESCONDERIJO  ponto perto de parede (canto, atrás de móvel) visto pelo menor número de portas da sala.
    ///   ESCONDIDA  (hider_mode 4, e parte dos episódios no 5): o jogador agachado num canto. Só vai a esconderijos,
    ///            fica em cada um _hiddenPauseMin..MaxSeconds e só foge com o monstro colado ou vendo-o de perto. Anda e
    ///            fica AGACHADA: mais baixa (a cápsula encolhe e a visão do monstro mira cabeça e peito por ela, então
    ///            mesa e sofá tapam), mais lenta e com pegada fraca. Fugindo, levanta e corre.
    ///   CHAVES   com chaves no episódio (FreeKeyField, lições finais): a maior parte dos destinos de passeio é a chave
    ///            mais perto pelo NavMesh; passando a menos de _keyPickupRadius dela, pega. Levar todas = escapou.
    /// Modos (currículo hider_mode): 0 Nenhum, 1 Parado, 2 Anda, 3 Foge (o enum do GraphHider), 4 Escondida, 5 Misto
    /// (o FreeArenaController traduz 4 e 5 em Foge + Hides).
    /// Um por arena, filho dela; o <see cref="FreeArenaController"/> configura (mapa, parede) e reposiciona.
    /// </summary>
    public class FreeHider : MonoBehaviour, IGraphTarget
    {
        [Header("-----Movimento-----")]
        // Velocidade CORRENDO (m/s) quando o currículo não manda outra (hider_speed). 10.2 = a corrida do PlayerDummy
        // (6 x 1.7); o monstro persegue a 11.5 (GraphLocomotion).
        [SerializeField, Min(0f)] private float _speed = 10.2f;

        // Andar = correr x isto. 0.588 = 6 / 10.2, a razão andar/correr do jogador.
        [SerializeField, Range(0.1f, 1f)] private float _walkFraction = 0.588f;

        // Só gasta fugindo. Segundos do tanque vêm do currículo (hider_stamina); 0 lá = os daqui.
        [SerializeField] private GraphStamina _stamina = new GraphStamina();

        // Pausa máxima ao chegar no destino, em steps de física (sorteada entre 0 e isto). Fugindo não pausa.
        [SerializeField, Min(0)] private int _maxPauseSteps = 100;

        // Altura (m) do pivô acima do NavMesh. O modelo do hider de treino tem o pivô no pé.
        [SerializeField] private float _heightOffset = 0.05f;

        // Quina do caminho mais perto que isto (m) já foi alcançada.
        [SerializeField, Min(0.05f)] private float _cornerReached = 0.3f;

        [Header("-----Fuga-----")]
        // Distância planar (m) a partir da qual foge em vez de andar.
        [SerializeField, Min(0f)] private float _fleeRadius = 12f;

        // Segundos que CONTINUA fugindo depois que o monstro sai do raio e da vista (sem isso hesita na borda).
        [SerializeField, Min(0f)] private float _fleeMemorySeconds = 3f;

        // Foge também ao VER o monstro (linha livre) até esta distância (m): o jogador corre assim que vê. 0 = só o raio.
        [SerializeField, Min(0f)] private float _fleeSightDistance = 25f;

        // Na escolha da fuga, ponto FORA da vista do monstro vale como se estivesse estes metros mais longe: dobrar a
        // esquina ou passar a porta ganha de correr reto num corredor à vista.
        [SerializeField, Min(0f)] private float _hiddenExitBonus = 10f;

        // Pontos sorteados por fuga, a uma distância planar entre min e max (m) do hider.
        [SerializeField, Min(1)] private int _fleeCandidates = 10;
        [SerializeField, Min(1f)] private float _fleeMinDistance = 6f;
        [SerializeField, Min(2f)] private float _fleeMaxDistance = 16f;

        // De quanto em quanto tempo (s) a fuga é refeita (o monstro se mexe).
        [SerializeField, Min(0.1f)] private float _fleeReplanSeconds = 0.5f;

        // Altura (m) do olho do monstro acima do pivô dele (GraphHiderPerception._eyeHeight) e até onde ele vê (m).
        [SerializeField] private float _seekerEyeHeight = 1.67f;
        [SerializeField, Min(1f)] private float _seekerViewDistance = 35f;

        [Header("-----Esconderijo-----")]
        // Chance de o próximo destino (andando) ser um esconderijo.
        [SerializeField, Range(0f, 1f)] private float _hideChance = 0.5f;

        // Pontos sorteados para escolher o esconderijo, e quanto (m) cada um se afasta do ponto da grade sorteado.
        [SerializeField, Min(1)] private int _hideCandidates = 8;
        [SerializeField, Min(0f)] private float _spotSpread = 1.5f;

        // Peso do ponto POUCO VISTO pelo monstro no esconderijo (FreeArenaController.PointRarity, 0 a 1), em "metros de
        // parede": 10 = o ponto que ele nunca vê ganha de um 10 m mais colado em parede que ele sempre vê. Abaixo de ser
        // visto por uma porta (100 por porta): esconderijo à vista da entrada continua ruim. Os candidatos também são
        // sorteados puxando para os pontos raros (_hideRareFloor + raridade). 14/10.
        [SerializeField, Min(0f)] private float _hideRareScore = 10f;
        [SerializeField, Min(0.01f)] private float _hideRareFloor = 0.2f;

        [Header("-----Fora da ronda do monstro (v8.2)-----")]
        // Destinos sorteados ao passear (sala pelo peso da arena + ponto); fica o de caminho MENOS exposto: o que passa
        // menos pelos pontos que o monstro vê sempre (a ronda decorada dele). 1 = sem comparar.
        [SerializeField, Min(1)] private int _wanderCandidates = 3;

        // De quantos em quantos metros do caminho mede a exposição (o ponto do FreeMap mais perto de cada amostra).
        [SerializeField, Min(0.5f)] private float _exposureStep = 4f;

        // Chegou a um esconderijo numa sala com raridade >= isto (FreeArenaController.RoomRarity: 1 = nunca vista):
        // FICA, agachada, até o monstro chegar perto ou vê-la (a fuga de quem está escondida). Se ele nunca vai lá, ela
        // nunca sai: é o jogador escondido onde a ronda não passa. 0.6 = salas bem menos vistas que a mais vista.
        [SerializeField, Range(0f, 1f)] private float _settleRarity = 0.6f;

        // Multiplicador da pausa quando escondido.
        [SerializeField, Min(1f)] private float _hidePauseMultiplier = 3f;

        // Altura (m) acima do piso de quem olha da porta (o olho do monstro, FreeExplorationMemory._eyeHeight) e do
        // ponto olhado: a CABEÇA da presa agachada (1.35 de 1.52 m, 14/10; era 1 com a presa de 2 m). A visão 3D do
        // monstro mira a grade do corpo a partir daí para baixo: tapada a cabeça, quase sempre tapa o resto.
        [SerializeField, Min(0.1f)] private float _doorEyeHeight = 2.5f;
        [SerializeField, Min(0.1f)] private float _hiderChestHeight = 1.35f;

        [Header("-----Escondida (hider_mode 4; metade dos episódios no 5)-----")]
        // Quanto tempo (s) fica em cada esconderijo: o bastante para o monstro ter de achá-la parada, sem o movimento
        // que a denuncia na lição Foge (lá a pausa máxima escondida é ~6 s).
        [SerializeField, Min(0f)] private float _hiddenPauseMinSeconds = 15f;
        [SerializeField, Min(0f)] private float _hiddenPauseMaxSeconds = 45f;

        // Só foge com o monstro a menos disto (m), ou vendo-o a menos de _hiddenFleeSightDistance: quem está escondido
        // não sai correndo ao ver o monstro do outro lado da sala, espera ser achado.
        [SerializeField, Min(0f)] private float _hiddenFleeRadius = 5f;
        [SerializeField, Min(0f)] private float _hiddenFleeSightDistance = 8f;

        [Header("-----Agachada (escondida)-----")]
        // Altura da cápsula agachada / em pé. 0.5 (14/10; era 0.55): no Map - V8 a presa tem a altura do jogador
        // (PlayerDummy - V8.2: 3.04 m em pé, com o topo na câmera, e 1.5 m agachado, nos ombros), então 1.52 de 3.04 m.
        [SerializeField, Range(0.2f, 1f)] private float _crouchHeightFraction = 0.5f;

        // Andar agachada = andar x isto. 0.45 = o crouchSpeedMultiplier do PlayerDummy (2.7 m/s com o andar de 6).
        [SerializeField, Range(0.1f, 1f)] private float _crouchSpeedMultiplier = 0.45f;

        [Header("-----Fugir e se esconder (v8.2)-----")]
        // Chance de, ao DESPISTAR o monstro (a fuga acaba), ir até um esconderijo que ele não vê de onde está e ficar lá
        // agachada, como o jogador faz: corre para quebrar a linha de visão e se esconde. 0 = volta a passear (até a
        // v8.1, a presa que fugia só voltava a andar, e achá-la era questão de olhar o corredor certo).
        [SerializeField, Range(0f, 1f)] private float _hideAfterFleeChance = 0.8f;

        // Quanto tempo (s) fica nesse esconderijo; lá só foge com o monstro colado (os limites da escondida).
        [SerializeField, Min(0f)] private float _hideAfterFleeMinSeconds = 8f;
        [SerializeField, Min(0f)] private float _hideAfterFleeMaxSeconds = 25f;

        // Esconderijos sorteados (na sala atual e nas vizinhas) até achar um fora da vista do monstro.
        [SerializeField, Min(1)] private int _hideAfterFleeTries = 4;

        [Header("-----Chaves (lições finais)-----")]
        // Chance de o próximo destino ser a chave mais perto (o resto é esconderijo ou passeio, como sem chaves): o
        // jogador vai atrás delas, mas se esconde no caminho.
        [SerializeField, Range(0f, 1f)] private float _keySeekChance = 0.7f;

        // Distância (m) do pivô à chave para pegá-la.
        [SerializeField, Min(0.1f)] private float _keyPickupRadius = 1.2f;

        [Header("-----Spawn-----")]
        // Distância mínima (m pelo NavMesh) entre o spawn do hider e o monstro; numa sala diferente da dele.
        [SerializeField, Min(0f)] private float _minSpawnDistanceMeters = 40f;

        [Header("-----Referências-----")]
        // Opcional e só visual (Animator do modelo): vazio = acha nos filhos; sem ele o hider treina igual.
        [SerializeField] private GraphHiderAnimationSystem _animationSystem;

        private Rigidbody _rigidbody;
        private CapsuleCollider _capsule;
        private float _standHeight;
        private Vector3 _standCenter;
        private FreeMap _map;
        private FreeArenaController _arena;
        private FreeKeyField _keys;
        private LayerMask _walls;
        private Transform _seeker;
        private GraphHider.Mode _mode = GraphHider.Mode.None;
        private bool _hides;
        private float _currentSpeed;

        // Caminho atual (quinas do NavMesh) e a próxima quina.
        private readonly List<Vector3> _corners = new List<Vector3>();
        private int _cornerIndex;
        private bool _goalIsHiding;
        private int _pauseLeft;
        private int _previousRoom = -1;

        private bool _fleeing;
        private int _fleeMemoryLeft;
        private float _replanLeft;

        // Depois da fuga: indo ao esconderijo (anda em pé) e parada nele (agachada, foge só com o monstro colado).
        private bool _goingToHide;
        private bool _hiddenAfterFlee;

        // Instalada num esconderijo de sala pouco vista (_settleRarity): só sai fugindo.
        private bool _settled;

        // Escondida agora: a de sempre (lição Escondida), a que se escondeu depois de fugir ou a instalada.
        private bool Concealed => _hides || _hiddenAfterFlee || _settled;

        // Números da partida: os do Inspector, ou os do jogador imitado (FreeHiderProfile, treino do diretor).
        private float _fleeRadiusNow, _fleeSightNow, _hiddenFleeRadiusNow, _hiddenFleeSightNow, _hiddenExitBonusNow;
        private float _fleeReplanNow, _hideChanceNow, _keySeekNow, _hideAfterFleeNow;
        private int _fleeCandidatesNow, _hideCandidatesNow;
        private float _pauseScale = 1f, _hideTimeScale = 1f, _sneakRadius, _keyThreatWeight;

        // Chaves que ela conhece: perto (KeyKnowRadius), vistas de perto, ou destacadas pelo diretor. Sem perfil, todas.
        private float _keyKnowRadius = float.MaxValue;
        private bool[] _knownKeys = new bool[0];
        private const float KeySightMeters = 15f;
        private bool _savesStamina;

        public FreeHiderProfile Profile { get; private set; }

        // Métricas do episódio (Hider/* no TensorBoard): conferir que ela foge e se esconde de verdade.
        private int _episodeSteps;
        private int _hiddenSteps;

        /// <summary>Fugas começadas no episódio.</summary>
        public int EpisodeFlees { get; private set; }

        /// <summary>Vezes que despistou e foi se esconder (achou esconderijo fora da vista do monstro).</summary>
        public int EpisodeHidesAfterFlee { get; private set; }

        /// <summary>Vezes que se instalou num esconderijo de sala pouco vista (fica até o monstro chegar).</summary>
        public int EpisodeSettles { get; private set; }

        /// <summary>Fração do episódio agachada num esconderijo (escondida ou depois da fuga), sem fugir.</summary>
        public float HiddenFraction => _episodeSteps > 0 ? (float)_hiddenSteps / _episodeSteps : 0f;

        /// <summary>O que ela está fazendo agora, para o painel de debug.</summary>
        public string StateLabel =>
            _fleeing ? (IsRunning ? "fugindo (correndo)" : "fugindo (sem fôlego)")
            : _goingToHide ? "indo se esconder"
            : Concealed && _corners.Count == 0 ? "escondida"
            : Concealed ? "escondida, trocando de esconderijo" : "passeando";

        // Criados na primeira consulta: o construtor do NavMeshPath é nativo e não deve rodar na serialização.
        private NavMeshPath _path;
        private NavMeshPath _probe;

        public bool IsActive => _mode != GraphHider.Mode.None;

        public Vector3 Position => transform.position;

        /// <summary>Correndo neste step (fugindo com estamina). Pegadas e animação.</summary>
        public bool IsRunning { get; private set; }

        /// <summary>Agachada (escondida e sem fugir): cápsula baixa, passo lento, pegada fraca (FreeFootprintTrail).</summary>
        public bool IsCrouching { get; private set; }

        public float StaminaFraction => _stamina.Fraction;

        /// <summary>Fôlego extra de uma vez (ajuda do diretor de jogo ao "jogador" do treino).</summary>
        public void BoostStamina(float fraction) => _stamina.Refill(fraction);

        // Sem grafo não há nó nem ping (o GraphPingSystem da v5 é que lia isto; a v8 não usa).
        public int CurrentNode => -1;

        public int ConsumeArrival() => -1;

        private void Awake()
        {
            _rigidbody = GetComponent<Rigidbody>();
            _capsule = GetComponent<CapsuleCollider>();
            if (_capsule != null)
            {
                _standHeight = _capsule.height;
                _standCenter = _capsule.center;
            }

            if (_animationSystem == null)
                _animationSystem = GetComponentInChildren<GraphHiderAnimationSystem>();
            if (_animationSystem != null)
                _animationSystem.Initialize();
        }

        /// <summary>O mapa (pontos, salas, portas), a parede e as chaves da arena. Antes do primeiro ResetEpisode.</summary>
        public void Configure(FreeMap map, LayerMask walls, FreeKeyField keys = null, FreeArenaController arena = null)
        {
            _map = map;
            _walls = walls;
            _keys = keys;
            _arena = arena;
        }

        private float RoomWeight(int room) => _arena != null ? _arena.HiderSpawnWeight(room) : 1f;

        // Quanto o caminho até goal passa pelo que o monstro vê sempre: média de (1 - raridade) dos pontos do FreeMap
        // a cada _exposureStep m (0 = só pontos que ele nunca vê, 1 = a ronda dele). Sem caminho = o pior.
        private float PathExposure(Vector3 goal)
        {
            if (_arena == null)
                return 0f;

            _probe ??= new NavMeshPath();
            if (!NavMesh.CalculatePath(Ground, goal, NavMesh.AllAreas, _probe) || _probe.status != NavMeshPathStatus.PathComplete)
                return float.MaxValue;

            Vector3[] corners = _probe.corners;
            float sum = 0f;
            int samples = 0;
            for (int i = 0; i + 1 < corners.Length; i++)
            {
                float length = Vector3.Distance(corners[i], corners[i + 1]);
                int steps = Mathf.Max(1, Mathf.CeilToInt(length / _exposureStep));
                for (int k = 0; k < steps; k++)
                {
                    int point = _map.NearestPoint(Vector3.Lerp(corners[i], corners[i + 1], (float)k / steps));
                    if (point < 0)
                        continue;
                    sum += 1f - _arena.PointRarity(point);
                    samples++;
                }
            }

            return samples > 0 ? sum / samples : 0f;
        }

        // Sala sorteada pelo peso da arena (as que o monstro menos vê pesam mais); sem arena, uniforme.
        private int PickSpawnRoom(int seekerRoom)
        {
            int rooms = _map.RoomCount;
            if (_arena == null)
                return Random.Range(0, rooms);

            float total = 0f;
            for (int r = 0; r < rooms; r++)
                if (r != seekerRoom || rooms == 1)
                    total += _arena.HiderSpawnWeight(r);

            float pick = Random.value * total;
            for (int r = 0; r < rooms; r++)
            {
                if (r == seekerRoom && rooms > 1)
                    continue;
                pick -= _arena.HiderSpawnWeight(r);
                if (pick <= 0f)
                    return r;
            }

            return Random.Range(0, rooms);
        }

        /// <summary>
        /// Chamar DEPOIS do spawn do monstro (nasce longe dele). Modo Nenhum desliga o objeto. Velocidade e estamina
        /// &lt;= 0 na lição usam as do Inspector.
        /// </summary>
        public void ResetEpisode(in FreeEpisodeSettings settings, Transform seeker)
        {
            _seeker = seeker;
            _mode = settings.HiderMode;
            _hides = settings.HiderHides;
            float staminaSeconds = settings.HiderStamina > 0f ? settings.HiderStamina : _stamina.DefaultSeconds;
            float skillStamina = settings.Profile.Active ? settings.Profile.StaminaScale : 1f;
            _stamina.Reset(staminaSeconds * settings.StaminaScale * skillStamina);
            _currentSpeed = (settings.HiderSpeed > 0f ? settings.HiderSpeed : _speed) * settings.SpeedScale;
            ApplyProfile(settings.Profile);
            IsRunning = false;
            _corners.Clear();
            _goalIsHiding = false;
            _pauseLeft = 0;
            _previousRoom = -1;
            _fleeing = false;
            _fleeMemoryLeft = 0;
            _replanLeft = 0f;
            _goingToHide = false;
            _hiddenAfterFlee = false;
            _settled = false;
            _episodeSteps = 0;
            _hiddenSteps = 0;
            EpisodeFlees = 0;
            EpisodeHidesAfterFlee = 0;
            EpisodeSettles = 0;
            if (_animationSystem != null)
                _animationSystem.ResetEpisode();

            if (_mode == GraphHider.Mode.None || _map == null || _map.RoomCount == 0)
            {
                Deactivate();
                return;
            }

            gameObject.SetActive(true);
            SetCrouching(_hides);
            Place(PickSpawn(seeker.position));

            // Escondida já começa parada no esconderijo do spawn.
            if (_hides)
            {
                _goalIsHiding = true;
                _pauseLeft = HiddenPauseSteps();
            }
        }

        private void ApplyProfile(in FreeHiderProfile profile)
        {
            Profile = profile;
            bool on = profile.Active;
            _fleeRadiusNow = _fleeRadius * (on ? profile.AwarenessScale : 1f);
            _fleeSightNow = _fleeSightDistance * (on ? profile.AwarenessScale : 1f);
            _hiddenFleeRadiusNow = _hiddenFleeRadius * (on ? profile.HiddenNerveScale : 1f);
            _hiddenFleeSightNow = _hiddenFleeSightDistance * (on ? profile.HiddenNerveScale : 1f);
            _hiddenExitBonusNow = _hiddenExitBonus * (on ? profile.EvasionScale : 1f);
            // Pontos e replanejamento limitados a 1.5x: com 40 arenas a 20x, caminho de NavMesh vira o gargalo da CPU.
            float search = on ? Mathf.Min(1.5f, profile.EvasionScale) : 1f;
            _fleeCandidatesNow = Mathf.Max(1, Mathf.RoundToInt(_fleeCandidates * search));
            _fleeReplanNow = _fleeReplanSeconds / Mathf.Max(0.5f, search);
            _hideCandidatesNow = Mathf.Max(1, Mathf.RoundToInt(_hideCandidates * (on ? profile.HideSearchScale : 1f)));
            _hideChanceNow = on ? profile.HideChance : _hideChance;
            _keySeekNow = on ? profile.KeySeekChance : _keySeekChance;
            _hideAfterFleeNow = on ? Mathf.Clamp01(profile.HideAfterFleeChance) : _hideAfterFleeChance;
            _pauseScale = on ? profile.PauseScale : 1f;
            _hideTimeScale = on ? profile.HideTimeScale : 1f;
            _sneakRadius = on ? profile.SneakRadius : 0f;
            _savesStamina = on && profile.SavesStamina;
            _keyThreatWeight = on ? profile.KeyThreatWeight : 0f;
            _keyKnowRadius = on ? profile.KeyKnowRadius : float.MaxValue;
            _stamina.RateScale = 1f;
            int keyCount = _keys != null ? _keys.Count : 0;
            if (_knownKeys.Length != keyCount)
                _knownKeys = new bool[keyCount];
            for (int k = 0; k < keyCount; k++)
                _knownKeys[k] = !on;
        }

        // Quem guarda fôlego (o jogador bom) só corre exposta ao monstro ou com ele perto, e não gasta a reserva longe.
        private bool WantsToRun(bool exposed)
        {
            if (!_savesStamina || _seeker == null)
                return true;

            Vector3 toSeeker = _seeker.position - transform.position;
            float distance = new Vector2(toSeeker.x, toSeeker.z).magnitude;
            if (distance < 6f)
                return true;
            if (_stamina.Fraction < 0.25f && distance > 10f)
                return false;
            return exposed;
        }

        // O monstro tem linha livre até ela, ao alcance da visão dele (sem cone: a cabeça vira rápido).
        private bool Exposed()
        {
            Vector3 toSeeker = _seeker.position - transform.position;
            return new Vector2(toSeeker.x, toSeeker.z).magnitude <= _seekerViewDistance && LineIsClear(transform.position, _seeker.position);
        }

        /// <summary>Diretor de jogo: taxa do fôlego (gasta e enche). 1 = sem diretor.</summary>
        public void SetStaminaRate(float scale) => _stamina.RateScale = scale;

        /// <summary>Ela já conhece todas as chaves que faltam (destacar não adianta).</summary>
        public bool KnowsAllRemainingKeys
        {
            get
            {
                if (_keys == null)
                    return true;
                for (int k = 0; k < _knownKeys.Length; k++)
                    if (!_knownKeys[k] && !_keys.IsTaken(k))
                        return false;
                return true;
            }
        }

        /// <summary>Destaque do diretor (no jogo, brilho e som na chave): passa a conhecer a chave mais perto que não conhecia.</summary>
        public bool RevealNearestKey()
        {
            if (_keys == null)
                return false;

            int best = -1;
            float bestDistance = float.MaxValue;
            for (int k = 0; k < _knownKeys.Length; k++)
            {
                if (_knownKeys[k] || _keys.IsTaken(k))
                    continue;
                float distance = Vector3.Distance(_keys.Get(k).Position, transform.position);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = k;
                }
            }

            if (best < 0)
                return false;
            _knownKeys[best] = true;
            return true;
        }

        private void UpdateKnownKeys()
        {
            if (_keys == null)
                return;

            Vector3 eye = transform.position + Vector3.up * _hiderChestHeight;
            for (int k = 0; k < _knownKeys.Length; k++)
            {
                if (_knownKeys[k] || _keys.IsTaken(k))
                    continue;
                Vector3 key = _keys.Get(k).Position;
                float distance = Vector3.Distance(key, transform.position);
                _knownKeys[k] = distance <= _keyKnowRadius
                                || (distance <= KeySightMeters
                                    && !Physics.Linecast(eye, key + Vector3.up * 0.5f, _walls, QueryTriggerInteraction.Ignore));
            }
        }

        private bool SeekerWithin(float meters)
        {
            Vector3 toSeeker = _seeker.position - transform.position;
            return new Vector2(toSeeker.x, toSeeker.z).magnitude <= meters;
        }

        /// <summary>Tira o hider de cena (modo de jogo: quem foge é o jogador).</summary>
        public void Deactivate()
        {
            _mode = GraphHider.Mode.None;
            IsRunning = false;
            SetCrouching(false);
            gameObject.SetActive(false);
        }

        private void FixedUpdate()
        {
            if (_mode == GraphHider.Mode.None)
                return;

            bool moved = _mode != GraphHider.Mode.Static && StepMovement();
            if (_animationSystem != null)
                _animationSystem.Tick(moved, moved && IsRunning);
        }

        // Um step de física. Devolve se o corpo andou (parado, pausado ou chegando = idle, para a animação).
        private bool StepMovement()
        {
            bool wasFleeing = _fleeing;
            UpdateFleeing();

            // Jogador bom: fora da vista do monstro se afasta sem correr (andando, ou agachado com ele ao alcance do
            // barulho do passo); só corre exposto ou com ele colado.
            bool exposed = _seeker != null && _fleeing && Exposed();

            // Antes da pausa: parado também recupera estamina.
            bool wantsRun = _fleeing && WantsToRun(exposed);
            IsRunning = _stamina.Step(wantsRun, Time.fixedDeltaTime);

            // Despistou: vai se esconder fora da vista dele (ou, sem sorte no sorteio, volta a passear).
            if (_fleeing)
            {
                if (!wasFleeing)
                    EpisodeFlees++;
                _goingToHide = false;
                _hiddenAfterFlee = false;
                _settled = false;
            }
            else if (wasFleeing && !_goingToHide && !_hides && Random.value < _hideAfterFleeNow)
            {
                StartHidingAfterFlee();
            }

            // Escondida fica agachada até ser achada; fugindo, levanta. Com o monstro perto, quem é cuidadosa anda
            // agachada (sem barulho, pegada fraca): passeando, indo se esconder, e fugindo fora da vista sem correr.
            bool sneaking = _sneakRadius > 0f && _seeker != null && SeekerWithin(_sneakRadius)
                            && (!_fleeing || (_savesStamina && !wantsRun));
            SetCrouching((Concealed && !_fleeing) || sneaking);
            _episodeSteps++;
            if (Concealed && !_fleeing && !_goingToHide)
                _hiddenSteps++;

            if (_episodeSteps % 25 == 0)
                UpdateKnownKeys();

            // Passou pela chave, pegou (fugindo também: no caminho, leva).
            if (_keys != null && _keys.Remaining > 0)
                _keys.TryTake(Ground, _keyPickupRadius);

            if (_fleeing)
            {
                // Começou a fugir agora: larga a pausa e o destino de passeio na hora.
                _replanLeft -= Time.fixedDeltaTime;
                if (!wasFleeing || _replanLeft <= 0f || _corners.Count == 0)
                {
                    _pauseLeft = 0;
                    PlanFlee();
                    _replanLeft = _fleeReplanNow;
                }
            }

            if (_pauseLeft > 0)
            {
                _pauseLeft--;
                return false;
            }

            if (_corners.Count == 0)
            {
                // Acabou o tempo no esconderijo de depois da fuga: levanta e volta a passear.
                _hiddenAfterFlee = false;
                ChooseWanderGoal();
                return false;
            }

            Vector3 corner = _corners[_cornerIndex] + Vector3.up * _heightOffset;
            Vector3 delta = corner - transform.position;
            delta.y = 0f;
            float distance = delta.magnitude;
            if (distance <= _cornerReached)
            {
                _cornerIndex++;
                if (_cornerIndex >= _corners.Count)
                    Arrive();
                return true;
            }

            float speed = IsRunning ? _currentSpeed : _currentSpeed * _walkFraction * (IsCrouching ? _crouchSpeedMultiplier : 1f);
            Vector3 step = delta / distance * Mathf.Min(distance, speed * Time.fixedDeltaTime);
            Vector3 next = transform.position + step;
            next.y = corner.y;
            Move(next, delta);
            return true;
        }

        private void Arrive()
        {
            _corners.Clear();
            if (_goingToHide)
            {
                _goingToHide = false;
                _hiddenAfterFlee = true;
                float seconds = _hideTimeScale * Random.Range(_hideAfterFleeMinSeconds, Mathf.Max(_hideAfterFleeMinSeconds, _hideAfterFleeMaxSeconds));
                _pauseLeft = Mathf.RoundToInt(seconds / Time.fixedDeltaTime);
                return;
            }

            _pauseLeft = _maxPauseSteps > 0 && !_fleeing ? Mathf.RoundToInt(Random.Range(0, _maxPauseSteps + 1) * _pauseScale) : 0;
            if (_goalIsHiding)
                _pauseLeft = _hides && !_fleeing ? HiddenPauseSteps() : Mathf.RoundToInt(_pauseLeft * _hidePauseMultiplier);

            // Esconderijo numa sala que o monstro quase não vê: fica até ele chegar (a fuga zera o _settled).
            if (_goalIsHiding && !_fleeing && _arena != null && _arena.RoomRarity(RoomAt(Ground)) >= _settleRarity)
            {
                if (!_settled)
                    EpisodeSettles++;
                _settled = true;
                _pauseLeft = int.MaxValue / 2;
            }
        }

        private int HiddenPauseSteps() =>
            Mathf.RoundToInt(_hideTimeScale * Random.Range(_hiddenPauseMinSeconds, Mathf.Max(_hiddenPauseMinSeconds, _hiddenPauseMaxSeconds)) / Time.fixedDeltaTime);

        // ================================================================================
        // Destinos
        // ================================================================================

        // Andando: a sala atual ou uma vizinha (evitando voltar à de onde veio), e nela um ponto ou um esconderijo.
        private void ChooseWanderGoal()
        {
            // Com chaves no mapa, a maior parte dos destinos é a chave mais perto.
            if (_keys != null && _keys.Remaining > 0 && Random.value < _keySeekNow
                && _keys.TrySafestRemaining(Ground, _seeker != null ? _seeker.position : Ground, _keyThreatWeight,
                                            _probe ??= new NavMeshPath(), out Vector3 key, _knownKeys))
            {
                _goalIsHiding = false;
                if (SetPathTo(key))
                    return;
            }

            int room = RoomAt(Ground);
            if (room < 0)
                return;

            // A sala (esta ou uma vizinha, sem voltar à de onde veio) sai pelo peso da arena: as que o monstro menos vê
            // primeiro, passeando ou indo se esconder. Entre _wanderCandidates destinos, o de caminho menos exposto
            // (fora da ronda dele).
            _goalIsHiding = _hides || Random.value < _hideChanceNow;
            int target = room;
            Vector3 goal = Vector3.zero;
            float bestExposure = float.MaxValue;
            for (int c = 0; c < Mathf.Max(1, _wanderCandidates); c++)
            {
                int candidate = room;
                float weightSum = RoomWeight(room);
                foreach (int door in _map.DoorsOfRoom(room))
                {
                    int other = _map.GetDoor(door).OtherRoom(room);
                    if (other < 0 || other == _previousRoom)
                        continue;

                    float weight = RoomWeight(other);
                    weightSum += weight;
                    if (Random.value * weightSum < weight)
                        candidate = other;
                }

                Vector3 spot = _goalIsHiding ? HidingSpot(candidate) : RandomSpot(candidate);
                float exposure = _wanderCandidates > 1 ? PathExposure(spot) : 0f;
                if (exposure < bestExposure)
                {
                    bestExposure = exposure;
                    target = candidate;
                    goal = spot;
                }
            }

            if (target != room)
                _previousRoom = room;

            if (!SetPathTo(goal))
                _goalIsHiding = false;
        }

        // Depois da fuga: um esconderijo da sala atual ou de uma vizinha que o monstro NÃO vê de onde está. Nenhum fora
        // da vista = segue passeando (esconder-se à vista dele não adianta).
        private void StartHidingAfterFlee()
        {
            int room = RoomAt(Ground);
            if (room < 0 || _seeker == null)
                return;

            // Esta sala e as vizinhas, a que o monstro MENOS vê primeiro (peso da arena; empate = sorteio).
            var rooms = new List<int> { room };
            foreach (int door in _map.DoorsOfRoom(room))
            {
                int other = _map.GetDoor(door).OtherRoom(room);
                if (other >= 0 && !rooms.Contains(other))
                    rooms.Add(other);
            }

            var order = rooms.Select(r => (room: r, key: RoomWeight(r) + Random.value * 0.01f))
                .OrderByDescending(c => c.key).Select(c => c.room).ToList();
            for (int k = 0; k < _hideAfterFleeTries; k++)
            {
                int target = order[k % order.Count];
                Vector3 spot = HidingSpot(target);
                if (SeekerCanSee(spot) || !SetPathTo(spot))
                    continue;

                _pauseLeft = 0;
                _goalIsHiding = true;
                _goingToHide = true;
                EpisodeHidesAfterFlee++;
                return;
            }
        }

        // Fugindo: entre pontos sorteados em volta, o mais longe DO MONSTRO pelo NavMesh (+ bônus se ele não vê), sem
        // caminho que comece correndo na direção dele.
        private void PlanFlee()
        {
            if (_seeker == null)
                return;

            Vector3 here = Ground;
            if (!TrySampleGround(_seeker.position, 2f, out Vector3 seekerGround))
                return;

            Vector3 toSeeker = seekerGround - here;
            toSeeker.y = 0f;
            float seekerDistance = toSeeker.magnitude;
            Vector3 seekerDirection = seekerDistance > 1e-3f ? toSeeker / seekerDistance : Vector3.zero;

            float bestScore = float.MinValue;
            Vector3 best = here;
            bool found = false;
            for (int k = 0; k < _fleeCandidatesNow; k++)
            {
                float angle = Random.Range(0f, Mathf.PI * 2f);
                float radius = Random.Range(_fleeMinDistance, Mathf.Max(_fleeMinDistance, _fleeMaxDistance));
                Vector3 probe = here + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
                if (!TrySampleGround(probe, 2f, out Vector3 candidate))
                    continue;

                if (!TryPath(here, candidate, _probe ??= new NavMeshPath(), out float travel, out Vector3 firstLeg)
                    || travel > _fleeMaxDistance * 2f)
                    continue;

                // Primeira perna na direção do monstro, com ele perto: correria para os braços dele.
                if (seekerDistance < _fleeRadiusNow * 1.5f && Vector3.Dot(firstLeg, seekerDirection) > 0.5f)
                    continue;

                if (!TryPath(seekerGround, candidate, _probe, out float fromSeeker, out _))
                    fromSeeker = 0f;

                float score = fromSeeker + (SeekerCanSee(candidate) ? 0f : _hiddenExitBonusNow);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                    found = true;
                }
            }

            if (found)
            {
                _goalIsHiding = false;
                SetPathTo(best);
            }
        }

        // Ponto do chão de uma sala: um ponto da grade do mapa, deslocado até _spotSpread e encaixado no NavMesh.
        private Vector3 RandomSpot(int room)
        {
            IReadOnlyList<int> points = _map.RoomPoints(room);
            Vector3 grid = _map.PointGround(points[Random.Range(0, points.Count)]);
            Vector3 probe = grid + new Vector3(Random.Range(-_spotSpread, _spotSpread), 0f, Random.Range(-_spotSpread, _spotSpread));
            return TrySampleGround(probe, _spotSpread, out Vector3 spot) && RoomAt(spot) == room ? spot : grid;
        }

        // Ponto da sala sorteado com peso _hideRareFloor + raridade (os que o monstro menos vê saem mais), espalhado
        // _spotSpread em volta. Sem arena, igual ao RandomSpot.
        private Vector3 RareSpot(int room, out float rarity)
        {
            IReadOnlyList<int> points = _map.RoomPoints(room);
            int chosen = points[Random.Range(0, points.Count)];
            if (_arena != null)
            {
                float total = 0f;
                foreach (int p in points)
                    total += _hideRareFloor + _arena.PointRarity(p);
                float pick = Random.value * total;
                foreach (int p in points)
                {
                    pick -= _hideRareFloor + _arena.PointRarity(p);
                    if (pick <= 0f)
                    {
                        chosen = p;
                        break;
                    }
                }
            }

            rarity = _arena != null ? _arena.PointRarity(chosen) : 0f;
            Vector3 grid = _map.PointGround(chosen);
            Vector3 probe = grid + new Vector3(Random.Range(-_spotSpread, _spotSpread), 0f, Random.Range(-_spotSpread, _spotSpread));
            return TrySampleGround(probe, _spotSpread, out Vector3 spot) && RoomAt(spot) == room ? spot : grid;
        }

        // Esconderijo: entre _hideCandidates pontos da sala (puxando para os que o monstro menos vê), o visto pelo menor
        // número de portas dela; depois o MENOS VISTO pelo monstro nos últimos episódios (_hideRareScore) e o mais colado
        // em parede (canto, atrás de móvel).
        private Vector3 HidingSpot(int room)
        {
            IReadOnlyList<int> doors = _map.DoorsOfRoom(room);
            Vector3 best = Vector3.zero;
            float bestScore = float.MaxValue;
            for (int k = 0; k < _hideCandidatesNow; k++)
            {
                Vector3 spot = RareSpot(room, out float rarity);
                int seen = 0;
                foreach (int door in doors)
                {
                    Vector3 eye = _map.GetDoor(door).Ground + Vector3.up * _doorEyeHeight;
                    if (!Physics.Linecast(eye, spot + Vector3.up * _hiderChestHeight, _walls, QueryTriggerInteraction.Ignore))
                        seen++;
                }

                float edge = NavMesh.FindClosestEdge(spot, out NavMeshHit hit, NavMesh.AllAreas) ? hit.distance : 5f;
                float score = seen * 100f + _hideRareScore * (1f - rarity) + edge;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = spot;
                }
            }

            return best;
        }

        // Spawn: sala diferente da do monstro, sorteada pelo peso da arena (as que ele menos vê pesam mais, 14/10),
        // esconderijo, a >= _minSpawnDistanceMeters dele pelo NavMesh; mapa pequeno demais aceita o mais longe sorteado.
        private Vector3 PickSpawn(Vector3 seekerPosition)
        {
            TrySampleGround(seekerPosition, 2f, out Vector3 seekerGround);
            int seekerRoom = RoomAt(seekerGround);
            Vector3 fallback = Vector3.zero;
            float fallbackDistance = -1f;

            for (int attempt = 0; attempt < _map.RoomCount * 2; attempt++)
            {
                int room = PickSpawnRoom(seekerRoom);
                if (room == seekerRoom && _map.RoomCount > 1)
                    continue;

                Vector3 spot = HidingSpot(room);
                if (!TryPath(seekerGround, spot, _probe ??= new NavMeshPath(), out float distance, out _))
                    continue;

                if (distance >= _minSpawnDistanceMeters)
                    return spot;

                if (distance > fallbackDistance)
                {
                    fallbackDistance = distance;
                    fallback = spot;
                }
            }

            return fallbackDistance >= 0f ? fallback : HidingSpot(Random.Range(0, _map.RoomCount));
        }

        // ================================================================================
        // Fuga: perto ou à vista
        // ================================================================================

        private void UpdateFleeing()
        {
            if (_mode != GraphHider.Mode.Flee || _seeker == null)
            {
                _fleeing = false;
                return;
            }

            if (SeekerIsNear() || SeesSeeker())
                _fleeMemoryLeft = Mathf.RoundToInt(_fleeMemorySeconds / Time.fixedDeltaTime);
            else if (_fleeMemoryLeft > 0)
                _fleeMemoryLeft--;

            _fleeing = _fleeMemoryLeft > 0;
        }

        private bool SeekerIsNear()
        {
            Vector3 toSeeker = _seeker.position - transform.position;
            bool hidden = Concealed;
            return new Vector2(toSeeker.x, toSeeker.z).magnitude <= (hidden ? _hiddenFleeRadiusNow : _fleeRadiusNow);
        }

        private bool SeesSeeker()
        {
            float sight = Concealed ? _hiddenFleeSightNow : _fleeSightNow;
            if (sight <= 0f)
                return false;

            Vector3 toSeeker = _seeker.position - transform.position;
            return new Vector2(toSeeker.x, toSeeker.z).magnitude <= sight && LineIsClear(transform.position, _seeker.position);
        }

        // O monstro veria este ponto se olhasse para ele (sem cone: a cabeça dele vira rápido)?
        private bool SeekerCanSee(Vector3 point)
        {
            Vector3 toPoint = point - _seeker.position;
            return new Vector2(toPoint.x, toPoint.z).magnitude <= _seekerViewDistance && LineIsClear(point, _seeker.position);
        }

        // Linha livre de parede entre dois pontos, na altura do olho do monstro sobre o X/Z de cada um.
        private bool LineIsClear(Vector3 a, Vector3 b)
        {
            if (_walls.value == 0)
                return true;

            float eyeY = _seeker.position.y + _seekerEyeHeight;
            Vector3 from = new Vector3(a.x, eyeY, a.z);
            Vector3 ray = new Vector3(b.x, eyeY, b.z) - from;
            float length = ray.magnitude;
            return length < 1e-3f || !Physics.Raycast(from, ray / length, length, _walls, QueryTriggerInteraction.Ignore);
        }

        // Agacha encolhendo a cápsula com a base parada no chão. É ela que a visão do monstro mira (cabeça e peito).
        private void SetCrouching(bool crouching)
        {
            if (IsCrouching == crouching)
                return;

            IsCrouching = crouching;
            if (_capsule == null)
                return;

            float height = crouching ? _standHeight * _crouchHeightFraction : _standHeight;
            _capsule.height = height;
            _capsule.center = _standCenter - Vector3.up * ((_standHeight - height) * 0.5f);
        }

        // ================================================================================
        // NavMesh
        // ================================================================================

        // O pivô no NavMesh (no piso).
        private Vector3 Ground =>
            TrySampleGround(transform.position - Vector3.up * _heightOffset, 2f, out Vector3 ground) ? ground : transform.position;

        private int RoomAt(Vector3 ground)
        {
            int point = _map.NearestPoint(ground);
            return point >= 0 ? _map.RoomOfPoint(point) : -1;
        }

        private static bool TrySampleGround(Vector3 position, float radius, out Vector3 ground)
        {
            bool ok = NavMesh.SamplePosition(position, out NavMeshHit hit, radius, NavMesh.AllAreas);
            ground = ok ? hit.position : position;
            return ok;
        }

        private bool SetPathTo(Vector3 goal)
        {
            _corners.Clear();
            _cornerIndex = 0;
            _path ??= new NavMeshPath();
            if (!NavMesh.CalculatePath(Ground, goal, NavMesh.AllAreas, _path) || _path.status != NavMeshPathStatus.PathComplete)
                return false;

            _corners.AddRange(_path.corners);
            // A quina 0 é onde ele já está.
            _cornerIndex = _corners.Count > 1 ? 1 : 0;
            return _corners.Count > 0;
        }

        // Comprimento (m) do caminho completo e a direção planar da primeira perna.
        private static bool TryPath(Vector3 from, Vector3 to, NavMeshPath path, out float length, out Vector3 firstLeg)
        {
            length = 0f;
            firstLeg = Vector3.zero;
            if (!NavMesh.CalculatePath(from, to, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
                return false;

            Vector3[] corners = path.corners;
            for (int i = 1; i < corners.Length; i++)
                length += Vector3.Distance(corners[i - 1], corners[i]);

            if (corners.Length > 1)
            {
                Vector3 leg = corners[1] - corners[0];
                leg.y = 0f;
                firstLeg = leg.sqrMagnitude > 1e-6f ? leg.normalized : Vector3.zero;
            }

            return true;
        }

        private void Place(Vector3 ground)
        {
            Vector3 position = ground + Vector3.up * _heightOffset;
            if (_rigidbody != null)
            {
                _rigidbody.position = position;
                // Cinemático não tem velocidade, e o Unity 6 avisa a cada reset se zerar.
                if (!_rigidbody.isKinematic)
                    _rigidbody.linearVelocity = Vector3.zero;
            }

            transform.position = position;
        }

        private void Move(Vector3 position, Vector3 facing)
        {
            Quaternion rotation = facing.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(facing.normalized, Vector3.up) : transform.rotation;
            if (_rigidbody != null)
            {
                _rigidbody.MovePosition(position);
                _rigidbody.MoveRotation(rotation);
            }
            else
            {
                transform.SetPositionAndRotation(position, rotation);
            }
        }

        // Gizmo verde-azulado escuro: o caminho que falta (vermelho fugindo).
        private void OnDrawGizmos()
        {
            if (!Application.isPlaying || _mode == GraphHider.Mode.None || _corners.Count == 0)
                return;

            Gizmos.color = _fleeing ? new Color(0.9f, 0.2f, 0.2f, 0.9f) : new Color(0f, 0.55f, 0.5f, 0.9f);
            Vector3 lift = Vector3.up * 0.3f;
            Vector3 from = transform.position + lift;
            for (int i = _cornerIndex; i < _corners.Count; i++)
            {
                Gizmos.DrawLine(from, _corners[i] + lift);
                from = _corners[i] + lift;
            }
        }
    }
}
