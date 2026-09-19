using UnityEngine;

namespace Assets.Scripts.Seeker
{
    /// <summary>
    /// Relay dos Animation Events de passo dos clipes Rig|Walk e Rig|Run (definidos na aba
    /// Animation do importer do SeekerModel.fbx). Só existe porque o Unity procura o método do
    /// evento no MESMO GameObject do Animator — o "char" dentro de Visual — e a configuração de
    /// som mora no <see cref="SeekerAudioSystem"/>, no pai. Não decide nada: repassa a posição.
    ///
    /// Diferente do player, que mede deslocamento para decidir o passo, aqui o passo vem do pé
    /// encostar no chão na animação: com blend tree o ritmo já muda sozinho entre Walk e Run
    /// (TimeScale dos filhos) e o som acompanha de graça.
    /// </summary>
    public class SeekerFootsteps : MonoBehaviour
    {
        private SeekerAudioSystem _audio;

        private void Awake()
        {
            _audio = GetComponentInParent<SeekerAudioSystem>();

            if (_audio == null)
                Debug.LogWarning($"{name}: SeekerAudioSystem não encontrado nos pais — passos do seeker mudos.", this);
        }

        /// <summary>
        /// Nome chamado pelo Animation Event. Mantenha igual ao dos eventos no importer — o Unity
        /// não avisa em compile time se descasar, só em runtime.
        /// </summary>
        public void OnFootstep()
        {
            // Posição do char, não do root do agente: o Visual tem offset em Y e escala 2, então
            // é este transform que está de fato nos pés do modelo.
            if (_audio != null)
                _audio.PlayFootstep(transform.position);
        }
    }
}
