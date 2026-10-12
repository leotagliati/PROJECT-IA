using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Xenon
{
    public class OutlinePassFinal : ScriptableRenderPass
    {
        private class PassData
        {
            internal TextureHandle FilterTextureHandle;
            internal Material Material;
        }

        private static readonly int OutlineScale = Shader.PropertyToID("_OutlineScale");
        private static readonly int NormalThreshold = Shader.PropertyToID("_NormalThreshold");
        private static readonly int DepthThreshold = Shader.PropertyToID("_DepthThreshold");
        private static readonly int OccluderGap = Shader.PropertyToID("_OccluderGap");
        private static readonly int OutlineColor = Shader.PropertyToID("_OutlineColor");

        private readonly Material _material;
        private readonly OutlineRenderFeature.OutlineSettings _outlineSettings;

        public OutlinePassFinal(OutlineRenderFeature.Settings settings, OutlineRenderFeature.OutlineSettings outlineSettings, Material material)
        {
            renderPassEvent = settings.RenderPassEvent;
            _material = material;
            _outlineSettings = outlineSettings;

            // O composite compara a máscara com a profundidade da cena para saber o que está na frente.
            ConfigureInput(ScriptableRenderPassInput.Depth);
        }

        private static void ExecutePass(PassData passData, RasterGraphContext context)
        {
            Blitter.BlitTexture(context.cmd, passData.FilterTextureHandle, new Vector4(1, 1, 0, 0), passData.Material, 0);
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resourceData = frameData.Get<UniversalResourceData>();
            var outlineData = frameData.Get<OutlineRenderFeature.OutlineData>();

            // Sair depois de abrir o builder deixa um pass sem render function no grafo, e o
            // RenderGraph lança exceção por isso. As checagens têm que vir antes.
            if (!outlineData.FilterTextureHandle.IsValid())
                return;

            if (_material == null)
                return;

            // Valores lidos todo frame para o ajuste no Inspector valer em Play Mode.
            _material.SetFloat(OutlineScale, _outlineSettings.OutlineScale);
            _material.SetFloat(NormalThreshold, _outlineSettings.NormalThreshold);
            _material.SetFloat(DepthThreshold, _outlineSettings.DepthThreshold);
            _material.SetFloat(OccluderGap, _outlineSettings.OccluderGap);
            _material.SetColor(OutlineColor, _outlineSettings.OutlineColor);

            using var builder = renderGraph.AddRasterRenderPass<PassData>("OutlinePass_Final", out var passData, new ProfilingSampler("OutlinePass_Final"));

            passData.Material = _material;
            passData.FilterTextureHandle = outlineData.FilterTextureHandle;

            builder.AllowPassCulling(false);
            builder.UseTexture(passData.FilterTextureHandle);
            if (resourceData.cameraDepthTexture.IsValid())
                builder.UseTexture(resourceData.cameraDepthTexture);
            builder.SetRenderAttachment(resourceData.cameraColor, index: 0);
            builder.SetRenderFunc<PassData>(ExecutePass);
        }
    }
}
