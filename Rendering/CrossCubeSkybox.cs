using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace CodexEngine.Rendering;

/// <summary>
/// A sky drawn from a horizontal-cross cubemap (4x3 faces: -X,+Z,+X,-Z in the middle row) in Unity's world axes.
/// <paramref name="rotation"/> is Unity's skybox _Rotation in degrees: Unity rotates the sky geometry by it.
/// <paramref name="gain"/> multiplies the stored texels; see Tools/UnityFidelity/sky.py for the HDR encoding.
/// </summary>
public sealed class CrossCubeSkybox : IDisposable
{
    private readonly GraphicsDevice gpu;
    private readonly BasicEffect shader;
    private readonly VertexPositionTexture[] vertices = new VertexPositionTexture[36];
    public CrossCubeSkybox(GraphicsDevice gpu,Texture2D cross,float rotation,Vector3 gain)
    {
        this.gpu=gpu;
        shader=new(gpu) { TextureEnabled=true,Texture=cross,DiffuseColor=gain,World=Matrix.CreateRotationY(MathHelper.ToRadians(-rotation)) };
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
    /// <summary>The draw gain for a cross stored by sky.py with Unity's linear sky multiplier m: encode(m) per channel.</summary>
    public static Vector3 Gain(float[] linearMultiplier)
    {
        static float Encode(float c) => c <= .0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1 / 2.4f) - .055f;
        return linearMultiplier is { Length: >= 3 } m ? new(Encode(m[0]), Encode(m[1]), Encode(m[2])) : Vector3.One;
    }
    public void Draw(Matrix view, Matrix projection)
    {
        view.Translation=Vector3.Zero;
        shader.View=view; shader.Projection=projection;
        gpu.BlendState=BlendState.Opaque; gpu.DepthStencilState=DepthStencilState.None;
        gpu.RasterizerState=RasterizerState.CullNone; gpu.SamplerStates[0]=SamplerState.LinearClamp;
        shader.CurrentTechnique.Passes[0].Apply();
        gpu.DrawUserPrimitives(PrimitiveType.TriangleList,vertices,0,12);
    }
    public void Dispose()=>shader.Dispose();
}
