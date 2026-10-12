using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

namespace Xenon
{
    public class OutlineRenderFeature : ScriptableRendererFeature
    {
        [Serializable]
        public class Settings
        {
            public RenderPassEvent RenderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;

            public LayerMask LayerMask = ~0;

            public RenderingLayerMask RenderingLayerMask = 0;

            public Shader MaskShader;

            public Shader CompositeShader;
        }

        [Serializable]
        public class OutlineSettings
        {
            [Tooltip("Meia espessura do contorno, em pixels.")]
            public float OutlineScale = 1f;

            [Tooltip("Diferença entre normais (0–2) que conta como quina interna. Acima de 2 só a silhueta é contornada.")]
            public float NormalThreshold = 1f;

            [Tooltip("Salto de profundidade interno, como fração da distância, que conta como borda.")]
            public float DepthThreshold = 0.1f;

            [Tooltip("Oclusor a menos disso (m) do objeto é tratado como encostado (batente) e gera contorno; mais longe (corpo do player) esconde.")]
            public float OccluderGap = 0.15f;

            public Color OutlineColor = Color.white;
        }

        public class OutlineData : ContextItem
        {
            public TextureHandle FilterTextureHandle;

            public override void Reset()
            {
                FilterTextureHandle = TextureHandle.nullHandle;
            }
        }

        public Settings FeatureSettings;
        public OutlineSettings MaterialSettings;

        private Material _maskMaterial;
        private Material _compositeMaterial;
        private OutlinePassFilter _outlinePassFilter;
        private OutlinePassFinal _outlinePassFinal;

        public override void Create()
        {
            DestroyMaterials();
            _maskMaterial = CreateMaterial(FeatureSettings.MaskShader, "Hidden/Xenon/OutlineMask");
            _compositeMaterial = CreateMaterial(FeatureSettings.CompositeShader, "Hidden/Xenon/OutlineComposite");

            _outlinePassFilter = new OutlinePassFilter(FeatureSettings, _maskMaterial);
            _outlinePassFinal = new OutlinePassFinal(FeatureSettings, MaterialSettings, _compositeMaterial);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (_maskMaterial == null || _compositeMaterial == null)
                return;

            renderer.EnqueuePass(_outlinePassFilter);
            renderer.EnqueuePass(_outlinePassFinal);
        }

        protected override void Dispose(bool disposing)
        {
            DestroyMaterials();
        }

        private static Material CreateMaterial(Shader shader, string fallbackName)
        {
            if (shader == null)
                shader = Shader.Find(fallbackName);

            return shader != null ? CoreUtils.CreateEngineMaterial(shader) : null;
        }

        private void DestroyMaterials()
        {
            CoreUtils.Destroy(_maskMaterial);
            CoreUtils.Destroy(_compositeMaterial);
            _maskMaterial = null;
            _compositeMaterial = null;
        }
    }
}
