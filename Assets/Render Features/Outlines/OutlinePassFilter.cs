using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Xenon
{
    public class OutlinePassFilter : ScriptableRenderPass
    {
        private class PassData
        {
            internal RendererListHandle RendererListHandle;
        }

        private readonly Material _overrideMaterial;
        private readonly LayerMask _layerMask;
        private readonly RenderingLayerMask _renderingLayerMask;

        private readonly List<ShaderTagId> _shaderTagIds = new()
        {
            new ShaderTagId("UniversalForwardOnly"),
            new ShaderTagId("UniversalForward"),
            new ShaderTagId("SRPDefaultUnlit"),
            new ShaderTagId("LightweightForward"),
        };

        public OutlinePassFilter(OutlineRenderFeature.Settings settings, Material overrideMaterial)
        {
            renderPassEvent = settings.RenderPassEvent;

            _layerMask = settings.LayerMask;
            _renderingLayerMask = settings.RenderingLayerMask;
            _overrideMaterial = overrideMaterial;
        }

        private RendererListHandle CreateRendererList(ContextContainer context, RenderGraph renderGraph)
        {
            var renderingData = context.Get<UniversalRenderingData>();
            var cameraData = context.Get<UniversalCameraData>();
            var lightData = context.Get<UniversalLightData>();

            var filterSettings = new FilteringSettings(RenderQueueRange.opaque, _layerMask, _renderingLayerMask);
            var drawSettings = RenderingUtils.CreateDrawingSettings(_shaderTagIds, renderingData, cameraData, lightData, cameraData.defaultOpaqueSortFlags);
            drawSettings.overrideMaterial = _overrideMaterial;

            return renderGraph.CreateRendererList(new RendererListParams(renderingData.cullResults, drawSettings, filterSettings));
        }

        private static void ExecutePass(PassData passData, RasterGraphContext context)
        {
            context.cmd.ClearRenderTarget(true, true, Color.clear);
            context.cmd.DrawRendererList(passData.RendererListHandle);
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resourceData = frameData.Get<UniversalResourceData>();
            var outlineData = frameData.GetOrCreate<OutlineRenderFeature.OutlineData>();

            using var builder = renderGraph.AddRasterRenderPass<PassData>("OutlinePass_Render", out var passData, new ProfilingSampler("OutlinePass_Render"));

            var colorDesc = renderGraph.GetTextureDesc(resourceData.cameraColor);
            colorDesc.name = "_OutlineMask";
            colorDesc.format = GraphicsFormat.R16G16B16A16_SFloat;
            colorDesc.msaaSamples = MSAASamples.None;
            colorDesc.bindTextureMS = false;
            colorDesc.filterMode = FilterMode.Point;
            colorDesc.clearBuffer = false;

            // Depth próprio, e não o da câmera: a máscara precisa do objeto inteiro, inclusive o que
            // está atrás de outra coisa. Quem decide visibilidade é o OutlineComposite.
            var depthDesc = colorDesc;
            depthDesc.name = "_OutlineMaskDepth";
            depthDesc.depthBufferBits = DepthBits.Depth32;

            var maskHandle = renderGraph.CreateTexture(colorDesc);
            var depthHandle = renderGraph.CreateTexture(depthDesc);
            outlineData.FilterTextureHandle = maskHandle;

            passData.RendererListHandle = CreateRendererList(frameData, renderGraph);

            builder.AllowPassCulling(false);
            builder.UseRendererList(passData.RendererListHandle);
            builder.SetRenderAttachment(maskHandle, 0);
            builder.SetRenderAttachmentDepth(depthHandle);
            builder.SetRenderFunc<PassData>(ExecutePass);
        }
    }
}
