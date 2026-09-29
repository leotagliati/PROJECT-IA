using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// Mantém este objeto SEM rotação no mundo, mesmo com o pai girando. Vai num filho do
    /// agente que carrega o RayPerceptionSensor3D.
    ///
    /// Por quê: o corpo do agente vira para onde anda (SeekerMovementSystem), mas as ações e
    /// todas as direções da observação vetorial (vizinhos, âncora, fronteira) são no referencial
    /// do MUNDO — e nada na observação diz para onde o corpo está virado. Com os raios presos ao
    /// corpo, a rede recebia "parede a 1 m no raio 0" sem saber se o raio 0 era norte ou leste,
    /// então não conseguia traduzir isso na ação que desvia. Resultado: empurrar a quina até o
    /// timeout (node4_noarrow_01). Preso ao mundo, o raio 0 é sempre +Z, igual às ações.
    ///
    /// Execution order bem negativa: a física gira o pai DEPOIS dos FixedUpdate, e os raios são
    /// lançados no FixedUpdate do Academy (ordem 0). Endireitar antes dele garante que o cast
    /// sai no referencial certo. O LateUpdate é só para o gizmo não tremer no editor.
    ///
    /// Só funciona sem distorção porque o pai gira apenas em Y e tem escala igual em X e Z.
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
