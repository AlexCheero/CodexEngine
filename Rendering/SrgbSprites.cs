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
    readonly EffectParameter transform, sprite, gain;
    public SrgbSprites(GraphicsDevice gpu, string effectPath)
    {
        effect = new Effect(gpu, File.ReadAllBytes(effectPath));
        transform = effect.Parameters["WorldViewProjection"]; sprite = effect.Parameters["Sprite"]; gain = effect.Parameters["Gain"];
        gain.SetValue(1f);
    }
    public Matrix Projection { set => transform.SetValue(value); }
    public Texture2D Texture { set => sprite.SetValue(value); }
    /// <summary>Linear multiplier for HDR sprites stored as sRGB(linear/scale); 1 for ordinary sprites.</summary>
    public float Gain { set => gain.SetValue(value); }
    public EffectTechnique CurrentTechnique => effect.CurrentTechnique;
    public void Dispose() => effect.Dispose();
}
