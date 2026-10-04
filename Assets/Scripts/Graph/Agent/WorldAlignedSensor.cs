using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// Mantém este objeto SEM rotação no mundo mesmo com o pai girando; vai num filho do agente
    /// que carrega o RayPerceptionSensor3D. As ações e as direções da observação são no
    /// referencial do MUNDO e nada diz para onde o corpo está virado; com os raios presos ao
    /// corpo a rede não sabia se o raio 0 era norte ou leste. Preso ao mundo, o raio 0 é sempre +Z.
    ///
    /// Execution order bem negativa: os raios saem no FixedUpdate do Academy (ordem 0), então o
    /// endireitamento tem que vir antes (a física gira o pai depois dos FixedUpdate). O LateUpdate
    /// é só para o gizmo não tremer. Sem distorção só porque o pai gira apenas em Y, com escala X = Z.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public class WorldAlignedSensor : MonoBehaviour
    {
        private void OnEnable() => Align();

        private void FixedUpdate() => Align();

        private void LateUpdate() => Align();

        private void Align() => transform.rotation = Quaternion.identity;
    }
}
