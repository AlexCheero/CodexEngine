using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace CodexEngine.Rendering;

/// <summary>
/// A sky drawn from a horizontal-cross cubemap (4x3 faces: -X,+Z,+X,-Z in the middle row), in a left-handed world with +Y up.
/// <paramref name="rotation"/> turns the sky about the vertical axis, in degrees: the sky geometry is rotated by it.
/// <paramref name="gain"/> multiplies the stored texels in the pixel shader, so values above 1 reach an HDR target;
/// see the sky conversion script (sky.py) for the encoding. <paramref name="effectPath"/> is CrossCubeSky.fxb.
/// </summary>
public sealed class CrossCubeSkybox : IDisposable
{
    private readonly GraphicsDevice gpu;
    private readonly Effect shader;
    private readonly Matrix world;
    private readonly VertexPositionTexture[] vertices = new VertexPositionTexture[36];
    public CrossCubeSkybox(GraphicsDevice gpu,string effectPath,Texture2D cross,float rotation,Vector3 gain)
    {
        this.gpu=gpu;
        shader=new(gpu,File.ReadAllBytes(effectPath));
        shader.Parameters["Cross"].SetValue(cross);shader.Parameters["Gain"].SetValue(gain);
        world=Matrix.CreateRotationY(MathHelper.ToRadians(-rotation));
        int n=0;
        Face(Vector3.UnitX,-Vector3.UnitZ,Vector3.UnitY,2,1);
        Face(-Vector3.UnitX,Vector3.UnitZ,Vector3.UnitY,0,1);
        Face(Vector3.UnitY,Vector3.UnitX,-Vector3.UnitZ,1,0);
        Face(-Vector3.UnitY,Vector3.UnitX,Vector3.UnitZ,1,2);
        Face(Vector3.UnitZ,Vector3.UnitX,Vector3.UnitY,1,1);
        Face(-Vector3.UnitZ,-Vector3.UnitX,Vector3.UnitY,3,1);
        void Face(Vector3 center,Vector3 right,Vector3 up,int col,int row)
        {
            VertexPositionTexture Corner(int x,int y)=>new(center+right*(2*x-1)+up*(1-2*y),
                new((col+x+(x==0?.5f:-.5f)*4/cross.Width)/4,(row+y+(y==0?.5f:-.5f)*3/cross.Height)/3));
            vertices[n++]=Corner(0,0); vertices[n++]=Corner(1,0); vertices[n++]=Corner(1,1);
            vertices[n++]=Corner(0,0); vertices[n++]=Corner(1,1); vertices[n++]=Corner(0,1);
        }
    }
    /// <summary>The draw gain for a cross stored by sky.py with the linear sky multiplier m: encode(m) per channel.</summary>
    public static Vector3 Gain(float[] linearMultiplier)
    {
        static float Encode(float c) => c <= .0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1 / 2.4f) - .055f;
        return linearMultiplier is { Length: >= 3 } m ? new(Encode(m[0]), Encode(m[1]), Encode(m[2])) : Vector3.One;
    }
    /// <summary>Write linear radiance (for a linear scene target) instead of the encoded value.</summary>
    public bool LinearOutput { set => shader.Parameters["LinearOutput"].SetValue(value ? 1f : 0f); }
    /// <param name="blend">Opaque by default. A frame with further colour targets bound passes one that writes only the first.</param>
    public void Draw(Matrix view, Matrix projection, BlendState blend = null)
    {
        view.Translation=Vector3.Zero;
        shader.Parameters["WorldViewProjection"].SetValue(world*view*projection);
        gpu.BlendState=blend ?? BlendState.Opaque; gpu.DepthStencilState=DepthStencilState.None;
        gpu.RasterizerState=RasterizerState.CullNone;
        shader.CurrentTechnique.Passes[0].Apply();
        gpu.DrawUserPrimitives(PrimitiveType.TriangleList,vertices,0,12);
    }
    public void Dispose()=>shader.Dispose();
}
