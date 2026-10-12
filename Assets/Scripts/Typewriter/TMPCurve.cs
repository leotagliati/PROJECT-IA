using TMPro;
using UnityEngine;

/// <summary>
/// Enrola o texto num cilindro, para assentar numa folha curva. Dobra os vértices toda vez que o TMP gera a
/// malha (TEXT_CHANGED_EVENT) — inclusive a cada maxVisibleCharacters novo — porque a geração seguinte
/// sobrescreve qualquer deformação.
///
/// Alinhar: selecione o texto e ajuste <see cref="radius"/> e <see cref="tangentAt"/> até o gizmo amarelo
/// colar na superfície da folha; o texto acompanha no editor.
/// </summary>
[ExecuteAlways]
[RequireComponent(typeof(TMP_Text))]
public class TMPCurve : MonoBehaviour
{
    public enum Axis
    {
        Vertical,
        Horizontal
    }

    [Tooltip("Vertical: a folha enrola em volta do rolo (linhas retas, curva de cima a baixo). " +
             "Horizontal: curva de um lado ao outro.")]
    [SerializeField] private Axis axis = Axis.Vertical;

    [Tooltip("Raio do cilindro em unidades locais do texto. Positivo = legível do lado de fora do cilindro (folha bojuda para o leitor); negativo = do lado de dentro. 0 = reto.")]
    [SerializeField] private float radius = 0.5f;

    [Tooltip("Coordenada local (Y no Vertical, X no Horizontal) onde o texto encosta no cilindro e fica parado.")]
    [SerializeField] private float tangentAt;

    private TMP_Text _text;

    private void OnEnable()
    {
        _text = GetComponent<TMP_Text>();
        TMPro_EventManager.TEXT_CHANGED_EVENT.Add(OnTextChanged);
        _text.ForceMeshUpdate();
    }

    private void OnDisable()
    {
        TMPro_EventManager.TEXT_CHANGED_EVENT.Remove(OnTextChanged);

        // Regenera para devolver o texto reto.
        if (_text != null)
            _text.ForceMeshUpdate();
    }

    private void OnValidate()
    {
        if (_text != null && isActiveAndEnabled)
            _text.havePropertiesChanged = true;
    }

    private void OnTextChanged(Object changed)
    {
        if (changed != _text || Mathf.Approximately(radius, 0f))
            return;

        TMP_TextInfo info = _text.textInfo;

        for (int m = 0; m < info.meshInfo.Length; m++)
        {
            Vector3[] vertices = info.meshInfo[m].vertices;
            if (vertices == null)
                continue;

            for (int i = 0; i < vertices.Length; i++)
                vertices[i] = Bend(vertices[i]);
        }

        _text.UpdateVertexData(TMP_VertexDataUpdateFlags.Vertices);

        // Sem isso o renderer culla pelos bounds da malha reta.
        for (int m = 0; m < info.meshInfo.Length; m++)
            if (info.meshInfo[m].mesh != null)
                info.meshInfo[m].mesh.RecalculateBounds();
    }

    private Vector3 Bend(Vector3 v)
    {
        // A distância ao longo da folha vira arco: comprimento preservado, a folha não estica. Bordas para -Z é o
        // que deixa o texto legível do lado de fora do cilindro (com +Z ele ficava lido por dentro).
        if (axis == Axis.Vertical)
        {
            float angle = (v.y - tangentAt) / radius;
            v.y = tangentAt + radius * Mathf.Sin(angle);
            v.z -= radius * (1f - Mathf.Cos(angle));
        }
        else
        {
            float angle = (v.x - tangentAt) / radius;
            v.x = tangentAt + radius * Mathf.Sin(angle);
            v.z -= radius * (1f - Mathf.Cos(angle));
        }

        return v;
    }

    private void OnDrawGizmosSelected()
    {
        if (Mathf.Approximately(radius, 0f))
            return;

        var rect = GetComponent<RectTransform>();
        if (rect == null)
            return;

        Rect r = rect.rect;
        float from = axis == Axis.Vertical ? r.yMin : r.xMin;
        float to = axis == Axis.Vertical ? r.yMax : r.xMax;

        Gizmos.color = Color.yellow;
        Gizmos.matrix = transform.localToWorldMatrix;

        const int segments = 24;
        Vector3 previous = Bend(axis == Axis.Vertical ? new Vector3(r.center.x, from, 0f) : new Vector3(from, r.center.y, 0f));

        for (int i = 1; i <= segments; i++)
        {
            float s = Mathf.Lerp(from, to, i / (float)segments);
            Vector3 point = Bend(axis == Axis.Vertical ? new Vector3(r.center.x, s, 0f) : new Vector3(s, r.center.y, 0f));
            Gizmos.DrawLine(previous, point);
            previous = point;
        }
    }
}
