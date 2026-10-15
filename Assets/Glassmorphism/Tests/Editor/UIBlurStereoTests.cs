using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using System.Reflection;

namespace Glassmorphism.Tests
{
    /// GPU regression check without a headset/provider. Real headset validation is separate.
    public sealed class UIBlurStereoTests
    {
        [Test]
        public void BlurAndPanelKeepLeftAndRightEyeSlicesSeparate()
        {
            if (!SystemInfo.supports2DArrayTextures || !SystemInfo.supportsInstancing
                || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                Assert.Ignore("Requires a GPU with array textures and instancing.");
            var active = RenderTexture.active;
            bool oldStereoKeyword = Shader.IsKeywordEnabled("STEREO_INSTANCING_ON");
            int oldViewCount = Shader.GetGlobalInt("_XRViewCount");
            var oldStereoMatrices = Shader.GetGlobalMatrixArray("unity_StereoMatrixVP");
            var oldBackdrop = Shader.GetGlobalTexture("_WorldGlassBackdrop");
            float oldCameraId = Shader.GetGlobalFloat("_WorldGlassCameraID");
            var source = new Texture2DArray(32,32,2,TextureFormat.RGBA32,false,true);
            source.filterMode=FilterMode.Bilinear;
            source.wrapMode=TextureWrapMode.Clamp;
            var red = new Color[32*32]; var blue = new Color[32*32];
            for(int i=0;i<red.Length;i++) { red[i]=new Color(1,.1f,.1f,1); blue[i]=new Color(.1f,.1f,1,1); }
            source.SetPixels(red,0); source.SetPixels(blue,1); source.Apply();
            var desc = new RenderTextureDescriptor(16,16,RenderTextureFormat.ARGB32,0);
            desc.dimension=UnityEngine.Rendering.TextureDimension.Tex2DArray; desc.volumeDepth=2;
            desc.vrUsage=VRTextureUsage.TwoEyes; desc.sRGB=false;
            var a=new RenderTexture(desc); var b=new RenderTexture(desc); var panelOutput=new RenderTexture(desc);
            a.Create(); b.Create(); panelOutput.Create();
            var blur = new Material(Shader.Find("Glassmorphism/WorldGlassBlur"));
            var panel = new Material(Shader.Find("Glassmorphism/WorldSpaceGlass"));
            new Glassmorphism.UIBlurGaussianKernel().Apply(blur,1);
            blur.SetVector("_DownsampleTargetSize",new Vector4(1f/16,1f/16,0,0));
            panel.SetFloat("_PanelCameraID",123);
            panel.SetFloat("_OverlayEnabled",0);
            panel.SetColor("_TintColor",Color.clear);
            panel.SetVector("_PanelRect",new Vector4(-1,-1,2,2));
            var mesh=new Mesh();
            mesh.vertices=new[]{new Vector3(-1,-1,0),new Vector3(1,-1,0),new Vector3(1,1,0),new Vector3(-1,1,0)};
            mesh.uv=new[]{Vector2.zero,Vector2.right,Vector2.one,Vector2.up};
            mesh.colors=new[]{Color.white,Color.white,Color.white,Color.white};
            mesh.triangles=new[]{0,1,2,0,2,3};
            var cmd=new UnityEngine.Rendering.CommandBuffer();
            Texture2D read=null;
            try {
                cmd.EnableShaderKeyword("STEREO_INSTANCING_ON");
                cmd.SetInstanceMultiplier(2);
                cmd.SetGlobalInt("_XRViewCount",2);
                cmd.SetRenderTarget(a,0,CubemapFace.Unknown,-1);
                cmd.ClearRenderTarget(false,true,Color.black);
                UnityEngine.Rendering.Blitter.BlitTexture(cmd,new UnityEngine.Rendering.RenderTargetIdentifier(source),new Vector4(1,1,0,0),blur,0);
                cmd.SetRenderTarget(b,0,CubemapFace.Unknown,-1);
                cmd.ClearRenderTarget(false,true,Color.black);
                UnityEngine.Rendering.Blitter.BlitTexture(cmd,new UnityEngine.Rendering.RenderTargetIdentifier(a),new Vector4(1,1,0,0),blur,1);
                cmd.SetRenderTarget(a,0,CubemapFace.Unknown,-1);
                UnityEngine.Rendering.Blitter.BlitTexture(cmd,new UnityEngine.Rendering.RenderTargetIdentifier(b),new Vector4(1,1,0,0),blur,2);
                cmd.SetRenderTarget(panelOutput,0,CubemapFace.Unknown,-1);
                cmd.ClearRenderTarget(false,true,Color.black);
                cmd.SetViewProjectionMatrices(Matrix4x4.identity,Matrix4x4.identity);
                cmd.SetGlobalMatrixArray("unity_StereoMatrixVP",new[]{Matrix4x4.identity,Matrix4x4.identity});
                cmd.SetGlobalTexture("_WorldGlassBackdrop",a);
                cmd.SetGlobalFloat("_WorldGlassCameraID",123);
                cmd.DrawMesh(mesh,Matrix4x4.identity,panel,0,0);
                cmd.SetInstanceMultiplier(1);
                cmd.DisableShaderKeyword("STEREO_INSTANCING_ON");
                Graphics.ExecuteCommandBuffer(cmd);
                string report="";
                read=new Texture2D(16,16,TextureFormat.RGBA32,false,true);
                foreach(var output in new[]{a,panelOutput}) {
                    for(int eye=0;eye<2;eye++) {
                        Graphics.SetRenderTarget(output,0,CubemapFace.Unknown,eye);
                        read.ReadPixels(new Rect(0,0,16,16),0,0);read.Apply();
                        var color=read.GetPixel(8,8);
                        report+=(output==a?"blur":"panel")+" eye "+eye+"="+color+"; ";
                        if(eye==0 && (color.r<.9f || color.b>.2f)) throw new System.Exception(report+" LEFT eye invalid");
                        if(eye==1 && (color.b<.9f || color.r>.2f)) throw new System.Exception(report+" RIGHT eye invalid");
                    }
                }
                TestContext.WriteLine(report);
            } finally {
                cmd.Release();
                if(oldStereoKeyword) Shader.EnableKeyword("STEREO_INSTANCING_ON");
                else Shader.DisableKeyword("STEREO_INSTANCING_ON");
                Shader.SetGlobalInt("_XRViewCount",oldViewCount);
                if(oldStereoMatrices.Length>0) Shader.SetGlobalMatrixArray("unity_StereoMatrixVP",oldStereoMatrices);
                Shader.SetGlobalTexture("_WorldGlassBackdrop",oldBackdrop);
                Shader.SetGlobalFloat("_WorldGlassCameraID",oldCameraId);
                RenderTexture.active=active;
                if(read!=null)UnityEngine.Object.DestroyImmediate(read);
                UnityEngine.Object.DestroyImmediate(mesh);
                UnityEngine.Object.DestroyImmediate(blur);
                UnityEngine.Object.DestroyImmediate(panel);
                UnityEngine.Object.DestroyImmediate(source);
                a.Release();b.Release();panelOutput.Release();
                UnityEngine.Object.DestroyImmediate(a);UnityEngine.Object.DestroyImmediate(b);UnityEngine.Object.DestroyImmediate(panelOutput);
            }
        }

        [TestCase(TextureDimension.Tex2DArray, 2)]
        [TestCase(TextureDimension.Tex2D, 1)]
        public void RendererPreservesStereoOrMonoTextureLayout(TextureDimension dimension, int slices)
        {
            var feature = ScriptableObject.CreateInstance<UIBlurRendererFeature>();
            try
            {
                feature.Create();
                var pass = typeof(UIBlurRendererFeature).GetField("pass", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(feature);
                var material = typeof(UIBlurRendererFeature).GetField("material", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(feature);
                pass.GetType().GetMethod("Setup").Invoke(pass, new object[] { material, true, 123, 4, 1f, 1, false });
                var descriptor = new RenderTextureDescriptor(1024, 1024, RenderTextureFormat.ARGB32, 24)
                {
                    dimension = dimension, volumeDepth = slices,
                    vrUsage = slices == 2 ? VRTextureUsage.TwoEyes : VRTextureUsage.None
                };
                var result = (RenderTextureDescriptor)pass.GetType()
                    .GetMethod("BlurDescriptor", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(pass, new object[] { descriptor });
                Assert.That(result.dimension, Is.EqualTo(dimension));
                Assert.That(result.volumeDepth, Is.EqualTo(slices));
                Assert.That(result.vrUsage, Is.EqualTo(descriptor.vrUsage));
                Assert.That(result.msaaSamples, Is.EqualTo(1));
                Assert.That(result.depthBufferBits, Is.EqualTo(0));
            }
            finally
            {
                feature.Dispose();
                Object.DestroyImmediate(feature);
            }
        }
    }
}
