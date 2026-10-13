using UnityEngine;

/// <summary>
/// Cacos do smash do <see cref="ThrowableItem"/>. Fica num filho DESLIGADO da garrafa, com o ParticleSystem
/// montado no Inspector; ao quebrar ele se solta (a garrafa é destruída no mesmo frame), liga e se apaga
/// sozinho quando as partículas acabam. Cone/forma emitindo em +Z: o filho é virado para a normal da batida.
/// </summary>
[RequireComponent(typeof(ParticleSystem))]
public class GlassShatter : MonoBehaviour
{
    public void Shatter(Vector3 point, Vector3 normal)
    {
        // false: mantém a escala local. O ParticleSystem (Scaling Mode Local) usa só ela, então o que
        // se vê no preview dentro da garrafa é o que sai no mundo, mesmo com a garrafa escalada.
        transform.SetParent(null, false);
        transform.SetPositionAndRotation(point + normal * 0.02f,
            Quaternion.LookRotation(normal.sqrMagnitude > 1e-6f ? normal : Vector3.up));

        var system = GetComponent<ParticleSystem>();

        // Forçados aqui e não deixados para o Inspector: com loop ou sem Destroy o objeto solto nunca some.
        var main = system.main;
        main.loop = false;
        main.stopAction = ParticleSystemStopAction.Destroy;
        main.simulationSpace = ParticleSystemSimulationSpace.World;

        gameObject.SetActive(true);
        system.Play();
    }
}
