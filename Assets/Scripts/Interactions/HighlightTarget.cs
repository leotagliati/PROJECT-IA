using UnityEngine;
using UnityEngine.Rendering;

public class HighlightTarget : MonoBehaviour
{
    private const string OutlineRenderingLayerName = "Outline";

    private static uint _outlineMask;
    private static bool _maskResolved;

    private Renderer[] _renderers;
    private bool _highlighted;

    public bool IsHighlighted => _highlighted;

    protected virtual void Awake()
    {
        ResolveOutlineMask();
        _renderers = GetComponentsInChildren<Renderer>(includeInactive: true);
    }

    protected virtual void OnDisable()
    {
        SetHighlighted(false);
    }

    public virtual bool CanHighlight()
    {
        return true;
    }

    public void SetHighlighted(bool value)
    {
        if (_highlighted == value || _renderers == null)
            return;

        _highlighted = value;

        foreach (var r in _renderers)
        {
            if (r == null)
                continue;

            if (value)
                r.renderingLayerMask |= _outlineMask;
            else
                r.renderingLayerMask &= ~_outlineMask;
        }
    }

    private static void ResolveOutlineMask()
    {
        if (_maskResolved)
            return;

        _maskResolved = true;

        int layer = RenderingLayerMask.NameToRenderingLayer(OutlineRenderingLayerName);

        if (layer < 0)
        {
            Debug.LogError($"[HighlightTarget] Rendering layer \"{OutlineRenderingLayerName}\" não existe em " +
                           "Project Settings → Tags and Layers → Rendering Layers. O contorno não vai aparecer.");
            return;
        }

        _outlineMask = 1u << layer;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        _outlineMask = 0;
        _maskResolved = false;
    }
}
