using System;
using Assets.Scripts.Graph;
using UnityEngine;
using UnityEngine.AI;

namespace Assets.Scripts.Director
{
    /// <summary>Distância pelo NavMesh, o mesmo caminho que o som e o monstro usam.</summary>
    public static class DirectorNav
    {
        public static bool TryGround(Vector3 position, out Vector3 ground, float snap = 3f)
        {
            bool found = NavMesh.SamplePosition(position, out NavMeshHit hit, snap, NavMesh.AllAreas);
            ground = found ? hit.position : position;
            return found;
        }

        /// <summary>Comprimento do caminho, ou -1 sem caminho completo.</summary>
        public static float PathLength(Vector3 from, Vector3 to, NavMeshPath path)
        {
            if (!TryGround(from, out Vector3 a) || !TryGround(to, out Vector3 b)
                || !NavMesh.CalculatePath(a, b, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
                return -1f;

            float length = 0f;
            Vector3[] corners = path.corners;
            for (int i = 1; i < corners.Length; i++)
                length += Vector3.Distance(corners[i - 1], corners[i]);
            return length;
        }
    }

    /// <summary>
    /// O que o diretor MEDE do jogo, um por diretor: tensão (0..1), close calls, tempo sem ser visto, distância pelo
    /// caminho. Close call = o jogador escapa de uma perseguição (perdeu de vista e não reencontrou, ou GaveUp), o
    /// monstro passa colado sem vê-lo, ou passa perto em alerta e o jogador sai de fininho (v4); só com o jogador vivo e
    /// com folga entre um e outro.
    /// </summary>
    [Serializable]
    public class DirectorSenses
    {
        [Tooltip("Passar a menos disto (m, em linha reta) do jogador sem vê-lo é close call.")]
        [SerializeField, Min(0.5f)] private float _nearMissMeters = 5f;

        [Tooltip("O passar colado só conta de novo depois que o monstro se afasta além disto (m).")]
        [SerializeField, Min(1f)] private float _nearMissRearmMeters = 12f;

        [Tooltip("Perdeu de vista e não reencontrou em tanto tempo (s): o jogador escapou, close call.")]
        [SerializeField, Min(1f)] private float _escapeSeconds = 12f;

        [Tooltip("Monstro em alerta a menos disto (m, pelo caminho) sem ver o jogador; se ele se afasta sem pegar, close call.")]
        [SerializeField, Min(1f)] private float _alertMissMeters = 10f;
        [SerializeField, Min(2f)] private float _alertMissClearMeters = 20f;

        [Tooltip("Folga mínima (s) entre dois close calls contados: desistir logo depois de passar colado é o mesmo susto.")]
        [SerializeField, Min(0f)] private float _closeCallCooldown = 15f;

        [Tooltip("Tensão cai pela metade nesse tempo (s) sem perigo.")]
        [SerializeField, Min(1f)] private float _tensionHalfLife = 20f;

        [Tooltip("Abaixo desta tensão o tempo conta como vazio; acima de 0.35 zera.")]
        [SerializeField, Range(0f, 1f)] private float _boredTension = 0.2f;

        [Tooltip("Distância pelo caminho (m) a partir da qual o monstro perto não pesa mais na tensão.")]
        [SerializeField, Min(1f)] private float _tensionRange = 35f;

        private NavMeshPath _path;
        private bool _nearMissArmed;
        private bool _gaveUpPending;
        private float _sinceCountedCloseCall;
        private bool _wasSeeing;
        private float _escapeLeft = -1f;
        private bool _alertMissPending;
        private bool _hunting;
        private float _pathSum;
        private int _pathSamples;

        public float Tension { get; private set; }
        public float BoredSeconds { get; private set; }
        public float PathDistance { get; private set; } = -1f;
        public float StraightDistance { get; private set; }
        public float SecondsSinceSeen { get; private set; }
        public bool HasSeen { get; private set; }
        public int CloseCalls { get; private set; }
        public float SecondsSinceCloseCall { get; private set; }

        // Do step atual: consumidos pelo DirectorAgent.
        public bool CloseCallThisStep { get; private set; }
        public float SecondsSincePreviousCloseCall { get; private set; }

        public float TensionSum { get; private set; }
        public float BoredTime { get; private set; }

        // Como o jogador está indo NESTA partida (o diretor deduz o nível dele por isto): vezes que foi visto, tempo com
        // o monstro em alerta ou perseguição, distância média pelo caminho.
        public int Sightings { get; private set; }
        public float HuntedTime { get; private set; }
        public float MeanPathDistance => _pathSamples > 0 ? _pathSum / _pathSamples : -1f;

        public void Reset()
        {
            Tension = 0f;
            BoredSeconds = 0f;
            PathDistance = -1f;
            StraightDistance = 0f;
            SecondsSinceSeen = 0f;
            HasSeen = false;
            CloseCalls = 0;
            SecondsSinceCloseCall = 0f;
            CloseCallThisStep = false;
            SecondsSincePreviousCloseCall = 0f;
            TensionSum = 0f;
            BoredTime = 0f;
            _nearMissArmed = true;
            _gaveUpPending = false;
            _sinceCountedCloseCall = float.MaxValue;
            _wasSeeing = false;
            _escapeLeft = -1f;
            _alertMissPending = false;
            _hunting = false;
            _pathSum = 0f;
            _pathSamples = 0;
            Sightings = 0;
            HuntedTime = 0f;
        }

        public void NotifyGaveUp() => _gaveUpPending = true;

        /// <summary>A cada decisão: o caminho é caro para todo step de física.</summary>
        public void UpdatePathDistance(Vector3 monster, Vector3 prey)
        {
            if (_path == null)
                _path = new NavMeshPath();
            PathDistance = DirectorNav.PathLength(monster, prey, _path);
            if (PathDistance >= 0f)
            {
                _pathSum += PathDistance;
                _pathSamples++;
                if (_hunting && !_wasSeeing && PathDistance < _alertMissMeters)
                    _alertMissPending = true;
            }
        }

        public void Tick(float dt, Vector3 monster, Vector3 prey, bool seeing, GraphLocomotion.Awareness state)
        {
            CloseCallThisStep = false;
            SecondsSinceCloseCall += dt;
            _sinceCountedCloseCall += dt;

            StraightDistance = new Vector2(prey.x - monster.x, prey.z - monster.z).magnitude;
            if (seeing)
            {
                HasSeen = true;
                SecondsSinceSeen = 0f;
            }
            else
            {
                SecondsSinceSeen += dt;
            }

            bool nearMiss = false;
            if (StraightDistance > _nearMissRearmMeters)
            {
                _nearMissArmed = true;
            }
            else if (_nearMissArmed && !seeing && StraightDistance < _nearMissMeters)
            {
                _nearMissArmed = false;
                nearMiss = true;
            }

            _hunting = state != GraphLocomotion.Awareness.Patrol;
            if (_hunting)
                HuntedTime += dt;
            if (seeing && !_wasSeeing)
                Sightings++;

            // Passou por perto em alerta e o jogador saiu de fininho: o monstro se afastou ou desistiu sem vê-lo.
            bool slipped = false;
            if (seeing)
                _alertMissPending = false;
            else if (_alertMissPending && (!_hunting || PathDistance > _alertMissClearMeters))
            {
                _alertMissPending = false;
                slipped = true;
            }

            // Escapou de uma perseguição: perdeu de vista e não reencontrou a tempo (pegar encerra antes).
            bool escaped = false;
            if (seeing)
                _escapeLeft = -1f;
            else if (_wasSeeing)
                _escapeLeft = _escapeSeconds;
            else if (_escapeLeft > 0f && (_escapeLeft -= dt) <= 0f)
                escaped = true;
            _wasSeeing = seeing;

            if ((nearMiss || escaped || slipped || _gaveUpPending) && _sinceCountedCloseCall >= _closeCallCooldown)
            {
                CloseCalls++;
                CloseCallThisStep = true;
                SecondsSincePreviousCloseCall = SecondsSinceCloseCall;
                SecondsSinceCloseCall = 0f;
                _sinceCountedCloseCall = 0f;
            }

            _gaveUpPending = false;
            UpdateTension(dt, seeing, state);
        }

        private void UpdateTension(float dt, bool seeing, GraphLocomotion.Awareness state)
        {
            float target = seeing ? 1f
                : state == GraphLocomotion.Awareness.Chase ? 0.85f
                : state == GraphLocomotion.Awareness.Alert ? 0.55f : 0f;
            if (PathDistance >= 0f)
                target = Mathf.Max(target, 0.6f * Mathf.Clamp01(1f - PathDistance / _tensionRange));

            // Sobe rápido, cai devagar.
            Tension = target > Tension
                ? Mathf.MoveTowards(Tension, target, dt)
                : target + (Tension - target) * Mathf.Exp(-0.6931f * dt / _tensionHalfLife);

            if (Tension < _boredTension)
                BoredSeconds += dt;
            else if (Tension > 0.35f)
                BoredSeconds = 0f;

            TensionSum += Tension * dt;
            if (Tension < _boredTension)
                BoredTime += dt;
        }
    }
}
