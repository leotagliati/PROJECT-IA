using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Assets.Scripts.Seeker
{
    public class SeekerDebugOverlay : MonoBehaviour
    {
        [Tooltip("Vazio: o primeiro SeekerManager da cena. Tab alterna entre os existentes.")]
        [SerializeField] private SeekerManager _target;

        [SerializeField] private bool _visible = false;
        [SerializeField] private Key _toggleKey = Key.F3;
        [SerializeField] private Key _cycleKey = Key.Tab;

        [Tooltip("Desenha raios, ação e última posição no mundo (Debug.DrawRay: precisa de Gizmos ligado no Game view).")]
        [SerializeField] private bool _drawInWorld = true;

        [Header("-----Limiares-----")]
        [Tooltip("Steps na janela da média móvel de recompensa.")]
        [SerializeField, Min(1)] private int _rollingWindow = 200;

        [Tooltip("Custo acumulado de CONTATO com parede no episódio a partir do qual fica vermelho. Amarelo a partir da metade.")]
        [SerializeField] private float _wallContactBad = -0.5f;

        [Tooltip("Fração do episódio sem captura a partir da qual o contador de steps fica vermelho (só treino).")]
        [SerializeField, Range(0f, 1f)] private float _lateEpisodeFraction = 0.8f;

        [Tooltip("Magnitude de ação abaixo disto conta como parado.")]
        [SerializeField, Range(0f, 1f)] private float _idleActionMagnitude = 0.1f;

        [Tooltip("Eficiência de movimento (real ÷ pedido) abaixo disto = preso.")]
        [SerializeField, Range(0f, 1f)] private float _stuckEfficiency = 0.3f;

        [Tooltip("Steps mostrados na faixa de histórico.")]
        [SerializeField, Range(8, 128)] private int _timelineSteps = 64;

        private static readonly Color Good = new(0.35f, 0.9f, 0.4f);
        private static readonly Color Warn = new(1f, 0.85f, 0.3f);
        private static readonly Color Bad = new(1f, 0.35f, 0.35f);
        private static readonly Color Neutral = new(0.8f, 0.8f, 0.8f);
        private static readonly Color Dim = new(0.55f, 0.55f, 0.55f);
        private static readonly Color Info = new(0.45f, 0.75f, 1f);

        // Mesma ordem de SeekerPerceptionSystem.Directions.
        private static readonly string[] CompassLabels = { "N ", "NE", "E ", "SE", "S ", "SW", "W ", "NW" };

        private readonly StringBuilder _text = new(2048);
        private SeekerManager[] _all = new SeekerManager[0];
        private GUIStyle _style;
        private GUIStyle _box;

        private void Update()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard[_toggleKey].wasPressedThisFrame)
                    _visible = !_visible;

                if (_visible && keyboard[_cycleKey].wasPressedThisFrame)
                    CycleTarget();
            }

            if (_visible && _drawInWorld && _target != null)
                DrawInWorld();
        }

        private void OnGUI()
        {
            if (!_visible)
                return;

            EnsureStyles();

            if (_target == null)
                ResolveTarget();

            _text.Clear();

            if (_target == null)
            {
                _text.Append(Colored("Nenhum SeekerManager na cena.", Bad));
            }
            else
            {
                AppendHeader();
                AppendObservation();
                AppendAction();
                AppendWallDiagnosis();
                AppendRewards();
                AppendTimeline();
            }

            GUIContent content = new(_text.ToString());
            float width = 440f;
            float height = _style.CalcHeight(content, width - 16f) + 16f;

            GUI.Box(new Rect(10f, 10f, width, height), GUIContent.none, _box);
            GUI.Label(new Rect(18f, 18f, width - 16f, height - 16f), content, _style);
        }

        // ------------------------------------------------------------------ seções

        private void AppendHeader()
        {
            SeekerManager m = _target;

            Line($"<b>{m.name}</b>", Neutral);
            Line($"modo {m.Mode}   caçando {Flag(m.IsHunting)}   episódios {m.CompletedEpisodes}", Dim);

            // No treino um episódio longo sem captura é o próprio sinal de "não está achando".
            // No jogo não há fim de episódio, então o contador é só informativo.
            if (m.Mode == SeekerMode.Training && m.MaxEpisodeSteps > 0)
            {
                float fraction = (float)m.ElapsedSteps / m.MaxEpisodeSteps;
                Color color = fraction >= _lateEpisodeFraction ? Bad : fraction >= _lateEpisodeFraction * 0.5f ? Warn : Good;
                Line($"step {m.ElapsedSteps} / {m.MaxEpisodeSteps}  ({fraction:P0})", color);
            }
            else
            {
                Line($"step {m.ElapsedSteps}", Dim);
            }

            if (m.Arena != null)
            {
                // WallProximityScale < 1 é o sorteio de labirinto da arena (ver ApplyCurriculum).
                bool maze = m.Arena.WallProximityScale < 1f || m.Arena.ApproachRewardScale < 1f;
                Line($"lição {(maze ? "labirinto" : "sala aberta")}   approach ×{m.Arena.ApproachRewardScale:0.00}   parede ×{m.Arena.WallProximityScale:0.00}", Dim);
            }

            _text.AppendLine();
        }

        /// <summary>
        /// O vetor de observação como a rede recebe, na mesma ordem do CollectObservations: os 8
        /// raios, as duas flags, o vetor até a última posição e a janela 5x5. Se o comportamento
        /// não bate com o que está aqui, o problema é da política; se o que está aqui não bate
        /// com o mundo, é da percepção.
        /// </summary>
        private void AppendObservation()
        {
            SeekerPerceptionSystem p = _target.Perception;
            if (p == null)
                return;

            Line("<b>Observação</b> (o que a rede vê)", Neutral);

            if (!p.VisionEnabled)
                Line("visão desligada (fora de GameState.Playing)", Warn);

            // Raios de parede: 0 livre, 1 encostado. Vermelho a partir do limiar em que a
            // recompensa começa a punir — o mesmo número que o agente "deveria" temer.
            float danger = _target.Rewards != null ? _target.Rewards.WallDangerThreshold : 0.6f;
            float[] rays = p.WallProximities;

            _text.Append(Colored("paredes ", Dim));
            for (int i = 0; i < rays.Length && i < CompassLabels.Length; i++)
            {
                float value = rays[i];
                Color color = value >= danger ? Bad : value >= danger * 0.75f ? Warn : value > 0f ? Neutral : Dim;
                _text.Append(Colored($"{CompassLabels[i]}{value:0.00} ", color));
            }
            _text.AppendLine();

            Line($"vendo {Flag(p.IsSeeingHider)}   já viu {Flag(p.HasSeenHider)}", p.IsSeeingHider ? Good : p.HasSeenHider ? Warn : Dim);

            if (p.HasSeenHider)
            {
                Vector3 to = p.LastKnownHiderPosition - _target.transform.position;
                Vector2 planar = new(to.x, to.z);
                float distance = planar.magnitude;
                Vector2 unit = distance > 1e-4f ? planar / distance : Vector2.zero;
                Line($"hider: dir ({unit.x:+0.00;-0.00}, {unit.y:+0.00;-0.00})  dist {distance:0.0} m  obs {Mathf.Clamp01(distance / _target.MaxHiderDistance):0.000}", p.IsSeeingHider ? Good : Warn);
            }
            else
            {
                Line("hider: sem pista (obs zeradas) — patrulhando", Dim);
            }

            SeekerExplorationMemory mem = _target.Exploration;
            if (mem != null)
            {
                if (mem.HasFrontier)
                {
                    Vector3 step = mem.FrontierStepDirectionWorld;
                    Line($"fronteira: passo ({step.x:+0.00;-0.00}, {step.z:+0.00;-0.00}) {CompassLabels[CompassIndex(new Vector2(step.x, step.z))].Trim()}  a {mem.FrontierDistanceCells} células  obs {mem.FrontierDistanceNormalized:0.000}", Info);
                }
                else
                {
                    Line("fronteira: nenhuma célula livre por visitar (obs zeradas)", Warn);
                }
            }

            if (_target.Chase != null)
                Line($"perseguição {Flag(_target.Chase.IsChasing)}   blend {_target.Chase.Blend:0.00}   (cosmético, não é observação)", Dim);

            AppendWindow();

            _text.AppendLine();
        }

        /// <summary>
        /// Janela 5x5 desenhada com o norte (+Z) em cima e o agente no centro. '·' é célula que a
        /// rede vê como NÃO visitada — é isso que a recompensa de exploração puxa. A janela não
        /// sabe de parede: uma célula '·' pode estar do outro lado dela.
        /// </summary>
        private void AppendWindow()
        {
            SeekerExplorationMemory e = _target.Exploration;
            if (e == null || e.Window == null)
                return;

            float[] window = e.Window;
            int side = Mathf.RoundToInt(Mathf.Sqrt(window.Length));
            int radius = side / 2;
            int unvisited = 0;

            Line($"janela {side}x{side} ('·' não visitada, '█' visitada/fora, '#' parede, 'A' agente)   visitadas {e.VisitedCellCount}/{e.CellCount} livres", Dim);

            // FillWindow percorre dz de -r..r (linha) e dx de -r..r (coluna). Linha 0 é o sul.
            for (int dz = radius; dz >= -radius; dz--)
            {
                _text.Append("   ");
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int index = (dz + radius) * side + (dx + radius);
                    bool visited = window[index] > 0.5f;

                    if (dx == 0 && dz == 0)
                        _text.Append(Colored("A ", Info));
                    else if (e.IsWindowCellBlocked(index))
                        _text.Append(Colored("# ", Bad));
                    else if (visited)
                        _text.Append(Colored("█ ", Dim));
                    else
                    {
                        _text.Append(Colored("· ", Good));
                        unvisited++;
                    }
                }
                _text.AppendLine();
            }

            Line($"   {unvisited} não visitadas na janela", unvisited > 0 ? Neutral : Dim);
        }

        private void AppendAction()
        {
            Vector2 action = _target.LastAction;
            float magnitude = action.magnitude;

            Line("<b>Ação</b>", Neutral);

            // Parado não é necessariamente ruim (fim de episódio, fora da caça), então é só
            // amarelo; mas parado COM pista de hider é o agente ignorando o que sabe.
            bool idle = magnitude < _idleActionMagnitude;
            bool hasLead = _target.Perception != null && _target.Perception.HasSeenHider;
            Color color = idle ? (hasLead ? Bad : Warn) : Good;

            Line($"x {action.x:+0.00;-0.00}   z {action.y:+0.00;-0.00}   |a| {magnitude:0.00}  {(idle ? "parado" : CompassLabels[CompassIndex(action)].Trim())}", color);

            float efficiency = _target.MovementEfficiency;
            Color efficiencyColor = idle ? Dim : efficiency < _stuckEfficiency ? Bad : efficiency < 0.7f ? Warn : Good;
            Line($"eficiência de movimento {efficiency:P0}  (real ÷ pedido; baixo = empurrando algo)", efficiencyColor);

            _text.AppendLine();
        }

        /// <summary>
        /// Cruza ação, raios, janela e hider para dizer o que está puxando o agente para a parede.
        /// </summary>
        private void AppendWallDiagnosis()
        {
            SeekerPerceptionSystem p = _target.Perception;
            SeekerRewardSystem r = _target.Rewards;
            if (p == null || r == null)
                return;

            Line("<b>Diagnóstico de parede</b>", Neutral);

            Vector2 action = _target.LastAction;
            bool idle = action.magnitude < _idleActionMagnitude;
            float danger = r.WallDangerThreshold;

            // 1. A ação aponta para uma parede que a rede enxerga?
            if (!idle)
            {
                int index = CompassIndex(action);
                float ahead = p.WallProximities[index];
                Line($"parede na direção da ação ({CompassLabels[index].Trim()}): {ahead:0.00}",
                    ahead >= danger ? Bad : ahead >= danger * 0.75f ? Warn : Good);
            }

            // 2. O que está puxando para lá: células não visitadas no semiplano da ação, e/ou o
            //    hider naquele lado. Os dois são legítimos para a política — o problema é quando
            //    há parede no meio, que a observação não relaciona com nenhum dos dois.
            if (!idle && _target.Exploration != null && _target.Exploration.Window != null)
            {
                int aheadCells = 0, behindCells = 0;
                CountUnvisited(_target.Exploration.Window, action, ref aheadCells, ref behindCells);
                Line($"células não visitadas: {aheadCells} no lado da ação, {behindCells} no oposto",
                    aheadCells > behindCells ? Info : Dim);
            }

            if (!idle && p.HasSeenHider)
            {
                Vector3 to = p.LastKnownHiderPosition - _target.transform.position;
                Vector2 planar = new(to.x, to.z);
                float alignment = planar.sqrMagnitude > 1e-6f ? Vector2.Dot(planar.normalized, action.normalized) : 0f;
                Line($"hider está {(alignment > 0.5f ? "no lado da ação" : alignment < -0.5f ? "no lado oposto" : "de lado")} (cos {alignment:+0.00;-0.00})",
                    alignment > 0.5f ? Info : Dim);
            }

            // 2b. As penalidades de intenção e de travamento, como o reward system as vê.
            if (r.LastStep.BlockedHeading < 0f)
                Line("ação aponta para célula bloqueada (pagando rumo bloqueado)", Bad);

            Line($"deslocamento líquido em {r.StuckWindowSteps} steps: {_target.RecentNetDisplacementForDebug:0.00} m  (travado abaixo de {r.StuckDistance:0.0})",
                r.LastStep.Stuck < 0f ? Bad : Good);

            // 3. Encostar está sendo pago?
            int steps = Mathf.Max(1, _target.ElapsedSteps);
            float touchingFraction = (float)r.StepsTouchingWall / steps;
            SeekerRewardBreakdown wt = r.WhileTouchingWall;

            Line($"encostado em {r.StepsTouchingWall} steps ({touchingFraction:P0} do episódio)",
                touchingFraction > 0.25f ? Bad : touchingFraction > 0.1f ? Warn : Good);

            if (r.StepsTouchingWall > 0)
            {
                float net = wt.Total - wt.Existential;
                Line($"saldo nesses steps (sem existencial) {net:+0.000;-0.000}:  contato {wt.WallContact:+0.000;-0.000}  aprox {wt.Approach:+0.000;-0.000}  célula {wt.NewCell:+0.000;-0.000}",
                    net > 0f ? Bad : Warn);

                if (net > 0f)
                    Line("→ encostar está sendo RECOMPENSADO: aproximação/célula nova pagam mais que o contato custa", Bad);
                else if (wt.Approach > 0f)
                    Line("→ aproximação positiva encostado: o alvo está atrás da parede (distância euclidiana)", Warn);
            }

            _text.AppendLine();
        }

        private void AppendRewards()
        {
            SeekerRewardSystem r = _target.Rewards;
            if (r == null)
                return;

            SeekerRewardBreakdown last = r.LastStep;
            SeekerRewardBreakdown total = r.EpisodeTotal;

            Line("<b>Recompensa</b>", Neutral);

            // O acumulado sozinho engana: ele começa em zero e cai pela pressão existencial
            // mesmo com o agente fazendo tudo certo. O que diz se o episódio está rendendo é
            // o que sobrou DEPOIS de pagar o existencial.
            float cumulative = _target.GetCumulativeReward();
            float earned = cumulative - total.Existential;
            Line($"acumulado {cumulative:+0.000;-0.000}   líquido s/ existencial {earned:+0.000;-0.000}", Sign(earned));

            int window = Mathf.Min(_rollingWindow, r.StepsRecorded);
            float average = r.RecentAverage(_rollingWindow);
            float existentialPerStep = last.Existential;

            // Média acima de zero: o shaping está pagando mais que o existencial cobra. Entre
            // zero e o existencial puro: andando sem ganhar nada. Abaixo: parede.
            Color averageColor = average > 0f ? Good : average >= existentialPerStep ? Warn : Bad;
            Line($"média últimos {window} steps {average * 1000f:+0.00;-0.00} ‰", averageColor);

            _text.AppendLine();
            Line("<i>termo            step         episódio</i>", Dim);

            Term("existencial", last.Existential, total.Existential, Dim);
            Term("proximidade", last.WallProximity, total.WallProximity, last.WallProximity < 0f ? Warn : Dim);
            Term("contato", last.WallContact, total.WallContact,
                total.WallContact <= _wallContactBad ? Bad : total.WallContact <= _wallContactBad * 0.5f ? Warn : Dim);
            Term("célula nova", last.NewCell, total.NewCell, last.NewCell > 0f ? Good : Dim);
            Term("avistamento", last.Sight, total.Sight, last.Sight > 0f ? Good : Dim);
            Term("aproximação", last.Approach, total.Approach, last.Approach > 0f ? Good : last.Approach < 0f ? Bad : Dim);
            Term("rumo bloqueado", last.BlockedHeading, total.BlockedHeading, last.BlockedHeading < 0f ? Bad : Dim);
            Term("travado", last.Stuck, total.Stuck, last.Stuck < 0f ? Bad : Dim);
            Term("fronteira", last.FrontierApproach, total.FrontierApproach, last.FrontierApproach > 0f ? Good : last.FrontierApproach < 0f ? Bad : Dim);
            Term("total", last.Total, total.Total, Sign(last.Total));

            _text.AppendLine();
        }

        /// <summary>
        /// Últimos N steps, do mais antigo ao mais recente: ▲ recompensa positiva, ▼ abaixo do
        /// existencial (parede), · só existencial; em vermelho quando encostado. Dá o contexto
        /// temporal que os totais escondem — "bateu, e AÍ a recompensa subiu".
        /// </summary>
        private void AppendTimeline()
        {
            SeekerRewardSystem r = _target.Rewards;
            if (r == null)
                return;

            float existential = r.LastStep.Existential;
            int count = Mathf.Min(_timelineSteps, r.StepsRecorded);

            Line($"<b>Últimos {count} steps</b>  (▲ ganhou  · só existencial  ▼ pagou; vermelho = encostado)", Neutral);
            _text.Append("   ");

            for (int stepsAgo = count; stepsAgo >= 1; stepsAgo--)
            {
                if (!r.TryGetHistory(stepsAgo, out float total, out bool touching))
                    continue;

                string glyph = total > 0f ? "▲" : total < existential - 1e-6f ? "▼" : "·";
                Color color = touching ? Bad : total > 0f ? Good : total < existential - 1e-6f ? Warn : Dim;
                _text.Append(Colored(glyph, color));
            }

            _text.AppendLine();
        }

        // ------------------------------------------------------------------ mundo

        private void DrawInWorld()
        {
            SeekerPerceptionSystem p = _target.Perception;
            if (p == null)
                return;

            Vector3 origin = p.RayOrigin;
            float danger = _target.Rewards != null ? _target.Rewards.WallDangerThreshold : 0.6f;

            // Raios de parede, no comprimento que a rede lê (proximidade × alcance).
            float[] rays = p.WallProximities;
            for (int i = 0; i < rays.Length; i++)
            {
                float value = rays[i];
                Color color = value >= danger ? Bad : value > 0f ? Warn : Dim;
                float length = value > 0f ? (1f - value) * p.DetectionRange : p.DetectionRange;
                Debug.DrawRay(origin, SeekerPerceptionSystem.GetDirection(i) * length, color);
            }

            // Cone de visão.
            Color cone = p.IsSeeingHider ? Good : new Color(0.5f, 0.5f, 0.5f, 0.4f);
            for (int i = 0; i < p.RayCount; i++)
                Debug.DrawRay(origin, p.GetVisionRayDirection(i) * p.VisionRange, cone);

            // Ação: para onde a política mandou, com o comprimento da magnitude.
            Vector2 action = _target.LastAction;
            Vector3 actionWorld = new(action.x, 0f, action.y);
            Debug.DrawRay(origin + Vector3.up * 0.2f, actionWorld * 2f, Info);

            // Janela de exploração no chão: vermelho = bloqueada por parede, verde = ainda não
            // visitada (o que puxa o agente), nada = já visitada.
            SeekerExplorationMemory e = _target.Exploration;
            if (e != null && e.Window != null)
            {
                float halfCell = e.CellSize * 0.45f;
                for (int i = 0; i < e.Window.Length; i++)
                {
                    bool blocked = e.IsWindowCellBlocked(i);
                    if (!blocked && e.Window[i] > 0.5f)
                        continue;

                    Vector3 c = e.WindowCellWorldCenter(i) + Vector3.up * 0.05f;
                    Color color = blocked ? Bad : Good;
                    Vector3 a = c + new Vector3(-halfCell, 0f, -halfCell);
                    Vector3 b = c + new Vector3(halfCell, 0f, -halfCell);
                    Vector3 d = c + new Vector3(halfCell, 0f, halfCell);
                    Vector3 f = c + new Vector3(-halfCell, 0f, halfCell);
                    Debug.DrawLine(a, b, color); Debug.DrawLine(b, d, color);
                    Debug.DrawLine(d, f, color); Debug.DrawLine(f, a, color);
                }
            }

            // Fronteira: primeiro passo (seta azul curta) e a célula-alvo (losango azul).
            SeekerExplorationMemory mem = _target.Exploration;
            if (mem != null && mem.HasFrontier)
            {
                Debug.DrawRay(origin + Vector3.up * 0.3f, mem.FrontierStepDirectionWorld * 1.5f, Info);
                Vector3 f = mem.FrontierCellWorldCenter + Vector3.up * 0.1f;
                float r = mem.CellSize * 0.3f;
                Debug.DrawLine(f + Vector3.left * r, f + Vector3.forward * r, Info);
                Debug.DrawLine(f + Vector3.forward * r, f + Vector3.right * r, Info);
                Debug.DrawLine(f + Vector3.right * r, f + Vector3.back * r, Info);
                Debug.DrawLine(f + Vector3.back * r, f + Vector3.left * r, Info);
            }

            // Última posição conhecida e a linha até ela.
            if (p.HasSeenHider)
            {
                Vector3 target = p.LastKnownHiderPosition;
                Debug.DrawLine(origin, target, p.IsSeeingHider ? Good : Warn);
                Debug.DrawLine(target + Vector3.left * 0.3f, target + Vector3.right * 0.3f, Warn);
                Debug.DrawLine(target + Vector3.back * 0.3f, target + Vector3.forward * 0.3f, Warn);
            }
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Índice do raio (N, NE, E, ...) mais alinhado com a ação, em mundo.</summary>
        private static int CompassIndex(Vector2 actionXZ)
        {
            float angle = Mathf.Atan2(actionXZ.x, actionXZ.y) * Mathf.Rad2Deg; // 0 = +Z, 90 = +X
            int index = Mathf.RoundToInt(angle / 45f);
            return ((index % 8) + 8) % 8;
        }

        private static void CountUnvisited(float[] window, Vector2 actionXZ, ref int ahead, ref int behind)
        {
            int side = Mathf.RoundToInt(Mathf.Sqrt(window.Length));
            int radius = side / 2;

            for (int dz = -radius; dz <= radius; dz++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (dx == 0 && dz == 0)
                        continue;

                    if (window[(dz + radius) * side + (dx + radius)] > 0.5f)
                        continue;

                    float dot = dx * actionXZ.x + dz * actionXZ.y;
                    if (dot > 0f) ahead++;
                    else if (dot < 0f) behind++;
                }
            }
        }

        private void Term(string label, float step, float episode, Color color)
        {
            Line($"{label,-14} {step,9:+0.0000;-0.0000}   {episode,9:+0.000;-0.000}", color);
        }

        private void Line(string text, Color color)
        {
            _text.AppendLine(Colored(text, color));
        }

        private static string Colored(string text, Color color) =>
            $"<color=#{ColorUtility.ToHtmlStringRGB(color)}>{text}</color>";

        private static string Flag(bool value) => value ? "sim" : "não";

        private static Color Sign(float value) => value > 0f ? Good : value < 0f ? Bad : Dim;

        private void ResolveTarget()
        {
            _all = FindObjectsByType<SeekerManager>(FindObjectsSortMode.InstanceID);
            _target = _all.Length > 0 ? _all[0] : null;
        }

        private void CycleTarget()
        {
            _all = FindObjectsByType<SeekerManager>(FindObjectsSortMode.InstanceID);
            if (_all.Length == 0)
            {
                _target = null;
                return;
            }

            int index = System.Array.IndexOf(_all, _target);
            _target = _all[(index + 1) % _all.Length];
        }

        private void EnsureStyles()
        {
            if (_style != null)
                return;

            // Monoespaçada para as colunas dos termos e a janela alinharem sem tabela.
            Font mono = Font.CreateDynamicFontFromOSFont(new[] { "Consolas", "Courier New", "DejaVu Sans Mono" }, 12);

            _style = new GUIStyle(GUI.skin.label)
            {
                richText = true,
                font = mono,
                fontSize = 12,
                wordWrap = false,
                alignment = TextAnchor.UpperLeft,
            };

            _box = new GUIStyle(GUI.skin.box);
            var background = new Texture2D(1, 1);
            background.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.8f));
            background.Apply();
            _box.normal.background = background;
        }
    }
}
