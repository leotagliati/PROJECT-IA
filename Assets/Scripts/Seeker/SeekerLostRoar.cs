using UnityEngine;

namespace Assets.Scripts.Seeker
{
    /// <summary>
    /// Grito procedural de raiva quando o monstro desiste do jogador (GraphLocomotion.GaveUp): ergue a cabeça, sacode e levanta os braços.
    /// Só visual e só no jogo. Ordem 10: depois do Animator e do GraphAnimationSystem (0), que reescrevem os ossos no LateUpdate.
    /// </summary>
    [DefaultExecutionOrder(10)]
    public class SeekerLostRoar : MonoBehaviour
    {
        [Header("-----Ossos (vazio = acha pelo nome no Animator)-----")]
        [SerializeField] private Animator _animator;
        [SerializeField] private Transform _chest;
        [SerializeField] private Transform _neck;
        [SerializeField] private Transform _head;
        [SerializeField] private Transform _upperArmL;
        [SerializeField] private Transform _forearmL;
        [SerializeField] private Transform _handL;
        [SerializeField] private Transform _upperArmR;
        [SerializeField] private Transform _forearmR;
        [SerializeField] private Transform _handR;

        [SerializeField] private string _chestName = "spine.003";
        [SerializeField] private string _neckName = "spine.004";
        [SerializeField] private string _headName = "spine.006";

        [Header("-----Tempo (s)-----")]
        [SerializeField, Min(0.5f)] private float _duration = 2.8f;
        [SerializeField, Min(0.05f)] private float _riseSeconds = 0.35f;
        [SerializeField, Min(0.05f)] private float _releaseSeconds = 0.6f;

        [Tooltip("Tempo para desfazer a pose se ele vê o jogador no meio do grito.")]
        [SerializeField, Min(0.05f)] private float _cancelSeconds = 0.2f;

        [Header("-----Cabeça e peito (graus)-----")]
        [Tooltip("Quanto a cabeça sobe (40% pescoço, 60% cabeça).")]
        [SerializeField, Range(0f, 80f)] private float _lookUpAngle = 40f;

        [Tooltip("Arqueio do peito para trás.")]
        [SerializeField, Range(0f, 40f)] private float _chestArch = 12f;

        [Tooltip("Giro da cabeça de um lado para o outro.")]
        [SerializeField, Range(0f, 60f)] private float _shakeAngle = 25f;

        [Tooltip("Idas e vindas da cabeça.")]
        [SerializeField, Range(0.5f, 6f)] private float _shakeCycles = 2.5f;

        [Tooltip("Segundos até a cabeça começar a sacudir.")]
        [SerializeField, Min(0f)] private float _shakeDelay = 0.5f;

        [Tooltip("Inverte o 'para cima' se o rig girar ao contrário.")]
        [SerializeField] private bool _invertPitch = false;

        [Header("-----Braços (direções no espaço do corpo, braço DIREITO; o esquerdo é espelhado)-----")]
        [SerializeField, Range(0f, 1f)] private float _armWeight = 0.85f;

        [Tooltip("Ombro ao cotovelo: x = lado, y = cima, z = frente.")]
        [SerializeField] private Vector3 _upperArmDirection = new Vector3(0.45f, -0.8f, 0.25f);

        [Tooltip("Cotovelo à mão: x = lado, y = cima, z = frente.")]
        [SerializeField] private Vector3 _forearmDirection = new Vector3(0.3f, 0.45f, 0.8f);

        [Header("-----Som-----")]
        [Tooltip("Entrada da AudioLibrary (vazio = sem som).")]
        [SerializeField] private string _roarSoundId = "seeker_roar";

        [Tooltip("Volume em relação ao da entrada.")]
        [SerializeField, Range(0f, 2f)] private float _roarVolume = 1f;

        [Tooltip("Pitch em relação ao da entrada (< 1 = mais grave).")]
        [SerializeField, Range(0.3f, 1.5f)] private float _roarPitch = 0.7f;

        private float _time = -1f;
        private float _cancelFrom = -1f;
        private float _cancelWeight;
        private AudioHandle _sound;

        public bool IsRoaring => _time >= 0f && _cancelFrom < 0f;

        private void Awake()
        {
            if (_animator == null)
                _animator = GetComponentInChildren<Animator>();

            Transform root = _animator != null ? _animator.transform : transform;
            _chest = Resolve(_chest, root, _chestName);
            _neck = Resolve(_neck, root, _neckName);
            _head = Resolve(_head, root, _headName);
            _upperArmL = Resolve(_upperArmL, root, "upper_arm.L");
            _forearmL = Resolve(_forearmL, root, "forearm.L");
            _handL = Resolve(_handL, root, "hand.L");
            _upperArmR = Resolve(_upperArmR, root, "upper_arm.R");
            _forearmR = Resolve(_forearmR, root, "forearm.R");
            _handR = Resolve(_handR, root, "hand.R");

            if (_head == null)
                Debug.LogWarning($"{name}: SeekerLostRoar sem o osso da cabeça ('{_headName}'): o grito não mexe a cabeça.", this);
        }

        private void OnDisable()
        {
            _time = -1f;
            _cancelFrom = -1f;
        }

        // O AudioPool sobrevive à troca de cena: o grito não pode vazar para a partida nova.
        private void OnDestroy() => AudioProvider.Stop(_sound);

        public void Play()
        {
            if (!isActiveAndEnabled || IsRoaring)
                return;

            _time = 0f;
            _cancelFrom = -1f;

            Vector3 mouth = _head != null ? _head.position : transform.position;
            if (!string.IsNullOrEmpty(_roarSoundId))
                _sound = AudioProvider.PlayFollowing(_roarSoundId, _head != null ? _head : transform, _roarVolume, _roarPitch);
            SeekerRoarEvents.Emit(mouth);
        }

        public void Cancel()
        {
            if (!IsRoaring)
                return;

            _cancelWeight = Envelope(_time, _riseSeconds);
            _cancelFrom = _time;
            AudioProvider.Stop(_sound);
        }

        private void LateUpdate()
        {
            if (_time < 0f)
                return;

            _time += Time.deltaTime;

            float pose;
            if (_cancelFrom >= 0f)
            {
                float k = (_time - _cancelFrom) / _cancelSeconds;
                pose = _cancelWeight * (1f - Mathf.Clamp01(k));
                if (k >= 1f)
                {
                    _time = -1f;
                    _cancelFrom = -1f;
                    return;
                }
            }
            else if (_time >= _duration)
            {
                _time = -1f;
                return;
            }
            else
            {
                pose = Envelope(_time, _riseSeconds);
            }

            ApplyPose(pose, Shake());
        }

        private float Envelope(float t, float rise)
        {
            float up = Mathf.SmoothStep(0f, 1f, t / rise);
            float down = Mathf.SmoothStep(0f, 1f, (_duration - t) / _releaseSeconds);
            return Mathf.Min(up, down);
        }

        private float Shake()
        {
            float span = _duration - _shakeDelay - _releaseSeconds * 0.5f;
            if (_cancelFrom >= 0f || span <= 0f || _time < _shakeDelay)
                return 0f;

            float u = Mathf.Clamp01((_time - _shakeDelay) / span);
            float fade = Mathf.Sin(u * Mathf.PI);
            return Mathf.Sin(u * _shakeCycles * 2f * Mathf.PI) * _shakeAngle * fade;
        }

        private void ApplyPose(float pose, float shake)
        {
            if (pose <= 0f)
                return;

            Vector3 right = transform.right;
            float up = _invertPitch ? 1f : -1f; // girar em +direita abaixa a frente

            // Pai antes dos filhos: o giro do peito leva pescoço e cabeça.
            Rotate(_chest, right, up * _chestArch * pose);
            Rotate(_neck, right, up * _lookUpAngle * 0.4f * pose);
            Rotate(_neck, Vector3.up, shake * 0.4f);
            Rotate(_head, right, up * _lookUpAngle * 0.6f * pose);
            Rotate(_head, Vector3.up, shake * 0.6f);

            float arms = pose * _armWeight;
            AimArm(_upperArmR, _forearmR, _handR, 1f, arms);
            AimArm(_upperArmL, _forearmL, _handL, -1f, arms);
        }

        private static void Rotate(Transform bone, Vector3 axis, float degrees)
        {
            if (bone != null && Mathf.Abs(degrees) > 1e-3f)
                bone.rotation = Quaternion.AngleAxis(degrees, axis) * bone.rotation;
        }

        // Braço e antebraço apontados pela direção osso -> filho, para não depender dos eixos locais do rig.
        private void AimArm(Transform upper, Transform forearm, Transform hand, float side, float weight)
        {
            if (upper == null || forearm == null || weight <= 0f)
                return;

            Aim(upper, forearm.position, BodyDirection(_upperArmDirection, side), weight);
            if (hand != null)
                Aim(forearm, hand.position, BodyDirection(_forearmDirection, side), weight);
        }

        private Vector3 BodyDirection(Vector3 local, float side)
        {
            return transform.TransformDirection(new Vector3(local.x * side, local.y, local.z)).normalized;
        }

        private static void Aim(Transform bone, Vector3 childPosition, Vector3 target, float weight)
        {
            Vector3 current = childPosition - bone.position;
            if (current.sqrMagnitude < 1e-8f)
                return;

            Quaternion delta = Quaternion.FromToRotation(current.normalized, target);
            bone.rotation = Quaternion.Slerp(Quaternion.identity, delta, weight) * bone.rotation;
        }

#if UNITY_EDITOR
        [ContextMenu("Testar grito (Play)")]
        private void TestRoar()
        {
            if (Application.isPlaying)
                Play();
        }
#endif

        private static Transform Resolve(Transform assigned, Transform root, string boneName)
        {
            if (assigned != null || string.IsNullOrEmpty(boneName))
                return assigned;

            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == boneName)
                    return t;
            return null;
        }
    }
}
