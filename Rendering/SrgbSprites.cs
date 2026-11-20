using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace CodexEngine.Rendering;

/// <summary>
/// A BasicEffect replacement for unlit textured, vertex-coloured sprites authored for display-encoded output.
/// It decodes texture*colour from sRGB so the sprites blend as linear radiance into an HDR scene target.
/// </summary>
public sealed class SrgbSprites : IDisposable
{
    readonly Effect effect;
    readonly EffectParameter transform, sprite;
    public SrgbSprites(GraphicsDevice gpu, string effectPath)
    {
        effect = new Effect(gpu, File.ReadAllBytes(effectPath));
        transform = effect.Parameters["WorldViewProjection"]; sprite = effect.Parameters["Sprite"];
    }
    public Matrix Projection { set => transform.SetValue(value); }
    public Texture2D Texture { set => sprite.SetValue(value); }
    public EffectTechnique CurrentTechnique => effect.CurrentTechnique;
    public void Dispose() => effect.Dispose();
}
