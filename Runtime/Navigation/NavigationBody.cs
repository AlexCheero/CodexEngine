namespace CodexEngine.Navigation;

/// <summary>
/// Envelope of one walking body class: standing clearance, footprint radius and the highest step it takes
/// without climbing. Layered navigation bakes and routes each class separately.
/// </summary>
public readonly record struct NavigationBody(float Height,float Radius,float Step)
{
    public bool Covers(float height,float radius,float step)=>
        height<=Height+.0001f && radius<=Radius+.0001f && step>=Step-.0001f;

    /// <summary>The first profile that covers the body. The last profile catches everything larger.</summary>
    public static int Select(ReadOnlySpan<NavigationBody> profiles,float height,float radius,float step)
    {
        for(int i=0;i<profiles.Length-1;i++)
            if(profiles[i].Covers(height,radius,step))return i;
        return profiles.Length-1;
    }
}
